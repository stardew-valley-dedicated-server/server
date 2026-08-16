using JunimoServer.Tests.Clients;
using JunimoServer.Tests.Helpers;
using JunimoServer.Tests.Infrastructure;
using Xunit;

namespace JunimoServer.Tests;

/// <summary>
/// E2E coverage for the staged, admin-driven strategy migration ('cabins migrate' +
/// '!migrate place'). Core properties under test, all asserted via the /cabins snapshot
/// (tests-assert-via-http-api): the old strategy stays live for the whole staging window;
/// placement is validator-gated and non-destructive (an occupied designated spot is skipped
/// and survives); "remaining" is live (a join during staging raises it) and commit refuses
/// while it is nonzero; commit flips strategy + settings file (so it survives /reload);
/// the staging record survives an interim reload and blocks file-driven strategy switches;
/// abort returns staged cabins to the hidden stack.
///
/// Exclusive: every test rewrites the server's world and strategy via /newgame + console
/// migration commands, and the class resets the server (and its settings file) on dispose.
/// </summary>
[TestServer(Isolation = IsolationMode.SharedAssembly, Priority = 90, Exclusive = true)]
public class CabinMigrationTests : TestBase
{
    /// <summary>
    /// Designated cabin spot on the Standard farm used as the deliberately-obstructed
    /// position (observed in prior None-strategy run logs; see cabin-system invariant 9).
    /// The primary player's !cabin-placed cabin occupies it, so auto-place must skip it
    /// and every later pass must leave it untouched.
    /// </summary>
    private const int ObstructedSpotX = 23;
    private const int ObstructedSpotY = 31;

    private bool _needsServerReset;

    public override async ValueTask DisposeAsync()
    {
        if (_needsServerReset && Lease != null)
        {
            try
            {
                try
                {
                    await DisconnectAsync();
                }
                catch (Exception ex)
                {
                    LogWarning(
                        $"Primary disconnect during cleanup failed (may not be connected): {ex.Message}"
                    );
                }

                // DisconnectAsync settles the client only
                // (disconnect-settles-client-not-server): gate on server-side removal so
                // the reset /newgame can't 409 against a still-registered player and
                // needlessly retire a healthy server through the catch below.
                if (!await ServerApi.WaitForAllPlayersRemovedAsync())
                {
                    LogWarning(
                        "Players still registered server-side after disconnect; the reset may 409."
                    );
                }

                // Explicit-everything reset: a committed migration wrote its target
                // strategy into the in-container settings file, and every /newgame
                // persists the created config there too — the reset restores both the
                // world and the file to the pooled configuration in one call.
                await ResetServerToPooledConfigAsync();
            }
            catch (Exception ex)
            {
                // A failed reset leaves a server whose world/settings file no longer match
                // the config hash it is pooled under (a reuser could inherit a
                // None-strategy world whose settings file also says None). Retire it so
                // the pool boots a fresh instance instead.
                LogWarning($"Server reset failed during cleanup ({ex.Message}); retiring server.");
                Lease.Managed.PoisonServer(
                    $"Cleanup reset to pooled config failed: {ex.Message}",
                    ManagedServer.PoisonReasonCode.TestRetiredServer
                );
            }
        }
        _needsServerReset = false;
        await base.DisposeAsync();
    }

