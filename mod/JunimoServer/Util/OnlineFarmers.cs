using System.Collections.Generic;
using System.Linq;
using StardewValley;

namespace JunimoServer.Util;

/// <summary>
/// The one definition of "players online besides the host". Do NOT use
/// <c>Game1.otherFarmers.Count</c> for emptiness: a disconnect during an active event only
/// sets the <c>isDisconnecting</c> mark — the actual removal
/// (<c>Multiplayer.removeDisconnectedFarmers</c>, Multiplayer.cs:1821) is gated on
/// <c>CurrentEvent == null</c>, so the count never drops mid-event (host-automation.md
/// invariant 7). <c>isDisconnecting</c> is the signal the engine itself uses to see through
/// that; vanilla <c>DedicatedServer</c> builds its <c>onlineIds</c> the same way
/// (DedicatedServer.cs:286-293).
/// </summary>
public static class OnlineFarmers
{
    /// <summary>Connected non-host farmers, excluding disconnect-marked ones.</summary>
    public static List<Farmer> Others() =>
        Game1
            .getOnlineFarmers()
            .Where(f =>
                f.UniqueMultiplayerID != Game1.player.UniqueMultiplayerID
                && !Game1.Multiplayer.isDisconnecting(f)
            )
            .ToList();

    /// <summary>Count-only variant for hot paths (no list allocation).</summary>
    public static int CountOthers() =>
        Game1
            .getOnlineFarmers()
            .Count(f =>
                f.UniqueMultiplayerID != Game1.player.UniqueMultiplayerID
                && !Game1.Multiplayer.isDisconnecting(f)
            );
}
