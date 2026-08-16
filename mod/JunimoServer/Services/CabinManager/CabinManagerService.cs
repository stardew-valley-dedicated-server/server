using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using JunimoServer.Services.Auth;
using JunimoServer.Services.Lobby;
using JunimoServer.Services.MessageInterceptors;
using JunimoServer.Services.PersistentOption;
using JunimoServer.Services.Roles;
using JunimoServer.Services.SaveImport;
using JunimoServer.Services.ServerOptim;
using JunimoServer.Services.Settings;
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

public partial class CabinManagerService : ModService
{
    public CabinManagerData Data
    {
        get => _cabinManagerData;
        set { _cabinManagerData = value; }
    }

    public static readonly Point HiddenCabinLocation = CabinPositions.PlayerStack;

    public readonly PersistentOptions options;

    private readonly RoleService roleService;

    // Needed by migration commit: SyncFromSettings reapplies the settings file on every
    // boot/reload, so committing a strategy flip must also write it into
    // server-settings.json or the next reload would flip it back.
    private readonly ServerSettingsLoader settings;

    // One-way dependency (CabinManagerService → SaveImportService). Injected solely to read+clear
    // the pending save-import finalize intent; all engine-touching finalizer logic is this service's
    // own private code (Layer B). A mutual injection would be a startup-fatal constructor cycle.
    private readonly SaveImportService saveImportService;

    // Ownership map lifecycle: every site that clears a stamp or deletes a farmhand entry
    // must drop the matching owner record too, or the slot stays locked to a ghost.
    private readonly FarmhandOwnershipService farmhandOwnership;

    private static readonly int minEmptyCabins = 1;

    private readonly HashSet<long> farmersInFarmhouse = new HashSet<long>();

    // Tiles of visible (non-hidden, non-lobby) cabins removed by DestroyCabin. The
    // delete→rebuild path (DELETE /farmhands → EnsureAtLeastXCabins) re-places onto the
    // just-vacated tile: a deleted cabin may have been !cabin-moved off its designated
    // spot, and rebuilding at the designated position instead would bulldoze whatever
    // players developed there since. Strictly world-scoped: cleared on ReturnedToTitle
    // (every /newgame, /reload, and import passes through the title, and a new game builds
    // its cabins before its SaveLoaded even fires) — a tile carried across worlds would
    // divert the next game's up-front None placement onto a stale position. In-memory only;
    // after a restart the rebuild falls back to designated positions.
    private readonly Queue<Point> _freedVisibleCabinTiles = new Queue<Point>();

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
        ServerSettingsLoader settings,
        SaveImportService saveImportService,
        FarmhandOwnershipService farmhandOwnership
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
        this.settings = settings;
        this.saveImportService = saveImportService;
        this.farmhandOwnership = farmhandOwnership;

        Data = new CabinManagerData(helper, monitor);

        Helper.Events.GameLoop.SaveLoaded += OnSaveLoaded;
        Helper.Events.GameLoop.DayStarted += OnDayStarted;
        Helper.Events.GameLoop.ReturnedToTitle += OnReturnedToTitle;

        // Registered unconditionally, gated per-message/per-tick on options.IsNone: the
        // strategy can change in-process (settings-file /reload, migration commit), so
        // constructor-time gating on the BOOT strategy would leave a None-booted server
        // without interceptors after a switch to a stacked strategy.

        // Disable default starting cabin logic, we handle it — for every strategy: the mod
        // places None cabins itself (vanilla's placement doesn't survive the headless
        // new-game path; cabin-system invariant 9).
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

        // Hijack outgoing location introductions for cabin warp manipulation. Location
        // DELTAS are deliberately not intercepted: server-side warp rewrites (Relocate,
        // !cabin reset, the migration commit pass) mutate the global interior's warps via
        // SetWarpsToFarm, which always emits a replication delta. The delta is load-bearing
        // even right after an introduction: the client re-derives interior warps locally
        // from the building position while deserializing it (buildings.OnValueAdded →
        // updateInteriorWarps), clobbering the introduction's own targets.
        messageInterceptorsService.Add(
            Multiplayer.locationIntroduction,
            OnLocationIntroductionMessage
        );

        // Monitor farmhouse access; only the server host can enter (no human players)
        Helper.Events.GameLoop.UpdateTicked += OnTicked;

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

    private void OnReturnedToTitle(object sender, ReturnedToTitleEventArgs e)
    {
        // World teardown: freed-cabin tiles belong to the world that freed them (see the
        // field comment).
        _freedVisibleCabinTiles.Clear();
    }

    private void OnSaveLoaded(object sender, SaveLoadedEventArgs e)
    {
        // Restore the cabin-layout choice before anything resolves designated positions.
        // Game1.cabinsSeparate is not save-persisted (the engine resets it to false), and
        // FarmCabinPositions mirrors vanilla's layout selection off it; the settings file
        // is the durable source of the choice. Runs before the save-import finalizer,
        // which can build a visible cabin under None.
        Game1.cabinsSeparate = !settings.CabinLayoutNearby;

        // Save-import Layer B finalizer — MUST run before Data.Read() and the whole
        // reconciliation chain (only the side-effect-free layout restore above precedes
        // it). Ordering is load-bearing two ways: (1) the demoted owner is
        // homed+bound before the reconciliation absorbs it; (2) running before
        // ClearStaleFarmhandReferences means the owner's homeLocation is already the new cabin (not
        // the stale FarmHouse), so that sweep leaves it alone. (The abandoned-claim sweep can't
        // touch the bind either way: the owner is isCustomized=true and the record is
        // operator-origin.) No-op (zero cost) on normal loads with no pending import.
        TryFinalizeOnLoad();

        Data.Read();

        // Detect and handle strategy changes between runs
        DetectAndApplyStrategySwitch();

        // A save import defers its None-cap reset to the imported save's FIRST load (see
        // PendingNoneCapClearSaveName); consume or drop the marker before the freeze below.
        var pendingCapClear = options.Data.PendingNoneCapClearSaveName;
        if (!string.IsNullOrEmpty(pendingCapClear))
        {
            if (string.Equals(pendingCapClear, Constants.SaveFolderName, StringComparison.Ordinal))
            {
                options.Data.NoneCabinCount = 0;
                Monitor.Log(
                    "Cleared the frozen None cabin cap for the imported save; a fresh cap is "
                        + "computed on this load.",
                    LogLevel.Debug
                );
            }
            // A non-matching marker belongs to an import that never booted (canceled or
            // retargeted); this save keeps its own cap.
            options.Data.PendingNoneCapClearSaveName = null;
            options.Save();
        }

        // Terminal-state guard: None must never run with cabins still in the hidden stack.
        // Runs before the freeze so the cap sees the reconciled visible count.
        ReconcileHiddenCabinsUnderNone();

        // Freeze the None cap for saves where NoneCabinCount is unset (0): persist
        // min(designated positions, MaxPlayers) so a later MaxPlayers raise can't grow the
        // ceiling onto a developed farm — floored at the visible cabin count, because a
        // save can legitimately hold more cabins than the formula (pre-freeze saves built
        // against both marker layouts; imports): a cap below the real count would make
        // every farmhand deletion permanently shrink capacity (delete→rebuild sees zero
        // headroom). Imported farms arrive with the field cleared (marker above), so they
        // compute a fresh cap here instead of inheriting the previous game's frozen
        // number. Premise gap for such farms: they never got the placed-up-front
        // treatment, so on-demand growth (buildStructure(skipSafetyChecks)
        // + ClearTerrainBelow, i.e. bulldozing) still fires on them up to this cap — the
        // no-bulldoze guarantee is absolute only for farms created under None.
        if (options.IsNone && options.Data.NoneCabinCount == 0)
        {
            var noneFarm = Game1.getFarm();
            if (noneFarm != null)
            {
                var visibleCount = noneFarm.buildings.Count(b =>
                    b.isCabin && !b.IsInHiddenStack() && !b.IsLobbyOrEditing()
                );
                var frozen = Math.Max(
                    Math.Min(
                        FarmCabinPositions.GetDesignatedPositions(noneFarm).Count,
                        options.Data.MaxPlayers
                    ),
                    visibleCount
                );
                if (frozen > 0)
                {
                    options.Data.NoneCabinCount = frozen;
                    options.Save();
                    Monitor.Log(
                        $"Froze None cabin cap at {frozen} "
                            + "(min of designated map positions and MaxPlayers, floored at "
                            + $"the {visibleCount} existing visible cabin(s)).",
                        LogLevel.Info
                    );
                }
            }
        }

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

        // Heal farmhands whose durable home/spawn fields point at a lobby interior — the
        // one-shot migration for saves poisoned by the old lobby redirect (which wrote the
        // lobby into homeLocation; the client's copy echoed it back nightly). Runs before
        // EnsureAtLeastXCabins so a heal that consumes a cabin gets the pool topped back up.
        HealLobbyHomedFarmhands(HealContextSaveLoaded);

        EnsureAtLeastXCabins();

        // NPC pass after the farmhand pass: married NPCs re-derive their home from the (now
        // healed) spouse homeLocation.
        HealLobbyHomedResidents(HealContextSaveLoaded);
    }