    /// <summary>
    /// Full stacked→None staged flow: obstructed designated spot is skipped by auto-place
    /// and survives; a join during staging raises the live remaining count; commit refuses
    /// while remaining > 0; manual placement works via both '!migrate place' (admin chat)
    /// and 'cabins migrate place x y' (console); commit flips the strategy, makes all
    /// cabins visible, and survives a /reload (settings file updated at commit).
    /// </summary>
    [Fact]
    public async Task StagedMigration_StackedToNone_ObstructedSpotManualPlaceCommit()
    {
        LogSection("Staged CabinStack → None migration: obstruction, live remaining, commit");

        var ct = TestCt;
        _needsServerReset = true;
        await CreateNewGameOnServerAsync(farmType: 0, cabinStrategy: "CabinStack");

        var primary = await Farmers.ConnectNewAsync(ct: ct);
        var ownerId = primary.JoinResult.UniqueMultiplayerId;

        // Obstruct the designated spot with the player's own !cabin-placed cabin — the one
        // "building on a spot the admin won't demolish" primitive available to a vanilla
        // client. ClearArea first: new-farm debris would otherwise fail the validator.
        await CabinPlacementHelper.WarpAndClearFootprintAsync(
            GameClient,
            ObstructedSpotX - 1,
            ObstructedSpotY,
            ct
        );
        CabinInfoResponse? obstruction = null;
        var moved = await PollingHelper.WaitUntilAsync(
            WaitName.Polling_CabinPlacement_Moved,
            async () =>
            {
                await GameClient.SendChat("!cabin");
                obstruction = await GetCabinByOwnerAsync(ownerId, ct);
                return (obstruction.TileX, obstruction.TileY) == (ObstructedSpotX, ObstructedSpotY);
            },
            TestTimings.CabinAssignmentTimeout,
            cancellationToken: ct
        );
        Assert.True(
            moved,
            $"!cabin should move the primary's cabin onto the designated spot "
                + $"({ObstructedSpotX},{ObstructedSpotY}) before the migration starts"
        );

        // Pre-clear the two manual-placement footprints (validator-gated placement needs
        // debris-free tiles; a new farm's debris is seed-random).
        await CabinPlacementHelper.WarpAndClearFootprintAsync(GameClient, 40, 18, ct);
        await CabinPlacementHelper.WarpAndClearFootprintAsync(GameClient, 40, 30, ct);

        var start = await ServerApi.RunConsoleCommand(
            "cabins",
            new[] { "migrate", "start", "None" },
            ct
        );
        Assert.True(start?.Success == true, $"cabins migrate start failed: {start?.Error}");

        var staged = await PollingHelper.WaitUntilAsync(
            WaitName.Polling_CabinMigration_Staged,
            async () => (await ServerApi.GetCabins(ct))?.Migration != null,
            TestTimings.CabinAssignmentTimeout,
            cancellationToken: ct
        );
        Assert.True(staged, "/cabins Migration should become non-null after migrate start");

        var during = await ServerApi.GetCabins(ct);
        Assert.NotNull(during);
        Assert.Equal("CabinStack", during.Strategy); // old strategy stays live while staging
        Assert.NotNull(during.Migration);
        Assert.Equal("CabinStack", during.Migration!.FromStrategy);
        Assert.Equal("None", during.Migration.ToStrategy);
        var obstructionDuring = await GetCabinByOwnerAsync(ownerId, ct);
        Assert.True(
            (obstructionDuring.TileX, obstructionDuring.TileY)
                == (ObstructedSpotX, ObstructedSpotY),
            "auto-place must skip the occupied designated spot and leave the obstructing "
                + $"cabin at ({ObstructedSpotX},{ObstructedSpotY}); found it at "
                + $"({obstructionDuring.TileX},{obstructionDuring.TileY})"
        );

        // A join during staging adds a hidden cabin (the joiner claims the spare; the
        // ensure pass rebuilds one) — the live remaining count must reflect it.
        await using var second = await Farmers.ConnectSecondFarmerAsync(ct: ct);
        var remainingGrew = await PollingHelper.WaitUntilAsync(
            WaitName.Polling_CabinMigration_RemainingGrew,
            async () => (await ServerApi.GetCabins(ct))?.Migration?.RemainingCount >= 1,
            TestTimings.CabinAssignmentTimeout,
            cancellationToken: ct
        );
        Assert.True(
            remainingGrew,
            "RemainingCount should be >= 1 after a second farmer joined during staging"
        );

        // Commit must refuse while remaining > 0: the strategy stays live and the record
        // stays active (the refused command runs within a tick; the placements below give
        // it ample time to have landed before we assert).
        var refusedCommit = await ServerApi.RunConsoleCommand(
            "cabins",
            new[] { "migrate", "commit" },
            ct
        );
        Assert.True(
            refusedCommit?.Success == true,
            "commit invocation should succeed (refusal is semantic)"
        );

        // Place the remaining cabins (1 or 2, depending on whether auto-place found a
        // debris-free designated spot for the original spare). First via the admin chat
        // command: the primary stands at (40,30) after the last footprint clear, so
        // '!migrate place' targets (41,30). Resent each poll — the handler reads the
        // server's view of the farmer position, which can lag; a resend after success is
        // refused harmlessly (the spot is occupied by the just-placed cabin).
        var grant = await ServerApi.GrantAdminById(ownerId, ct);
        Assert.True(grant?.Success == true, "GrantAdminById must succeed for !migrate place");

        var chatPlaced = await PollingHelper.WaitUntilAsync(
            WaitName.Polling_CabinMigration_ChatPlaced,
            async () =>
            {
                await GameClient.SendChat("!migrate place");
                var cabins = await ServerApi.GetCabins(ct);
                return cabins?.Cabins.Any(c => (c.TileX, c.TileY) == (41, 30) && !c.IsHidden)
                    == true;
            },
            TestTimings.CabinAssignmentTimeout,
            cancellationToken: ct
        );
        Assert.True(chatPlaced, "'!migrate place' should stage a cabin at (41,30)");

        // Any remainder goes through the console coordinate variant at (41,18).
        var allPlaced = await PollingHelper.WaitUntilAsync(
            WaitName.Polling_CabinMigration_PlacedAll,
            async () =>
            {
                var cabins = await ServerApi.GetCabins(ct);
                if (cabins?.Migration == null)
                {
                    return false;
                }
                if (cabins.Migration.RemainingCount == 0)
                {
                    return true;
                }
                await ServerApi.RunConsoleCommand(
                    "cabins",
                    new[] { "migrate", "place", "41", "18" },
                    ct
                );
                return false;
            },
            TestTimings.CabinAssignmentTimeout,
            cancellationToken: ct
        );
        Assert.True(allPlaced, "all staged placements should complete (RemainingCount == 0)");

        var afterPlacements = await ServerApi.GetCabins(ct);
        Assert.NotNull(afterPlacements);
        Assert.Equal("CabinStack", afterPlacements.Strategy);
        Assert.NotNull(afterPlacements.Migration); // the earlier commit was refused

        // Disconnect everyone before committing: /reload later requires no clients, and a
        // commit with no players online saves the world immediately (SaveNow policy).
        await second.DisconnectAsync();
        await DisconnectAsync();
        var removed = await ServerApi.WaitForPlayersRemovedByIdAsync(
            new[] { ownerId, second.Uid },
            ct: ct
        );
        Assert.True(removed, "both players should be removed server-side before commit");

        var commit = await ServerApi.RunConsoleCommand("cabins", new[] { "migrate", "commit" }, ct);
        Assert.True(commit?.Success == true, $"cabins migrate commit failed: {commit?.Error}");

        var committed = await PollingHelper.WaitUntilAsync(
            WaitName.Polling_CabinMigration_Committed,
            async () =>
            {
                var cabins = await ServerApi.GetCabins(ct);
                return cabins?.Strategy == "None" && cabins.Migration == null;
            },
            TestTimings.CabinAssignmentTimeout,
            cancellationToken: ct
        );
        Assert.True(committed, "commit should flip the strategy to None and clear Migration");

        var afterCommit = await ServerApi.GetCabins(ct);
        Assert.NotNull(afterCommit);
        AssertCommittedNoneLayout(afterCommit, "after commit");

        // Commit wrote the settings file, so the flip must survive an in-process reload
        // (SyncFromSettings reapplies the file; without the write it would revert).
        await ReloadServerAsync();
        var afterReload = await ServerApi.GetCabins(ct);
        Assert.NotNull(afterReload);
        AssertCommittedNoneLayout(afterReload, "after reload");

        await Exceptions.AssertNoExceptionsAsync("after staged CabinStack → None migration");
        Log("Staged migration committed, obstruction preserved, reload kept None");
    }

