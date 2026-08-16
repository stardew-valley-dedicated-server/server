using System;
using System.Collections.Generic;
using System.Linq;
using JunimoServer.Util;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.Locations;

namespace JunimoServer.Services.CabinManager;

// Staged, admin-driven strategy migration ('cabins migrate' / '!migrate place') for the
// directions that materialize a cabin on an existing save:
//   stacked → None            (real placements at admin-approved positions)
//   FarmhouseStack → CabinStack (the shared stack ghost's spot must be clear or chosen)
// Every other direction is a pure-hide switch and stays on the settings-file + /reload
// path. Core principles: the old strategy stays live until an explicit commit; all
// placement is CabinPlacementValidator-gated with NO terrain clearing, so abort is
// trivially safe (move staged cabins back to the stack, drop the record).
//
// A partial-class file, not a separate service: the flow shares CabinManagerService's
// non-public state (Data, HasSavedPosition, EnsureAtLeastXCabins, HiddenCabinLocation),
// which a separate service would force onto the public surface.
public partial class CabinManagerService
{
    private static bool UsesHiddenStack(CabinStrategy strategy) =>
        strategy == CabinStrategy.CabinStack || strategy == CabinStrategy.FarmhouseStack;

    /// <summary>
    /// The direction matrix's "materializing" half: switches that place (or reveal) a cabin
    /// on the live farm and therefore require the staged 'cabins migrate' flow.
    /// </summary>
    public static bool RequiresStagedMigration(CabinStrategy from, CabinStrategy to) =>
        (UsesHiddenStack(from) && to == CabinStrategy.None)
        || (from == CabinStrategy.FarmhouseStack && to == CabinStrategy.CabinStack);

    /// <summary>
    /// Live status of the active staged migration, or null when none is active. Placed is
    /// the number of staging placements done; Remaining is computed live (hidden non-lobby
    /// cabins for → None; the unresolved ghost spot for FarmhouseStack → CabinStack), so a
    /// join or a mid-staging obstruction moves it. StackSpot is the effective shared-stack
    /// spot for the FarmhouseStack → CabinStack direction (null for → None), so 'status'
    /// can show the chosen spot mid-staging.
    /// </summary>
    public readonly record struct CabinMigrationStatus(
        CabinStrategy FromStrategy,
        CabinStrategy ToStrategy,
        int PlacedCount,
        int RemainingCount,
        Point? StackSpot
    );

    public CabinMigrationStatus? GetMigrationStatus()
    {
        var migration = Data.ActiveMigration;
        if (migration == null || !Game1.hasLoadedGame)
        {
            return null;
        }

        var farm = Game1.getFarm();
        if (farm == null)
        {
            return null;
        }

        // Placed is derived from the live placed-tile reader (not the raw record) so a
        // staged cabin its owner sent back to the stack via '!cabin reset' stops counting —
        // its name stays recorded for idempotent re-staging, but it is not placed.
        var placed =
            migration.ToStrategy == CabinStrategy.None
                ? GetMigrationPlacedTiles().Count
                : (migration.StackSpotOverride.HasValue ? 1 : 0);
        Point? stackSpot =
            migration.ToStrategy == CabinStrategy.CabinStack
                ? GetEffectiveStackSpot(farm, migration).ToPoint()
                : null;
        return new CabinMigrationStatus(
            migration.FromStrategy,
            migration.ToStrategy,
            placed,
            GetMigrationRemaining(farm, migration),
            stackSpot
        );
    }

    /// <summary>
    /// Tiles of the cabins currently placed by the active staging (for 'cabins migrate
    /// status' and the PlacedCount). Skips a recorded cabin its owner has since sent back
    /// to the hidden stack via '!cabin reset' — reporting it would print the hidden-stack
    /// tile as a placement.
    /// </summary>
    public List<Point> GetMigrationPlacedTiles()
    {
        var tiles = new List<Point>();
        var migration = Data.ActiveMigration;
        var farm = Game1.hasLoadedGame ? Game1.getFarm() : null;
        if (migration == null || farm == null)
        {
            return tiles;
        }

        foreach (var name in migration.PlacedCabinIndoorNames)
        {
            var building = FindCabinBuildingByIndoorName(farm, name);
            if (building != null && !building.IsInHiddenStack())
            {
                tiles.Add(new Point(building.tileX.Value, building.tileY.Value));
            }
        }

        return tiles;
    }

    private static Building FindCabinBuildingByIndoorName(Farm farm, string indoorName)
    {
        return farm.buildings.FirstOrDefault(b =>
            b.isCabin && b.GetIndoors<Cabin>()?.NameOrUniqueName == indoorName
        );
    }

