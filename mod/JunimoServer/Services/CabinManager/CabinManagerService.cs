using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using JunimoServer.Services.Lobby;
using JunimoServer.Services.MessageInterceptors;
using JunimoServer.Services.PersistentOption;
using JunimoServer.Services.Roles;
using JunimoServer.Services.ServerOptim;
using JunimoServer.Shared;
using JunimoServer.Util;
using Microsoft.Xna.Framework;
using Netcode;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.Locations;
using StardewValley.Network;

namespace JunimoServer.Services.CabinManager;

public class ServerJoinedEventArgs : EventArgs
{
    private long peerId;

    public long PeerId => peerId;

    public ServerJoinedEventArgs(long peerId)
    {
        this.peerId = peerId;
    }
}

public delegate void ServerJoinedHandler(object sender, ServerJoinedEventArgs e);

public class CabinManagerService : ModService
{
    public CabinManagerData Data
    {
        get => _cabinManagerData;
        set { _cabinManagerData = value; }
    }

    public static readonly Point HiddenCabinLocation = CabinPositions.PlayerStack;

    public readonly PersistentOptions options;

    private readonly RoleService roleService;

    private static readonly int minEmptyCabins = 1;

    private readonly HashSet<long> farmersInFarmhouse = new HashSet<long>();

    // Static reference ONLY for Harmony patches (unavoidable)
    private static CabinManagerService _instance;

    // Instance data - NOT static
    private CabinManagerData _cabinManagerData;

    public CabinManagerService(
        IModHelper helper,
        IMonitor monitor,
        Harmony harmony,
        RoleService roleService,
        MessageInterceptorsService messageInterceptorsService,
        PersistentOptions options
    )
        : base(helper, monitor)
    {
        if (_instance != null)
        {
            throw new InvalidOperationException(
                "CabinManagerService already initialized - only one instance allowed"
            );
        }

        _instance = this;

        this.roleService = roleService;
        this.options = options;

        Data = new CabinManagerData(helper, monitor);

        Helper.Events.GameLoop.SaveLoaded += OnSaveLoaded;

        // For None strategy, let vanilla handle starting cabins and skip message
        // interception and farmhouse monitoring. Cabins are real and visible.
        if (!options.IsNone)
        {
            // Disable default starting cabin logic, we handle it
            harmony.Patch(
                original: AccessTools.Method(
                    typeof(GameLocation),
                    nameof(GameLocation.BuildStartingCabins)
                ),
                prefix: new HarmonyMethod(
                    typeof(ServerOptimizerOverrides),
                    nameof(ServerOptimizerOverrides.Disable_Prefix)
                )
            );

            // Hijack outgoing messages for cabin warp manipulation
            messageInterceptorsService
                .Add(Multiplayer.locationIntroduction, OnLocationIntroductionMessage)
                .Add(Multiplayer.locationDelta, OnLocationDeltaMessage);

            // Monitor farmhouse access; only the server host can enter (no human players)
            Helper.Events.GameLoop.UpdateTicked += OnTicked;
        }

        // Always hook player join. Needed for peer tracking and auto-cabin creation.
        harmony.Patch(
            original: AccessTools.Method(
                typeof(GameServer),
                nameof(GameServer.sendServerIntroduction)
            ),
            postfix: new HarmonyMethod(typeof(CabinManagerService), nameof(OnServerJoined_Postfix))
        );

        // Always hook player disconnect to release abandoned slot claims, on ALL transports.
        // GameServer.playerDisconnected is the single choke point every transport routes through
        // (Steam SDR, GOG/Galaxy, LAN). This patch is registered here — unconditionally — rather
        // than in PasswordProtectionService, whose patches are skipped entirely on passwordless
        // servers (its constructor returns early when !IsEnabled), which would leave the heal
        // dead for the common no-password case.
        harmony.Patch(
            original: AccessTools.Method(typeof(GameServer), nameof(GameServer.playerDisconnected)),
            postfix: new HarmonyMethod(
                typeof(CabinManagerService),
                nameof(OnPlayerDisconnected_Postfix)
            )
        );

        // Defensive: make Utility.getHomeOfFarmer null-safe.
        // The vanilla implementation calls RequireLocation which throws KeyNotFoundException
        // if the cabin interior isn't findable yet (e.g. during new game setup, day transitions,
        // or transient states where indoors.Value is briefly null). SDV itself uses null-safe
        // patterns (TryAssignFarmhandHome, `is Cabin` checks) in connection code, but dozens
        // of other callers go through getHomeOfFarmer without protection.
        harmony.Patch(
            original: AccessTools.Method(typeof(Utility), nameof(Utility.getHomeOfFarmer)),
            prefix: new HarmonyMethod(typeof(CabinManagerService), nameof(GetHomeOfFarmer_Prefix))
        );
    }