    /// <summary>
    /// Item-6 live convergence: a CabinStack → None commit heals a CONNECTED peer's door-dead dummy
    /// interior in place (via NetRef MarkReassigned), so the peer sees the migrated world without a
    /// reconnect. The primary moves its cabin out and reconnects so its client renders a door-dead
    /// dummy at the shared stack (HasInterior == false); after the → None commit — while it stays
    /// connected — every cabin in its own farm view becomes enterable (HasInterior == true).
    /// </summary>
    [Fact]
    public async Task StagedMigration_StackedToNone_HealsConnectedPeerDummyInteriorLive()
    {
        LogSection(
            "Staged CabinStack → None migration: connected peer's dummy interior heals live"
        );

        var ct = TestCt;
        _needsServerReset = true;
        await CreateNewGameOnServerAsync(farmType: 0, cabinStrategy: "CabinStack");

        var primary = await Farmers.ConnectNewAsync(ct: ct);
        var ownerId = primary.JoinResult.UniqueMultiplayerId;

        // Pre-clear the manual-placement footprints (validator-gated placement needs debris-free
        // tiles; a new farm's debris is seed-random).
        await CabinPlacementHelper.WarpAndClearFootprintAsync(GameClient, 40, 18, ct);
        await CabinPlacementHelper.WarpAndClearFootprintAsync(GameClient, 40, 30, ct);

        // Move the cabin out of the stack at the standard footprint, then reconnect so the client
        // receives a fresh Farm introduction carrying the door-dead dummy at the shared stack.
        await CabinPlacementHelper.WarpAndClearFootprintAsync(GameClient, ct);
        CabinInfoResponse? movedCabin = null;
        var moved = await PollingHelper.WaitUntilAsync(
            WaitName.Polling_CabinStrategy_OurCabinAssigned,
            async () =>
            {
                await GameClient.SendChat("!cabin");
                movedCabin = await GetCabinByOwnerAsync(ownerId, ct);
                return !movedCabin.IsHidden;
            },
            TestTimings.CabinAssignmentTimeout,
            cancellationToken: ct
        );
        Assert.True(moved, "the primary's cabin should move out of the stack via !cabin");
        var movedTile = (movedCabin!.TileX, movedCabin.TileY);

        await Farmers.DisconnectAndWaitForSlotAsync(ownerId, primary.FarmerName, ct);
        await Farmers.ReconnectAsync(primary.FarmerName, ct: ct);

        // Pre-condition: the client renders a door-dead dummy (HasInterior == false) alongside its
        // own enterable moved cabin — the nulled interior the commit must heal.
        var sawDummy = await PollingHelper.WaitUntilAsync(
            WaitName.Polling_DummyCabin_VisibleInClientFarm,
            async () =>
            {
                var view = await GameClient.Actions.GetFarmBuildings(ct);
                if (view?.Success != true)
                {
                    return false;
                }
                var visible = view.Cabins.Where(c => c.TileX >= 0).ToList();
                return visible.Any(c => !c.HasInterior)
                    && visible.Any(c => (c.TileX, c.TileY) == movedTile && c.HasInterior);
            },
            TestTimings.CabinAssignmentTimeout,
            cancellationToken: ct
        );
        Assert.True(
            sawDummy,
            "the client should render a door-dead dummy before the migration commit"
        );

        // Stage and complete the CabinStack → None migration (the peer stays connected throughout).
        var start = await ServerApi.RunConsoleCommand(
            "cabins",
            new[] { "migrate", "start", "None" },
            ct
        );
        Assert.True(start?.Success == true, $"cabins migrate start failed: {start?.Error}");
        await PollingHelper.WaitUntilAsync(
            WaitName.Polling_CabinMigration_Staged,
            async () => (await ServerApi.GetCabins(ct))?.Migration != null,
            TestTimings.CabinAssignmentTimeout,
            cancellationToken: ct
        );

        var allPlaced = await PollingHelper.WaitUntilAsync(
            WaitName.Polling_CabinMigration_PlacedAll,
            async () =>
            {
                var cabins = await ServerApi.GetCabins(ct);
                if (cabins?.Migration == null)
                {
                    return false;
                }
                if (cabins.Migration.RemainingCount == 0)
                {
                    return true;
                }
                await ServerApi.RunConsoleCommand(
                    "cabins",
                    new[] { "migrate", "place", "41", "18" },
                    ct
                );
                await ServerApi.RunConsoleCommand(
                    "cabins",
                    new[] { "migrate", "place", "41", "30" },
                    ct
                );
                return false;
            },
            TestTimings.CabinAssignmentTimeout,
            cancellationToken: ct
        );
        Assert.True(allPlaced, "all staged placements should complete (RemainingCount == 0)");

        // Commit WITH the peer connected — the heal fires (OnlineFarmers.CountOthers() > 0).
        var commit = await ServerApi.RunConsoleCommand("cabins", new[] { "migrate", "commit" }, ct);
        Assert.True(commit?.Success == true, $"cabins migrate commit failed: {commit?.Error}");
        await PollingHelper.WaitUntilAsync(
            WaitName.Polling_CabinMigration_Committed,
            async () =>
            {
                var cabins = await ServerApi.GetCabins(ct);
                return cabins?.Strategy == "None" && cabins.Migration == null;
            },
            TestTimings.CabinAssignmentTimeout,
            cancellationToken: ct
        );

        // The live heal: without any reconnect, every cabin in the peer's own farm view is now
        // enterable — the door-dead dummy's interior was resent via indoors.MarkReassigned().
        var healed = await PollingHelper.WaitUntilAsync(
            WaitName.Polling_CabinDummyInterior_HealedLive,
            async () =>
            {
                var view = await GameClient.Actions.GetFarmBuildings(ct);
                if (view?.Success != true)
                {
                    return false;
                }
                var visible = view.Cabins.Where(c => c.TileX >= 0).ToList();
                return visible.Count > 1 && visible.All(c => c.HasInterior);
            },
            TestTimings.NetworkSyncTimeout,
            cancellationToken: ct
        );
        Assert.True(
            healed,
            "after the → None commit the connected peer's door-dead dummy interior should heal live "
                + "(all cabins in its farm view enterable) without a reconnect"
        );

        await DisconnectAsync();
        var removed = await ServerApi.WaitForPlayerRemovedByIdAsync(ownerId, ct: ct);
        Assert.True(removed, "the player should be removed server-side before the class reset");
        await Exceptions.AssertNoExceptionsAsync("after CabinStack → None live interior heal");
    }

