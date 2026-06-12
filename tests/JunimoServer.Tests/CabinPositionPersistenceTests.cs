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
        // Reset the shared server to a clean default game so the MoveToStack config
        // and any moved cabins don't leak into sibling tests.
        if (Lease != null)
        {
            try
            {
                await CreateNewGameOnServerAsync(farmType: 0);
            }
            catch (Exception ex)
            {
                LogWarning($"Server reset failed during cleanup: {ex.Message}");
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
        var ct = TestContext.Current.CancellationToken;
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
        var ct = TestContext.Current.CancellationToken;
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
        var ct = TestContext.Current.CancellationToken;
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
        var ct = TestContext.Current.CancellationToken;
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