    private int GetMigrationRemaining(Farm farm, CabinMigrationState migration)
    {
        if (migration.ToStrategy == CabinStrategy.None)
        {
            // Live count, never stored: a join during staging creates a new hidden cabin
            // (EnsureAtLeastXCabins keeps running under the still-live old strategy), which
            // correctly becomes one more required placement. Lobby cabins live on row -21,
            // outside the (-20,-20) hidden-stack check, so they're excluded automatically.
            return farm.buildings.Count(b => b.isCabin && b.IsInHiddenStack());
        }

        // FarmhouseStack → CabinStack: the single materialization is the shared stack ghost.
        // Re-validated live so a mid-staging obstruction re-opens the placement.
        return IsStackSpotResolved(farm, migration) ? 0 : 1;
    }

    /// <summary>
    /// The stack spot the ghost would use after commit: the staged override, else the same
    /// resolution StackLocation.Create applies (persisted DefaultCabinLocation → first
    /// designated position → fallback).
    /// </summary>
    private Vector2 GetEffectiveStackSpot(Farm farm, CabinMigrationState migration)
    {
        return migration.StackSpotOverride
            ?? Data.DefaultCabinLocation
            ?? FarmCabinPositions.GetDefaultStackPosition(farm);
    }

    private bool IsStackSpotResolved(Farm farm, CabinMigrationState migration)
    {
        var spot = GetEffectiveStackSpot(farm, migration);
        var probe = farm.buildings.FirstOrDefault(b => b.isCabin && b.IsInHiddenStack());
        if (probe == null)
        {
            // No hidden cabin means no ghost will ever render there — nothing to validate.
            return true;
        }

        return CabinPlacementValidator.TryValidate(farm, probe, spot.ToPoint(), out _);
    }

    /// <summary>
    /// Starts a staged migration to <paramref name="target"/>: validates the direction,
    /// writes the persisted record, and (for → None) runs the non-destructive auto-place
    /// pass over the designated map spots. The old strategy stays live until commit.
    /// </summary>
    public bool TryStartMigration(CabinStrategy target, out string message)
    {
        if (!Game1.hasLoadedGame)
        {
            message = "No game loaded yet.";
            return false;
        }

        var active = Data.ActiveMigration;
        if (active != null)
        {
            message =
                $"A migration {active.FromStrategy} → {active.ToStrategy} is already staged — "
                + "use 'cabins migrate status', 'commit' or 'abort'.";
            return false;
        }

        var current = options.Data.CabinStrategy;
        if (target == current)
        {
            message = $"{target} is already the active strategy.";
            return false;
        }

        if (!RequiresStagedMigration(current, target))
        {
            message =
                $"{current} → {target} is a pure-hide switch and needs no staged migration: "
                + $"set cabinStrategy to \"{target}\" in server-settings.json and reload.";
            return false;
        }

        var farm = Game1.getFarm();
        var migration = new CabinMigrationState { FromStrategy = current, ToStrategy = target };
        Data.ActiveMigration = migration;

        int autoPlaced = 0;
        if (target == CabinStrategy.None)
        {
            autoPlaced = AutoPlaceStagedCabins(farm);
        }

        Data.Write();

        var remaining = GetMigrationRemaining(farm, migration);
        Diagnostics.ModEventLog.Emit(
            "cabin_migration_started",
            new
            {
                fromStrategy = current.ToString(),
                toStrategy = target.ToString(),
                autoPlaced,
                remaining,
            }
        );

        if (target == CabinStrategy.None)
        {
            message =
                $"Staged migration {current} → None started: {autoPlaced} cabin(s) auto-placed "
                + $"at designated spots, {remaining} remaining."
                + (
                    remaining == 0
                        ? " Run 'cabins migrate commit' to finish."
                        : " Stand where each remaining cabin should go and run '!migrate place' "
                            + "(footprint 5x3, to your right), or use 'cabins migrate place <x> <y>'."
                )
                + " Under None capacity IS the cabin count — keep spare cabins placed if new "
                + "players should be able to join. Nothing is destroyed; 'cabins migrate abort' "
                + "undoes the staging.";
        }
        else
        {
            var spot = GetEffectiveStackSpot(farm, migration).ToPoint();
            message =
                remaining == 0
                    ? $"Staged migration {current} → CabinStack started: the shared stack spot "
                        + $"({spot.X},{spot.Y}) is clear — run 'cabins migrate commit' to finish, or "
                        + "pick a different spot first with '!migrate place' / 'cabins migrate place <x> <y>'."
                    : $"Staged migration {current} → CabinStack started: the shared stack spot "
                        + $"({spot.X},{spot.Y}) is obstructed — stand where the shared cabin should "
                        + "appear and run '!migrate place', or use 'cabins migrate place <x> <y>'. "
                        + "Then run 'cabins migrate commit'.";
        }

        Monitor.Log(message, LogLevel.Info);
        return true;
    }