    /// <summary>
    /// Shared layout assertions for the committed stacked→None world: strategy None, no
    /// staging record, exactly 3 cabins (primary's + the joiner's + the rebuilt spare),
    /// all visible, with the obstructing cabin still on its designated spot.
    /// </summary>
    private void AssertCommittedNoneLayout(CabinsResponse cabins, string when)
    {
        Assert.Equal("None", cabins.Strategy);
        Assert.Null(cabins.Migration);
        Assert.True(
            cabins.TotalCount == 3,
            $"expected exactly 3 cabins {when} (primary + joiner + rebuilt spare); "
                + $"got {cabins.TotalCount}"
        );
        Assert.True(
            cabins.Cabins.All(c => !c.IsHidden),
            $"every cabin must be visible {when} — a hidden cabin means a placement was lost"
        );
        Assert.True(
            cabins.Cabins.Any(c => (c.TileX, c.TileY) == (ObstructedSpotX, ObstructedSpotY)),
            $"the obstructing cabin must still sit at ({ObstructedSpotX},{ObstructedSpotY}) "
                + $"{when} — staging must never move or bulldoze it"
        );
    }

    /// <summary>
    /// Staging-window integrity: a pure-hide direction is refused by start (no record); the
    /// staging record and a staged placement survive a save + reload; a settings-file
    /// strategy switch during staging is refused (strategy reverts, record stays); abort
    /// returns the staged cabin to the hidden stack and clears the record.
    /// </summary>
    [Fact]
    public async Task StagedMigration_RecordSurvivesReload_FileSwitchRefused_AbortRestores()
    {
        LogSection("Staged migration: reload survival, file-switch refusal, abort");

        var ct = TestCt;
        _needsServerReset = true;
        await CreateNewGameOnServerAsync(farmType: 0, cabinStrategy: "CabinStack");

        var primary = await Farmers.ConnectNewAsync(ct: ct);
        var ownerId = primary.JoinResult.UniqueMultiplayerId;

        // Clear a footprint for the deterministic manual placement below.
        await CabinPlacementHelper.WarpAndClearFootprintAsync(GameClient, 40, 18, ct);

        // A pure-hide direction must not create a record. Proof is the next start: if the
        // refused start had staged anything, 'start None' would be rejected as
        // already-staged and Migration.ToStrategy would read FarmhouseStack.
        var refusedStart = await ServerApi.RunConsoleCommand(
            "cabins",
            new[] { "migrate", "start", "FarmhouseStack" },
            ct
        );
        Assert.True(refusedStart?.Success == true);

        var start = await ServerApi.RunConsoleCommand(
            "cabins",
            new[] { "migrate", "start", "None" },
            ct
        );
        Assert.True(start?.Success == true, $"cabins migrate start failed: {start?.Error}");

        var staged = await PollingHelper.WaitUntilAsync(
            WaitName.Polling_CabinMigration_Staged,
            async () => (await ServerApi.GetCabins(ct))?.Migration != null,
            TestTimings.CabinAssignmentTimeout,
            cancellationToken: ct
        );
        Assert.True(staged, "/cabins Migration should become non-null after migrate start");
        var duringStaging = await ServerApi.GetCabins(ct);
        Assert.Equal("None", duringStaging!.Migration!.ToStrategy); // start FarmhouseStack was refused

        // One deterministic staged placement at the cleared footprint. Re-issued each
        // poll: the client-side footprint clear replicates to the server with a lag, so
        // the first attempt can be refused ("blocked by terrain or object") — the same
        // lag the !cabin tests absorb by resending. A re-issue after success is refused
        // harmlessly (the spot is occupied by the just-placed cabin).
        var placedVisible = await PollingHelper.WaitUntilAsync(
            WaitName.Polling_CabinMigration_PlacedAll,
            async () =>
            {
                await ServerApi.RunConsoleCommand(
                    "cabins",
                    new[] { "migrate", "place", "41", "18" },
                    ct
                );
                return (await ServerApi.GetCabins(ct))?.Cabins.Any(StagedCabinAt41x18) == true;
            },
            TestTimings.CabinAssignmentTimeout,
            cancellationToken: ct
        );
        Assert.True(placedVisible, "a staged cabin should appear at (41,18) after place");

        // Persist the staging (record + moved building live in the save), then reload with
        // the settings file switched to FarmhouseStack: the file-driven change must be
        // refused while the record is active, and the staged cabin must survive the reload
        // (the MoveToStack/migration sweeps exempt migration-placed cabins).
        await SleepToSaveAsync(ct);
        await Farmers.DisconnectAndWaitForSlotAsync(ownerId, primary.FarmerName, ct);

        try
        {
            await ServerSettingsFileHelper.SwitchCabinStrategyAsync(Server, "FarmhouseStack", ct);
            await ReloadServerAsync();

            var afterReload = await ServerApi.GetCabins(ct);
            Assert.NotNull(afterReload);
            Assert.True(
                afterReload.Strategy == "CabinStack",
                "a file-driven strategy switch during staging must be refused; strategy "
                    + $"reverted to CabinStack, got {afterReload.Strategy}"
            );
            Assert.NotNull(afterReload.Migration); // record survived the reload
            Assert.True(
                afterReload.Cabins.Any(StagedCabinAt41x18),
                "the staged cabin at (41,18) must survive the interim reload un-swept"
            );
        }
        finally
        {
            // Restore the file so later reloads don't re-trigger the refusal (and, after
            // the abort below, don't actually migrate to FarmhouseStack).
            await ServerSettingsFileHelper.SwitchCabinStrategyAsync(Server, "CabinStack", ct);
        }

        var abort = await ServerApi.RunConsoleCommand("cabins", new[] { "migrate", "abort" }, ct);
        Assert.True(abort?.Success == true, $"cabins migrate abort failed: {abort?.Error}");

        var aborted = await PollingHelper.WaitUntilAsync(
            WaitName.Polling_CabinMigration_Aborted,
            async () =>
            {
                var cabins = await ServerApi.GetCabins(ct);
                return cabins?.Migration == null && cabins?.Cabins.Any(StagedCabinAt41x18) == false;
            },
            TestTimings.CabinAssignmentTimeout,
            cancellationToken: ct
        );
        Assert.True(
            aborted,
            "abort should clear the record and return the staged cabin at (41,18) to the "
                + "hidden stack"
        );
        var afterAbort = await ServerApi.GetCabins(ct);
        Assert.Equal("CabinStack", afterAbort!.Strategy);

        await Exceptions.AssertNoExceptionsAsync("after staged-migration abort");
        Log("Record survived reload, file switch refused, abort restored the stack");
    }

