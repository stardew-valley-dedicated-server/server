using JunimoServer.Tests.Fixtures;
using JunimoServer.Tests.Helpers;
using Xunit;
using Xunit.Abstractions;

namespace JunimoServer.Tests;

/// <summary>
/// Integration tests for farmhand management via the server API.
/// Verifies farmhand listing, customization state, and deletion behavior.
///
/// Uses IntegrationTestBase which provides:
/// - Automatic retry on connection failures
/// - Exception monitoring with early abort
/// - Server/client log streaming
/// </summary>
[Collection("Integration")]
public class FarmhandManagementTests : IntegrationTestBase
{
    public FarmhandManagementTests(IntegrationTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    /// <summary>
    /// Verifies that GET /farmhands (server API) correctly reflects which farmhand
    /// slots are customized vs uncustomized after a player creates a character.
    /// </summary>
    [Fact]
    public async Task ServerFarmhands_ReflectCustomizationState()
    {
        await EnsureDisconnectedAsync();

        var farmerName = GenerateFarmerName("Cust");
        TrackFarmer(farmerName);

        // Join and enter world using retry helper
        var joinResult = await JoinWorldWithRetryAsync(farmerName);
        AssertJoinSuccess(joinResult);

        // Check server-side farmhand list
        var farmhands = await ServerApi.GetFarmhands();
        Assert.NotNull(farmhands);
        Assert.NotEmpty(farmhands.Farmhands);

        Log($"Server reports {farmhands.Farmhands.Count} farmhand(s):");
        foreach (var fh in farmhands.Farmhands)
        {
            Log($"  Name='{fh.Name}', IsCustomized={fh.IsCustomized}, Id={fh.Id}");
        }

        // Our farmer should be listed as customized
        var ourFarmhand = farmhands.Farmhands.FirstOrDefault(f =>
            f.Name.Equals(farmerName, StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(ourFarmhand);
        Assert.True(ourFarmhand.IsCustomized,
            $"Farmhand '{farmerName}' should be marked as customized");

        // There should be at least one uncustomized slot (the default cabins)
        var uncustomized = farmhands.Farmhands.Where(f => !f.IsCustomized).ToList();
        Assert.NotEmpty(uncustomized);
        Log($"Found {uncustomized.Count} uncustomized slot(s) as expected");

        await AssertNoExceptionsAsync("at end of test");
    }

    /// <summary>
    /// Verifies that deleting an offline farmhand via DELETE /farmhands?name=X
    /// succeeds and the farmhand no longer appears in the list.
    /// </summary>
    [Fact]
    public async Task DeleteFarmhand_WhenOffline_Succeeds()
    {
        await EnsureDisconnectedAsync();

        var farmerName = GenerateFarmerName("Del");
        TrackFarmer(farmerName);

        // Join and enter world
        var joinResult = await JoinWorldWithRetryAsync(farmerName);
        AssertJoinSuccess(joinResult);

        // Disconnect so the farmhand is offline
        await DisconnectAsync();

        // Wait for server to fully process the disconnection
        await Task.Delay(TestTimings.DisconnectProcessingDelayMs);

        // Delete the farmhand
        Log($"Deleting offline farmhand '{farmerName}'...");
        var deleteResult = await ServerApi.DeleteFarmhand(farmerName);
        Assert.NotNull(deleteResult);
        Assert.True(deleteResult.Success, $"Delete should succeed: {deleteResult.Error}");
        Log($"Delete response: {deleteResult.Message}");

        // Verify the farmhand is gone
        var farmhands = await ServerApi.GetFarmhands();
        Assert.NotNull(farmhands);
        var deleted = farmhands.Farmhands.FirstOrDefault(f =>
            f.Name.Equals(farmerName, StringComparison.OrdinalIgnoreCase));
        Assert.Null(deleted);
        Log($"Verified: farmhand '{farmerName}' no longer in list");

        // Remove from cleanup list since we already deleted it
        CreatedFarmers.Remove(farmerName);

        await AssertNoExceptionsAsync("at end of test");
    }

    /// <summary>
    /// Verifies that after deleting an offline farmhand, the slot becomes available
    /// and a new player can join using that freed slot.
    /// This tests the full create → delete → reuse cycle.
    /// </summary>
    [Fact]
    public async Task DeleteFarmhand_SlotBecomesReusable()
    {
        await EnsureDisconnectedAsync();

        // Get initial slot count
        var initialFarmhands = await ServerApi.GetFarmhands();
        Assert.NotNull(initialFarmhands);
        var initialSlotCount = initialFarmhands.Farmhands.Count;
        Log($"Initial state: {initialSlotCount} farmhand slot(s)");

        // Create first farmer
        var farmerName1 = GenerateFarmerName("Reuse1");
        TrackFarmer(farmerName1);

        var joinResult1 = await JoinWorldWithRetryAsync(farmerName1);
        AssertJoinSuccess(joinResult1);
        await DisconnectAsync();
        await Task.Delay(TestTimings.DisconnectProcessingDelayMs);

        // Verify farmer1 exists
        var afterJoin = await ServerApi.GetFarmhands();
        Assert.NotNull(afterJoin);
        var farmer1Exists = afterJoin.Farmhands.Any(f =>
            f.Name.Equals(farmerName1, StringComparison.OrdinalIgnoreCase) && f.IsCustomized);
        Assert.True(farmer1Exists, $"Farmer '{farmerName1}' should exist after joining");
        Log($"After first join: farmer '{farmerName1}' exists");

        // Delete farmer1
        Log($"Deleting farmhand '{farmerName1}'...");
        var deleteResult = await ServerApi.DeleteFarmhand(farmerName1);
        Assert.NotNull(deleteResult);
        Assert.True(deleteResult.Success, $"Delete should succeed: {deleteResult.Error}");
        CreatedFarmers.Remove(farmerName1);

        // Verify slot is available (uncustomized)
        var afterDelete = await ServerApi.GetFarmhands();
        Assert.NotNull(afterDelete);
        var uncustomizedSlots = afterDelete.Farmhands.Count(f => !f.IsCustomized);
        Assert.True(uncustomizedSlots >= 1, "Should have at least 1 uncustomized slot after deletion");
        Log($"After delete: {uncustomizedSlots} uncustomized slot(s) available");

        // Create second farmer using the freed slot
        var farmerName2 = GenerateFarmerName("Reuse2");
        TrackFarmer(farmerName2);

        var joinResult2 = await JoinWorldWithRetryAsync(farmerName2);
        AssertJoinSuccess(joinResult2);

        // Verify farmer2 exists
        var afterReuse = await ServerApi.GetFarmhands();
        Assert.NotNull(afterReuse);
        var farmer2Exists = afterReuse.Farmhands.Any(f =>
            f.Name.Equals(farmerName2, StringComparison.OrdinalIgnoreCase) && f.IsCustomized);
        Assert.True(farmer2Exists, $"Farmer '{farmerName2}' should exist after reusing slot");
        Log($"Slot reuse successful: farmer '{farmerName2}' joined using freed slot");

        await AssertNoExceptionsAsync("at end of test");
    }

    /// <summary>
    /// Verifies that attempting to delete a farmhand who is currently connected
    /// returns an error and does not remove them.
    /// </summary>
    [Fact]
    public async Task DeleteFarmhand_WhenOnline_Fails()
    {
        await EnsureDisconnectedAsync();

        var farmerName = GenerateFarmerName("Onl");
        TrackFarmer(farmerName);

        // Join and enter world
        var joinResult = await JoinWorldWithRetryAsync(farmerName);
        AssertJoinSuccess(joinResult);

        // Try to delete while still connected - should fail
        Log($"Attempting to delete online farmhand '{farmerName}'...");
        var deleteResult = await ServerApi.DeleteFarmhand(farmerName);
        Assert.NotNull(deleteResult);
        Assert.False(deleteResult.Success, "Delete should fail for an online farmhand");
        Assert.NotNull(deleteResult.Error);
        Assert.Contains("online", deleteResult.Error, StringComparison.OrdinalIgnoreCase);
        Log($"Delete correctly refused: {deleteResult.Error}");

        // Verify the farmhand still exists
        var farmhands = await ServerApi.GetFarmhands();
        Assert.NotNull(farmhands);
        var stillExists = farmhands.Farmhands.FirstOrDefault(f =>
            f.Name.Equals(farmerName, StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(stillExists);
        Log($"Verified: farmhand '{farmerName}' still exists after failed delete");

        await AssertNoExceptionsAsync("at end of test");
    }

}
