using JunimoServer.Tests.Clients;
using JunimoServer.Tests.Containers;
using JunimoServer.Tests.Helpers;
using JunimoServer.Tests.Infrastructure;
using Xunit;

namespace JunimoServer.Tests;

/// <summary>
/// Regression tests for issue #64 — a cabin moved via /cabin must keep its position
/// across a save+reload. The bug lived in the save-load bulk movers
/// (CabinManagerService.SyncExistingCabins / MigrateCabins), which swept every
/// visible cabin back into the hidden stack on OnSaveLoaded with no exemption for an
/// intentionally-placed cabin.
///
/// These tests exercise the real persistence path: move a cabin, sleep to write the
/// save, then reload the world in-process via POST /reload (which re-fires
/// OnSaveLoaded). A plain assertion after /cabin without a reload would pass even with
/// the bug present, so every persistence test reloads.
///
/// Exclusive + a fresh game per test (like CabinStrategyNoneTests) so the farm starts
/// clean and assertions can be scoped to our single farmer.
/// </summary>
[TestServer(
    Isolation = IsolationMode.SharedAssembly,
    Exclusive = true,
    ExistingCabinBehavior = "MoveToStack"
)]
public class CabinPositionPersistenceTests : TestBase
{
    public CabinPositionPersistenceTests() { }

    public override async ValueTask DisposeAsync()
    {
        // Disconnect the primary so the next test body's own /newgame doesn't 409.
        // Most tests in this class disconnect mid-body, but a few (two-player and the
        // dummy reconnect tests) leave the primary connected. No reset /newgame is
        // needed: this class's config hash (ExistingCabinBehavior=MoveToStack) is
        // unique to it, so no sibling reuses its server, and every body opens with
        // its own CreateNewGameOnServerAsync that rebuilds the world from scratch.
        if (Lease != null)
        {
            // Tolerant: a no-op throw when already at title must not block cleanup.
            try
            {
                await DisconnectAsync();
            }
            catch (Exception ex)
            {
                LogWarning(
                    $"Primary disconnect during cleanup failed (may already be at title): {ex.Message}"
                );
            }
        }
        await base.DisposeAsync();
    }

    /// <summary>
    /// Core bug (#64), merge gate: a /cabin-placed cabin under MoveToStack keeps its
    /// position after a save+reload instead of being swept back to the hidden stack.
    /// </summary>
    [Fact]
    public async Task MoveToStack_PlacedCabinSurvivesReload()
    {
        var ct = TestCt;
        await CreateNewGameOnServerAsync(farmType: 0, cabinStrategy: "CabinStack");

        var client = await Farmers.ConnectNewAsync(ct: ct);
        var ownerId = client.JoinResult.UniqueMultiplayerId;

        var movedTile = await MoveCabinViaCommandAsync(ownerId, ct);
        Log($"Cabin placed at ({movedTile.X},{movedTile.Y}) for '{client.FarmerName}'");

        await SleepToSaveAsync(ct);
        // Disconnect before reload: /reload returns 409 while a client is connected.
        await Farmers.DisconnectAndWaitForPersistenceAsync(client.FarmerName, ct);
        await ReloadServerAsync();

        var afterReload = await GetOurCabinAsync(ownerId, ct);
        Assert.False(
            afterReload.IsHidden,
            $"Cabin reverted to hidden stack after reload (at {afterReload.TileX},{afterReload.TileY})"
        );
        Assert.Equal(movedTile, (afterReload.TileX, afterReload.TileY));
        Log($"Cabin survived reload at ({afterReload.TileX},{afterReload.TileY})");
    }