    private void OnSaveLoaded(object sender, SaveLoadedEventArgs e)
    {
        Data.Read();

        // Detect and handle strategy changes between runs
        DetectAndMigrateStrategyChange();

        // Register existing cabin owners from imported saves
        SyncExistingCabins();

        // Defense-in-depth: clear stale farmhand references from prior sessions
        // before any reconnect could pick them up. Runtime DestroyCabin already handles
        // this for in-process deletions; this catches save files where cabins were
        // removed between SDV process restarts. currentLocation is [XmlIgnore] so
        // it's null after deserialize; only homeLocation / lastSleepLocation can
        // carry stale refs across the save boundary.
        ClearStaleFarmhandReferences();

        // Release abandoned slot claims that survived into the save. The disconnect-path heal
        // (OnPlayerDisconnected_Postfix) covers every clean disconnect, but a stuck claim whose
        // home cabin still exists is NOT cleared by vanilla's load-time ResetFarmhandState —
        // it clears userID only when TryAssignFarmhandHome fails (no valid cabin), and a homed
        // farmhand takes the early-return branch (NetWorldState.cs:783) leaving userID intact.
        // So a claim stamped right before a host crash (or carried by a pre-fix corrupted save)
        // would reload still-locked. This sweep closes that gap. It runs after
        // ClearStaleFarmhandReferences, which has already purged cabin-less orphans.
        ClearAbandonedCabinClaimsOnLoad();

        EnsureAtLeastXCabins();
    }

    private void ClearStaleFarmhandReferences()
    {
        var farm = Game1.getFarm();
        if (farm == null)
        {
            return;
        }

        var validCabinNames = new HashSet<string>();
        foreach (var building in farm.buildings)
        {
            if (!building.isCabin)
            {
                continue;
            }

            var name = building.GetIndoors<Cabin>()?.NameOrUniqueName;
            if (!string.IsNullOrEmpty(name))
            {
                validCabinNames.Add(name);
            }
        }

        var farmhandData = Game1.netWorldState.Value.farmhandData;
        var toRemove = new List<long>();
        int homeCleared = 0;
        int lastSleepCleared = 0;
        foreach (var kvp in farmhandData.FieldDict)
        {
            var f = kvp.Value.Value;
            if (f == null)
            {
                continue;
            }

            var home = f.homeLocation.Value;
            var lastSleep = f.lastSleepLocation.Value;
            var homeStale = !string.IsNullOrEmpty(home) && !validCabinNames.Contains(home);
            var lastSleepStale =
                !string.IsNullOrEmpty(lastSleep) && !validCabinNames.Contains(lastSleep);
            if (!homeStale && !lastSleepStale)
            {
                continue;
            }

            if (!f.isCustomized.Value)
            {
                // Slot placeholder whose cabin vanished across sessions. Purge.
                Monitor.Log(
                    $"Removing orphan uncustomized farmhand (dictKey={kvp.Key}) at save load: "
                        + $"home='{home ?? "(null)"}' (stale={homeStale}) lastSleep='{lastSleep ?? "(null)"}' (stale={lastSleepStale})",
                    LogLevel.Debug
                );
                toRemove.Add(kvp.Key);
            }
            else
            {
                if (homeStale)
                {
                    Monitor.Log(
                        $"Cleared stale homeLocation '{home}' from farmhand '{ChatRedaction.MaskValue(f.Name)}' (id={f.UniqueMultiplayerID}) at save load",
                        LogLevel.Debug
                    );
                    f.homeLocation.Value = "";
                    homeCleared++;
                }
                if (lastSleepStale)
                {
                    Monitor.Log(
                        $"Cleared stale lastSleepLocation '{lastSleep}' from farmhand '{ChatRedaction.MaskValue(f.Name)}' (id={f.UniqueMultiplayerID}) at save load",
                        LogLevel.Debug
                    );
                    f.lastSleepLocation.Value = null;
                    lastSleepCleared++;
                }
            }
        }
        foreach (var key in toRemove)
        {
            farmhandData.Remove(key);
        }

        if (toRemove.Count > 0 || homeCleared > 0 || lastSleepCleared > 0)
        {
            Diagnostics.ModEventLog.Emit(
                "farmhand_references_cleaned",
                new
                {
                    orphansRemoved = toRemove.Count,
                    homeCleared,
                    lastSleepCleared,
                    validCabins = validCabinNames.Count,
                }
            );
        }
    }

    private void OnTicked(object sender, UpdateTickedEventArgs e)
    {
        MonitorFarmhouse();
    }

    private static void OnServerJoined_Postfix(long peer)
    {
        _instance?.OnServerJoined(peer);
    }

    // Postfix on GameServer.playerDisconnected — runs while the disconnecting farmhand is still
    // in otherFarmers (removeDisconnectedFarmers is deferred to later in the same update), and
    // after vanilla saveFarmhand has cloned its state into farmhandData. Releases any abandoned
    // slot claim on the persisted entry. CleanupAbandonedCabinClaim null-guards _instance.
    private static void OnPlayerDisconnected_Postfix(long disconnectee)
    {
        CleanupAbandonedCabinClaim(disconnectee);
    }

    private void OnServerJoined(long peer)
    {
        AddPeer(peer);
        EnsureAtLeastXCabins(excludePeer: peer);
    }

    #region Strategy Change Migration

    private void DetectAndMigrateStrategyChange()
    {
        var previousStrategy = options.PreviousCabinStrategy;
        var currentStrategy = options.Data.CabinStrategy;

        if (previousStrategy == currentStrategy)
        {
            return;
        }

        Monitor.Log(
            $"CabinStrategy changed from {previousStrategy} to {currentStrategy}, migrating cabins...",
            LogLevel.Warn
        );
        MigrateCabins(previousStrategy, currentStrategy);
    }

