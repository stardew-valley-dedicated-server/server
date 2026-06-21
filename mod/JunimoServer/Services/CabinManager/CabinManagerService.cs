using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using JunimoServer.Services.Lobby;
using JunimoServer.Services.MessageInterceptors;
using JunimoServer.Services.PersistentOption;
using JunimoServer.Services.Roles;
using JunimoServer.Services.SaveImport;
using JunimoServer.Services.ServerOptim;
using JunimoServer.Shared;
using JunimoServer.Util;
using Microsoft.Xna.Framework;
using Netcode;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.Characters;
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

    // One-way dependency (CabinManagerService → SaveImportService). Injected solely to read+clear
    // the pending save-import finalize intent; all engine-touching finalizer logic is this service's
    // own private code (Layer B). A mutual injection would be a startup-fatal constructor cycle.
    private readonly SaveImportService saveImportService;

    private static readonly int minEmptyCabins = 1;

    private readonly HashSet<long> farmersInFarmhouse = new HashSet<long>();

    // Static reference ONLY for Harmony patches (unavoidable)
    private static CabinManagerService _instance;

    // Count of save-import finalizer SUCCESS-path runs this process. Exposed via /diagnostics/state
    // so the single-shot E2E test can assert the finalizer runs exactly once across two reloads
    // (i.e. the intent was cleared and did NOT re-fire) — a property the owner's customized+bound
    // state alone can't distinguish from a harmless re-run.
    private static int _saveImportFinalizeCount;

    /// <summary>Count of save-import finalize success-path completions since process start (test probe).</summary>
    public static int SaveImportFinalizeCount => _saveImportFinalizeCount;

    // Instance data - NOT static
    private CabinManagerData _cabinManagerData;

    public CabinManagerService(
        IModHelper helper,
        IMonitor monitor,
        Harmony harmony,
        RoleService roleService,
        MessageInterceptorsService messageInterceptorsService,
        PersistentOptions options,
        SaveImportService saveImportService
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
        this.saveImportService = saveImportService;

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
        // Save-import Layer B finalizer — MUST be the first statement, before Data.Read() and the
        // whole reconciliation chain. Ordering is load-bearing three ways: (1) the demoted owner is
        // homed+bound before the reconciliation absorbs it; (2) running before
        // ClearStaleFarmhandReferences means the owner's homeLocation is already the new cabin (not
        // the stale FarmHouse), so that sweep leaves it alone; (3) running before
        // ClearAbandonedCabinClaimsOnLoad (which only clears uncustomized farmhands) plus keeping
        // the owner isCustomized=true both protect the fresh userID stamp. No-op (zero cost) on
        // normal loads with no pending import.
        TryFinalizeOnLoad();

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
    /// Thin <c>bool</c> wrapper over <see cref="BuildNewCabinReturning(GameLocation)"/> for callers
    /// that don't need the handle (EnsureAtLeastXCabins, CabinsConsoleCommand).
    /// </summary>
    public bool BuildNewCabin(GameLocation location) => BuildNewCabinReturning(location) != null;

    /// <summary>
    /// Build a cabin at a real, visible farm position (for None strategy). Thin <c>bool</c> wrapper
    /// over <see cref="BuildNewCabinVisibleReturning(GameLocation)"/>.
    /// </summary>
    public bool BuildNewCabinVisible(GameLocation location) =>
        BuildNewCabinVisibleReturning(location) != null;

    /// <summary>
    /// Build a hidden-stack cabin and return its <see cref="Cabin"/> interior handle (null on
    /// failure). The save-import finalizer needs the handle — under CabinStack all hidden cabins
    /// share tile (-20,-20), so position can't disambiguate; the returned handle is the only reliable
    /// reference. Logs build failures at <c>Warn</c> (NOT Error — Error is server-side test poison).
    /// </summary>
    public Cabin BuildNewCabinReturning(GameLocation location)
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
                    LogLevel.Warn
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
                return null;
            }

            return indoors;
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
        return null;
    }

    /// <summary>
    /// Build a visible-position cabin (None strategy) and return its <see cref="Cabin"/> interior
    /// handle (null on failure). See <see cref="BuildNewCabinReturning"/> for why the handle matters.
    /// Logs build failures at <c>Warn</c> (NOT Error — Error is server-side test poison).
    /// </summary>
    public Cabin BuildNewCabinVisibleReturning(GameLocation location)
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
            return null;
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
                    LogLevel.Warn
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
                return null;
            }

            Monitor.Log(
                $"Built visible cabin at ({position.Value.X}, {position.Value.Y})",
                LogLevel.Info
            );
            return indoors;
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
        return null;
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
            // Warn, not Error: this runs on the server game thread (incl. the save-import finalize
            // build path), where LogLevel.Error trips ServerContainer's ERROR/FATAL test-poison scan.
            // A null interior is surfaced as a failed build by the callers (cabin_build_failed event +
            // their own Warn) — this is a recoverable condition, not a test-failure-worthy one.
            Monitor.Log(
                "Cabin interior creation failed. Cabin will have no interior!",
                LogLevel.Warn
            );
        }

        return cabin;
    }

    #endregion

    #region Save Import Finalizer (Layer B)

    /// <summary>
    /// One-shot save-import finalizer. Reads the pending finalize intent (written by
    /// <see cref="SaveImportService.ExecuteImport"/> during a swap import); if present and for this
    /// save, demotes the imported owner into a known cabin, moves their farmhouse contents and
    /// household NPCs into it, and re-stamps their platform userID. Self-heals (Warn + clear +
    /// return) on any pre-condition miss, and clears the intent on EVERY exit (including a throw
    /// after a world mutation) so a failed finalize never retries against an already-changed world.
    /// </summary>
    private void TryFinalizeOnLoad()
    {
        var intent = saveImportService.TryReadIntent();
        if (intent == null)
        {
            return; // zero cost on normal loads
        }

        // Wrong-save guard: a stale intent (or an unrelated loader write between import and reboot)
        // must not mis-finalize a different save.
        if (!string.Equals(Constants.SaveFolderName, intent.SaveName, StringComparison.Ordinal))
        {
            Monitor.Log(
                $"Save-import intent targets '{intent.SaveName}' but loaded '{Constants.SaveFolderName}'; "
                    + "clearing the orphan intent.",
                LogLevel.Warn
            );
            saveImportService.ClearIntent();
            return;
        }

        var farmhandData = Game1.netWorldState?.Value?.farmhandData;
        if (farmhandData == null)
        {
            // Should be impossible at SaveLoaded (the world is fully loaded), but never NRE an
            // unrelated load — clear the intent and bail.
            Monitor.Log(
                "Save-import: netWorldState/farmhandData unavailable at finalize; clearing intent.",
                LogLevel.Warn
            );
            saveImportService.ClearIntent();
            return;
        }
        if (!farmhandData.TryGetValue(intent.OwnerUid, out var owner) || owner == null)
        {
            Monitor.Log(
                $"Save-import: demoted owner {intent.OwnerUid} not found in farmhandData; "
                    + "clearing intent (nothing to finalize).",
                LogLevel.Warn
            );
            saveImportService.ClearIntent();
            return;
        }

        int contentsMoved = 0;
        int npcsMoved = 0;
        string failedStep = "";

        try
        {
            // Step 4 — resolve the owner's cabin handle (reuse an auto-assigned cabin if the load
            // coroutine already homed the owner into a spare one, else build a fresh cabin).
            failedStep = "resolve_cabin";
            var cabin = ResolveOrBuildOwnerCabin(owner, out var builtFresh);
            if (cabin == null)
            {
                // Loud-fail at Warn (not Error). The owner stays a customized-but-cabin-less
                // farmhand (progress intact, recoverable by a later reassignment); never proceed to
                // the world-mutating steps.
                Monitor.Log(
                    $"Save-import: could not resolve or build a cabin for owner {intent.OwnerUid}; "
                        + "finalize aborted (owner kept, progress intact).",
                    LogLevel.Warn
                );
                Diagnostics.ModEventLog.Emit(
                    "save_import_partial",
                    new { ownerUid = intent.OwnerUid, failedStep }
                );
                return; // intent cleared in finally
            }

            // Realize a freshly-built cabin's interior map to the owner's upgrade level before the
            // contents move, so the move targets the realized layout (belt-and-suspenders; the
            // day-start updateFarmLayout would otherwise heal it). Furniture-preserving method, NOT
            // the bare HouseUpgradeLevel setter (host-automation invariant 5 is a different path).
            if (builtFresh && owner.HouseUpgradeLevel > 0)
            {
                cabin.setMapForUpgradeLevel(owner.HouseUpgradeLevel);
            }

            var farmHouse = Game1.getLocationFromName("FarmHouse") as FarmHouse;

            // Step 5 — move the owner's farmhouse contents into the cabin.
            failedStep = "move_contents";
            contentsMoved = TransferFarmhouseContentsToCabin(farmHouse, cabin, builtFresh);

            // Step 6 — relocate the owner's household NPCs (pet, spouse, children) into the cabin.
            failedStep = "relocate_household";
            npcsMoved = RelocateHouseholdToCabin(farmHouse, cabin, owner);

            // Step 7 — move the owner's cellar contents (casks/wine, built in the master-keyed
            // "Cellar"-1 while they were the master) into the cellar the engine reassigns to them as
            // a farmhand. Counts toward contentsMoved.
            failedStep = "move_cellar";
            contentsMoved += TransferOwnerCellarContents(owner, cabin);

            // Step 8 — re-stamp userID (idempotent whether vanilla cleared or preserved it). Owner
            // is now homed + customized + bound; on any later reload it is homed → ResetFarmhandState
            // early-returns → userID survives.
            failedStep = "restamp_userid";
            owner.userID.Value = intent.UserId;

            // Success — count it so the single-shot test can prove the finalizer ran exactly once
            // across reloads (a re-fire would bump this past 1).
            System.Threading.Interlocked.Increment(ref _saveImportFinalizeCount);

            Diagnostics.ModEventLog.Emit(
                "save_import_finalized",
                new
                {
                    ownerUid = intent.OwnerUid,
                    hasUserId = !string.IsNullOrEmpty(intent.UserId),
                    contentsMoved,
                    npcsMoved,
                }
            );
            Monitor.Log(
                $"Save-import finalized: owner {intent.OwnerUid} homed + bound; "
                    + $"moved {contentsMoved} item(s) and {npcsMoved} NPC(s) into the cabin.",
                LogLevel.Info
            );
        }
        catch (Exception ex)
        {
            // A throw after a world mutation leaves a partially-moved-but-stable world. The owner is
            // already customized + cabin-homed (progress intact), so a missing content/NPC move is a
            // recoverable cosmetic gap, not a boot-loop. Warn (never Error) + emit partial.
            Monitor.Log(
                $"Save-import finalize failed at step '{failedStep}': {ex.Message}. World is partially "
                    + "moved but stable; owner kept. Intent cleared (no retry).",
                LogLevel.Warn
            );
            Diagnostics.ModEventLog.Emit(
                "save_import_partial",
                new
                {
                    ownerUid = intent.OwnerUid,
                    failedStep,
                    contentsMoved,
                    npcsMoved,
                }
            );
        }
        finally
        {
            // Single-shot: clear on EVERY exit path, including a post-mutation throw.
            saveImportService.ClearIntent();
        }
    }

    /// <summary>
    /// Resolves the demoted owner's cabin. If the load coroutine already auto-homed the owner into a
    /// spare cabin (the common co-op-with-spare-cabins case), reuses that cabin. Otherwise builds a
    /// fresh one and assigns the owner. Returns null only on a build failure.
    /// </summary>
    private Cabin ResolveOrBuildOwnerCabin(Farmer owner, out bool builtFresh)
    {
        builtFresh = false;

        // Reuse path: the owner was already auto-homed into a spare cabin by the load coroutine's
        // ResetFarmhandState → TryAssignFarmhandHome. Reusing it avoids a double-assignment and a
        // vacated spare cabin. getLocationFromName resolves a cabin interior (visible or hidden-stack)
        // by its NameOrUniqueName — the same expression vanilla itself uses at NetWorldState.cs:783.
        if (
            Game1.getLocationFromName(owner.homeLocation.Value) is Cabin existing
            && existing.OwnerId == owner.UniqueMultiplayerID
        )
        {
            Monitor.Log(
                $"Save-import: reusing auto-assigned cabin '{existing.NameOrUniqueName}' for owner "
                    + $"{owner.UniqueMultiplayerID}.",
                LogLevel.Info
            );
            return existing;
        }

        // Build path: no cabin was auto-assigned (single-player import, or all cabins customized).
        var farm = Game1.getFarm();
        var cabin = options.IsNone
            ? BuildNewCabinVisibleReturning(farm)
            : BuildNewCabinReturning(farm);
        if (cabin == null)
        {
            return null;
        }

        builtFresh = true;
        // AssignFarmhand auto-deletes the just-built cabin's unclaimed placeholder owner (created by
        // buildStructure → Cabin.CreateFarmhand) then sets farmhandReference + the owner's
        // homeLocation in one call (Cabin.cs:92-104). No throw — the placeholder is isUnclaimedFarmhand.
        cabin.AssignFarmhand(owner);
        Monitor.Log(
            $"Save-import: built and assigned cabin '{cabin.NameOrUniqueName}' for owner "
                + $"{owner.UniqueMultiplayerID}.",
            LogLevel.Info
        );
        return cabin;
    }

    /// <summary>
    /// Moves the former owner's placed farmhouse contents (chests + contents, machines + held items,
    /// furniture, fridge, mini-jukebox, wallpaper/flooring) from the FarmHouse into their cabin, then
    /// clears the FarmHouse copies so the Server host boots into an empty house. The engine has no
    /// built-in farmhouse→cabin transfer, so this is hand-written from the source-derived content
    /// list. Returns the count of moved objects + furniture (for the finalize event).
    /// </summary>
    private int TransferFarmhouseContentsToCabin(FarmHouse farmHouse, Cabin cabin, bool builtFresh)
    {
        if (farmHouse == null || cabin == null)
        {
            return 0;
        }

        // Clear the destination's default starter contents ONLY when we built the cabin fresh: a new
        // Cabin runs AddStarterGiftBox + AddStarterFurniture in its ctor, which must go before the
        // merge or the owner's cabin ends up with a phantom giftbox / tile overlap. A REUSED cabin is
        // the engine's auto-assigned spare, which can be an uncustomized-but-furnished slot (Cabin.
        // DeleteFarmhand never clears the interior) — clearing it would delete that real player data,
        // so skip the clear and merge the master's farmhouse contents on top of what's there.
        if (builtFresh)
        {
            ClearStarterContents(cabin);
        }

        int moved = 0;

        // objects (placed chests + their contents, machines + held items, mini-fridges). Snapshot
        // the source positions first; mutating netObjects while enumerating it would tear the
        // enumeration.
        var sourceObjects = farmHouse.objects.Pairs.ToList();
        foreach (var kvp in sourceObjects)
        {
            var pos = kvp.Key;
            var obj = kvp.Value;
            farmHouse.objects.Remove(pos);
            // Place at the same tile; the cabin interior is the same map (Cabin : FarmHouse), so
            // the tile is valid (the destination's starter objects were just cleared above).
            cabin.objects[pos] = obj;
            moved++;
        }

        // mini-jukebox count/track (separate NetFields a raw objects move misses): a MiniJukebox
        // object rides along in objects above, but its count/track don't, so the FarmHouse would
        // strand a track (count>0, no object) and the cabin would show count=0 without this.
        if (farmHouse.miniJukeboxCount.Value > 0)
        {
            cabin.miniJukeboxCount.Set(farmHouse.miniJukeboxCount.Value);
            cabin.miniJukeboxTrack.Set(farmHouse.miniJukeboxTrack.Value);
            farmHouse.miniJukeboxCount.Set(0);
            farmHouse.miniJukeboxTrack.Set("");
        }

        // furniture (incl. beds and 2-tile furniture).
        var sourceFurniture = farmHouse.furniture.ToList();
        foreach (var f in sourceFurniture)
        {
            farmHouse.furniture.Remove(f);
            cabin.furniture.Add(f);
            moved++;
        }

        // fridge contents: a default cabin fridge is empty, so move the source fridge's items into
        // the destination fridge (keeps the destination's NetRef<Chest> identity intact).
        if (farmHouse.fridge.Value != null && cabin.fridge.Value != null)
        {
            var fridgeItems = farmHouse.fridge.Value.Items.ToList();
            foreach (var item in fridgeItems)
            {
                if (item != null)
                {
                    cabin.fridge.Value.Items.Add(item);
                }
            }
            farmHouse.fridge.Value.Items.Clear();
        }

        // Wallpaper / flooring (live decor stores). Copy both dictionaries then clear the FarmHouse
        // ones. Also carry the obsolete pre-1.6 DecorationFacades if present (cheap; avoids dropping
        // legacy decor).
        CopyAndClearDecor(farmHouse, cabin);

        // terrainFeatures / largeTerrainFeatures are normally empty for a farmhouse interior (interior
        // floor/wall decor lives in appliedFloor/appliedWallpaper, not terrainFeatures). Move anything
        // content-bearing if present (verified-once: do not assume empty).
        var srcTerrain = farmHouse.terrainFeatures.Pairs.ToList();
        foreach (var kvp in srcTerrain)
        {
            farmHouse.terrainFeatures.Remove(kvp.Key);
            cabin.terrainFeatures[kvp.Key] = kvp.Value;
            moved++;
        }
        var srcLargeTerrain = farmHouse.largeTerrainFeatures.ToList();
        foreach (var ltf in srcLargeTerrain)
        {
            farmHouse.largeTerrainFeatures.Remove(ltf);
            cabin.largeTerrainFeatures.Add(ltf);
            moved++;
        }

        return moved;
    }

    /// <summary>
    /// Removes a destination cabin's default starter giftbox + starter furniture before the contents
    /// merge (risk #11). A default cabin fridge is empty, so it needs no special handling here.
    /// </summary>
    private static void ClearStarterContents(Cabin cabin)
    {
        // Starter giftbox is a Chest with giftbox flag in objects; clear ALL starter objects (a fresh
        // cabin's objects are only the starter giftbox).
        foreach (var pos in cabin.objects.Keys.ToList())
        {
            cabin.objects.Remove(pos);
        }
        // Starter furniture.
        cabin.furniture.Clear();
    }

    /// <summary>Copies wallpaper/floor decor dictionaries FarmHouse→cabin and clears the source.</summary>
    private static void CopyAndClearDecor(FarmHouse farmHouse, Cabin cabin)
    {
        foreach (var key in farmHouse.appliedWallpaper.Keys.ToList())
        {
            cabin.appliedWallpaper[key] = farmHouse.appliedWallpaper[key];
        }
        foreach (var key in farmHouse.appliedFloor.Keys.ToList())
        {
            cabin.appliedFloor[key] = farmHouse.appliedFloor[key];
        }
        foreach (var key in farmHouse.appliedWallpaper.Keys.ToList())
        {
            farmHouse.appliedWallpaper.Remove(key);
        }
        foreach (var key in farmHouse.appliedFloor.Keys.ToList())
        {
            farmHouse.appliedFloor.Remove(key);
        }
    }

    /// <summary>
    /// Relocates the owner's household NPCs (pet, spouse, children) from the FarmHouse into their
    /// cabin. The owner's homeLocation is already the cabin (step 4), so each NPC's home resolves
    /// off that field automatically — this physically moves the NPC object so the day-zero state is
    /// correct. Returns the count of NPCs moved.
    /// </summary>
    private int RelocateHouseholdToCabin(FarmHouse farmHouse, Cabin cabin, Farmer owner)
    {
        if (farmHouse == null || cabin == null)
        {
            return 0;
        }

        int moved = 0;
        var bedSpot = Utility.PointToVector2(cabin.GetPlayerBedSpot());

        // Children: filter by the FarmHouse's characters list (Child resolution is by which house
        // the owner's homeLocation points to, NOT idOfParent). Move every Child out of the old
        // FarmHouse into the cabin. Move AFTER the owner is homed at the cabin (done in step 4) so
        // the master doesn't re-stamp idOfParent to the wrong farmer.
        foreach (var child in farmHouse.characters.OfType<Child>().ToList())
        {
            Game1.warpCharacter(child, cabin, bedSpot);
            moved++;
        }

        // Pet: move the pet into the cabin for day-zero. Resolve it with a FULL-world scan
        // (FindFarmPet), NOT Farmer.getPet(): getPet() scans only Game1.getFarm().characters then each
        // farmer's resolved home (Farmer.cs:getPet), but the finalizer runs at SaveLoaded — BEFORE the
        // reconciliation chain and before the day-start dayUpdate that warps the pet to its Farm bowl —
        // so the pet may be deserialized in an interior (a FarmHouse/Cabin) that neither loop covers
        // yet, and getPet() returns null intermittently (the npcsMoved:0 flake). A pet always exists
        // somewhere in Game1.locations, so scanning all locations finds it deterministically. Do NOT
        // repoint Pet.homeLocationName — the bowl is a Farm building resolved off it; warp-home follows
        // the owner anyway, and the pet returns to its bowl on the next dayUpdate.
        var pet = FindFarmPet();
        if (pet != null)
        {
            Game1.warpCharacter(pet, cabin, bedSpot);
            moved++;
        }

        // Spouse NPC: an NPC spouse relocates itself via marriageDuties on the first day, but move it
        // physically as belt-and-suspenders for day zero. Resolve by the owner's <spouse> name.
        if (!string.IsNullOrEmpty(owner.spouse))
        {
            var spouseNpc = farmHouse.characters.FirstOrDefault(c =>
                string.Equals(c.Name, owner.spouse, StringComparison.Ordinal)
            );
            if (spouseNpc != null)
            {
                Game1.warpCharacter(spouseNpc, cabin, bedSpot);
                moved++;
            }
        }

        return moved;
    }

    /// <summary>
    /// Finds the farm's pet by scanning every location (interiors included), returning the first
    /// <see cref="Pet"/> found, or null if the farm has none. Used instead of
    /// <see cref="Farmer.getPet"/> in the save-import finalizer: getPet() scans only the Farm and each
    /// farmer's resolved home, which misses a pet deserialized into an interior at SaveLoaded time
    /// (before the day-start dayUpdate warps it to its bowl). The pet is farm-scoped (one per save —
    /// Pet has no per-farmer owner field, only a bowl assignment), so the demoted owner's pet is the
    /// farm's pet.
    /// </summary>
    private static Pet FindFarmPet()
    {
        Pet found = null;
        Utility.ForEachLocation(location =>
        {
            foreach (var npc in location.characters)
            {
                if (npc is Pet pet)
                {
                    found = pet;
                    return false; // stop the scan
                }
            }
            return true; // keep scanning
        });
        return found;
    }

    /// <summary>
    /// Moves the demoted owner's cellar contents into the cellar the engine reassigns to them. The
    /// owner built their casks/wine inside "Cellar"-1 while they were the master, but
    /// updateCellarAssignments (Game1.cs:4515) hardwires "Cellar"-1 to the master (now the Server
    /// bot) and hands the owner one of the per-slot cellars ("Cellar2".."CellarN", pre-created up to
    /// HighestPlayerLimit at Game1.cs:7417-7422). Cellar contents are location-bound, so they don't
    /// follow the assignment — this transfers them, mirroring the farmhouse→cabin content move.
    /// Returns the count of moved objects. Falls back to a Warn (and leaves the contents in "Cellar"-1)
    /// only if no destination cellar can be resolved (no free slot under HighestPlayerLimit) — a
    /// graceful degrade, never a throw.
    /// </summary>
    private int TransferOwnerCellarContents(Farmer owner, Cabin cabin)
    {
        // Source: the master's "Cellar"-1, where the owner's casks physically live.
        var sourceCellar = Game1.getLocationFromName("Cellar");
        var sourceObjectCount = sourceCellar?.objects?.Count() ?? 0;
        if (sourceCellar == null || sourceObjectCount == 0)
        {
            return 0; // nothing built; no-op (don't cry wolf on a farm that never stocked a cellar)
        }

        // Ensure assignments are current, then resolve the owner's reassigned cellar by the engine's
        // own path (Cabin inherits GetCellarName(), which maps cellarAssignments → "Cellar"+N off the
        // owner's UID). updateCellarAssignments is idempotent (the engine calls it at load/day-start/
        // join) and assigns "Cellar"-1 to the master + the next free slot to each other farmer; the
        // owner is in farmhandData (getAllFarmers), so a free slot lands on them.
        Game1.updateCellarAssignments();
        var destCellarName = cabin.GetCellarName();
        var destCellar =
            destCellarName == null ? null : Game1.getLocationFromName(destCellarName) as Cellar;
        var maskedOwnerName = ChatRedaction.MaskValue(owner.Name);

        if (destCellar == null || ReferenceEquals(destCellar, sourceCellar))
        {
            // No free per-slot cellar (farmer count exceeds HighestPlayerLimit), or the owner somehow
            // still resolves to "Cellar"-1. Leave the contents where they are and warn — recoverable,
            // not a finalize failure.
            Monitor.Log(
                $"Save-import: former owner '{maskedOwnerName}' has {sourceObjectCount} cellar item(s) but "
                    + "no separate cellar could be assigned (player limit reached); they remain in the "
                    + "main farm cellar (now the Server host's).",
                LogLevel.Warn
            );
            return 0;
        }

        // Clearing the destination can't destroy another farmer's data: updateCellarAssignments only
        // ever hands the owner a slot whose prior holder no longer resolves (a per-slot cellar,
        // pre-created empty — cellars have no starter contents) or the owner's own already-held slot;
        // it never reassigns a still-held slot, and the ReferenceEquals guard above rules out the
        // master's "Cellar"-1. Cellar interiors share the same map, so source tiles are valid here.
        foreach (var pos in destCellar.objects.Keys.ToList())
        {
            destCellar.objects.Remove(pos);
        }

        int moved = 0;
        foreach (var kvp in sourceCellar.objects.Pairs.ToList())
        {
            sourceCellar.objects.Remove(kvp.Key);
            destCellar.objects[kvp.Key] = kvp.Value;
            moved++;
        }

        Monitor.Log(
            $"Save-import: moved {moved} cellar item(s) from the main farm cellar into former owner "
                + $"'{maskedOwnerName}'s cellar ('{destCellarName}').",
            LogLevel.Info
        );
        return moved;
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