    /// <summary>
    /// Over-fix guard: with NO /cabin move, an unclaimed visible cabin is still swept
    /// into the hidden stack on reload under MoveToStack. Proves the HasSavedPosition
    /// exemption didn't break the sweep's real job.
    /// </summary>
    [Fact]
    public async Task MoveToStack_UnclaimedCabinSweptOnReload()
    {
        var ct = TestCt;
        // None strategy starts cabins at real, visible map positions. After switching
        // to CabinStack + MoveToStack and reloading, those unclaimed cabins (no /cabin
        // intent) must be pulled into the hidden stack.
        await CreateNewGameOnServerAsync(farmType: 0, cabinStrategy: "None", startingCabins: 2);

        var before = await ServerApi.GetCabins(ct);
        Assert.NotNull(before);
        var visibleUnclaimed = before.Cabins.Where(c => !c.IsHidden && !c.IsAssigned).ToList();
        Assert.True(
            visibleUnclaimed.Count > 0,
            "Expected at least one visible unclaimed cabin under None strategy"
        );
        Log($"Before reload: {visibleUnclaimed.Count} visible unclaimed cabin(s)");

        await SwitchCabinStrategyAsync("CabinStack", ct);
        await ReloadServerAsync();

        var after = await ServerApi.GetCabins(ct);
        Assert.NotNull(after);
        var stillVisibleUnclaimed = after.Cabins.Count(c =>
            !c.IsHidden && !c.IsAssigned && c.Type != "Lobby"
        );
        Assert.Equal(0, stillVisibleUnclaimed);
        Log(
            $"After reload: unclaimed cabins swept to hidden stack ({after.Cabins.Count(c => c.IsHidden)} hidden)"
        );
    }

    /// <summary>
    /// Second bug path: the strategy-switch migration (MigrateCabins, None→CabinStack)
    /// must also respect a /cabin-placed cabin.
    /// </summary>
    [Fact]
    public async Task StrategySwitch_NoneToCabinStack_PlacedCabinSurvives()
    {
        var ct = TestCt;
        await CreateNewGameOnServerAsync(farmType: 0, cabinStrategy: "None");

        var client = await Farmers.ConnectNewAsync(ct: ct);
        var ownerId = client.JoinResult.UniqueMultiplayerId;

        var movedTile = await MoveCabinViaCommandAsync(ownerId, ct);
        Log($"Cabin placed at ({movedTile.X},{movedTile.Y}) under None for '{client.FarmerName}'");

        await SleepToSaveAsync(ct);
        // Disconnect before reload: /reload returns 409 while a client is connected.
        await Farmers.DisconnectAndWaitForPersistenceAsync(client.FarmerName, ct);
        await SwitchCabinStrategyAsync("CabinStack", ct);
        await ReloadServerAsync();

        var afterReload = await GetOurCabinAsync(ownerId, ct);
        Assert.False(
            afterReload.IsHidden,
            $"Cabin swept by None→CabinStack migration (at {afterReload.TileX},{afterReload.TileY})"
        );
        Assert.Equal(movedTile, (afterReload.TileX, afterReload.TileY));
        Log($"Cabin survived strategy switch at ({afterReload.TileX},{afterReload.TileY})");
    }