    /// <summary>
    /// The → None auto-place pass: moves hidden cabins onto designated map spots that pass
    /// validation, skipping occupied/blocked ones. Never clears terrain — a spot a player
    /// developed simply fails validation and is left for manual placement.
    /// </summary>
    private int AutoPlaceStagedCabins(Farm farm)
    {
        return PlaceHiddenCabinsOntoDesignatedSpots(
            farm,
            (cabin, spot) => TryStageCabin(cabin, spot, manual: false, out _)
        );
    }

    /// <summary>
    /// Walks the hidden non-lobby cabins against the map's available designated positions,
    /// invoking <paramref name="place"/> with the first validated spot for each. Stops as
    /// soon as no spot validates — all cabins share the same footprint, so no spot fits
    /// any of the rest either. Shared by the staged auto-place pass and the load-time
    /// None reconciliation guard; validation-only, so callers own all record/warp writes.
    /// Returns the number of successful placements.
    /// </summary>
    private static int PlaceHiddenCabinsOntoDesignatedSpots(
        Farm farm,
        Func<Building, Point, bool> place
    )
    {
        int placedCount = 0;
        var hiddenCabins = new Queue<Building>(
            farm.buildings.Where(b => b.isCabin && b.IsInHiddenStack())
        );

        while (hiddenCabins.Count > 0)
        {
            var cabin = hiddenCabins.Peek();
            Point? spot = null;
            // Re-queried per cabin: each placement occupies its spot.
            foreach (var position in FarmCabinPositions.GetAvailablePositions(farm))
            {
                if (CabinPlacementValidator.TryValidate(farm, cabin, position.ToPoint(), out _))
                {
                    spot = position.ToPoint();
                    break;
                }
            }

            if (spot == null)
            {
                break;
            }

            hiddenCabins.Dequeue();
            if (place(cabin, spot.Value))
            {
                placedCount++;
            }
        }

        return placedCount;
    }

    /// <summary>
    /// One staging placement. For → None: moves the next hidden cabin to
    /// <paramref name="topLeft"/> (validated, non-destructive). For FarmhouseStack →
    /// CabinStack: records <paramref name="topLeft"/> as the shared stack spot (applied at
    /// commit). Used by 'cabins migrate place &lt;x&gt; &lt;y&gt;' and the '!migrate place'
    /// chat command.
    /// </summary>
    public bool TryPlaceMigration(Point topLeft, bool manual, out string message)
    {
        if (!Game1.hasLoadedGame)
        {
            message = "No game loaded yet.";
            return false;
        }

        var migration = Data.ActiveMigration;
        if (migration == null)
        {
            message = "No staged migration — start one with 'cabins migrate start <strategy>'.";
            return false;
        }

        var farm = Game1.getFarm();

        if (migration.ToStrategy == CabinStrategy.None)
        {
            var cabin = farm.buildings.FirstOrDefault(b => b.isCabin && b.IsInHiddenStack());
            if (cabin == null)
            {
                message = "Nothing left to place — run 'cabins migrate commit'.";
                return false;
            }

            if (!CabinPlacementValidator.TryValidate(farm, cabin, topLeft, out var reason))
            {
                message = $"Can't place cabin at ({topLeft.X},{topLeft.Y}): {reason}.";
                return false;
            }

            if (!TryStageCabin(cabin, topLeft, manual, out var stageReason))
            {
                message = $"Can't place cabin at ({topLeft.X},{topLeft.Y}): {stageReason}.";
                return false;
            }

            Data.Write();
            var remaining = GetMigrationRemaining(farm, migration);
            message =
                $"Placed cabin at ({topLeft.X},{topLeft.Y}) — {remaining} left."
                + (remaining == 0 ? " Run 'cabins migrate commit' to finish." : "");
            Monitor.Log(message, LogLevel.Info);
            return true;
        }

        // FarmhouseStack → CabinStack: no building moves — the placement chooses the spot
        // where the shared stack ghost will appear after commit.
        var probe = farm.buildings.FirstOrDefault(b => b.isCabin && b.IsInHiddenStack());
        if (
            probe != null
            && !CabinPlacementValidator.TryValidate(farm, probe, topLeft, out var ghostReason)
        )
        {
            message = $"Can't use ({topLeft.X},{topLeft.Y}) as the stack position: {ghostReason}.";
            return false;
        }

        migration.StackSpotOverride = topLeft.ToVector2();
        Data.Write();
        // Its own event (not cabin_migration_placed): no cabin moves here — this records
        // the FarmhouseStack→CabinStack ghost's future spot.
        Diagnostics.ModEventLog.Emit(
            "cabin_migration_stackspot_set",
            new
            {
                tileX = topLeft.X,
                tileY = topLeft.Y,
                manual,
            }
        );
        message =
            $"Stack position set to ({topLeft.X},{topLeft.Y}) — run 'cabins migrate commit' to finish.";
        Monitor.Log(message, LogLevel.Info);
        return true;
    }

