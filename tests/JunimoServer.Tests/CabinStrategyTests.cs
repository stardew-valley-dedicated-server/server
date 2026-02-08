using JunimoServer.Tests.Clients;
using JunimoServer.Tests.Fixtures;
using JunimoServer.Tests.Helpers;
using Xunit;
using Xunit.Abstractions;

namespace JunimoServer.Tests;

/// <summary>
/// Integration tests for cabin strategy behavior.
///
/// Verifies cabin auto-creation, replenishment after player joins,
/// and the /cabins API endpoint accuracy. Tests run against the default
/// server configuration (CabinStack strategy with hidden cabins).
///
/// These tests run WITHOUT password protection to test pure cabin behavior
/// without lobby interference.
/// </summary>
[Collection("Integration-NoPassword")]
public class CabinStrategyTests : NoPasswordTestBase
{
    public CabinStrategyTests(NoPasswordFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    #region GET /cabins — basic state

    /// <summary>
    /// Verifies the /cabins endpoint returns a valid response with expected structure.
    /// </summary>
    [Fact]
    public async Task Cabins_ReturnsValidResponse()
    {
        var cabins = await ServerApi.GetCabins();

        Assert.NotNull(cabins);
        Assert.False(string.IsNullOrEmpty(cabins.Strategy));
        Assert.True(cabins.TotalCount >= 0);
        Assert.True(cabins.AssignedCount >= 0);
        Assert.True(cabins.AvailableCount >= 0);
        Assert.NotNull(cabins.Cabins);
        Log($"Cabins: strategy={cabins.Strategy}, total={cabins.TotalCount}, assigned={cabins.AssignedCount}, available={cabins.AvailableCount}");

        await AssertNoExceptionsAsync("at end of test");
    }

    /// <summary>
    /// Verifies that the active cabin strategy reported by /cabins matches /settings.
    /// </summary>
    [Fact]
    public async Task Cabins_StrategyMatchesSettings()
    {
        var cabins = await ServerApi.GetCabins();
        var settings = await ServerApi.GetSettings();

        Assert.NotNull(cabins);
        Assert.NotNull(settings);
        Assert.Equal(settings.Server.CabinStrategy, cabins.Strategy);
        Log($"Both report strategy: {cabins.Strategy}");

        await AssertNoExceptionsAsync("at end of test");
    }

    /// <summary>
    /// Verifies the default server has at least one available cabin for new players.
    /// The cabin manager auto-creates cabins to maintain a minimum pool.
    /// </summary>
    [Fact]
    public async Task DefaultCabinStack_HasAtLeastOneAvailableCabin()
    {
        var cabins = await ServerApi.GetCabins();

        Assert.NotNull(cabins);
        Assert.True(cabins.AvailableCount >= 1,
            $"Expected at least 1 available cabin, got {cabins.AvailableCount}");
        Log($"Available cabins: {cabins.AvailableCount}");

        await AssertNoExceptionsAsync("at end of test");
    }

    /// <summary>
    /// Verifies counts are consistent: total = assigned + available.
    /// </summary>
    [Fact]
    public async Task Cabins_CountsAreConsistent()
    {
        var cabins = await ServerApi.GetCabins();

        Assert.NotNull(cabins);
        Assert.Equal(cabins.TotalCount, cabins.AssignedCount + cabins.AvailableCount);
        Assert.Equal(cabins.TotalCount, cabins.Cabins.Count);
        Log($"Total={cabins.TotalCount}, Assigned={cabins.AssignedCount}, Available={cabins.AvailableCount}, List={cabins.Cabins.Count}");

        await AssertNoExceptionsAsync("at end of test");
    }

    /// <summary>
    /// Verifies that with CabinStack strategy, all CabinStack cabins are at the hidden location.
    /// Lobby cabins (if any) are excluded from this check as they have their own hidden location.
    /// </summary>
    [Fact]
    public async Task CabinStack_AllCabinsAreHidden()
    {
        var cabins = await ServerApi.GetCabins();

        Assert.NotNull(cabins);
        Assert.Equal("CabinStack", cabins.Strategy);
        Assert.NotEmpty(cabins.Cabins);

        // Filter to only CabinStack type cabins (exclude Lobby cabins)
        var cabinStackCabins = cabins.Cabins.Where(c => c.Type == "CabinStack").ToList();
        Assert.NotEmpty(cabinStackCabins);

        foreach (var cabin in cabinStackCabins)
        {
            Assert.True(cabin.IsHidden,
                $"Cabin at ({cabin.TileX}, {cabin.TileY}) should be hidden in CabinStack strategy");
        }

        Log($"All {cabinStackCabins.Count} CabinStack cabins are at hidden location");

        // Log any lobby cabins for visibility
        var lobbyCabins = cabins.Cabins.Where(c => c.Type == "Lobby").ToList();
        if (lobbyCabins.Count > 0)
        {
            Log($"Found {lobbyCabins.Count} Lobby cabin(s) (excluded from CabinStack check)");
        }

        await AssertNoExceptionsAsync("at end of test");
    }

    #endregion

    #region Cabin replenishment after player join

    /// <summary>
    /// Verifies that after a player joins and consumes a cabin slot,
    /// the cabin manager auto-creates a new one to maintain the pool.
    ///
    /// Steps:
    ///   1. Record available cabin count before join
    ///   2. Join server, create farmer, enter world
    ///   3. Disconnect
    ///   4. Assert available count is still >= 1 (replenishment happened)
    /// </summary>
    [Fact]
    public async Task CabinStack_AfterPlayerJoins_CabinReplenished()
    {
        await EnsureDisconnectedAsync();

        // Record state before join
        var cabinsBefore = await ServerApi.GetCabins();
        Assert.NotNull(cabinsBefore);
        var availableBefore = cabinsBefore.AvailableCount;
        Log($"Before join: total={cabinsBefore.TotalCount}, available={availableBefore}");

        // Join and enter world
        var farmerName = $"Cab{DateTime.UtcNow.Ticks % 1000}";
        TrackFarmer(farmerName);
        var joinResult = await JoinWorldWithRetryAsync(farmerName);
        AssertJoinSuccess(joinResult);

        // Disconnect so the farmhand goes offline
        await DisconnectAsync();

        // Poll until cabin state reflects the disconnect (at least 1 assigned, at least 1 available)
        CabinsResponse? cabinsAfter = null;
        var cabinsUpdated = await PollingHelper.WaitUntilAsync(async () =>
        {
            cabinsAfter = await ServerApi.GetCabins();
            return cabinsAfter?.AssignedCount >= 1 && cabinsAfter?.AvailableCount >= 1;
        }, TestTimings.FarmerDeleteTimeout);

        Assert.True(cabinsUpdated, "Cabin state should update after disconnect");
        Assert.NotNull(cabinsAfter);
        Log($"After join+disconnect: total={cabinsAfter.TotalCount}, assigned={cabinsAfter.AssignedCount}, available={cabinsAfter.AvailableCount}");

        // Replenishment: there should still be at least one available cabin
        Assert.True(cabinsAfter.AvailableCount >= 1,
            $"Cabin replenishment should maintain at least 1 available cabin, got {cabinsAfter.AvailableCount}");

        // Total should have increased (original cabins + replenished one)
        Assert.True(cabinsAfter.TotalCount >= cabinsBefore.TotalCount,
            $"Total cabins should not decrease: was {cabinsBefore.TotalCount}, now {cabinsAfter.TotalCount}");

        await AssertNoExceptionsAsync("at end of test");
    }

    /// <summary>
    /// Verifies that after a player joins, their cabin shows up in the /cabins list
    /// as assigned with the correct owner info.
    /// </summary>
    [Fact]
    public async Task CabinStack_PlayerJoin_CabinAssignedToPlayer()
    {
        await EnsureDisconnectedAsync();

        var farmerName = $"Own{DateTime.UtcNow.Ticks % 1000}";
        TrackFarmer(farmerName);
        var joinResult = await JoinWorldWithRetryAsync(farmerName);
        AssertJoinSuccess(joinResult);

        // Poll until cabin ownership appears in the API
        CabinsResponse? cabins = null;
        var ownerSynced = await PollingHelper.WaitUntilAsync(async () =>
        {
            cabins = await ServerApi.GetCabins();
            return cabins?.Cabins.Any(c =>
                c.OwnerName.Equals(farmerName, StringComparison.OrdinalIgnoreCase) && c.IsAssigned) == true;
        }, TestTimings.NetworkSyncTimeout);

        Assert.NotNull(cabins);

        Log($"Cabins after join ({cabins.Cabins.Count} total):");
        foreach (var c in cabins.Cabins)
        {
            LogDetail($"({c.TileX},{c.TileY}) type={c.Type} hidden={c.IsHidden} assigned={c.IsAssigned} owner='{c.OwnerName}' id={c.OwnerId}");
        }

        var ownedCabin = cabins.Cabins.FirstOrDefault(c =>
            c.OwnerName.Equals(farmerName, StringComparison.OrdinalIgnoreCase) && c.IsAssigned);

        Assert.NotNull(ownedCabin);
        Assert.True(ownedCabin.OwnerId != 0, "Owner ID should be set");
        LogSuccess($"Found cabin owned by '{farmerName}' at ({ownedCabin.TileX},{ownedCabin.TileY})");

        await AssertNoExceptionsAsync("at end of test");
    }

    /// <summary>
    /// Verifies that the /cabins and /farmhands endpoints agree on assigned player count.
    /// </summary>
    [Fact]
    public async Task Cabins_AssignedCountMatchesFarmhands()
    {
        await EnsureDisconnectedAsync();

        var farmerName = $"Sync{DateTime.UtcNow.Ticks % 1000}";
        TrackFarmer(farmerName);
        var joinResult = await JoinWorldWithRetryAsync(farmerName);
        AssertJoinSuccess(joinResult);

        // Poll until cabin assigned count matches customized farmhand count
        CabinsResponse? cabins = null;
        ServerFarmhandsResponse? farmhands = null;
        var synced = await PollingHelper.WaitUntilAsync(async () =>
        {
            cabins = await ServerApi.GetCabins();
            farmhands = await ServerApi.GetFarmhands();
            if (cabins == null || farmhands == null) return false;
            var customized = farmhands.Farmhands.Count(f => f.IsCustomized);
            return customized == cabins.AssignedCount && cabins.AssignedCount > 0;
        }, TestTimings.NetworkSyncTimeout);

        Assert.NotNull(cabins);
        Assert.NotNull(farmhands);

        var customizedFarmhands = farmhands.Farmhands.Count(f => f.IsCustomized);
        Log($"Cabins assigned: {cabins.AssignedCount}, Farmhands customized: {customizedFarmhands}");

        Assert.Equal(customizedFarmhands, cabins.AssignedCount);

        await AssertNoExceptionsAsync("at end of test");
    }

    /// <summary>
    /// Verifies that after deleting a farmhand, cabin counts and farmhand counts
    /// remain consistent. This ensures deletion properly resets the slot.
    /// </summary>
    [Fact]
    public async Task Cabins_AfterDeletion_CountsRemainConsistent()
    {
        await EnsureDisconnectedAsync();

        // Create and join
        var farmerName = $"Cons{DateTime.UtcNow.Ticks % 1000}";
        TrackFarmer(farmerName);
        var joinResult = await JoinWorldWithRetryAsync(farmerName);
        AssertJoinSuccess(joinResult);
        await DisconnectAsync();

        // Delete the farmhand (poll until server processes disconnect)
        Log($"Deleting farmhand '{farmerName}'...");
        FarmhandOperationResponse? deleteResult = null;
        var deleted = await PollingHelper.WaitUntilAsync(async () =>
        {
            deleteResult = await ServerApi.DeleteFarmhand(farmerName);
            return deleteResult?.Success == true;
        }, TestTimings.FarmerDeleteTimeout);
        Assert.True(deleted, $"Delete should succeed: {deleteResult?.Error ?? "timeout"}");
        CreatedFarmers.Remove(farmerName);

        // Poll until deletion is reflected in cabin/farmhand counts
        CabinsResponse? cabins = null;
        ServerFarmhandsResponse? farmhands = null;
        await PollingHelper.WaitUntilAsync(async () =>
        {
            cabins = await ServerApi.GetCabins();
            farmhands = await ServerApi.GetFarmhands();
            if (cabins == null || farmhands == null) return false;
            // Deletion is reflected when counts are consistent
            return cabins.TotalCount == cabins.AssignedCount + cabins.AvailableCount
                && farmhands.Farmhands.Any(f => !f.IsCustomized);
        }, TestTimings.NetworkSyncTimeout);

        Assert.NotNull(cabins);
        Assert.NotNull(farmhands);

        // Total = Assigned + Available (cabin side)
        Assert.Equal(cabins.TotalCount, cabins.AssignedCount + cabins.AvailableCount);

        // Customized farmhands = Assigned cabins
        var customizedCount = farmhands.Farmhands.Count(f => f.IsCustomized);
        var uncustomizedCount = farmhands.Farmhands.Count(f => !f.IsCustomized);

        Log($"Cabins: total={cabins.TotalCount}, assigned={cabins.AssignedCount}, available={cabins.AvailableCount}");
        Log($"Farmhands: customized={customizedCount}, uncustomized={uncustomizedCount}");

        Assert.Equal(customizedCount, cabins.AssignedCount);

        // Should have at least one uncustomized slot available
        Assert.True(uncustomizedCount >= 1,
            "After deletion, there should be at least 1 uncustomized farmhand slot");

        await AssertNoExceptionsAsync("at end of test");
    }

    #endregion

}