    private void OnDayStarted(object sender, DayStartedEventArgs e)
    {
        // Tripwire sweep (see the heal-context tags in the Lobby-Homed Heal region). Ordering
        // vs. marriageDuties is deliberately not load-bearing: if marriageDuties already ran
        // with a poisoned value this morning, the NPC pass pulls the spouse back and the
        // farmhand pass fixes the source for tomorrow; if the sweep ran first, marriageDuties
        // reads healed values and the NPC pass no-ops.
        HealLobbyHomedFarmhands(HealContextDayStarted);
        HealLobbyHomedResidents(HealContextDayStarted);
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
        farmhandOwnership.RemoveOwners(toRemove);

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
        // Under None cabins are real and visible; farmhouse access isn't managed.
        if (options.IsNone)
        {
            return;
        }

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

    #region Strategy Switch (settings file)

    // The settings-file strategy-switch path ("edit cabinStrategy + reload"). Distinct from
    // the staged, admin-driven migration flow in the next region, which enforces different
    // rules — only the diagnostic event names (cabin_strategy_migration*) are shared.

    private void DetectAndApplyStrategySwitch()
    {
        var previousStrategy = options.PreviousCabinStrategy;
        var currentStrategy = options.Data.CabinStrategy;

        // Post-commit crash heal: the strategy flip (global options + settings file) is
        // disk-durable at commit, but the world changes (staged positions, record clear,
        // warp pass) live in the save and persist only at the next game save. A crash in
        // that window reloads a pre-commit world — active record, cabins still staged or
        // hidden — under the already-flipped strategy. Revert the strategy to the record's
        // FromStrategy (which the loaded world matches) and leave the record active, so the
        // admin resumes staging and re-commits.
        // (previous == current distinguishes this from a settings-file edit to the target
        // strategy during staging, which the in-progress refusal below handles instead.)
        if (
            Data.ActiveMigration != null
            && currentStrategy == Data.ActiveMigration.ToStrategy
            && previousStrategy == currentStrategy
        )
        {
            var record = Data.ActiveMigration;
            Monitor.Log(
                $"Loaded a pre-commit world for a committed migration {record.FromStrategy} → "
                    + $"{record.ToStrategy} (the commit's world changes were not yet saved). "
                    + $"Reverting strategy to {record.FromStrategy}; the staging is still active — "
                    + "re-run 'cabins migrate commit' when ready.",
                LogLevel.Warn
            );
            if (record.ToStrategy == CabinStrategy.None)
            {
                // The commit also froze the None cap; undo that with the strategy, or a much
                // later legitimate switch to None would inherit this staging's stale count
                // (the load-time freeze only computes at 0). A re-commit re-freezes it.
                options.Data.NoneCabinCount = 0;
            }
            ApplyStrategyDurably(record.FromStrategy);
            return;
        }

        if (previousStrategy == currentStrategy)
        {
            return;
        }

        // A staged migration owns the strategy for its whole staging window: a file-driven
        // change would flip the strategy underneath the record, so refuse it and revert to
        // the strategy the record started from. The admin finishes or aborts via
        // 'cabins migrate'. Revert-only (the settings file is left untouched), so every
        // reload re-warns and re-reverts until the operator edits the file back.
        if (Data.ActiveMigration != null)
        {
            var active = Data.ActiveMigration;
            Monitor.Log(
                $"CabinStrategy change {previousStrategy} → {currentStrategy} via settings file "
                    + $"refused: a staged migration {active.FromStrategy} → {active.ToStrategy} is in "
                    + $"progress. Reverting to {active.FromStrategy}; use 'cabins migrate commit' or "
                    + "'cabins migrate abort' first. This warning repeats on every reload until "
                    + $"server-settings.json is edited back to {active.FromStrategy}.",
                LogLevel.Warn
            );
            options.Data.CabinStrategy = active.FromStrategy;
            options.Save();
            return;
        }

        Monitor.Log(
            $"CabinStrategy changed from {previousStrategy} to {currentStrategy}, applying switch...",
            LogLevel.Warn
        );
        ApplyStrategySwitch(previousStrategy, currentStrategy);
    }

    /// <summary>
    /// Durably applies a strategy flip: persisted options AND server-settings.json.
    /// SyncFromSettings reapplies the file on every boot/reload, so both writes must land
    /// together or the next reload reverts the flip. The refusal sites deliberately do NOT
    /// use this — they revert the persisted options only and leave the file to the operator.
    /// </summary>
    private void ApplyStrategyDurably(CabinStrategy strategy)
    {
        options.Data.CabinStrategy = strategy;
        options.Save();
        settings.SetCabinStrategy(strategy);
    }

    private void ApplyStrategySwitch(CabinStrategy from, CabinStrategy to)
    {
        var farm = Game1.getFarm();
        int migrated = 0;

        if (RequiresStagedMigration(from, to))
        {
            // A materializing direction never happens via the settings file: switching to
            // None physically places cabins at designated map positions via
            // buildStructure(skipSafetyChecks) + ClearTerrainBelow (bulldozing whatever a
            // player built there), and FarmhouseStack → CabinStack makes the shared stack
            // ghost appear on a farm spot players may have developed. Both are rejected on a
            // developed save and the persisted strategy reverted; the supported paths are a
            // fresh game or the staged 'cabins migrate' flow (validated, never-destructive,
            // flip-at-commit).
            //
            // Gated on hiddenCabins.Count — nothing hidden means nothing can materialize, so
            // the switch is safe. Fresh games never reach this branch at all:
            // SetPersistentOptions aligns PreviousCabinStrategy at game creation (a fresh
            // stacked game parks a hidden cabin before its first SaveLoaded, so without the
            // alignment a stale previous strategy would false-trip this rejection and
            // silently revert the fresh game's strategy).
            var hiddenCabins = farm.buildings.Where(b => b.isCabin && b.IsInHiddenStack()).ToList();

            if (hiddenCabins.Count > 0)
            {
                var consequence =
                    to == CabinStrategy.None
                        ? "would place real cabins on the farm and bulldoze anything on the "
                            + "designated spots"
                        : "would make the shared stack cabin appear on a farm spot players may "
                            + "have developed";
                Monitor.Log(
                    $"CabinStrategy switch {from} → {to} rejected: switching an existing save "
                        + $"via the settings file {consequence}. Reverting strategy to {from}; "
                        + $"use the staged 'cabins migrate start {to}' console flow (or create a "
                        + $"new game) to switch to {to}. This warning repeats on every reload "
                        + $"until server-settings.json is edited back to {from}.",
                    LogLevel.Warn
                );

                Diagnostics.ModEventLog.Emit(
                    "cabin_strategy_migration_aborted",
                    new
                    {
                        fromStrategy = from.ToString(),
                        toStrategy = to.ToString(),
                        hiddenCabinCount = hiddenCabins.Count,
                        reason = "requires_staged_migration",
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
        }
        else if (!UsesHiddenStack(from) && UsesHiddenStack(to))
        {
            // None → Stacked: move sweepable visible cabins to the hidden stack.
            var visibleCabins = farm
                .buildings.Where(b => b.isCabin && IsSweepableIntoStack(b))
                .ToList();

            foreach (var cabin in visibleCabins)
            {
                cabin.SetPosition(HiddenCabinLocation);
                Monitor.Log($"  Migrated cabin to hidden stack", LogLevel.Info);
                migrated++;
            }

            // A stale DefaultCabinLocation override can point the shared stack ghost at a
            // developed tile, silently weakening the "ghost lands on the just-vacated spot"
            // guarantee of this switch. Validate it now that the sweep has vacated the
            // designated spots; on failure fall back to the map default.
            if (to == CabinStrategy.CabinStack && Data.DefaultCabinLocation.HasValue)
            {
                var probe = farm.buildings.FirstOrDefault(b => b.isCabin && b.IsInHiddenStack());
                if (
                    probe != null
                    && !CabinPlacementValidator.TryValidate(
                        farm,
                        probe,
                        Data.DefaultCabinLocation.Value.ToPoint(),
                        out var overrideReason
                    )
                )
                {
                    Monitor.Log(
                        $"Stack-position override {Data.DefaultCabinLocation.Value} is no longer "
                            + $"valid ({overrideReason}); falling back to the map's default stack "
                            + "position.",
                        LogLevel.Warn
                    );
                    Data.DefaultCabinLocation = null;
                    Data.Write();
                }
            }
        }
        // Remaining directions (CabinStack → FarmhouseStack, or a materializing direction
        // with an empty hidden stack): pure-hide, only warp behavior changes.

        // Rejections emit cabin_strategy_migration_aborted and return early, so this
        // success event never carries a failure count.
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

    /// <summary>
    /// Load-time terminal-state guard: a live None strategy must never coexist with hidden
    /// non-lobby cabins — under None no interceptor runs, so a hidden cabin is invisible
    /// with dead warps, yet it still counts against the None cap, so joins are refused
    /// while nothing is ever built. No supported flow produces the combination, but two
    /// crash/ordering shapes can: a staged migration whose start AND commit both fell into
    /// one unsaved window (the record never reached the save, so the pre-commit heal has
    /// nothing to detect), and an import of a stacked-strategy save onto a None-configured
    /// server (previous == current, so no switch is detected at all). Reconcile the way a
    /// commit would: validator-gated, non-destructive placement onto designated spots,
    /// then point every visible cabin's exit at its own door. A cabin that finds no valid
    /// spot stays hidden with a Warn.
    /// </summary>
    private void ReconcileHiddenCabinsUnderNone()
    {
        if (!options.IsNone || Data.ActiveMigration != null)
        {
            return;
        }

        var farm = Game1.getFarm();
        if (farm == null || !farm.buildings.Any(b => b.isCabin && b.IsInHiddenStack()))
        {
            return;
        }

        Monitor.Log(
            "None strategy loaded with cabins still in the hidden stack (crashed migration "
                + "commit, or a stacked-strategy save imported onto a None server); placing "
                + "them at designated map spots.",
            LogLevel.Warn
        );

        var placed = PlaceHiddenCabinsOntoDesignatedSpots(
            farm,
            (cabin, spot) =>
            {
                cabin.SetPosition(spot);
                return true;
            }
        );

        foreach (var building in farm.buildings)
        {
            if (building.isCabin && !building.IsInHiddenStack() && !building.IsLobbyOrEditing())
            {
                building.SetWarpsToFarmCabinDoor();
            }
        }

        var leftHidden = farm.buildings.Count(b => b.isCabin && b.IsInHiddenStack());
        if (leftHidden > 0)
        {
            Monitor.Log(
                $"{leftHidden} hidden cabin(s) found no valid designated spot and stay "
                    + "hidden (their slots are unusable); free up designated spots and "
                    + "reload.",
                LogLevel.Warn
            );
        }

        Diagnostics.ModEventLog.Emit("cabin_none_reconciled", new { placed, leftHidden });
    }

    #endregion

    #region Stack Spot (CabinStack)

    /// <summary>
    /// Read model for the CabinStack shared stack spot: the tile each player's ghost cabin
    /// renders at, whether it comes from the persisted override or the map default, and
    /// whether the spot currently fails placement validation (obstructed).
    /// </summary>
    public readonly record struct StackSpotStatus(
        Point Spot,
        bool IsOverride,
        bool IsObstructed,
        string ObstructionReason
    );

    /// <summary>
    /// Live stack-spot status, or null when it doesn't apply (no loaded game, or the
    /// active strategy is not CabinStack — FarmhouseStack renders no stack cabin and None
    /// has no hidden cabins).
    /// </summary>
    public StackSpotStatus? GetStackSpotStatus()
    {
        if (!Game1.hasLoadedGame || !options.IsCabinStack)
        {
            return null;
        }

        var farm = Game1.getFarm();
        if (farm == null)
        {
            return null;
        }

        var spot = StackLocation.Create(Data).ToPoint();
        var probe = farm.buildings.FirstOrDefault(b => b.isCabin && b.IsInHiddenStack());
        string reason = null;
        var obstructed =
            probe != null && !CabinPlacementValidator.TryValidate(farm, probe, spot, out reason);
        return new StackSpotStatus(
            spot,
            Data.DefaultCabinLocation.HasValue,
            obstructed,
            reason ?? ""
        );
    }

    /// <summary>
    /// Sets the CabinStack shared stack spot (writes the persisted DefaultCabinLocation).
    /// CabinStack-only; refused during a staged migration (the migration owns the spot
    /// choice via 'cabins migrate place'); validator-gated against a hidden probe cabin.
    /// Connected players keep seeing the old spot until they reconnect — the ghost position
    /// is written into each peer's location-introduction message, and deltas rewrite warps,
    /// not positions.
    /// </summary>
    public bool TrySetStackSpot(Point topLeft, out string message)
    {
        if (!Game1.hasLoadedGame)
        {
            message = "No game loaded yet.";
            return false;
        }

        if (!options.IsCabinStack)
        {
            message =
                "The stack spot applies only to the CabinStack strategy "
                + $"(active: {options.Data.CabinStrategy}).";
            return false;
        }

        if (Data.ActiveMigration != null)
        {
            message =
                "A staged migration is in progress — choose the spot with "
                + "'cabins migrate place <x> <y>' (or '!migrate place'), or finish the "
                + "migration first.";
            return false;
        }

        var farm = Game1.getFarm();
        var probe = farm.buildings.FirstOrDefault(b => b.isCabin && b.IsInHiddenStack());
        if (
            probe != null
            && !CabinPlacementValidator.TryValidate(farm, probe, topLeft, out var reason)
        )
        {
            message = $"Can't use ({topLeft.X},{topLeft.Y}) as the stack spot: {reason}.";
            return false;
        }

        Data.DefaultCabinLocation = topLeft.ToVector2();
        Data.Write();
        message =
            $"Stack spot set to ({topLeft.X},{topLeft.Y}). Connected players see it after "
            + "they reconnect.";
        Monitor.Log(message, LogLevel.Info);
        return true;
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

    /// <summary>
    /// True for a cabin the bulk movers (SyncExistingCabins' MoveToStack sweep,
    /// ApplyStrategySwitch's None→stacked sweep) may pull into the hidden stack: not
    /// already hidden, not a lobby/editing cabin, not /cabin-placed (player intent
    /// outranks the sweep), and not placed by the active staged migration (must survive
    /// interim reloads). Callers pre-filter on isCabin.
    /// </summary>
    private bool IsSweepableIntoStack(Building cabin) =>
        !cabin.IsInHiddenStack()
        && !cabin.IsLobbyOrEditing()
        && !HasSavedPosition(cabin)
        && !IsMigrationPlaced(cabin);

    /// <summary>
    /// True if the cabin was placed by the active staged migration. Such a cabin must
    /// survive interim reloads: the bulk movers (MoveToStack / strategy migration) exempt
    /// it exactly like a /cabin-placed one. Matched by interior NameOrUniqueName — unique
    /// and save-stable; spare cabins are ownerless so an owner key won't do.
    /// </summary>
    private bool IsMigrationPlaced(Building cabin)
    {
        var placedNames = Data.ActiveMigration?.PlacedCabinIndoorNames;
        if (placedNames == null || placedNames.Count == 0)
        {
            return false;
        }

        var name = cabin.GetIndoors<Cabin>()?.NameOrUniqueName;
        return name != null && placedNames.Contains(name);
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
            var visibleCabins = allCabins.Where(IsSweepableIntoStack).ToList();
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
        // Under None there is no hidden stack and no warp manipulation — leave the
        // message untouched. Checked per message (not at registration) because the
        // strategy can change in-process.
        if (options.IsNone)
        {
            return;
        }

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
                // A cabin its owner moved out via !cabin exits at its own door; everyone
                // else keeps the shared farmhouse-door exit. Gate on HasSavedPosition
                // (intent), NOT !IsInHiddenStack(): lobby cabins live on row y=-21, outside
                // the (-20,-20) hidden-stack check, and must not be exempted.
                if (HasSavedPosition(fhCabin))
                {
                    fhCabin.SetWarpsToFarmCabinDoor();
                }
                else
                {
                    fhCabin.SetWarpsToFarmFarmhouseDoor();
                }
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

        // None cap: total cabins never grow past min(designated positions, MaxPlayers) — the
        // honest player ceiling. This bounds every caller (join, save-load, farmhand-menu
        // reservations, delete-rebuild) so on-demand growth can't place a cabin onto a
        // developed farm. The save-import finalizer is deliberately exempt: it builds via
        // BuildNewCabinVisibleReturning directly, so a swap-host import can always place its
        // owner's cabin (counted against the cap afterwards).
        if (options.IsNone && cabinsMissingCount > 0)
        {
            var totalCount = farm.buildings.Count(b => b.isCabin && !b.IsLobbyOrEditing());
            var headroom = Math.Max(0, GetNoneCabinCap(farm) - totalCount);
            if (cabinsMissingCount > headroom)
            {
                Monitor.Log(
                    $"Cabin check: None cap reached ({totalCount}/{GetNoneCabinCap(farm)} cabins), "
                        + $"clamping build request from {cabinsMissingCount} to {headroom}",
                    LogLevel.Debug
                );
                cabinsMissingCount = headroom;
            }
        }

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
    /// The None strategy's total-cabin ceiling: the count frozen at game creation
    /// (min(designated positions, MaxPlayers) — see GameCreatorService.CreateNewGame). A later
    /// MaxPlayers raise does NOT grow it; deletes replenish in place only, onto the just-freed
    /// designated spot. For a save predating the frozen field (NoneCabinCount == 0), fall back
    /// to computing the same min live.
    /// </summary>
    public int GetNoneCabinCap(Farm farm)
    {
        var frozen = options.Data.NoneCabinCount;
        if (frozen > 0)
        {
            return frozen;
        }

        return Math.Min(
            FarmCabinPositions.GetDesignatedPositions(farm).Count,
            options.Data.MaxPlayers
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
            .buildings.Where(b => b.isCabin && !b.IsLobbyOrEditing())
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
    /// the slot carries any ownership marker — a userID stamp (a player clicked the slot, which
    /// stamps their platform ID via vanilla Client.sendPlayerIntroduction) and/or an ownership-map
    /// record (written at the join gate's approve moment) — but isCustomized is false (they quit
    /// before finishing character creation). Vanilla FarmhandMenu greys a stamped slot for every
    /// other player, and the ownership gate locks a mapped slot to the ghost; a map-only claim is
    /// real too (a Galaxy-auth-failed Steam client stamps nothing, but the gate recorder maps it
    /// at approve), so an empty stamp must NOT early-return. Clearing both re-opens the slot.
    ///
    /// Operator-origin records (farmhand rebind / save-import bind) are NOT abandoned claims —
    /// a pre-assignment on a not-yet-customized slot is deliberate and must survive restarts and
    /// the assignee's own quit-during-character-creation; only the ghost stamp is cleared then.
    ///
    /// Caller-agnostic: both the disconnect heal (CleanupAbandonedCabinClaim) and the save-load
    /// sweep (ClearAbandonedCabinClaimsOnLoad) pass the persisted farmhandData entry. The
    /// farmhand mutation is only the userID NetString value write; the rest stays in its
    /// default uncustomized state. Returns true if a claim was cleared.
    /// </summary>
    private bool TryClearAbandonedClaim(Farmer farmhand)
    {
        if (farmhand == null || farmhand.isCustomized.Value)
        {
            return false; // no entry / real player — must not touch
        }

        var hasStamp = !string.IsNullOrEmpty(farmhand.userID.Value);
        var hasOwner = farmhandOwnership.TryGetOwner(farmhand.UniqueMultiplayerID, out var owner);
        var clearOwner = hasOwner && owner.Origin != FarmhandOwnershipService.OriginOperator;
        if (!hasStamp && !clearOwner)
        {
            return false; // no claim (or only a deliberate operator pre-assignment)
        }

        Monitor.Log(
            $"Releasing abandoned cabin claim (userID='{ChatRedaction.MaskValue(farmhand.userID.Value)}', "
                + $"hadOwnerRecord={hasOwner}, ownerRecordKept={hasOwner && !clearOwner}, "
                + "slot was claimed but not customized)",
            LogLevel.Info
        );
        Diagnostics.ModEventLog.Emit(
            "cabin_claim_abandoned",
            new
            {
                clearedUserId = farmhand.userID.Value,
                ownerUniqueMultiplayerId = farmhand.UniqueMultiplayerID,
                hadOwnerRecord = hasOwner,
                ownerRecordKept = hasOwner && !clearOwner,
            }
        );
        farmhand.userID.Value = "";
        if (clearOwner)
        {
            farmhandOwnership.RemoveOwner(farmhand.UniqueMultiplayerID);
        }

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
        // The cabin is created before its position is picked so the freed-tile check can
        // run the full placement validation against its real footprint; buildStructure
        // assigns the position (and OnValueAdded re-derives the interior warps), so the
        // placeholder position never leaks. A just-freed visible-cabin tile (DestroyCabin)
        // outranks the designated positions: re-placing there keeps the delete→rebuild
        // slot in place — validated at take time, so it can never bulldoze player work.
        var cabin = CreateCabinBuilding(Vector2.Zero);
        var position =
            TakeFreedVisibleCabinTile(farm, cabin)
            ?? FarmCabinPositions.GetNextAvailablePosition(farm);

        if (!position.HasValue)
        {
            Monitor.Log("No available designated cabin position on farm map", LogLevel.Warn);
            Diagnostics.ModEventLog.Emit(
                "cabin_build_failed",
                new { hidden = false, reason = "no_available_map_position" }
            );
            return null;
        }

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
    /// The most recently freed visible-cabin tile that still passes full placement
    /// validation for <paramref name="probe"/>, or null. Validated at take time with
    /// <see cref="CabinPlacementValidator"/> (footprint, terrain/objects, farmers) — not
    /// mere anchor occupancy: the tile may have been freed days ago, and anything a player
    /// built or planted on the footprint since must disqualify it, because the caller
    /// builds with skipSafetyChecks + ClearTerrainBelow. A failing tile is dropped.
    /// </summary>
    private Vector2? TakeFreedVisibleCabinTile(Farm farm, Building probe)
    {
        while (_freedVisibleCabinTiles.Count > 0)
        {
            var tile = _freedVisibleCabinTiles.Dequeue();
            if (CabinPlacementValidator.TryValidate(farm, probe, tile, out _))
            {
                return new Vector2(tile.X, tile.Y);
            }
        }

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

        if (ownerId != 0)
        {
            farmhandOwnership.RemoveOwner(ownerId);
        }

        // Vanilla Cabin.demolish() resets every villager homed in the cabin to its canonical home
        // before removal (Cabin.cs:196-203); our raw-remove path skips it, stranding an NPC that
        // married this cabin's owner — its DefaultMap stays "FarmHouse<guid>", and the next
        // NPC.dayUpdate warp-home throws KeyNotFoundException (NPC.cs:6105). Reset them the way
        // vanilla divorce does. Game1.fixProblems' own stranded-spouse heal (Game1.cs:7489) can't
        // help: its guard matches "cabin"/"FarmHouse" literally and misses our FarmHouse<guid> name.
        if (indoors != null && !string.IsNullOrEmpty(deletedName))
        {
            ResetVillagersHomedAt(indoors, deletedName);
        }

        if (!cabinBuilding.IsInHiddenStack() && !cabinBuilding.IsLobbyOrEditing())
        {
            _freedVisibleCabinTiles.Enqueue(
                new Point(cabinBuilding.tileX.Value, cabinBuilding.tileY.Value)
            );
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
    /// Reset every villager NPC homed at the cabin being destroyed back to its canonical home,
    /// mirroring vanilla <c>Cabin.demolish()</c> (Cabin.cs:196-203). A marriage to the cabin's
    /// farmhand owner sets the villager's <c>DefaultMap</c> to "FarmHouse&lt;guid&gt;"
    /// (NPC.cs:6366); once the cabin is gone, <c>NPC.dayUpdate</c> warps the villager to that
    /// now-missing location and throws <c>KeyNotFoundException</c> (NPC.cs:6105) on the next day
    /// transition. The reset is the same one both vanilla divorce paths use
    /// (<c>reloadDefaultLocation</c> + warp, NPC.cs:3537).
    ///
    /// Matched by <c>DefaultMap</c> string OR current-location identity so a spouse mid-schedule
    /// outside the cabin (e.g. in Town) is still caught — vanilla's in-cabin-only loop would miss
    /// it. Idempotent and safe on unowned lobby/editing cabins (no villager is homed there, so the
    /// scan no-ops). Master-only operations (warp + NetField writes); this server is always master
    /// (multiplayerMode=2).
    /// </summary>
    private void ResetVillagersHomedAt(Cabin indoors, string deletedName)
    {
        // Collect first, warp after: Game1.warpCharacter mutates the location's `characters`
        // collection (Game1.cs:9713), which ForEachVillager iterates live — warping inside the
        // scan would throw "collection modified". Vanilla demolish() snapshots for the same reason
        // (new List<NPC>(characters), Cabin.cs:196).
        var stranded = new List<NPC>();
        Utility.ForEachVillager(npc =>
        {
            if (npc.DefaultMap == deletedName || ReferenceEquals(npc.currentLocation, indoors))
            {
                stranded.Add(npc);
            }
            return true; // scan every villager
        });

        foreach (var npc in stranded)
        {
            // The deleted cabin's farmhand is already removed from farmhandData, so a spouse
            // NPC resolves getSpouse() == null and takes the unmarried branch (village home).
            ReHomeNpc(npc, $"destroyed cabin '{deletedName}'", LogLevel.Warn);
        }
    }

    /// <summary>
    /// Create a new cabin Building with interior properly initialized.
    /// Uses load() for save-deserialization-style initialization (no construction
    /// animations or sounds), then verifies the interior was actually created.
    /// </summary>
    public Building CreateCabinBuilding(Vector2 tilePosition)
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

    #region Lobby-Homed Heal

    // Context tags for the heal passes. SaveLoaded is the one-shot migration for saves poisoned
    // by the old lobby redirect (heals expected on the first boot after deploy, logged at Info);
    // DayStarted is a tripwire whose steady state is zero heals (logged at Warn so an unknown
    // ingress route surfaces as a signal); Join is PasswordProtectionService's per-join repair.
    public const string HealContextSaveLoaded = "save_loaded";
    public const string HealContextDayStarted = "day_started";
    public const string HealContextJoin = "join";

    private static LogLevel HealLogLevel(string context) =>
        context == HealContextDayStarted ? LogLevel.Warn : LogLevel.Info;

    /// <summary>
    /// Ensures a farmhand's durable home fields point at a real (non-lobby) cabin and — in
    /// the sweep contexts only, never at join — scrubs lobby-poisoned spawn hints. Shared
    /// core behind the load/day-start sweeps and PasswordProtectionService's join-time
    /// repair. Reassignment is ownership-first: the
    /// farmhand's own cabin (matched by owner or farmhandReference) wins over vanilla
    /// TryAssignFarmhandHome, whose building-order scan could hand an owning farmhand a fresh
    /// cabin instead of their own.
    ///
    /// Field writes only — never warps a live player (an unauthenticated player standing in the
    /// lobby must stay there until they authenticate). Quiet when there is nothing to fix.
    /// </summary>
    public void EnsureFarmhandRealHome(Farmer farmer, string context)
    {
        if (farmer == null || farmer.IsMainPlayer)
        {
            return; // the host's home is the main FarmHouse by design
        }

        // Resolve by building iteration, never getLocationFromName: the location-lookup cache
        // is flushed during day transitions and may not have re-learned structure interiors
        // (same rationale as PasswordProtectionService.FindPlayerCabin).
        var beforeHome = farmer.homeLocation.Value ?? "";
        var homeCabin = FindCabinInteriorByName(beforeHome);
        var homeValid = homeCabin != null && !LobbyService.IsLobbyCabin(homeCabin.ParentBuilding);

        string reassignPath = null;
        if (!homeValid)
        {
            var owned = FindOwnedCabin(farmer.UniqueMultiplayerID);
            if (owned != null)
            {
                // Re-link the farmhand to THEIR OWN cabin. AssignFarmhand re-sets
                // farmhandReference + homeLocation in one call and is idempotent for the owner.
                owned.AssignFarmhand(farmer);
                reassignPath = "owned_cabin";
            }
            else
            {
                // Vanilla assignment. Clear the home first — TryAssignFarmhandHome's first
                // condition short-circuits on ANY resolvable Cabin, lobby included. Null a
                // lobby lastSleepLocation too: its lastSleepLocation condition would try it
                // (LobbyService's CanAssignTo postfix already blocks lobby cabins, but don't
                // lean on a single defense layer).
                farmer.homeLocation.Value = "";
                var staleSleepCabin = FindCabinInteriorByName(farmer.lastSleepLocation.Value);
                if (
                    staleSleepCabin != null
                    && LobbyService.IsLobbyCabin(staleSleepCabin.ParentBuilding)
                )
                {
                    farmer.lastSleepLocation.Value = null;
                }

                if (Game1.netWorldState.Value.TryAssignFarmhandHome(farmer))
                {
                    reassignPath = "vanilla_assign";
                }
                else
                {
                    // Cabin pool exhausted — build one and retry.
                    EnsureAtLeastXCabins(1);
                    if (Game1.netWorldState.Value.TryAssignFarmhandHome(farmer))
                    {
                        reassignPath = "built_and_assigned";
                    }
                }
            }

            if (reassignPath == null)
            {
                // Total failure (build failed twice, e.g. None strategy with an exhausted
                // position pool). A married farmhand must never be left with an empty
                // homeLocation — marriageDuties calls RequireLocation on it raw and would
                // crash the day loop — so restore the old value if it still resolves
                // (poisoned-but-stable beats crashing); the next sweep retries.
                if (FindCabinInteriorByName(beforeHome) != null)
                {
                    farmer.homeLocation.Value = beforeHome;
                }
                Monitor.Log(
                    $"Could not reassign a real cabin to farmhand "
                        + $"'{ChatRedaction.MaskValue(farmer.Name)}' (id={farmer.UniqueMultiplayerID}, "
                        + $"home='{beforeHome}', context={context}): no cabin available and building one failed.",
                    LogLevel.Warn
                );
                Diagnostics.ModEventLog.Emit(
                    "farmhand_home_heal_failed",
                    new
                    {
                        farmhandId = farmer.UniqueMultiplayerID,
                        beforeHome,
                        context,
                    }
                );
                return;
            }

            homeCabin = FindCabinInteriorByName(farmer.homeLocation.Value);
        }

        // Scrub lobby-poisoned spawn hints even when the home itself is valid: a pre-auth
        // disconnect persists lobby lastSleepLocation/disconnectLocation (plus disconnectDay =
        // today) on an entry whose homeLocation was already healed at join — a rejoin on a
        // later passwordless boot would then take wake-up branch 1 (disconnect fields)
        // straight into the sealed lobby. These are transient hints re-stamped by real
        // gameplay, so rewriting them is safe — EXCEPT during an active join under password:
        // there the lobby hints are load-bearing (the client lands in the lobby via wake-up
        // branches 1-2, and SendServerIntroduction's explicit lobby-location send keys on
        // disconnectLocation == lobby), so the join context must leave them alone.
        var scrubbedLastSleep = false;
        var scrubbedDisconnect = false;
        if (context != HealContextJoin)
        {
            var sleepCabin = FindCabinInteriorByName(farmer.lastSleepLocation.Value);
            if (sleepCabin != null && LobbyService.IsLobbyCabin(sleepCabin.ParentBuilding))
            {
                if (homeCabin != null)
                {
                    farmer.lastSleepLocation.Value = homeCabin.NameOrUniqueName;
                    farmer.lastSleepPoint.Value = homeCabin.GetPlayerBedSpot();
                }
                else
                {
                    farmer.lastSleepLocation.Value = null;
                }
                scrubbedLastSleep = true;
            }

            // Scrub lobby disconnect hints only while they can still fire: wake-up branch 1
            // requires disconnectDay == today's DaysPlayed (its own arming condition), so a
            // stale-day lobby disconnectLocation is inert. It is also PERMANENT residue on a
            // connected client's root (the client only rewrites disconnect fields on a real
            // disconnect, and its nightly resend re-imports them) — scrubbing it would fire
            // the tripwire every single morning for every lobby-joined farmhand.
            var disconnectCabin = FindCabinInteriorByName(farmer.disconnectLocation.Value);
            if (
                disconnectCabin != null
                && LobbyService.IsLobbyCabin(disconnectCabin.ParentBuilding)
                && farmer.disconnectDay.Value == (int)Game1.MasterPlayer.stats.DaysPlayed
            )
            {
                farmer.disconnectLocation.Value = null;
                farmer.disconnectPosition.Value = Vector2.Zero;
                farmer.disconnectDay.Value = -1;
                scrubbedDisconnect = true;
            }
        }

        if (reassignPath != null || scrubbedLastSleep || scrubbedDisconnect)
        {
            Monitor.Log(
                $"Healed lobby-homed farmhand '{ChatRedaction.MaskValue(farmer.Name)}' "
                    + $"(id={farmer.UniqueMultiplayerID}, context={context}): "
                    + $"home '{beforeHome}' -> '{farmer.homeLocation.Value}'"
                    + (reassignPath != null ? $" via {reassignPath}" : " (spawn hints only)")
                    + $", lastSleepScrubbed={scrubbedLastSleep}, disconnectScrubbed={scrubbedDisconnect}",
                HealLogLevel(context)
            );
            Diagnostics.ModEventLog.Emit(
                "farmhand_home_healed",
                new
                {
                    farmhandId = farmer.UniqueMultiplayerID,
                    beforeHome,
                    afterHome = farmer.homeLocation.Value ?? "",
                    path = reassignPath ?? "spawn_hints_only",
                    scrubbedLastSleep,
                    scrubbedDisconnect,
                    context,
                }
            );
        }
    }

    /// <summary>
    /// Heals every farmhand entry via <see cref="EnsureFarmhandRealHome"/>. An online farmhand
    /// exists as TWO Farmer objects — the persisted farmhandData entry (the clone target of the
    /// nightly SaveFarmhand) and the live otherFarmers root (what getAllFarmhands hands to
    /// marriageDuties while they're online) — so both are healed, or the missed one stays
    /// authoritative for its consumer.
    /// </summary>
    private void HealLobbyHomedFarmhands(string context)
    {
        var farmhandData = Game1.netWorldState?.Value?.farmhandData;
        if (farmhandData == null)
        {
            return;
        }

        // Snapshot: a heal can build a cabin (EnsureAtLeastXCabins), which adds a fresh
        // farmhand entry and would tear a live FieldDict enumeration.
        var entries = farmhandData
            .FieldDict.Select(kvp => (Uid: kvp.Key, Farmer: kvp.Value.Value))
            .ToList();

        foreach (var (uid, persisted) in entries)
        {
            if (persisted != null)
            {
                EnsureFarmhandRealHome(persisted, context);
            }

            if (
                Game1.otherFarmers.TryGetValue(uid, out var live)
                && !ReferenceEquals(live, persisted)
            )
            {
                EnsureFarmhandRealHome(live, context);
            }
        }
    }

    /// <summary>
    /// Re-homes every NPC stranded by a lobby interior: villagers whose DefaultMap is a
    /// lobby/editing cabin interior, villagers whose DefaultMap resolves to no location at all
    /// (stranded by an individual lobby destroyed on an older build — the next dayUpdate warp
    /// would fail loudly), any villager physically inside a lobby interior, and children/pets
    /// found in lobby interiors. Runs after the farmhand pass so married NPCs re-derive their
    /// home from a healed spouse homeLocation.
    /// </summary>
    private void HealLobbyHomedResidents(string context)
    {
        var farm = Game1.getFarm();
        if (farm == null)
        {
            return;
        }

        // Snapshot matches first: warpCharacter mutates location.characters during iteration.
        var stranded = new List<NPC>();

        Utility.ForEachVillager(npc =>
        {
            if (IsLobbyOrEditingInterior(npc.currentLocation))
            {
                stranded.Add(npc);
            }
            else if (!string.IsNullOrEmpty(npc.DefaultMap))
            {
                var mapCabin = FindCabinInteriorByName(npc.DefaultMap);
                var isLobbyMap =
                    mapCabin != null && LobbyService.IsLobbyCabin(mapCabin.ParentBuilding);
                // Do NOT scope the unresolvable check to existing lobby interiors — a villager
                // stranded by a destroyed lobby has no interior left to match.
                var isDanglingMap =
                    mapCabin == null && Game1.getLocationFromName(npc.DefaultMap) == null;
                if (isLobbyMap || isDanglingMap)
                {
                    stranded.Add(npc);
                }
            }
            return true; // scan every villager
        });

        // Children and pets aren't villagers (ForEachVillager filters on IsVillager); catch any
        // physically stranded inside a lobby/editing interior.
        foreach (var building in farm.buildings)
        {
            if (!building.isCabin || !building.IsLobbyOrEditing())
            {
                continue;
            }

            var interior = building.GetIndoors<Cabin>();
            if (interior == null)
            {
                continue;
            }

            foreach (var character in interior.characters)
            {
                if (character is Child || character is Pet)
                {
                    stranded.Add(character);
                }
            }
        }

        foreach (var npc in stranded)
        {
            ReHomeNpc(npc, context, HealLogLevel(context));
        }
    }

    /// <summary>
    /// Re-homes a household NPC out of a lobby or destroyed interior. Married villagers are
    /// re-pointed at their spouse-farmer's home — DefaultMap AND DefaultPosition together, the
    /// way the engine pairs them (Game1.prepareSpouseForWedding); a stale DefaultPosition would
    /// re-land the NPC at the (-1000,-1000) sentinel on the next dayUpdate warp. Unmarried
    /// villagers re-derive their village home from character data (reloadDefaultLocation has no
    /// married-gate; the gate is at its data-reload call site). Children go to their parent's
    /// home, pets to the Farm — never repoint Pet.homeLocationName; the bowl is a Farm building
    /// resolved off it and the pet returns to it on the next dayUpdate.
    /// </summary>
    private void ReHomeNpc(NPC npc, string reason, LogLevel logLevel)
    {
        var beforeMap = npc.DefaultMap ?? "";
        var beforeLocation = npc.currentLocation?.NameOrUniqueName ?? "";
        string branch;

        if (npc is Child child)
        {
            // Game1.GetPlayer resolves offline farmhands via farmhandData.
            var parent = Game1.GetPlayer(child.idOfParent.Value);
            var parentHome =
                parent != null ? ResolveRealFarmhouse(parent.homeLocation.Value) : null;
            if (parentHome != null)
            {
                Game1.warpCharacter(
                    child,
                    parentHome,
                    Utility.PointToVector2(parentHome.GetPlayerBedSpot())
                );
            }
            else
            {
                var farm = Game1.getFarm();
                Game1.warpCharacter(
                    child,
                    farm,
                    Utility.PointToVector2(farm.GetMainFarmHouseEntry())
                );
            }
            branch = "child";
        }
        else if (npc is Pet)
        {
            var farm = Game1.getFarm();
            Game1.warpCharacter(npc, farm, Utility.PointToVector2(farm.GetMainFarmHouseEntry()));
            branch = "pet";
        }
        else if (
            npc.isMarried() // getSpouse() alone also matches engaged couples, who don't move in until the wedding
            && npc.getSpouse() is Farmer spouse
            && ResolveRealFarmhouse(spouse.homeLocation.Value) is FarmHouse home
        )
        {
            // The level-0 (-1000,-1000) bed-spot sentinel is exact vanilla parity with
            // marriageDuties, reachable only via synthesized level-0 marriages.
            var bedSpot = home.getSpouseBedSpot(npc.Name);
            npc.DefaultMap = spouse.homeLocation.Value;
            npc.DefaultPosition = Utility.PointToVector2(bedSpot) * 64f;
            npc.ClearSchedule();
            Game1.warpCharacter(npc, home, Utility.PointToVector2(bedSpot));
            branch = "married";
        }
        else if (Game1.characterData.ContainsKey(npc.Name))
        {
            // A married NPC lands here only when the spouse home is unresolvable — the village
            // home beats a crash (never RequireLocation on a bad name).
            npc.reloadDefaultLocation();
            var target = Game1.getLocationFromName(npc.DefaultMap);
            if (target == null)
            {
                // reloadDefaultLocation keeps the old DefaultMap when character data has no
                // matching Home entry; warping would throw. Leave the NPC for a later sweep.
                Monitor.Log(
                    $"Cannot re-home NPC '{npc.Name}' ({reason}): DefaultMap "
                        + $"'{npc.DefaultMap}' does not resolve after reloadDefaultLocation.",
                    LogLevel.Warn
                );
                return;
            }
            npc.ClearSchedule();
            Game1.warpCharacter(npc, target, npc.DefaultPosition / 64f);
            branch = "unmarried";
        }
        else
        {
            // No character data — no canonical home to derive (mirrors Cabin.demolish's guard).
            return;
        }

        Monitor.Log(
            $"Re-homed NPC '{npc.Name}' ({branch}, {reason}): map '{beforeMap}' -> "
                + $"'{npc.DefaultMap}', location '{beforeLocation}' -> "
                + $"'{npc.currentLocation?.NameOrUniqueName}'",
            logLevel
        );
        Diagnostics.ModEventLog.Emit(
            "npc_rehomed",
            new
            {
                npcName = npc.Name,
                branch,
                beforeMap,
                afterMap = npc.DefaultMap ?? "",
                beforeLocation,
                afterLocation = npc.currentLocation?.NameOrUniqueName ?? "",
                reason,
            }
        );
    }

    /// <summary>
    /// Finds a cabin interior by NameOrUniqueName via farm-building iteration (lookup-cache
    /// independent). Returns lobby/editing interiors too — callers classify via
    /// LobbyService.IsLobbyCabin on the ParentBuilding.
    /// </summary>
    private static Cabin FindCabinInteriorByName(string locationName)
    {
        if (string.IsNullOrEmpty(locationName))
        {
            return null;
        }

        var farm = Game1.getFarm();
        if (farm == null)
        {
            return null;
        }

        foreach (var building in farm.buildings)
        {
            if (!building.isCabin)
            {
                continue;
            }

            var cabin = building.GetIndoors<Cabin>();
            if (cabin != null && cabin.NameOrUniqueName == locationName)
            {
                return cabin;
            }
        }

        return null;
    }

    /// <summary>
    /// Finds the non-lobby cabin owned by the given player (matched by owner id or a defined
    /// farmhandReference uid), or null. Mirrors the primary lookup of
    /// PasswordProtectionService.FindPlayerCabin, which returns a warp handle — this one exists
    /// so EnsureFarmhandRealHome can re-link fields on the farmhand's own cabin.
    /// </summary>
    private static Cabin FindOwnedCabin(long playerId)
    {
        var farm = Game1.getFarm();
        if (farm == null)
        {
            return null;
        }

        foreach (var building in farm.buildings)
        {
            if (!building.isCabin || building.IsLobbyOrEditing())
            {
                continue;
            }

            var cabin = building.GetIndoors<Cabin>();
            if (cabin == null)
            {
                continue;
            }

            if (cabin.owner?.UniqueMultiplayerID == playerId)
            {
                return cabin;
            }

            if (
                cabin.farmhandReference.defined.Value
                && cabin.farmhandReference.uid.Value == playerId
            )
            {
                return cabin;
            }
        }

        return null;
    }

    /// <summary>
    /// Resolves a location name to a real (non-lobby) FarmHouse — a player cabin interior or
    /// the main FarmHouse (host household) — or null. Never resolves lobby/editing interiors.
    /// </summary>
    private static FarmHouse ResolveRealFarmhouse(string name)
    {
        var cabin = FindCabinInteriorByName(name);
        if (cabin != null)
        {
            return LobbyService.IsLobbyCabin(cabin.ParentBuilding) ? null : cabin;
        }

        return Game1.getLocationFromName(name) as FarmHouse;
    }

    private static bool IsLobbyOrEditingInterior(GameLocation location)
    {
        return location is Cabin cabin && LobbyService.IsLobbyCabin(cabin.ParentBuilding);
    }

    #endregion

    #region Save Import Finalizer (Layer B)

    /// <summary>
    /// One-shot save-import finalizer. Reads the pending finalize intent (written by
    /// <see cref="SaveImportService.ExecuteImport"/> during a swap import); if present and for this
    /// save, binds the demoted owner's platform identity in the ownership map (first, so any
    /// later failure leaves them claimable), then homes them into a known cabin and moves their
    /// farmhouse contents and household NPCs into it. Self-heals (Warn + clear + return) on any
    /// pre-condition miss, and clears the intent on EVERY exit (including a throw after a world
    /// mutation) so a failed finalize never retries against an already-changed world.
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
            // Step 4 — record the bind FIRST (idempotent map write; depends only on the owner uid
            // and the intent's userId), so a failure in any later cabin/content step still leaves
            // the demoted owner claimable by their platform identity. Also clear any legacy userID
            // stamp: the map is what the join gate and visibility filter enforce; a stamp would
            // additionally trigger vanilla client-side graying by Galaxy-space compare, which
            // would show the bound owner their own farmhand as LOCKED on any Steam/GOG client
            // (no transport ever presents a Steam64 as getUserID()).
            failedStep = "bind_ownership";
            farmhandOwnership.RecordOwner(
                owner.UniqueMultiplayerID,
                FarmhandOwnershipService.ClassifyPlatformId(intent.UserId),
                intent.UserId,
                FarmhandOwnershipService.OriginOperator
            );
            owner.userID.Value = "";

            // Step 5 — resolve the owner's cabin handle (reuse an auto-assigned cabin if the load
            // coroutine already homed the owner into a spare one, else build a fresh cabin).
            failedStep = "resolve_cabin";
            var cabin = ResolveOrBuildOwnerCabin(owner, out var builtFresh);
            if (cabin == null)
            {
                // Loud-fail at Warn (not Error). The owner stays a customized, cabin-less but
                // BOUND farmhand (progress intact, claimable; a later reassignment can re-home);
                // never proceed to the world-mutating steps.
                Monitor.Log(
                    $"Save-import: could not resolve or build a cabin for owner {intent.OwnerUid}; "
                        + "finalize aborted (owner kept and bound, progress intact).",
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

            // Step 6 — move the owner's farmhouse contents into the cabin.
            failedStep = "move_contents";
            contentsMoved = TransferFarmhouseContentsToCabin(farmHouse, cabin, builtFresh);

            // Step 7 — relocate the owner's household NPCs (pet, spouse, children) into the cabin.
            failedStep = "relocate_household";
            npcsMoved = RelocateHouseholdToCabin(farmHouse, cabin, owner);

            // Step 8 — move the owner's cellar contents (casks/wine, built in the master-keyed
            // "Cellar"-1 while they were the master) into the cellar the engine reassigns to them as
            // a farmhand. Counts toward contentsMoved.
            failedStep = "move_cellar";
            contentsMoved += TransferOwnerCellarContents(owner, cabin);

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
            // A throw after a world mutation leaves a partially-moved-but-stable world. The owner
            // is already bound (step 4 runs first) and customized (progress intact), so a missing
            // content/NPC move is a recoverable cosmetic gap, not a boot-loop. Warn (never Error)
            // + emit partial.
            Monitor.Log(
                $"Save-import finalize failed at step '{failedStep}': {ex.Message}. World is partially "
                    + "moved but stable; owner kept and bound. Intent cleared (no retry).",
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
