using JunimoServer.Tests.Clients;
using JunimoServer.Tests.Containers;
using JunimoServer.Tests.Helpers;
using JunimoServer.Tests.Infrastructure;
using Xunit;

namespace JunimoServer.Tests;

/// <summary>
/// Tests that verify game creation and player joining works for all 8 farm map types.
/// Uses a single shared server and POST /newgame to reset between tests instead of
/// spinning up separate containers.
///
/// Do NOT change to <see cref="IsolationMode.PerTest"/> — measured ~5x slower than
/// the shared-server + /newgame approach because each theory case would spin up a
/// fresh container.
///
/// None-strategy cabin tests are in CabinStrategyNoneTests (runs on a separate
/// server instance to shorten the exclusive sequential chain).
/// </summary>
[TestServer(Isolation = IsolationMode.SharedAssembly, Priority = 90, Exclusive = true)]
public class FarmMapTypeTests : TestBase
{
    public FarmMapTypeTests() { }

    [Theory]
    [InlineData(0, "Standard")]
    [InlineData(1, "Riverland")]
    [InlineData(2, "Forest")]
    [InlineData(3, "Hilltop")]
    [InlineData(4, "Wilderness")]
    [InlineData(5, "FourCorners")]
    [InlineData(6, "Beach")]
    [InlineData(7, "MeadowlandsFarm")]
    public async Task NewGame_WithFarmType_CabinsBuiltAndPlayerCanJoin(int farmType, string expectedFarmTypeKey)
    {
        LogSection($"Testing {expectedFarmTypeKey} farm (type {farmType})");

        // Create a new game with the specified farm type on the shared server
        await CreateNewGameOnServerAsync(farmType);

        Log($"Server ready: {Server.BaseUrl}");

        // Verify cabins were created via API
        // CabinStack maintains at least 1 available cabin automatically
        var cabinsResponse = await ServerApi.GetCabins(TestContext.Current.CancellationToken);

        Assert.NotNull(cabinsResponse);
        Assert.True(cabinsResponse.TotalCount >= 1,
            $"Expected at least 1 cabin, got {cabinsResponse.TotalCount}");
        Log($"Cabins created: {cabinsResponse.TotalCount} (strategy: {cabinsResponse.Strategy})");

        // Verify actual loaded farm type via GetFarmTypeKey()
        var statusResponse = await ServerApi.GetStatus(TestContext.Current.CancellationToken);
        Assert.NotNull(statusResponse);
        Assert.Equal(expectedFarmTypeKey, statusResponse.FarmTypeKey);
        Log($"Farm type key verified: {statusResponse.FarmTypeKey}");

        // Join the server with a test farmer
        await Farmers.ConnectNewAsync(
            farmerName: $"Test_{expectedFarmTypeKey}",
            ct: TestContext.Current.CancellationToken);

        Log($"Successfully joined {expectedFarmTypeKey} farm!");
    }
}