    /// <summary>
    /// Deleting a farmhand clears its saved-position intent. After !cabin, the owner's
    /// id appears in /cabins SavedPositionPlayerIds; after the farmhand is deleted, it
    /// is gone (ExecuteFarmhandDeletion removes PlayerCabinPositions[ownerId]). No
    /// reload needed — the intent map is observable directly.
    /// </summary>
    [Fact]
    public async Task Deletion_ClearsSavedPositionIntent()
    {
        var ct = TestCt;
        await CreateNewGameOnServerAsync(farmType: 0, cabinStrategy: "CabinStack");

        var client = await Farmers.ConnectNewAsync(ct: ct);
        var ownerId = client.JoinResult.UniqueMultiplayerId;

        await MoveCabinViaCommandAsync(ownerId, ct);

        // Intent is recorded for this owner.
        var withIntent = await ServerApi.GetCabins(ct);
        Assert.NotNull(withIntent);
        Assert.Contains(ownerId, withIntent.SavedPositionPlayerIds);
        Log($"Intent recorded for owner {ownerId}");

        // Delete the farmhand; this must also clear PlayerCabinPositions[ownerId].
        await Farmers.DisconnectAndWaitForPersistenceAsync(client.FarmerName, ct);
        var deleteResult = await ServerApi.WaitForFarmhandDeletedByNameAsync(
            client.FarmerName,
            ct: ct
        );
        Assert.True(
            deleteResult?.Success,
            $"Delete should succeed: {deleteResult?.Error ?? "timeout"}"
        );
        Farmers.CreatedFarmers.RemoveAll(f => f.Uid == ownerId);

        // /cabins is snapshot-backed, so the cleared intent can lag the deletion by a
        // snapshot tick. Poll until the owner is gone instead of asserting immediately.
        CabinsResponse? afterDelete = null;
        var cleared = await PollingHelper.WaitUntilAsync(
            WaitName.Polling_CabinStrategy_FarmerDeletionReflected,
            async () =>
            {
                afterDelete = await ServerApi.GetCabins(ct);
                return afterDelete != null && !afterDelete.SavedPositionPlayerIds.Contains(ownerId);
            },
            TestTimings.CabinAssignmentTimeout,
            cancellationToken: ct
        );

        Assert.True(cleared, $"Saved-position intent was not cleared for deleted owner {ownerId}");
        Log($"Intent cleared for deleted owner {ownerId}");
    }

    /// <summary>
    /// !cabin reset undoes a placement: the cabin returns to the hidden stack and the
    /// saved-position intent is cleared. The reset must survive a save+reload — under
    /// MoveToStack the now-unsaved cabin stays hidden (it isn't resurrected), confirming
    /// the intent really was cleared rather than just the building moved.
    /// </summary>
    [Fact]
    public async Task ResetCabin_ReturnsCabinToStackAndClearsIntent()
    {
        var ct = TestCt;
        await CreateNewGameOnServerAsync(farmType: 0, cabinStrategy: "CabinStack");

        var client = await Farmers.ConnectNewAsync(ct: ct);
        var ownerId = client.JoinResult.UniqueMultiplayerId;

        var movedTile = await MoveCabinViaCommandAsync(ownerId, ct);
        Log($"Cabin placed at ({movedTile.X},{movedTile.Y}) for '{client.FarmerName}'");

        var withIntent = await ServerApi.GetCabins(ct);
        Assert.NotNull(withIntent);
        Assert.Contains(ownerId, withIntent.SavedPositionPlayerIds);

        // Resend each poll: /cabins is snapshot-backed, so the hide can lag the reset by a
        // tick. Resending !cabin reset is idempotent — once hidden it just replies
        // "nothing to reset".
        CabinInfoResponse? afterReset = null;
        CabinsResponse? snapshot = null;
        var reset = await PollingHelper.WaitUntilAsync(
            WaitName.Polling_CabinReset_CabinHidden,
            async () =>
            {
                await GameClient.SendChat("!cabin reset");
                snapshot = await ServerApi.GetCabins(ct);
                afterReset = snapshot?.Cabins.FirstOrDefault(c => c.OwnerId == ownerId);
                return afterReset?.IsHidden == true
                    && snapshot?.SavedPositionPlayerIds.Contains(ownerId) == false;
            },
            TestTimings.CabinAssignmentTimeout,
            cancellationToken: ct
        );

        Assert.True(reset, "Cabin did not return to the hidden stack after !cabin reset");
        Log($"Cabin reset to hidden stack; intent cleared for owner {ownerId}");

        await SleepToSaveAsync(ct);
        // Disconnect before reload: /reload returns 409 while a client is connected.
        await Farmers.DisconnectAndWaitForPersistenceAsync(client.FarmerName, ct);
        await ReloadServerAsync();

        var afterReload = await GetOurCabinAsync(ownerId, ct);
        Assert.True(
            afterReload.IsHidden,
            $"Cabin resurfaced after reload (at {afterReload.TileX},{afterReload.TileY}); reset did not survive the save"
        );
        Log("Cabin stayed hidden across reload after reset");
    }