    private static bool StagedCabinAt41x18(CabinInfoResponse c) =>
        (c.TileX, c.TileY) == (41, 18) && !c.IsHidden;

    /// <summary>
    /// FarmhouseStack → CabinStack staged flow: the single materialization is the shared
    /// stack ghost, so staging is a stack-spot choice ('cabins migrate place') rather than
    /// building moves. Commit flips the strategy, keeps the stacked cabins hidden, and
    /// survives a /reload.
    /// </summary>
    [Fact]
    public async Task StagedMigration_FarmhouseStackToCabinStack_SpotOverrideAndCommit()
    {
        LogSection("Staged FarmhouseStack → CabinStack migration: ghost spot + commit");

        var ct = TestCt;
        _needsServerReset = true;
        await CreateNewGameOnServerAsync(farmType: 0, cabinStrategy: "FarmhouseStack");

        var primary = await Farmers.ConnectNewAsync(ct: ct);
        var ownerId = primary.JoinResult.UniqueMultiplayerId;

        // Clear a footprint so the stack-spot override always validates (the map-default
        // spot's state is seed-random debris — deliberately not asserted).
        await CabinPlacementHelper.WarpAndClearFootprintAsync(GameClient, 40, 18, ct);

        var start = await ServerApi.RunConsoleCommand(
            "cabins",
            new[] { "migrate", "start", "CabinStack" },
            ct
        );
        Assert.True(start?.Success == true, $"cabins migrate start failed: {start?.Error}");

        var staged = await PollingHelper.WaitUntilAsync(
            WaitName.Polling_CabinMigration_Staged,
            async () => (await ServerApi.GetCabins(ct))?.Migration != null,
            TestTimings.CabinAssignmentTimeout,
            cancellationToken: ct
        );
        Assert.True(staged, "/cabins Migration should become non-null after migrate start");

        var during = await ServerApi.GetCabins(ct);
        Assert.NotNull(during);
        Assert.Equal("FarmhouseStack", during.Strategy); // old strategy live during staging
        Assert.Equal("FarmhouseStack", during.Migration!.FromStrategy);
        Assert.Equal("CabinStack", during.Migration.ToStrategy);

        // Choose the ghost's spot explicitly (works whether or not the map default was
        // clear) and wait for the live remaining to resolve to 0. Re-issued each poll for
        // the same clear-replication lag as above; re-setting the override to the same
        // validated spot is idempotent.
        var resolved = await PollingHelper.WaitUntilAsync(
            WaitName.Polling_CabinMigration_PlacedAll,
            async () =>
            {
                await ServerApi.RunConsoleCommand(
                    "cabins",
                    new[] { "migrate", "place", "41", "18" },
                    ct
                );
                return (await ServerApi.GetCabins(ct))?.Migration?.RemainingCount == 0;
            },
            TestTimings.CabinAssignmentTimeout,
            cancellationToken: ct
        );
        Assert.True(resolved, "the stack spot should be resolved (RemainingCount == 0)");

        await Farmers.DisconnectAndWaitForSlotAsync(ownerId, primary.FarmerName, ct);

        var commit = await ServerApi.RunConsoleCommand("cabins", new[] { "migrate", "commit" }, ct);
        Assert.True(commit?.Success == true, $"cabins migrate commit failed: {commit?.Error}");

        var committed = await PollingHelper.WaitUntilAsync(
            WaitName.Polling_CabinMigration_Committed,
            async () =>
            {
                var cabins = await ServerApi.GetCabins(ct);
                return cabins?.Strategy == "CabinStack" && cabins.Migration == null;
            },
            TestTimings.CabinAssignmentTimeout,
            cancellationToken: ct
        );
        Assert.True(committed, "commit should flip the strategy to CabinStack and clear Migration");

        var afterCommit = await ServerApi.GetCabins(ct);
        Assert.NotNull(afterCommit);
        Assert.True(
            afterCommit.Cabins.Where(c => c.Type == "CabinStack").All(c => c.IsHidden),
            "stacked player cabins must stay hidden after the FarmhouseStack → CabinStack "
                + "commit — the ghost is per-peer message rewriting, not a real move"
        );

        await ReloadServerAsync();
        var afterReload = await ServerApi.GetCabins(ct);
        Assert.NotNull(afterReload);
        Assert.Equal("CabinStack", afterReload.Strategy);
        Assert.Null(afterReload.Migration);

        await Exceptions.AssertNoExceptionsAsync(
            "after staged FarmhouseStack → CabinStack migration"
        );
        Log("FarmhouseStack → CabinStack committed with admin-chosen stack spot");
    }