    private void MigrateCabins(CabinStrategy from, CabinStrategy to)
    {
        var farm = Game1.getFarm();
        bool fromUsesHidden = (
            from == CabinStrategy.CabinStack || from == CabinStrategy.FarmhouseStack
        );
        bool toUsesHidden = (to == CabinStrategy.CabinStack || to == CabinStrategy.FarmhouseStack);

        int migrated = 0;

        if (fromUsesHidden && !toUsesHidden)
        {
            // Stacked → None: move hidden cabins to visible farm positions
            var hiddenCabins = farm.buildings.Where(b => b.isCabin && b.IsInHiddenStack()).ToList();

            var availablePositions = FarmCabinPositions.GetAvailablePositions(farm);

            if (hiddenCabins.Count > availablePositions.Count)
            {
                Monitor.Log(
                    $"CabinStrategy migration {from} → {to} aborted: {hiddenCabins.Count} hidden cabin(s) "
                        + $"but only {availablePositions.Count} designated position(s) available on this farm map. "
                        + $"Reverting strategy to {from}. Add cabin positions to the Paths layer or remove "
                        + $"surplus cabins before retrying.",
                    LogLevel.Warn
                );

                Diagnostics.ModEventLog.Emit(
                    "cabin_strategy_migration_aborted",
                    new
                    {
                        fromStrategy = from.ToString(),
                        toStrategy = to.ToString(),
                        hiddenCabinCount = hiddenCabins.Count,
                        availablePositionCount = availablePositions.Count,
                        deficit = hiddenCabins.Count - availablePositions.Count,
                        reason = "insufficient_designated_positions",
                    }
                );

                // Revert the persisted strategy so the on-disk state matches what actually
                // happened. RecaptureAndSync already wrote the new strategy to Data and disk
                // (via SyncFromSettings → Save) before OnSaveLoaded ran; without this revert,
                // the next load captures previous == current == new and never retries.
                options.Data.CabinStrategy = from;
                options.Save();
                return;
            }

            foreach (var cabin in hiddenCabins)
            {
                var nextPos = FarmCabinPositions.GetNextAvailablePosition(farm);
                // Pre-validation above guarantees a slot for every hidden cabin: MigrateCabins
                // runs synchronously on the game thread inside OnSaveLoaded, no buildStructure
                // call can interleave, so the count cannot shrink mid-loop. nextPos is always set.
                cabin.Relocate(nextPos.Value);
                Monitor.Log(
                    $"  Migrated cabin to ({nextPos.Value.X}, {nextPos.Value.Y})",
                    LogLevel.Info
                );
                migrated++;
            }
        }
        else if (!fromUsesHidden && toUsesHidden)
        {
            // None → Stacked: move visible cabins to hidden stack (exclude lobby/editing
            // cabins and any cabin a player explicitly placed via /cabin)
            var visibleCabins = farm
                .buildings.Where(b =>
                    b.isCabin
                    && !b.IsInHiddenStack()
                    && !b.IsLobbyOrEditing()
                    && !HasSavedPosition(b)
                )
                .ToList();

            foreach (var cabin in visibleCabins)
            {
                cabin.SetPosition(HiddenCabinLocation);
                Monitor.Log($"  Migrated cabin to hidden stack", LogLevel.Info);
                migrated++;
            }
        }
        // Stacked ↔ Stacked: no relocation needed, only warp behavior changes

        // Aborts (insufficient positions) emit cabin_strategy_migration_aborted and
        // return early, so this success event never carries a failure count.
        Diagnostics.ModEventLog.Emit(
            "cabin_strategy_migration",
            new
            {
                fromStrategy = from.ToString(),
                toStrategy = to.ToString(),
                migrated,
            }
        );
    }

    #endregion

    #region Existing Cabin Import Handling

    /// <summary>
    /// True if the cabin's owner has explicitly placed it via the /cabin command.
    /// Such a cabin must not be pulled back into the hidden stack by the bulk
    /// movers (MoveToStack / strategy migration). This distinguishes a /cabin-placed
    /// cabin from an imported-but-claimed one, which MoveToStack should still sweep.
    /// </summary>
    private bool HasSavedPosition(Building cabin)
    {
        var ownerId = cabin.GetIndoors<Cabin>()?.owner?.UniqueMultiplayerID ?? 0;
        return ownerId != 0 && Data.PlayerCabinPositions.ContainsKey(ownerId);
    }

