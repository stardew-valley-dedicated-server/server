using JunimoServer.Tests.Infrastructure;
using Xunit;

namespace JunimoServer.Tests;

/// <summary>
/// Tests that game creation and player joining work for the vanilla farm types (0-6) plus
/// modded-farm selection by Data/AdditionalFarms Id (base-game MeadowlandsFarm) and the
/// unknown-Id → Standard fallback. By-Id disambiguation between two AdditionalFarms entries
/// needs a fixture mod and lives in <see cref="ModFarmDisambiguationTests"/>.
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
    // Built-in farms by index.
    [InlineData(0, "Standard")]
    [InlineData(1, "Riverland")]
    [InlineData(2, "Forest")]
    [InlineData(3, "Hilltop")]
    [InlineData(4, "Wilderness")]
    [InlineData(5, "FourCorners")]
    [InlineData(6, "Beach")]
    // Meadowlands is a built-in farm too — selectable by index 7 or its Id, interchangeably.
    [InlineData(7, "MeadowlandsFarm")]
    [InlineData("MeadowlandsFarm", "MeadowlandsFarm")]
    // Vanilla farms are also selectable by name (case/space-insensitive).
    [InlineData("Standard", "Standard")]
    [InlineData("four corners", "FourCorners")]
    // Unknown Id, out-of-range index, and the "modded" keyword with no mod farm installed all
    // fall back to Standard with a Warn, not a crash. ("modded" resolving to an actual mod farm
    // is covered by ModFarmDisambiguationTests, which loads the fixture mod.)
    [InlineData("Nonexistent.Farm", "Standard")]
    [InlineData(9, "Standard")]
    [InlineData("modded", "Standard")]
    public async Task NewGame_WithFarmType_CabinsBuiltAndPlayerCanJoin(
        object farmType,
        string expectedFarmTypeKey
    )
    {
        var farmTypeSetting = FarmTypeSetting.FromObject(farmType);
        LogSection($"Testing {expectedFarmTypeKey} farm (type {farmTypeSetting})");

        // Create a new game with the specified farm type on the shared server
        await CreateNewGameOnServerAsync(farmTypeSetting);

        Log($"Server ready: {Server.BaseUrl}");

        // Verify cabins were created via API
        // CabinStack maintains at least 1 available cabin automatically
        var cabinsResponse = await ServerApi.GetCabins(TestCt);

        Assert.NotNull(cabinsResponse);
        Assert.True(
            cabinsResponse.TotalCount >= 1,
            $"Expected at least 1 cabin, got {cabinsResponse.TotalCount}"
        );
        Log($"Cabins created: {cabinsResponse.TotalCount} (strategy: {cabinsResponse.Strategy})");

        // Verify actual loaded farm type via GetFarmTypeKey()
        var statusResponse = await ServerApi.GetStatus(TestCt);
        Assert.NotNull(statusResponse);
        Assert.Equal(expectedFarmTypeKey, statusResponse.FarmTypeKey);
        Log($"Farm type key verified: {statusResponse.FarmTypeKey}");

        // Join the server with a test farmer
        await Farmers.ConnectNewAsync(farmerName: $"Test_{expectedFarmTypeKey}", ct: TestCt);

        Log($"Successfully joined {expectedFarmTypeKey} farm!");
    }
}