    /// <summary>
    /// Same-pass sweep selectivity: in a single None→CabinStack migration, A's /cabin-placed
    /// cabin (saved intent) must survive while B's untouched cabin (no intent) is swept to the
    /// hidden stack. The existing reload tests prove each direction alone; this proves the
    /// HasSavedPosition filter is selective *within one pass* — catching filter inversion (A
    /// swept, B kept) and owner-id resolution returning the wrong farmer's key.
    /// </summary>
    [Fact]
    public async Task SameSweep_KeepsPlacedCabin_SweepsUntouchedCabin()
    {
        var ct = TestCt;
        await CreateNewGameOnServerAsync(farmType: 0, cabinStrategy: "None", startingCabins: 2);

        var clientA = await Farmers.ConnectNewAsync(ct: ct);
        var ownerIdA = clientA.JoinResult.UniqueMultiplayerId;

        await using var farmerB = await Farmers.ConnectSecondFarmerAsync(ct: ct);
        // Wait for B's farmhand to be customized so its cabin claim (owner UniqueMultiplayerID)
        // exists before the sweep resolves HasSavedPosition by owner id.
        var bCustomized = await ServerApi.WaitForFarmhandByNameAsync(
            farmerB.FarmerName,
            requireCustomized: true,
            ct: ct
        );
        Assert.True(bCustomized, $"Farmer B '{farmerB.FarmerName}' never customized");

        // A places via !cabin (records intent); B does nothing.
        var aTile = await MoveCabinViaCommandAsync(ownerIdA, ct);
        Log($"A placed cabin at ({aTile.X},{aTile.Y}); B untouched");

        // Disconnect B before sleeping: an idle connected farmer blocks the day-end ready
        // check. B's claim persists into farmhandData on disconnect and is written by the save.
        await farmerB.DisconnectAsync();
        var bPersisted = await ServerApi.WaitForFarmhandByNameAsync(
            farmerB.FarmerName,
            requireCustomized: true,
            ct: ct
        );
        Assert.True(bPersisted, $"Farmer B '{farmerB.FarmerName}' not persisted after disconnect");

        await SleepToSaveAsync(ct);
        // Disconnect A before reload: /reload returns 409 while a client is connected.
        await Farmers.DisconnectAndWaitForPersistenceAsync(clientA.FarmerName, ct);

        await SwitchCabinStrategyAsync("CabinStack", ct);
        await ReloadServerAsync();

        // A's saved cabin survived at its tile; B's untouched cabin was swept to the stack.
        // Poll, don't read once: /reload resolves on master finality, but /cabins is served
        // from a snapshot refreshed only on the ~1s UpdateTicked timer (ApiService
        // TakeGameStateSnapshot), which /reload does not force. So the swept state can lag the
        // reload by a snapshot tick — same reason Deletion_ClearsSavedPositionIntent polls.
        CabinInfoResponse? aAfter = null;
        CabinInfoResponse? bAfter = null;
        CabinsResponse? cabins = null;
        var settled = await PollingHelper.WaitUntilAsync(
            WaitName.Polling_CabinSweep_PostReloadSettled,
            async () =>
            {
                cabins = await ServerApi.GetCabins(ct);
                aAfter = cabins?.Cabins.FirstOrDefault(c => c.OwnerId == ownerIdA);
                bAfter = cabins?.Cabins.FirstOrDefault(c => c.OwnerId == farmerB.Uid);
                return aAfter is { IsHidden: false }
                    && (aAfter.TileX, aAfter.TileY) == aTile
                    && bAfter is { IsHidden: true };
            },
            TestTimings.CabinAssignmentTimeout,
            cancellationToken: ct
        );

        Assert.True(
            settled,
            $"Post-reload sweep not as expected: A={(aAfter == null ? "null" : $"({aAfter.TileX},{aAfter.TileY}) hidden={aAfter.IsHidden}")}, "
                + $"B={(bAfter == null ? "null" : $"({bAfter.TileX},{bAfter.TileY}) hidden={bAfter.IsHidden}")}"
        );

        Assert.NotNull(cabins);
        Assert.Contains(ownerIdA, cabins.SavedPositionPlayerIds);
        Assert.DoesNotContain(farmerB.Uid, cabins.SavedPositionPlayerIds);
        Log("Same-pass sweep kept A's placed cabin and swept B's untouched cabin");
    }