    /// <summary>
    /// One staging placement: moves the cabin (SetPosition, never Relocate — Relocate
    /// clears the terrain below the footprint, and staging must be non-destructive so abort
    /// stays trivially safe) and records its interior name in the staging record. Refuses —
    /// nothing moves — when the interior name can't resolve: an unrecorded staged cabin
    /// would not be sweep-exempt on an interim reload and would be invisible to abort.
    /// Warps are NOT touched here — the still-live old strategy's interceptors keep
    /// pointing staged cabins per its rules until commit (the → None commit pass re-points
    /// them).
    /// </summary>
    private bool TryStageCabin(Building cabin, Point topLeft, bool manual, out string reason)
    {
        var name = cabin.GetIndoors<Cabin>()?.NameOrUniqueName;
        if (string.IsNullOrEmpty(name))
        {
            reason = "the cabin's interior name could not be resolved";
            Monitor.Log(
                $"Refusing to stage a cabin at ({topLeft.X},{topLeft.Y}): its interior name "
                    + "is unresolved, so the staging record could not protect or abort it.",
                LogLevel.Warn
            );
            return false;
        }

        cabin.SetPosition(topLeft);

        // Contains guard: '!cabin reset' can send a staged cabin back to the stack directly
        // (reset is keyed on visibility), and re-placing it must not append its name twice —
        // a duplicate inflates PlacedCount, double-counts abort's return count, and
        // duplicates 'cabins migrate status' tiles.
        if (!Data.ActiveMigration.PlacedCabinIndoorNames.Contains(name))
        {
            Data.ActiveMigration.PlacedCabinIndoorNames.Add(name);
        }

        Diagnostics.ModEventLog.Emit(
            "cabin_migration_placed",
            new
            {
                tileX = topLeft.X,
                tileY = topLeft.Y,
                manual,
            }
        );
        reason = null;
        return true;
    }