    private void SyncExistingCabins()
    {
        var farm = Game1.getFarm();
        var allCabins = farm.buildings.Where(b => b.isCabin).ToList();
        var syncedCount = 0;

        foreach (var cabin in allCabins)
        {
            var indoors = cabin.GetIndoors<Cabin>();
            if (indoors?.owner == null)
            {
                continue;
            }

            // Only sync cabins that are actually claimed by a real player.
            // Unassigned cabins have auto-generated UniqueMultiplayerIDs but empty userIDs.
            var owner = indoors.owner;
            var ownerId = owner.UniqueMultiplayerID;
            if (
                ownerId != 0
                && !string.IsNullOrEmpty(owner.userID.Value)
                && Data.AllPlayerIdsEverJoined.Add(ownerId)
            )
            {
                syncedCount++;
            }
        }

        Diagnostics.ModEventLog.Emit(
            "cabin_sync",
            new
            {
                syncedCount,
                totalCabins = allCabins.Count,
                strategy = options.Data.CabinStrategy.ToString(),
            }
        );

        if (syncedCount > 0)
        {
            Monitor.Log($"Synced {syncedCount} existing cabin owner(s) from save", LogLevel.Info);
            Data.Write();
        }

        // Handle ExistingCabinBehavior for stacked strategies
        if (
            options.UsesHiddenCabins
            && options.Data.ExistingCabinBehavior == ExistingCabinBehavior.MoveToStack
        )
        {
            var visibleCabins = allCabins
                .Where(b => !b.IsInHiddenStack() && !b.IsLobbyOrEditing() && !HasSavedPosition(b))
                .ToList();
            if (visibleCabins.Count > 0)
            {
                Monitor.Log(
                    $"MoveToStack: relocating {visibleCabins.Count} visible cabin(s) to hidden stack",
                    LogLevel.Info
                );
                foreach (var cabin in visibleCabins)
                {
                    cabin.SetPosition(HiddenCabinLocation);
                }
            }
        }
    }

    #endregion

    #region Message Interception

    private void OnLocationIntroductionMessage(MessageContext context)
    {
        // Parse message
        var forceCurrentLocation = context.Reader.ReadBoolean();
        var netRootLocation = NetRoot<GameLocation>.Connect(context.Reader);

        // Check location
        if (netRootLocation.Value is not Farm netRootFarm)
        {
            return;
        }

        GameLocation farm;

        if (this.options.IsFarmHouseStack)
        {
            // Farmhouse stacking strategy:
            // Update warp coordinates on the server. Since there is only a single
            // farmhouse building, we adjust its warps while leaving all cabins in
            // `HiddenCabinLocation`.
            farm = Game1.getFarm();
            var fhCabin = farm.GetCabin(context.PeerId);
            if (fhCabin != null)
            {
                fhCabin.SetWarpsToFarmFarmhouseDoor();
            }
            else
            {
                Monitor.Log(
                    $"FarmhouseStack: cabin not found for peer {context.PeerId} during location introduction (cabin ownership may not be linked yet)",
                    LogLevel.Warn
                );
            }
        }
        else
        {
            // Cabin stacking strategy:
            // Relocate the player's cabin client-side so only the owner sees it.
            // Only relocate cabins that are in the hidden stack. Cabins at real
            // positions (e.g. from imported saves with KeepExisting) stay put.
            farm = netRootFarm;
            var cabin = farm.GetCabin(context.PeerId);
            if (cabin != null && cabin.IsInHiddenStack())
            {
                cabin.Relocate(StackLocation.Create(_cabinManagerData).ToPoint());
            }
            else if (cabin != null && !cabin.IsInHiddenStack())
            {
                // This peer's own cabin is NOT in the hidden stack (they moved it via
                // /cabin, or it imported visible under KeepExisting), so they'd otherwise
                // see an empty spot at the shared StackLocation where everyone else sees a
                // cabin. Render one hidden-stack cabin there as a *door-dead* dummy so the
                // empty spot is filled without exposing another player's home.
                //
                // Both routes (!cabin-moved and KeepExisting-imported-visible) reach this
                // branch through the identical `!IsInHiddenStack()` precondition, which is all
                // this branch reads — the saved-position intent map is not consulted here. So
                // the !cabin-moved E2E test (DummyCabin_AfterMoveAndReconnect...) covers the
                // KeepExisting route by equivalence; a separate KeepExisting test would
                // exercise the same dummy code with heavier strategy-setup, not new coverage.
                //
                // FirstOrDefault + null-guard are load-bearing defense: a First/. would turn
                // the (effectively unreachable on a real join — EnsureAtLeastXCabins replenishes
                // the hidden pool before locationIntroduction) zero-candidate case into a broken
                // join handshake. IsInHiddenStack() checks (-20,-20) only, so lobby cabins at
                // (-21,-21) are excluded automatically.
                var dummy = farm.buildings.FirstOrDefault(b =>
                    b.isCabin && b != cabin && b.IsInHiddenStack()
                );
                if (dummy != null)
                {
                    PlaceDoorDeadDummyCabin(dummy);
                }
            }
        }

        // Update the outgoing message
        context.ModifiedMessage = NetworkHelper.CreateMessageLocationIntroduction(
            context.PeerId,
            farm.Root,
            forceCurrentLocation
        );
    }

    /// <summary>
    /// Renders a hidden-stack cabin at the shared StackLocation as a door-dead dummy, mutating
    /// only the per-peer message copy passed in. The cabin fills the empty spot the peer would
    /// otherwise see, but its door is a no-op — stepping on it does nothing, so it never exposes
    /// the real owner's home.
    ///
    /// How the door is killed sure-fire: Building.doAction only warps the player inside when
    /// GetIndoors() != null (decompiled Building.cs:950). GetIndoors() reads indoors.Value then
    /// nonInstancedIndoorsName; null both → no interior → no entry. The client never re-creates
    /// the interior: LoadFromBuildingData's createIndoors is gated on `hasLoaded || forConstruction`
    /// (Building.cs:523), and hasLoaded is only ever set on the master (Building.load() early-returns
    /// `if (!Game1.IsMasterGame)`), so on a farmhand client hasLoaded stays false and the nulled
    /// interior survives deserialization. humanDoor IS reset from data on the client, but that's
    /// irrelevant once GetIndoors() is null. This mutates the deserialized copy only (NetRoot.Connect
    /// builds a fresh graph), so master state and other peers are untouched.
    /// </summary>
    private void PlaceDoorDeadDummyCabin(Building dummy)
    {
        dummy.SetPosition(StackLocation.Create(_cabinManagerData).ToPoint());
        // Kill the door: null both interior references so GetIndoors() returns null on the client.
        // No need to set interior warps — there is no interior to warp into anymore.
        dummy.indoors.Value = null;
        dummy.nonInstancedIndoorsName.Value = null;
    }

