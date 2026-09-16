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
    /// Verifies that deleting a farmhand frees its cabin for reuse. Runs on a dedicated
    /// single-cabin server: <c>CabinStrategy=None</c> caps the pool at
    /// <c>min(designated positions, MaxPlayers) = 1</c> and <c>EnsureAtLeastXCabins</c> can't grow
    /// it, so once that cabin is customized the freed slot is the ONLY way a later farmer can join.
    /// The default strategy always keeps a spare cabin, which would let a second join succeed
    /// without reusing anything — the reuse is only provable against a capped pool. Exclusive so no
    /// sibling test churns the capped pool.
    /// </summary>
    [Fact]
    [TestServer(Exclusive = true, CabinStrategy = "None", MaxPlayers = 1)]
    public async Task DeleteFarmhand_SlotBecomesReusable()
    {
        // Fill the single-cabin pool: a customized farmhand leaves it with no available slot.
        var client1 = await Farmers.ConnectFastAsync(ct: TestCt);
        CabinsResponse? cabins = null;
        var poolFull = await PollingHelper.WaitUntilAsync(
            WaitName.Polling_FarmhandManagement_SingleCabinFull,
            async () =>
            {
                cabins = await ServerApi.GetCabins(TestCt);
                return cabins is { TotalCount: 1, AvailableCount: 0 };
            },
            TestTimings.FarmerRemovalBudget,
            cancellationToken: TestCt
        );
        Assert.True(
            poolFull,
            "Capped pool should be full (1 cabin, 0 available) after the first farmer customizes; "
                + $"got total={cabins?.TotalCount}, available={cabins?.AvailableCount}"
        );

        await Farmers.DisconnectAndWaitForPersistenceAsync(client1.FarmerName, TestCt);

        // Delete the only farmhand. DELETE runs EnsureAtLeastXCabins, which rebuilds the freed cabin.
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

        // The pool was full and can't grow, so a second farmer can join ONLY by reusing the slot
        // the delete freed — there is nowhere else for it to go.
        var client2 = await Farmers.ConnectNewAsync(ct: TestCt);
        var farmer2Found = await ServerApi.WaitForFarmhandByNameAsync(
            client2.FarmerName,
            requireCustomized: true,
            ct: TestCt
        );
        Assert.True(
            farmer2Found,
            $"Farmer '{client2.FarmerName}' should join by reusing the one freed slot"
        );
        Log($"Slot reuse successful: farmer '{client2.FarmerName}' joined using the freed slot");
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