    /// <summary>
    /// Direction-matrix enforcement for the FarmhouseStack → CabinStack leg:
    /// CabinStack → FarmhouseStack via the settings file is a pure hide (allowed);
    /// switching back to CabinStack via the file is rejected at load (the shared stack
    /// ghost would materialize on a farm spot players may have developed) with the
    /// persisted strategy reverted; the staged 'cabins migrate' flow then commits the
    /// same direction.
    /// </summary>
    [Fact]
    public async Task StrategySwitch_FarmhouseStackToCabinStack_FileRejected_MigrateCommits()
    {
        LogSection("Direction matrix: FH→CS file switch rejected, staged migrate commits");

        var ct = TestCt;
        _needsServerReset = true;
        await CreateNewGameOnServerAsync(farmType: 0, cabinStrategy: "CabinStack");

        // Leg 1 — CabinStack → FarmhouseStack via file: pure hide, allowed.
        await ServerSettingsFileHelper.SwitchCabinStrategyAsync(Server, "FarmhouseStack", ct);
        await ReloadServerAsync();
        var afterHide = await ServerApi.GetCabins(ct);
        Assert.NotNull(afterHide);
        Assert.Equal("FarmhouseStack", afterHide.Strategy);

        // Leg 2 — FarmhouseStack → CabinStack via file: rejected, strategy reverts (the
        // fresh game parked ≥1 cabin in the hidden stack, so the materialization guard
        // trips). Restore the file afterwards so later reloads don't re-warn/re-revert.
        await ServerSettingsFileHelper.SwitchCabinStrategyAsync(Server, "CabinStack", ct);
        await ReloadServerAsync();
        var afterRejected = await ServerApi.GetCabins(ct);
        Assert.NotNull(afterRejected);
        Assert.True(
            afterRejected.Strategy == "FarmhouseStack",
            "a FarmhouseStack → CabinStack settings-file switch must be rejected at load "
                + $"(strategy reverted to FarmhouseStack); got {afterRejected.Strategy}"
        );
        await ServerSettingsFileHelper.SwitchCabinStrategyAsync(Server, "FarmhouseStack", ct);

        // Leg 3 — the staged flow commits the same direction. A client clears a footprint
        // so the stack-spot choice always validates (the map default spot's debris is
        // seed-random).
        var primary = await Farmers.ConnectNewAsync(ct: ct);
        var ownerId = primary.JoinResult.UniqueMultiplayerId;
        await CabinPlacementHelper.WarpAndClearFootprintAsync(GameClient, 40, 18, ct);

        var start = await ServerApi.RunConsoleCommand(
            "cabins",
            new[] { "migrate", "start", "CabinStack" },
            ct
        );
        Assert.True(start?.Success == true, $"cabins migrate start failed: {start?.Error}");

        var resolved = await PollingHelper.WaitUntilAsync(
            WaitName.Polling_CabinMigration_PlacedAll,
            async () =>
            {
                await ServerApi.RunConsoleCommand(
                    "cabins",
                    new[] { "migrate", "place", "41", "18" },
                    ct
                );
                return (await ServerApi.GetCabins(ct))?.Migration?.RemainingCount == 0;
            },
            TestTimings.CabinAssignmentTimeout,
            cancellationToken: ct
        );
        Assert.True(resolved, "the stack spot should be resolved (RemainingCount == 0)");

        await Farmers.DisconnectAndWaitForSlotAsync(ownerId, primary.FarmerName, ct);

        var commit = await ServerApi.RunConsoleCommand("cabins", new[] { "migrate", "commit" }, ct);
        Assert.True(commit?.Success == true, $"cabins migrate commit failed: {commit?.Error}");

        var committed = await PollingHelper.WaitUntilAsync(
            WaitName.Polling_CabinMigration_Committed,
            async () =>
            {
                var cabins = await ServerApi.GetCabins(ct);
                return cabins?.Strategy == "CabinStack" && cabins.Migration == null;
            },
            TestTimings.CabinAssignmentTimeout,
            cancellationToken: ct
        );
        Assert.True(
            committed,
            "the staged FarmhouseStack → CabinStack migration should commit after the file "
                + "path was rejected"
        );

        await Exceptions.AssertNoExceptionsAsync("after the FH→CS direction-matrix scenario");
        Log("File switch rejected both ways; staged migrate committed FH→CS");
    }