    private void OnLocationDeltaMessage(MessageContext context)
    {
        if (NetworkHelper.IsLocationDeltaMessageForLocation(context, out Cabin cabin))
        {
            if (this.options.IsFarmHouseStack)
            {
                cabin.SetWarpsToFarmFarmhouseDoor();
            }
            else
            {
                cabin.SetWarpsToFarmCabinDoor();
            }
        }
    }

    #endregion

    #region Peer Management

    private void AddPeer(long peerId)
    {
        Monitor.Log($"Adding peer '{peerId}'", LogLevel.Debug);
        var added = Data.AllPlayerIdsEverJoined.Add(peerId);
        Data.Write();
        Diagnostics.ModEventLog.Emit(
            "cabin_peer_added",
            new
            {
                playerId = peerId,
                firstTime = added,
                totalEverJoined = Data.AllPlayerIdsEverJoined.Count,
            }
        );
    }

    #endregion

    #region Farmhouse Access Control

    private void MonitorFarmhouse()
    {
        if (!Game1.hasLoadedGame)
        {
            return;
        }

        var farmersInFarmHouseCurrent = new HashSet<long>();
        var farmers = Game1.getLocationFromName("Farmhouse").farmers;

        foreach (var farmer in farmers)
        {
            farmersInFarmHouseCurrent.Add(farmer.UniqueMultiplayerID);
        }

        foreach (var farmer in farmers)
        {
            if (!farmersInFarmhouse.Contains(farmer.UniqueMultiplayerID))
            {
                farmersInFarmhouse.Add(farmer.UniqueMultiplayerID);

                // Block all human players from the farmhouse - it's reserved for the server host
                if (!roleService.IsServerHost(farmer))
                {
                    Helper.SendPrivateMessage(
                        farmer.UniqueMultiplayerID,
                        "Can't enter main building, porting to your own cabin"
                    );

                    farmer.WarpHome();
                }
            }
        }

        farmersInFarmhouse.RemoveWhere(farmerId => !farmersInFarmHouseCurrent.Contains(farmerId));
    }

    #endregion

    #region Cabin Creation

    public void EnsureAtLeastXCabins(int minRequired = 1, long excludePeer = 0)
    {
        var farm = Game1.getFarm();
        var availableCount = GetAvailableCabinCount(farm, excludePeer);
        var effectiveMin = Math.Max(minEmptyCabins, minRequired);
        var cabinsMissingCount = effectiveMin - availableCount;

        Monitor.Log(
            $"Cabin check: {availableCount}/{effectiveMin} available, building {Math.Max(0, cabinsMissingCount)}",
            LogLevel.Debug
        );

        int built = 0;
        int failed = 0;
        for (var i = 0; i < cabinsMissingCount; i++)
        {
            Monitor.Log(
                $"Cabin check: building cabin {i + 1}/{cabinsMissingCount}",
                LogLevel.Trace
            );

            bool success = options.IsNone ? BuildNewCabinVisible(farm) : BuildNewCabin(farm);

            if (success)
            {
                built++;
            }
            else
            {
                failed++;
                // Warn, not Error: a failed build is recoverable (server continues; the
                // failure is also surfaced via cabin_build_failed and the cabinsFailed
                // field below). LogLevel.Error trips ServerContainer's ERROR/FATAL test
                // poison. For None this is the expected "ran out of map positions" cap.
                Monitor.Log(
                    $"Cabin check: failed building cabin {i + 1}/{cabinsMissingCount}",
                    LogLevel.Warn
                );
            }
        }

        Diagnostics.ModEventLog.Emit(
            "cabin_ensure_checked",
            new
            {
                minRequired = effectiveMin,
                availableCount,
                cabinsBuilt = built,
                cabinsFailed = failed,
                excludePeer,
                strategy = options.Data.CabinStrategy.ToString(),
            }
        );
    }

    /// <summary>
    /// Count available (unassigned) cabins, strategy-aware.
    /// A cabin is available if its owner has NOT been customized (isCustomized = false)
    /// and has no userID assigned. This matches how SyncExistingCabins determines claimed cabins.
    /// Excludes lobby cabins which are managed separately by the password protection system.
    /// </summary>
    private int GetAvailableCabinCount(GameLocation farm, long excludePeer = 0)
    {
        return farm
            .buildings.Where(b => b.isCabin && !LobbyService.IsLobbyCabin(b))
            .Count(b => IsCabinAvailable(b, excludePeer));
    }

