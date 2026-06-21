using JunimoServer.Tests.Clients;
using JunimoServer.Tests.Helpers;
using JunimoServer.Tests.Infrastructure;
using Xunit;

namespace JunimoServer.Tests;

/// <summary>
/// Tests that verify game creation with the "None" (vanilla) cabin strategy.
/// Extracted from FarmMapTypeTests to run on a separate server instance,
/// shortening the exclusive sequential chain.
///
/// Uses the same SharedAssembly server pool as FarmMapTypeTests. With 2+
/// pre-started instances, these exclusive tests run on a different instance
/// concurrently with the farm type Theory tests.
/// </summary>
[TestServer(Isolation = IsolationMode.SharedAssembly, Priority = 90, Exclusive = true)]
public class CabinStrategyNoneTests : TestBase
{
    private bool _needsServerReset;

    public CabinStrategyNoneTests() { }

    public override async ValueTask DisposeAsync()
    {
        if (_needsServerReset && Lease != null)
        {
            try
            {
                // Disconnect the primary first: /newgame 409s while any client is connected.
                // The NewGame_* tests connect no client, but NoneStrategy_CabinMovesToFarmerTilePlusOne
                // leaves the primary connected — without this the reset would 409 and leak None
                // state. Tolerant: a no-op throw when never connected / already at title must
                // not block the reset.
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

                await CreateNewGameOnServerAsync(farmType: 0);
            }
            catch (Exception ex)
            {
                LogWarning($"Server reset failed during cleanup: {ex.Message}");
            }
        }
        _needsServerReset = false;
        await base.DisposeAsync();
    }

    [Fact]
    public async Task NewGame_NoneStrategy_DefaultStartingCabins()
    {
        LogSection("Testing None (vanilla) strategy with default starting cabins");

        _needsServerReset = true;
        await CreateNewGameOnServerAsync(farmType: 0, cabinStrategy: "None", startingCabins: 1);

        Log($"Server ready: {Server.BaseUrl}");

        var cabinsResponse = await ServerApi.GetCabins(TestContext.Current.CancellationToken);

        Assert.NotNull(cabinsResponse);
        Assert.Equal("None", cabinsResponse.Strategy);
        // Exact count, not a lower bound: a lower bound passed even when vanilla placed 0 and
        // the mod backfilled 1 (the bug this guards). startingCabins:1 must yield exactly 1.
        Assert.Equal(1, cabinsResponse.TotalCount);

        Log($"Cabins created: {cabinsResponse.TotalCount} (strategy: {cabinsResponse.Strategy})");
    }

    [Fact]
    public async Task NewGame_NoneStrategy_SixStartingCabins()
    {
        LogSection("Testing None (vanilla) strategy with 6 starting cabins");

        _needsServerReset = true;
        await CreateNewGameOnServerAsync(farmType: 0, cabinStrategy: "None", startingCabins: 6);

        Log($"Server ready: {Server.BaseUrl}");

        var cabinsResponse = await ServerApi.GetCabins(TestContext.Current.CancellationToken);

        Assert.NotNull(cabinsResponse);
        Assert.Equal("None", cabinsResponse.Strategy);

        // Exact count: startingCabins:6 must place 6 visible cabins (the Standard farm's Paths
        // layer has 7 designated positions, so 6 fit). A <= 6 upper bound previously passed even
        // when only 1 cabin existed — the silent regression this test now catches.
        Assert.Equal(6, cabinsResponse.TotalCount);

        Log($"Cabins created: {cabinsResponse.TotalCount} (strategy: {cabinsResponse.Strategy})");

        Assert.Equal(cabinsResponse.TotalCount, cabinsResponse.AvailableCount);
    }

    /// <summary>
    /// !cabin under None moves a visible cabin to farmer.Tile + (1,0): None has no hidden
    /// stack, so the move is between two real map positions. Proves the None happy path of
    /// the command (otherwise only exercised incidentally by the strategy-switch test).
    /// </summary>
    [Fact]
    public async Task NoneStrategy_CabinMovesToFarmerTilePlusOne()
    {
        LogSection("Testing !cabin under None (vanilla) strategy");

        _needsServerReset = true;
        await CreateNewGameOnServerAsync(farmType: 0, cabinStrategy: "None", startingCabins: 1);

        var ct = TestContext.Current.CancellationToken;
        var client = await Farmers.ConnectNewAsync(ct: ct);
        var ownerId = client.JoinResult.UniqueMultiplayerId;

        // Baseline is map-derived under None — never hard-coded.
        var baseline = await GetOurCabinAsync(ownerId, ct);
        Assert.False(baseline.IsHidden, "None-strategy cabin should start visible");

        await CabinPlacementHelper.WarpAndClearFootprintAsync(GameClient, ct);

        // Resend each poll: the !cabin handler reads the server's view of the farmer
        // location, which can lag the client warp by a tick.
        CabinInfoResponse? moved = null;
        var ok = await PollingHelper.WaitUntilAsync(
            WaitName.Polling_CabinPlacement_Moved,
            async () =>
            {
                await GameClient.SendChat("!cabin");
                moved = await GetOurCabinAsync(ownerId, ct);
                return (moved.TileX, moved.TileY) != (baseline.TileX, baseline.TileY);
            },
            TestTimings.CabinAssignmentTimeout,
            cancellationToken: ct
        );

        Assert.True(ok, "Cabin did not move to the farmer tile after !cabin under None");
        Assert.Equal(CabinPlacementHelper.ExpectedCabinTile, (moved!.TileX, moved.TileY));
        Assert.Equal("Normal", moved.Type);
        Assert.False(moved.IsHidden, "Moved None cabin must stay visible");

        await Exceptions.AssertNoExceptionsAsync("after !cabin under None");

        Log($"None-strategy cabin moved to ({moved.TileX},{moved.TileY})");
    }

    #region Helpers

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