    /// <summary>
    /// The stack-spot read/set surface (outside any staged migration): /cabins exposes the
    /// CabinStack shared spot (map default, no override, on a fresh game), and
    /// 'cabins stackspot &lt;x&gt; &lt;y&gt;' (validator-gated) sets the persisted override,
    /// reflected on /cabins as the E2E-visible surface of the command.
    /// </summary>
    [Fact]
    public async Task StackSpot_SetViaConsole_ReflectedOnCabinsEndpoint()
    {
        LogSection("Stack-spot read/set: /cabins surface + 'cabins stackspot'");

        var ct = TestCt;
        _needsServerReset = true;
        await CreateNewGameOnServerAsync(farmType: 0, cabinStrategy: "CabinStack");

        var before = await ServerApi.GetCabins(ct);
        Assert.NotNull(before);
        Assert.NotNull(before.StackSpot);
        Assert.False(
            before.StackSpot!.IsOverride,
            "a fresh CabinStack game must resolve the map-default stack spot (no override)"
        );

        // A client clears a footprint so the set always validates (debris is seed-random).
        var primary = await Farmers.ConnectNewAsync(ct: ct);
        var ownerId = primary.JoinResult.UniqueMultiplayerId;
        await CabinPlacementHelper.WarpAndClearFootprintAsync(GameClient, 40, 18, ct);

        // Re-issued per poll for the same clear-replication lag the migrate tests absorb;
        // re-setting the same validated spot is idempotent.
        var set = await PollingHelper.WaitUntilAsync(
            WaitName.Polling_CabinStackSpot_Set,
            async () =>
            {
                await ServerApi.RunConsoleCommand("cabins", new[] { "stackspot", "41", "18" }, ct);
                var cabins = await ServerApi.GetCabins(ct);
                return cabins?.StackSpot is { TileX: 41, TileY: 18, IsOverride: true };
            },
            TestTimings.CabinAssignmentTimeout,
            cancellationToken: ct
        );
        Assert.True(
            set,
            "'cabins stackspot 41 18' should set the override and surface it on /cabins"
        );

        await Farmers.DisconnectAndWaitForSlotAsync(ownerId, primary.FarmerName, ct);
        await Exceptions.AssertNoExceptionsAsync("after stack-spot set via console");
        Log("Stack spot override set and surfaced on /cabins");
    }

    #region Helpers

    private async Task<CabinInfoResponse> GetCabinByOwnerAsync(long ownerId, CancellationToken ct)
    {
        var cabins = await ServerApi.GetCabins(ct);
        Assert.NotNull(cabins);
        var ours = cabins.Cabins.FirstOrDefault(c => c.OwnerId == ownerId);
        Assert.NotNull(ours);
        return ours;
    }

    #endregion
}