    /// <summary>
    /// Determines if a cabin is available for a new player to claim.
    /// A cabin is available if it has NOT been customized by a player yet
    /// and no player is actively connected to it.
    /// </summary>
    private static bool IsCabinAvailable(Building cabinBuilding, long excludePeer = 0)
    {
        var cabin = cabinBuilding.GetIndoors<Cabin>();
        var owner = cabin?.owner;

        if (owner == null)
        {
            // No owner object = definitely available
            return true;
        }

        // A cabin is "taken" if the owner has been customized OR has a userID assigned
        // (userID is set when a player claims the farmhand slot via Steam/GOG)
        if (owner.isCustomized.Value)
        {
            return false;
        }

        if (!string.IsNullOrEmpty(owner.userID.Value))
        {
            return false;
        }

        // Check if a player is actively connected to this farmhand slot.
        // This handles LAN connections where userID is always empty and
        // first-join timing where isCustomized is still false.
        if (owner.isActive())
        {
            return false;
        }

        // The joining peer's farmhand isn't active yet at OnServerJoined time,
        // but the cabin is about to be occupied. Exclude it from available count.
        if (excludePeer != 0 && owner.UniqueMultiplayerID == excludePeer)
        {
            return false;
        }

        // Owner exists but is not customized, has no userID, and nobody is connected = available slot
        return true;
    }

    /// <summary>
    /// Releases an abandoned slot claim on a single farmhand entry. A claim is "abandoned" when
    /// the farmhand has a userID set (a player clicked the slot, which stamps their platform ID
    /// via vanilla Client.sendPlayerIntroduction) but isCustomized is false (they quit before
    /// finishing character creation). Vanilla FarmhandMenu then greys the slot out for every
    /// other player, locking it to the ghost. Clearing userID re-opens the slot.
    ///
    /// Caller-agnostic: both the disconnect heal (CleanupAbandonedCabinClaim) and the save-load
    /// sweep (ClearAbandonedCabinClaimsOnLoad) pass the persisted farmhandData entry. The only
    /// mutation is the userID NetString value write; the rest of the farmhand stays in its
    /// default uncustomized state. Returns true if a claim was cleared.
    /// </summary>
    private bool TryClearAbandonedClaim(Farmer farmhand)
    {
        if (farmhand == null)
        {
            return false;
        }

        if (string.IsNullOrEmpty(farmhand.userID.Value))
        {
            return false; // no claim
        }

        if (farmhand.isCustomized.Value)
        {
            return false; // real player — must not touch
        }

        Monitor.Log(
            $"Releasing abandoned cabin claim (userID='{ChatRedaction.MaskValue(farmhand.userID.Value)}', slot was claimed but not customized)",
            LogLevel.Info
        );
        Diagnostics.ModEventLog.Emit(
            "cabin_claim_abandoned",
            new
            {
                clearedUserId = farmhand.userID.Value,
                ownerUniqueMultiplayerId = farmhand.UniqueMultiplayerID,
            }
        );
        farmhand.userID.Value = "";
        return true;
    }

    /// <summary>
    /// Releases an abandoned slot claim for a disconnecting player. Called from this service's
    /// own always-on GameServer.playerDisconnected postfix (OnPlayerDisconnected_Postfix), so it
    /// covers Steam SDR, GOG/Galaxy, and LAN alike — including passwordless servers.
    ///
    /// Clears the persisted farmhandData entry — at postfix time the disconnecting farmhand is
    /// still in otherFarmers (removal is deferred to removeDisconnectedFarmers later in the same
    /// update), so Cabin.owner resolves to the live copy that's about to be discarded; vanilla's
    /// saveFarmhand already cloned the stuck userID into the persisted entry. We also clear the
    /// live copy so any read before removal (e.g. /diagnostics/state) reflects the heal.
    /// </summary>
    /// <param name="disconnecteeId">UniqueMultiplayerID of the disconnecting player.</param>
    public static void CleanupAbandonedCabinClaim(long disconnecteeId)
    {
        if (_instance == null)
        {
            return;
        }

        // TryGetValue, not the indexer: NetLongDictionary's indexer throws KeyNotFoundException
        // on a missing key (see NetworkTweaker's checkFarmhandRequest safe-lookup patch).
        if (!Game1.netWorldState.Value.farmhandData.TryGetValue(disconnecteeId, out var farmhand))
        {
            return;
        }

        if (
            _instance.TryClearAbandonedClaim(farmhand)
            && Game1.otherFarmers.TryGetValue(disconnecteeId, out var liveFarmhand)
        )
        {
            liveFarmhand.userID.Value = "";
        }
    }

    /// <summary>
    /// Sweeps the persisted farmhandData on save load and releases any abandoned slot claim that
    /// survived into the save. Covers the gap vanilla's load-time ResetFarmhandState leaves: it
    /// clears userID only for farmhands whose home cabin is missing (the else-branch when
    /// TryAssignFarmhandHome fails), so a stuck-but-homed claim — the normal shape, since the
    /// slot's cabin still exists — reloads with userID intact. The live disconnect heal can be
    /// skipped only by an unclean exit (host crash before the next disconnect, or a save written
    /// by a build predating that heal); this sweep catches those on the next load.
    ///
    /// Reuses TryClearAbandonedClaim, so the guard (userID set + not customized) and the
    /// cabin_claim_abandoned emit are identical to the disconnect path. No live otherFarmers
    /// clear is needed here: no farmhand is connected during save load, so Cabin.owner resolves
    /// to the persisted entry this mutates. FieldDict iteration is a read-only enumeration
    /// (mutation goes through the userID NetString setter), allowed by netdictionary-public-surface.
    /// </summary>
    private void ClearAbandonedCabinClaimsOnLoad()
    {
        var farmhandData = Game1.netWorldState.Value.farmhandData;
        int cleared = 0;
        foreach (var kvp in farmhandData.FieldDict)
        {
            if (TryClearAbandonedClaim(kvp.Value.Value))
            {
                cleared++;
            }
        }

        if (cleared > 0)
        {
            Diagnostics.ModEventLog.Emit("cabin_claims_swept_on_load", new { cleared });
        }
    }

