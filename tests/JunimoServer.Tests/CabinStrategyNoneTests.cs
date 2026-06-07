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
            try { await CreateNewGameOnServerAsync(farmType: 0); }
            catch (Exception ex) { LogWarning($"Server reset failed during cleanup: {ex.Message}"); }
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
}
