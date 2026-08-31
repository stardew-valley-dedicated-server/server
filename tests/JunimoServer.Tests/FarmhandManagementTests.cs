using JunimoServer.Tests.Clients;
using JunimoServer.Tests.Helpers;
using JunimoServer.Tests.Infrastructure;
using Xunit;

namespace JunimoServer.Tests;

/// <summary>
/// Integration tests for farmhand management via the server API.
/// Verifies farmhand listing and deletion behavior.
///
/// Uses TestBase which provides:
/// - Automatic retry on connection failures
/// - Exception monitoring with early abort
/// - Server/client log streaming
/// </summary>
[TestServer(Isolation = IsolationMode.SharedAssembly)]
public class FarmhandManagementTests : TestBase
{
    public FarmhandManagementTests() { }

    /// <summary>
    /// Verifies that deleting an offline farmhand via DELETE /farmhands?name=X
    /// succeeds and the farmhand no longer appears in the list.
    /// </summary>
    [Fact]
    public async Task DeleteFarmhand_WhenOffline_Succeeds()
    {
        var client = await Farmers.ConnectFastAsync(ct: TestCt);

        // Disconnect and wait for persistence
        await Farmers.DisconnectAndWaitForPersistenceAsync(client.FarmerName, TestCt);

        // Delete the farmhand
        Log($"Deleting offline farmhand '{client.FarmerName}'...");
        var deleteResult = await ServerApi.WaitForFarmhandDeletedByNameAsync(
            client.FarmerName,
            ct: TestCt
        );

        Assert.True(
            deleteResult?.Success,
            $"Delete should succeed: {deleteResult?.Error ?? "timeout"}"
        );
        Log($"Delete response: {deleteResult?.Message}");

        // Verify the farmhand is gone (poll in case server needs a tick to update farmhandData)
        ServerFarmhandsResponse? farmhands = null;
        var isGone = await PollingHelper.WaitUntilAsync(
            WaitName.Polling_FarmhandManagement_FarmhandGone,
            async () =>
            {
                farmhands = await ServerApi.GetFarmhands();
                return farmhands?.Farmhands.All(f =>
                        !f.Name.Equals(client.FarmerName, StringComparison.OrdinalIgnoreCase)
                    ) == true;
            },
            // Delete already confirmed at :87; this only waits a tick for /farmhands to reflect it.
            TestTimings.FarmerRemovalBudget,
            cancellationToken: TestCt
        );
        Assert.True(
            isGone,
            $"Farmhand '{client.FarmerName}' should no longer appear in /farmhands after deletion"
        );
        Log($"Verified: farmhand '{client.FarmerName}' no longer in list");

        // Remove from cleanup list since we already deleted it
        Farmers.CreatedFarmers.RemoveAll(f => f.Uid == client.JoinResult.UniqueMultiplayerId);
    }

    /// <summary>
    /// Verifies that after deleting an offline farmhand, the slot becomes available
    /// and a new player can join using that freed slot.
    /// This tests the full create → delete → reuse cycle.
    /// </summary>
    [Fact]
    public async Task DeleteFarmhand_SlotBecomesReusable()
    {
        // Create first farmer, disconnect, and wait for persistence
        var client1 = await Farmers.ConnectFastAsync(ct: TestCt);
        await Farmers.DisconnectAndWaitForPersistenceAsync(client1.FarmerName, TestCt);
        Log($"After first join: farmer '{client1.FarmerName}' exists");

        // Delete farmer1 (poll until server processes disconnect)
        Log($"Deleting farmhand '{client1.FarmerName}'...");
        var deleteResult = await ServerApi.WaitForFarmhandDeletedByNameAsync(
            client1.FarmerName,
            ct: TestCt
        );
        Assert.True(
            deleteResult?.Success,
            $"Delete should succeed: {deleteResult?.Error ?? "timeout"}"
        );
        Farmers.CreatedFarmers.RemoveAll(f => f.Uid == client1.JoinResult.UniqueMultiplayerId);

        // Verify slot is available (uncustomized)
        var afterDelete = await ServerApi.GetFarmhands(TestCt);
        Assert.NotNull(afterDelete);
        var uncustomizedSlots = afterDelete.Farmhands.Count(f => !f.IsCustomized);
        Assert.True(
            uncustomizedSlots >= 1,
            "Should have at least 1 uncustomized slot after deletion"
        );
        Log($"After delete: {uncustomizedSlots} uncustomized slot(s) available");

        // Create second farmer using the freed slot
        var client2 = await Farmers.ConnectNewAsync(ct: TestCt);

        // Verify farmer2 exists (poll until name syncs)
        var farmer2Found = await ServerApi.WaitForFarmhandByNameAsync(
            client2.FarmerName,
            requireCustomized: true,
            ct: TestCt
        );

        Assert.True(farmer2Found, $"Farmer '{client2.FarmerName}' should exist after reusing slot");
        Log($"Slot reuse successful: farmer '{client2.FarmerName}' joined using freed slot");
    }

    /// <summary>
    /// Verifies that attempting to delete a farmhand who is currently connected
    /// returns an error and does not remove them.
    /// </summary>
    [Fact]
    public async Task DeleteFarmhand_WhenOnline_Fails()
    {
        var client = await Farmers.ConnectFastAsync(ct: TestCt);

        // Try to delete while still connected - should fail. Use the UID overload
        // because fresh joiners may not have name-synced yet.
        Log(
            $"Attempting to delete online farmhand '{client.FarmerName}' (uid={client.JoinResult.UniqueMultiplayerId})..."
        );
        var deleteResult = await ServerApi.DeleteFarmhandById(
            client.JoinResult.UniqueMultiplayerId,
            TestCt
        );
        Assert.NotNull(deleteResult);
        Assert.False(deleteResult.Success, "Delete should fail for an online farmhand");
        Assert.NotNull(deleteResult.Error);
        Assert.Contains("online", deleteResult.Error, StringComparison.OrdinalIgnoreCase);
        Log($"Delete correctly refused: {deleteResult.Error}");

        // Verify the farmhand still exists (poll until name syncs)
        var stillFound = await ServerApi.WaitForFarmhandByNameAsync(client.FarmerName, ct: TestCt);

        Assert.True(
            stillFound,
            $"Farmhand '{client.FarmerName}' should still exist after failed delete"
        );
        Log($"Verified: farmhand '{client.FarmerName}' still exists after failed delete");
    }
}