    /// <summary>
    /// Build a cabin at the hidden out-of-bounds location (for CabinStack/FarmhouseStack).
    /// </summary>
    public bool BuildNewCabin(GameLocation location)
    {
        var cabinTilePosition = HiddenCabinLocation.ToVector2();
        var cabin = CreateCabinBuilding(cabinTilePosition);

        if (location.buildStructure(cabin, cabinTilePosition, Game1.player, true))
        {
            cabin.ClearTerrainBelow();

            var indoors = cabin.GetIndoors<Cabin>();
            if (indoors == null)
            {
                Monitor.Log(
                    "Hidden cabin was built but has no interior; farmhand not created",
                    LogLevel.Error
                );
                Diagnostics.ModEventLog.Emit(
                    "cabin_build_failed",
                    new
                    {
                        hidden = true,
                        tileX = (int)cabinTilePosition.X,
                        tileY = (int)cabinTilePosition.Y,
                        reason = "no_interior_after_buildStructure",
                    }
                );
                return false;
            }

            return true;
        }

        Diagnostics.ModEventLog.Emit(
            "cabin_build_failed",
            new
            {
                hidden = true,
                tileX = (int)cabinTilePosition.X,
                tileY = (int)cabinTilePosition.Y,
                reason = "buildStructure_returned_false",
            }
        );
        return false;
    }

    /// <summary>
    /// Build a cabin at a real, visible farm position (for None strategy).
    /// Uses map-designated positions from the Paths layer.
    /// </summary>
    public bool BuildNewCabinVisible(GameLocation location)
    {
        var farm = location as Farm ?? Game1.getFarm();
        var position = FarmCabinPositions.GetNextAvailablePosition(farm);

        if (!position.HasValue)
        {
            Monitor.Log("No available designated cabin position on farm map", LogLevel.Warn);
            Diagnostics.ModEventLog.Emit(
                "cabin_build_failed",
                new { hidden = false, reason = "no_available_map_position" }
            );
            return false;
        }

        var cabin = CreateCabinBuilding(position.Value);

        if (location.buildStructure(cabin, position.Value, Game1.player, true))
        {
            cabin.ClearTerrainBelow();

            var indoors = cabin.GetIndoors<Cabin>();
            if (indoors == null)
            {
                Monitor.Log(
                    $"Visible cabin at ({position.Value.X}, {position.Value.Y}) was built but has no interior; farmhand not created",
                    LogLevel.Error
                );
                Diagnostics.ModEventLog.Emit(
                    "cabin_build_failed",
                    new
                    {
                        hidden = false,
                        tileX = (int)position.Value.X,
                        tileY = (int)position.Value.Y,
                        reason = "no_interior_after_buildStructure",
                    }
                );
                return false;
            }

            Monitor.Log(
                $"Built visible cabin at ({position.Value.X}, {position.Value.Y})",
                LogLevel.Info
            );
            return true;
        }

        Diagnostics.ModEventLog.Emit(
            "cabin_build_failed",
            new
            {
                hidden = false,
                tileX = (int)position.Value.X,
                tileY = (int)position.Value.Y,
                reason = "buildStructure_returned_false",
            }
        );
        return false;
    }