    /// <summary>
    /// Two concurrent farmers each place their cabin via !cabin to a distinct, non-overlapping
    /// tile. Confirms placement is keyed by the sending farmer (msg.SourceFarmer): each owns
    /// the right tile and both ids appear in SavedPositionPlayerIds.
    /// </summary>
    [Fact]
    public async Task TwoPlayers_PlaceCabinsToDistinctTiles()
    {
        var ct = TestCt;
        await CreateNewGameOnServerAsync(farmType: 0, cabinStrategy: "CabinStack");

        var clientA = await Farmers.ConnectNewAsync(ct: ct);
        var ownerIdA = clientA.JoinResult.UniqueMultiplayerId;

        await using var farmerB = await Farmers.ConnectSecondFarmerAsync(ct: ct);

        // A places at the standard tile.
        var aTile = await MoveCabinViaCommandAsync(ownerIdA, ct);

        // B places at a second known-clear tile, far enough that footprints can't overlap.
        await CabinPlacementHelper.WarpAndClearFootprintAsync(
            farmerB.Client,
            CabinPlacementHelper.SecondFarmerTileX,
            CabinPlacementHelper.SecondFarmerTileY,
            ct
        );
        var bExpected = CabinPlacementHelper.ExpectedCabinTileFor(
            CabinPlacementHelper.SecondFarmerTileX,
            CabinPlacementHelper.SecondFarmerTileY
        );
        CabinInfoResponse? bMoved = null;
        var bOk = await PollingHelper.WaitUntilAsync(
            WaitName.Polling_CabinStrategy_OurCabinAssigned,
            async () =>
            {
                await farmerB.Client.SendChat("!cabin");
                bMoved = await GetOurCabinAsync(farmerB.Uid, ct);
                return !bMoved.IsHidden && (bMoved.TileX, bMoved.TileY) == bExpected;
            },
            TestTimings.CabinAssignmentTimeout,
            cancellationToken: ct
        );
        Assert.True(bOk, "B's cabin did not move to its expected tile");

        // Two distinct non-hidden tiles, each owned by the right uid.
        var cabins = await ServerApi.GetCabins(ct);
        Assert.NotNull(cabins);
        var aCabin = cabins.Cabins.FirstOrDefault(c => c.OwnerId == ownerIdA);
        var bCabin = cabins.Cabins.FirstOrDefault(c => c.OwnerId == farmerB.Uid);
        Assert.NotNull(aCabin);
        Assert.NotNull(bCabin);
        Assert.False(aCabin!.IsHidden);
        Assert.False(bCabin!.IsHidden);
        Assert.Equal(aTile, (aCabin.TileX, aCabin.TileY));
        Assert.Equal(bExpected, (bCabin.TileX, bCabin.TileY));
        Assert.NotEqual((aCabin.TileX, aCabin.TileY), (bCabin.TileX, bCabin.TileY));

        Assert.Contains(ownerIdA, cabins.SavedPositionPlayerIds);
        Assert.Contains(farmerB.Uid, cabins.SavedPositionPlayerIds);

        // Disconnect B before this class's DisposeAsync runs /newgame (409s while connected).
        await farmerB.DisconnectAsync();
        await Exceptions.AssertNoExceptionsAsync("after two-player distinct placement");
        Log($"A at ({aCabin.TileX},{aCabin.TileY}), B at ({bCabin.TileX},{bCabin.TileY})");
    }