    /// <summary>
    /// Commits the staged migration: refuses while placements remain; otherwise runs the
    /// per-direction reconciliation, flips the persisted strategy AND the settings file
    /// (SyncFromSettings reapplies the file on every boot/reload — without the file write
    /// the next reload would flip the strategy back), and clears the record.
    /// </summary>
    public bool TryCommitMigration(out string message)
    {
        if (!Game1.hasLoadedGame)
        {
            message = "No game loaded yet.";
            return false;
        }

        var migration = Data.ActiveMigration;
        if (migration == null)
        {
            message = "No staged migration to commit.";
            return false;
        }

        var farm = Game1.getFarm();
        var remaining = GetMigrationRemaining(farm, migration);
        if (remaining > 0)
        {
            message =
                migration.ToStrategy == CabinStrategy.None
                    ? $"Can't commit: {remaining} cabin(s) still need a position — use "
                        + "'!migrate place' / 'cabins migrate place <x> <y>', or 'cabins migrate abort'."
                    : "Can't commit: the shared stack spot is obstructed — pick one with "
                        + "'!migrate place' / 'cabins migrate place <x> <y>', or 'cabins migrate abort'.";
            return false;
        }

        int placedCount;
        if (migration.ToStrategy == CabinStrategy.None)
        {
            // Capacity under None IS the cabin count: freeze the cap at the final visible
            // non-lobby count (same field GameCreatorService freezes at creation), then
            // point every cabin's exit at its own door — the flip players perceive as the
            // migration happening (during staging the old strategy's interceptors kept the
            // old warp targets).
            var visibleCabins = farm
                .buildings.Where(b => b.isCabin && !b.IsInHiddenStack() && !b.IsLobbyOrEditing())
                .ToList();
            options.Data.NoneCabinCount = visibleCabins.Count;
            foreach (var building in visibleCabins)
            {
                building.SetWarpsToFarmCabinDoor();
            }

            placedCount = migration.PlacedCabinIndoorNames.Count;
            message =
                $"Migration committed: strategy is now None with {visibleCabins.Count} cabin(s) "
                + $"placed — capacity is frozen at {visibleCabins.Count} player(s).";
        }
        else
        {
            // → CabinStack: zero building moves; apply the staged stack spot (if one was
            // chosen) and let the per-message interceptors reconcile warps as deltas and
            // introductions flow — the ghost is the existing StackLocation path.
            if (migration.StackSpotOverride.HasValue)
            {
                Data.DefaultCabinLocation = migration.StackSpotOverride;
            }

            placedCount = migration.StackSpotOverride.HasValue ? 1 : 0;
            var spot = GetEffectiveStackSpot(farm, migration).ToPoint();
            message =
                $"Migration committed: strategy is now CabinStack; the shared stack renders at "
                + $"({spot.X},{spot.Y}).";
        }

        // Reconnect note: per-peer presentation (the CabinStack ghost's position, the
        // door-dead dummy's nulled interior) lives only in each peer's intro-message copy,
        // and Netcode cannot heal it live — a NetRef delta resends the full object only on
        // a real reassignment (NetRefBase.WriteDelta, RefDeltaType.Reassigned; MarkDirty
        // alone yields child deltas), and re-sending the farm's location introduction
        // (message 3) replaces the root out from under a peer standing inside one of its
        // structure interiors (readActiveLocation fixes up currentLocation only when it IS
        // the replaced root). The rejoin's fresh intro is the supported convergence point.
        if (OnlineFarmers.CountOthers() > 0)
        {
            message +=
                " Connected players keep seeing the pre-migration cabin layout until they "
                + "reconnect.";
        }

        ApplyStrategyDurably(migration.ToStrategy);

        Data.ActiveMigration = null;
        Data.Write();

        Diagnostics.ModEventLog.Emit(
            "cabin_migration_committed",
            new
            {
                fromStrategy = migration.FromStrategy.ToString(),
                toStrategy = migration.ToStrategy.ToString(),
                placedCount,
            }
        );

        // Make the commit's world half durable now when it's safe: the strategy flip
        // (global options + settings file) is already on disk, but staged positions, the
        // record clear, and the warp pass live in the save and would otherwise persist only
        // at the next game save. SaveNow's policy allows a mid-day save only with no
        // connected farmhands; with players online the next save covers it, and the
        // load-time pre-commit heal (DetectAndApplyStrategySwitch) covers a crash in
        // between.
        if (OnlineFarmers.CountOthers() == 0)
        {
            if (!SaveNow.TrySave(Helper, out var saveError))
            {
                Monitor.Log(
                    $"Could not save after migration commit ({saveError}); the commit persists "
                        + "at the next game save.",
                    LogLevel.Warn
                );
            }
        }
        else
        {
            Monitor.Log(
                "Players are online — the committed migration persists at the next game save.",
                LogLevel.Info
            );
        }

        Monitor.Log(message, LogLevel.Info);
        return true;
    }

    /// <summary>
    /// Aborts the staged migration: moves staged cabins back to the hidden stack (skipping
    /// any the owner has since /cabin-placed — player intent outranks staging) and clears
    /// the record. Trivially safe because staging never destroyed anything.
    /// </summary>
    public bool TryAbortMigration(out string message)
    {
        if (!Game1.hasLoadedGame)
        {
            message = "No game loaded yet.";
            return false;
        }

        var migration = Data.ActiveMigration;
        if (migration == null)
        {
            message = "No staged migration to abort.";
            return false;
        }

        var farm = Game1.getFarm();
        int returned = 0;
        foreach (var name in migration.PlacedCabinIndoorNames)
        {
            var building = FindCabinBuildingByIndoorName(farm, name);
            // The IsInHiddenStack skip covers a staged cabin its owner already sent back via
            // '!cabin reset' — re-SetPositioning it is a no-op that would still count it in
            // cabinsReturned.
            if (building == null || HasSavedPosition(building) || building.IsInHiddenStack())
            {
                continue;
            }

            building.SetPosition(HiddenCabinLocation);
            returned++;
        }

        Data.ActiveMigration = null;
        Data.Write();

        Diagnostics.ModEventLog.Emit(
            "cabin_migration_aborted",
            new
            {
                fromStrategy = migration.FromStrategy.ToString(),
                toStrategy = migration.ToStrategy.ToString(),
                cabinsReturned = returned,
            }
        );
        message =
            migration.ToStrategy == CabinStrategy.None
                ? $"Migration to None aborted — {returned} staged cabin(s) returned to the hidden stack."
                : "Migration to CabinStack aborted — the staged stack position was discarded.";
        Monitor.Log(message, LogLevel.Info);
        return true;
    }
}
