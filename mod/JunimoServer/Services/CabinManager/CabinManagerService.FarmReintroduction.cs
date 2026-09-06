using System.Collections.Generic;
using JunimoServer.Util;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Network;

namespace JunimoServer.Services.CabinManager;

// Live Farm re-introduction for connected peers. The per-peer CabinStack fiction (the own
// hidden cabin relocated to the shared stack spot, or a phantom filling it) exists only in the
// copy OnLocationIntroductionMessage serializes; no net field carries it, so after '!cabin
// reset', a migration commit or a stack-spot change the only repair is a fresh location
// introduction (message 3). Vanilla sends one at join and in answer to a warp request, and the
// client (Multiplayer.readActiveLocation) has two behaviours an unsolicited one must respect:
//   - While Game1.locationRequest is open (from fade start until the answer arrives) it takes
//     the message as that warp's answer whatever the force flag: the player lands in the Farm
//     at the foreign warp's coordinates and the fade-complete block, guarded on the request,
//     never runs. The server learns of a warp only at completion, so no server-side location
//     check can exclude this.
//   - It repoints Game1.currentLocation only when that IS the Farm. A peer inside a Farm
//     building keeps the old interior and, since getLocationFromName("Farm") answers from the
//     current location's root, walks back into the orphaned old Farm on exit.
// Hence two deliveries. Immediately: for the chat-command sender, who cannot be mid-warp while
// typing, and for every peer of an admin action (migration commit, stack spot), where the
// admin picks the moment and the docs name the mid-warp case as a reconnect. Deferred, for a
// peer inside a Farm building: piggybacked behind the answer to their own next warp request
// off the farm, where the request is provably closed and the peer stands off the Farm.
public partial class CabinManagerService
{
    private readonly HashSet<long> _pendingFarmReintroductions = new();

    /// <summary>
    /// Re-sends the Farm to <paramref name="peerId"/> right now. Only for the peer who just
    /// sent a chat command (see the file comment). Returns false, sending nothing, when the
    /// peer is offline or stands inside a Farm building; callers then fall back to
    /// <see cref="QueueFarmReintroduction"/>.
    /// </summary>
    public bool TryReintroduceFarmNow(long peerId)
    {
        if (!Game1.hasLoadedGame || Game1.server is not GameServer server)
        {
            return false;
        }

        var farmer = Game1.GetPlayer(peerId);
        if (farmer == null || farmer == Game1.player || Game1.Multiplayer.isDisconnecting(farmer))
        {
            return false;
        }

        // Target, not the interpolated value: a peer who just entered their cabin still reads
        // as "on the Farm" for 15 server ticks otherwise, and a forced re-send in that window
        // would pull them out onto the Farm at the interior's coordinates.
        var farm = Game1.getFarm();
        var location = farmer.GetTargetLocation();
        if (location != null && location != farm && location.Root?.Value == farm)
        {
            return false;
        }

        // On the Farm itself the force flag makes the client re-enter it (map modifications,
        // lights, seasonal tilesheets); elsewhere the plain swap suffices and the next warp
        // onto the Farm is a normal entry.
        SendFarmIntroduction(server, peerId, forceCurrent: location == farm, trigger: "now");
        return true;
    }

    /// <summary>
    /// Re-sends the Farm to <paramref name="peerId"/> behind the answer to their next warp
    /// request off the farm. A join or disconnect drops the entry.
    /// </summary>
    public void QueueFarmReintroduction(long peerId)
    {
        _pendingFarmReintroductions.Add(peerId);
    }

    /// <summary>
    /// Admin-triggered variant for every connected player: re-sends now to those the gate
    /// admits and queues the rest. Returns the names of the queued players, or null when
    /// nobody is online. An admin picks the moment, so a player mid-warp at that instant is
    /// documented as a reconnect case rather than deferring everyone.
    /// </summary>
    public List<string> ReintroduceFarmToOnlinePeers()
    {
        var peers = OnlineFarmers.Others();
        if (peers.Count == 0)
        {
            return null;
        }

        var deferred = new List<string>();
        foreach (var peer in peers)
        {
            if (!TryReintroduceFarmNow(peer.UniqueMultiplayerID))
            {
                QueueFarmReintroduction(peer.UniqueMultiplayerID);
                deferred.Add(peer.Name);
            }
        }
        return deferred;
    }

    /// <summary>
    /// Message suffix for <see cref="ReintroduceFarmToOnlinePeers"/>: empty when nobody was
    /// queued.
    /// </summary>
    public static string DescribeDeferredReintroductions(List<string> deferred)
    {
        if (deferred == null || deferred.Count == 0)
        {
            return "";
        }

        return $" {string.Join(", ", deferred)} "
            + (deferred.Count == 1 ? "is" : "are")
            + " inside a building and will see it after next leaving the farm (or a reconnect).";
    }

    private void SendFarmIntroduction(
        GameServer server,
        long peerId,
        bool forceCurrent,
        string trigger
    )
    {
        var farm = Game1.getFarm();

        // A pending Farm delta is broadcast after updateRoots ticks the clock, so it would
        // outrank the full's fields on the client and rewrite the fiction (a just-reset cabin
        // back off-map). Flushed here it precedes the full on the wire at the same version.
        Game1.Multiplayer.broadcastLocationDelta(farm);

        NetworkHelper.SendLocation(server, peerId, farm, forceCurrent);

        // readActiveLocation evicts only the Farm itself from the client's name lookup; the
        // interior names keep resolving to the orphaned old objects, and doors warp by name.
        foreach (var building in farm.buildings)
        {
            var indoors = building.indoors.Value;
            if (indoors != null)
            {
                server.sendMessage(
                    peerId,
                    Multiplayer.removeLocationFromLookup,
                    Game1.serverHost.Value,
                    indoors.NameOrUniqueName
                );
            }
        }

        Monitor.Log(
            $"Re-introduced the Farm to peer {peerId} ({trigger}, forceCurrent={forceCurrent})",
            LogLevel.Debug
        );
        Diagnostics.ModEventLog.Emit(
            "cabin_farm_reintroduced",
            new
            {
                playerId = peerId,
                forceCurrent,
                trigger,
            }
        );
    }

    // Postfix on GameServer.warpFarmer: runs after vanilla (or NetworkTweaker's WarpFix prefix)
    // answered the peer's warp request with a location introduction, so a Farm introduction
    // queued behind it cannot be mistaken for that answer.
    private static void WarpFarmer_Postfix(Farmer farmer)
    {
        _instance?.DeliverPendingFarmReintroduction(farmer);
    }

    private void DeliverPendingFarmReintroduction(Farmer farmer)
    {
        if (
            farmer == null
            || !_pendingFarmReintroductions.Contains(farmer.UniqueMultiplayerID)
            || Game1.server is not GameServer server
        )
        {
            return;
        }

        // warpFarmer set currentLocation to the answered destination. A Farm-rooted answer
        // (WarpFix resending the Farm for a mis-flagged structure) leaves the peer inside a
        // Farm building, so the entry stays queued for a later warp.
        var destination = farmer.currentLocation;
        if (destination == null || destination.Root?.Value == Game1.getFarm())
        {
            return;
        }

        _pendingFarmReintroductions.Remove(farmer.UniqueMultiplayerID);
        SendFarmIntroduction(server, farmer.UniqueMultiplayerID, forceCurrent: false, "warp");
    }
}