    /// <summary>
    /// Guard (proxy) for the dummy-cabin cosmetic: a player whose cabin was moved via !cabin
    /// triggers the new dummy-found branch in OnLocationIntroductionMessage on reconnect. The
    /// mutation lives only in the per-peer message copy, so /cabins (master) can't see it —
    /// this asserts the join completes, master's tile for the moved cabin is unchanged (the
    /// dummy relocate must not touch master state), and no errors fired.
    /// </summary>
    [Fact]
    public async Task DummyCabin_ReconnectAfterMove_JoinSucceedsAndMasterUnchanged()
    {
        var ct = TestCt;
        await CreateNewGameOnServerAsync(farmType: 0, cabinStrategy: "CabinStack");

        var client = await Farmers.ConnectNewAsync(ct: ct);
        var ownerId = client.JoinResult.UniqueMultiplayerId;

        var movedTile = await MoveCabinViaCommandAsync(ownerId, ct);
        Log($"Cabin placed at ({movedTile.X},{movedTile.Y}) for '{client.FarmerName}'");

        // Disconnect and reconnect: the reconnect's location introduction runs the dummy branch.
        await Farmers.DisconnectAndWaitForSlotAsync(ownerId, client.FarmerName, ct);
        await Farmers.ReconnectAsync(client.FarmerName, ct: ct);

        // Master cabin tile is unchanged — the dummy relocate mutated only the message copy.
        var afterReconnect = await GetOurCabinAsync(ownerId, ct);
        Assert.False(afterReconnect.IsHidden, "A's moved cabin should still be visible in master");
        Assert.Equal(movedTile, (afterReconnect.TileX, afterReconnect.TileY));

        await Exceptions.AssertNoExceptionsAsync("after dummy-branch reconnect");
        Log("Reconnect with moved cabin completed; master tile unchanged");
    }

    /// <summary>
    /// Positive observation for the dummy cabin: after a player moves their cabin via !cabin
    /// and reconnects, the player's *own client* sees a dummy cabin rendered at the shared
    /// stack location — so they don't see an empty spot where everyone else's cabin appears —
    /// AND that dummy is door-dead (HasInterior == false), so stepping on it never exposes the
    /// real owner's home. Master state (/cabins) can't observe either fact; the test-client's
    /// /actions/farm_buildings reads the client's own farm view (name/tile/HasInterior).
    /// The player's own moved cabin stays enterable (HasInterior == true).
    /// </summary>
    [Fact]
    public async Task DummyCabin_AfterMoveAndReconnect_ClientSeesDoorDeadDummyAtStack()
    {
        var ct = TestCt;
        await CreateNewGameOnServerAsync(farmType: 0, cabinStrategy: "CabinStack");

        var client = await Farmers.ConnectNewAsync(ct: ct);
        var ownerId = client.JoinResult.UniqueMultiplayerId;

        var movedTile = await MoveCabinViaCommandAsync(ownerId, ct);

        // Reconnect so the client receives a fresh Farm location introduction (where the
        // dummy is injected into this peer's copy).
        await Farmers.DisconnectAndWaitForSlotAsync(ownerId, client.FarmerName, ct);
        await Farmers.ReconnectAsync(client.FarmerName, ct: ct);

        // Poll the client's own farm view: the dummy can lag the reconnect's location intro.
        FarmBuildingsResult? view = null;
        FarmBuildingInfo? dummy = null;
        FarmBuildingInfo? ownCabin = null;
        var sawDummy = await PollingHelper.WaitUntilAsync(
            WaitName.Polling_DummyCabin_VisibleInClientFarm,
            async () =>
            {
                view = await GameClient.Actions.GetFarmBuildings(ct);
                if (view?.Success != true)
                {
                    return false;
                }

                // Cabins at visible tiles (tileX >= 0): the moved own cabin plus the dummy at
                // the shared stack. The hidden-stack spare stays at (-20,-20), excluded here.
                var visible = view.Cabins.Where(c => c.TileX >= 0).ToList();
                ownCabin = visible.FirstOrDefault(c =>
                    c.TileX == movedTile.X && c.TileY == movedTile.Y
                );
                dummy = visible.FirstOrDefault(c =>
                    !(c.TileX == movedTile.X && c.TileY == movedTile.Y)
                );
                return ownCabin != null && dummy != null;
            },
            TestTimings.CabinAssignmentTimeout,
            cancellationToken: ct
        );

        Assert.True(
            sawDummy,
            "Client did not render a dummy cabin at the shared stack alongside its moved cabin "
                + $"(saw: {(view == null ? "null" : string.Join(", ", view.Cabins.Select(c => $"({c.TileX},{c.TileY},interior={c.HasInterior})")))})"
        );

        // The dummy's door must be dead; the player's own cabin must stay enterable.
        Assert.False(
            dummy!.HasInterior,
            "Dummy cabin door is live — it must be door-dead (null interior)"
        );
        Assert.True(ownCabin!.HasInterior, "Player's own moved cabin should still be enterable");
        Log("Client rendered a door-dead dummy cabin at the shared stack after moving its own");

        await Exceptions.AssertNoExceptionsAsync("after dummy positive-observation");
    }