    /// <summary>
    /// Canonical cabin removal. Drops the owner's farmhand entry, removes the
    /// building, and clears any stale homeLocation references in surviving
    /// farmhandData entries.
    ///
    /// Vanilla's destroyStructure would also fire SendBuildingDemolishedEvent.
    /// We deliberately use raw buildings.Remove to match the existing
    /// ApiService.ExecuteFarmhandDeletion behavior; revisit later if we want
    /// to broadcast cabin removals to connected clients.
    /// </summary>
    public void DestroyCabin(Building cabinBuilding)
    {
        if (cabinBuilding == null)
        {
            return;
        }

        var farm = Game1.getFarm();
        if (farm == null || !farm.buildings.Contains(cabinBuilding))
        {
            return;
        }

        var indoors = cabinBuilding.GetIndoors<Cabin>();
        var deletedName = indoors?.NameOrUniqueName;
        var ownerId = indoors?.owner?.UniqueMultiplayerID ?? 0;
        var ownerName = indoors?.owner?.Name ?? "";

        if (indoors != null && indoors.HasOwner)
        {
            indoors.DeleteFarmhand();
        }

        farm.buildings.Remove(cabinBuilding);

        Diagnostics.ModEventLog.Emit(
            "cabin_destroyed",
            new
            {
                tileX = cabinBuilding.tileX.Value,
                tileY = cabinBuilding.tileY.Value,
                indoorsName = deletedName ?? "",
                ownerId,
                ownerName,
            }
        );

        if (indoors == null || string.IsNullOrEmpty(deletedName))
        {
            return;
        }

        // Scrub surviving farmhandData entries whose location refs point at the
        // removed cabin. Ownership removal is handled by Cabin.DeleteFarmhand
        // above (FarmerTeam.cs:1037-1041 removes the owner's key from farmhandData).
        // The match-by-location below clears stale homeLocation/currentLocation/
        // lastSleepLocation refs only — never deletes entries, since match by
        // location does not imply ownership.
        var farmhandData = Game1.netWorldState.Value.farmhandData;

        // Defense-in-depth: if the cabin owner is still in farmhandData after
        // Cabin.DeleteFarmhand returned, something is wrong with the vanilla
        // removal path. Surface it loudly so it's investigable; do not silently
        // delete (masking the regression would be worse than a noisy log).
        if (ownerId != 0 && farmhandData.FieldDict.ContainsKey(ownerId))
        {
            Monitor.Log(
                $"Cabin owner {ownerId} ('{ChatRedaction.MaskValue(ownerName)}') still in farmhandData after Cabin.DeleteFarmhand; "
                    + $"not removing to preserve investigability",
                LogLevel.Warn
            );
        }

        foreach (var kvp in farmhandData.FieldDict)
        {
            var f = kvp.Value.Value;
            if (f == null)
            {
                continue;
            }

            var homeMatch = f.homeLocation.Value == deletedName;
            var currentMatch = ReferenceEquals(f.currentLocation, indoors);
            var lastSleepMatch = f.lastSleepLocation.Value == deletedName;
            if (!homeMatch && !currentMatch && !lastSleepMatch)
            {
                continue;
            }

            Monitor.Log(
                $"Clearing stale cabin refs from farmhand '{f.Name}' (dictKey={kvp.Key}, isCustomized={f.isCustomized.Value}): "
                    + $"home={homeMatch} current={currentMatch} lastSleep={lastSleepMatch}",
                LogLevel.Warn
            );
            if (homeMatch)
            {
                f.homeLocation.Value = "";
            }

            if (currentMatch)
            {
                f.currentLocation = null;
            }

            if (lastSleepMatch)
            {
                f.lastSleepLocation.Value = null;
            }
        }
    }

    /// <summary>
    /// Create a new cabin Building with interior properly initialized.
    /// Uses load() for save-deserialization-style initialization (no construction
    /// animations or sounds), then verifies the interior was actually created.
    /// </summary>
    private Building CreateCabinBuilding(Vector2 tilePosition)
    {
        var cabin = new Building("Cabin", tilePosition);
        cabin.skinId.Value = "Log Cabin";
        cabin.daysOfConstructionLeft.Value = 0;
        cabin.load();

        // Building.load() creates the interior via createIndoors(), but for new buildings
        // (where indoors.Value starts null) it can fail to assign the result depending on
        // engine version and initialization order. Verify and fall back if needed.
        if (cabin.GetIndoors() == null)
        {
            Monitor.Log(
                "Cabin interior was not created by load(), retrying via ReloadBuildingData",
                LogLevel.Warn
            );
            cabin.ReloadBuildingData();
        }

        if (cabin.GetIndoors() == null)
        {
            Monitor.Log(
                "Cabin interior creation failed. Cabin will have no interior!",
                LogLevel.Error
            );
        }

        return cabin;
    }

    #endregion

    #region Harmony: Utility.getHomeOfFarmer

    /// <summary>
    /// Defensive prefix for Utility.getHomeOfFarmer.
    /// Vanilla calls RequireLocation which throws KeyNotFoundException if the cabin
    /// interior isn't registered yet (transient state during /newgame, day transitions,
    /// or when indoors.Value is briefly null). This prefix uses null-safe lookups
    /// and SDV's own TryAssignFarmhandHome recovery, falling back to main FarmHouse
    /// as a last resort (strictly better than crashing).
    /// </summary>
    private static bool GetHomeOfFarmer_Prefix(Farmer who, ref FarmHouse __result)
    {
        if (who == null)
        {
            __result = Game1.getLocationFromName("FarmHouse") as FarmHouse;
            return false;
        }

        // Fast path: location exists and is findable
        var home = Game1.getLocationFromName(who.homeLocation.Value) as FarmHouse;
        if (home != null)
        {
            __result = home;
            return false;
        }

        // SDV's own recovery: reassign home if cabin was rebuilt/moved
        if (Game1.netWorldState?.Value != null)
        {
            Game1.netWorldState.Value.TryAssignFarmhandHome(who);
            home = Game1.getLocationFromName(who.homeLocation.Value) as FarmHouse;
            if (home != null)
            {
                _instance?.Monitor.Log(
                    $"Recovered home for '{who.Name}' via TryAssignFarmhandHome → {who.homeLocation.Value}",
                    LogLevel.Warn
                );
                __result = home;
                return false;
            }
        }

        // Last resort: return main FarmHouse instead of throwing
        _instance?.Monitor.Log(
            $"Cannot find home '{who.homeLocation.Value}' for '{who.Name}', falling back to FarmHouse",
            LogLevel.Warn
        );
        __result = Game1.getLocationFromName("FarmHouse") as FarmHouse;
        return false;
    }

    #endregion
}