    #region Helpers

    /// <summary>
    /// Warps the farmer to the Farm and runs the !cabin command, then returns the
    /// resulting master cabin tile read from /cabins. The tile is captured (not
    /// predicted) so the test never assumes a warp landing tile or map position.
    /// </summary>
    private async Task<(int X, int Y)> MoveCabinViaCommandAsync(long ownerId, CancellationToken ct)
    {
        // !cabin requires the farmer on the Farm AND a clear footprint (CabinPlacementValidator
        // rejects the farmhouse/debris). The helper warps to a known-clear spot and clears the
        // footprint; we read the resulting position back rather than predicting it.
        await CabinPlacementHelper.WarpAndClearFootprintAsync(GameClient, ct);

        var before = await GetOurCabinAsync(ownerId, ct);

        // Resend on each poll: the !cabin handler checks the SERVER's view of the
        // farmer location, which can lag the client-side warp by a tick. Resending
        // until the move lands self-heals that race without a fixed sleep.
        CabinInfoResponse? moved = null;
        var ok = await PollingHelper.WaitUntilAsync(
            WaitName.Polling_CabinStrategy_OurCabinAssigned,
            async () =>
            {
                await GameClient.SendChat("!cabin");
                moved = await GetOurCabinAsync(ownerId, ct);
                return !moved.IsHidden
                    && (moved.TileX, moved.TileY) != (before.TileX, before.TileY);
            },
            TestTimings.CabinAssignmentTimeout,
            cancellationToken: ct
        );

        Assert.True(ok, "Cabin did not move out of the hidden stack after !cabin");
        return (moved!.TileX, moved.TileY);
    }

    /// <summary>
    /// Rewrites the in-container server-settings.json cabinStrategy value in place.
    /// The change is applied by the next ReloadServerAsync (which re-reads the file).
    /// Mirrors the real operator flow: edit settings, reload — no test-only API added.
    /// Uses sed (jq is not in the server image); the settings writer emits one
    /// "cabinStrategy": "..." line, so a keyed substitution is unambiguous.
    /// </summary>
    private async Task SwitchCabinStrategyAsync(string strategy, CancellationToken ct)
    {
        var script =
            $"sed -i 's/\"cabinStrategy\": \"[^\"]*\"/\"cabinStrategy\": \"{strategy}\"/' {ServerContainer.SettingsPath}";
        var result = await Server.Container.ExecAsync(new[] { "sh", "-c", script }, ct);
        Assert.True(
            result.ExitCode == DockerExitCodes.Success,
            $"Failed to rewrite settings cabinStrategy: {result.Stderr}"
        );
        Log($"Switched in-container cabinStrategy to {strategy}");
    }

    private async Task<CabinInfoResponse> GetOurCabinAsync(long ownerId, CancellationToken ct)
    {
        var cabins = await ServerApi.GetCabins(ct);
        Assert.NotNull(cabins);
        var ours = cabins.Cabins.FirstOrDefault(c => c.OwnerId == ownerId);
        Assert.NotNull(ours);
        return ours;
    }

    #endregion
}
