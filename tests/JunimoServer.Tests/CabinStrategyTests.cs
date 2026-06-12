using JunimoServer.Tests.Clients;
using JunimoServer.Tests.Helpers;
using JunimoServer.Tests.Infrastructure;
using Xunit;

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
[TestServer(Isolation = IsolationMode.SharedAssembly)]
public class CabinStrategyTests : TestBase
{
    public CabinStrategyTests() { }

    #region GET /cabins: basic state

    /// <summary>
    /// Verifies that the active cabin strategy reported by /cabins matches /settings.
    /// </summary>
    [Fact]
    [TestServer(Clients = 0)]
    public async Task Cabins_StrategyMatchesSettings()
    {
        var cabins = await ServerApi.GetCabins(TestContext.Current.CancellationToken);
        var settings = await ServerApi.GetSettings(TestContext.Current.CancellationToken);

        Assert.NotNull(cabins);
        Assert.NotNull(settings);
        Assert.Equal(settings.Server.CabinStrategy, cabins.Strategy);
        Log($"Both report strategy: {cabins.Strategy}");
    }

    /// <summary>
    /// Verifies the default server has at least one available cabin for new players.
    /// The cabin manager auto-creates cabins to maintain a minimum pool.
    /// </summary>
    [Fact]
    [TestServer(Clients = 0)]
    public async Task DefaultCabinStack_HasAtLeastOneAvailableCabin()
    {
        var cabins = await ServerApi.GetCabins(TestContext.Current.CancellationToken);

        Assert.NotNull(cabins);
        Assert.True(
            cabins.AvailableCount >= 1,
            $"Expected at least 1 available cabin, got {cabins.AvailableCount}"
        );
        Log($"Available cabins: {cabins.AvailableCount}");
    }

    /// <summary>
    /// Verifies counts are consistent: total = assigned + available.
    /// </summary>
    [Fact]
    [TestServer(Clients = 0)]
    public async Task Cabins_CountsAreConsistent()
    {
        var cabins = await ServerApi.GetCabins(TestContext.Current.CancellationToken);

        Assert.NotNull(cabins);
        Assert.Equal(cabins.TotalCount, cabins.AssignedCount + cabins.AvailableCount);
        Assert.Equal(cabins.TotalCount, cabins.Cabins.Count);
        Log(
            $"Total={cabins.TotalCount}, Assigned={cabins.AssignedCount}, Available={cabins.AvailableCount}, List={cabins.Cabins.Count}"
        );
    }

    /// <summary>
    /// Verifies that with CabinStack strategy, all CabinStack cabins are at the hidden location.
    /// Lobby cabins (if any) are excluded from this check as they have their own hidden location.
    /// </summary>
    [Fact]
    [TestServer(Clients = 0)]
    public async Task CabinStack_AllCabinsAreHidden()
    {
        var cabins = await ServerApi.GetCabins(TestContext.Current.CancellationToken);

        Assert.NotNull(cabins);
        Assert.Equal("CabinStack", cabins.Strategy);
        Assert.NotEmpty(cabins.Cabins);

        // Filter to only CabinStack type cabins (exclude Lobby cabins)
        var cabinStackCabins = cabins.Cabins.Where(c => c.Type == "CabinStack").ToList();
        Assert.NotEmpty(cabinStackCabins);

        foreach (var cabin in cabinStackCabins)
        {
            Assert.True(
                cabin.IsHidden,
                $"Cabin at ({cabin.TileX}, {cabin.TileY}) should be hidden in CabinStack strategy"
            );
        }

        Log($"All {cabinStackCabins.Count} CabinStack cabins are at hidden location");

        // Log any lobby cabins for visibility
        var lobbyCabins = cabins.Cabins.Where(c => c.Type == "Lobby").ToList();
        if (lobbyCabins.Count > 0)
        {
            Log($"Found {lobbyCabins.Count} Lobby cabin(s) (excluded from CabinStack check)");
        }
    }

    #endregion

    #region Cabin replenishment after player join

    /// <summary>
    /// Verifies that after a player joins and consumes a cabin slot,
    /// the cabin manager auto-creates a new one to maintain the pool.
    ///
    /// Cabin replenishment is event-driven: EnsureAtLeastXCabins() fires on
    /// OnServerJoined (peer connect), not on disconnect. OnServerJoined is a
    /// Harmony postfix on GameServer.sendServerIntroduction(), which only fires
    /// after a farmhand slot is selected and validated, NOT when the client
    /// merely reaches the farmhand selection screen.
    ///
    /// Steps:
    ///   1. Record available cabin count before join
    ///   2. Join server, create farmer, enter world
    ///   3. Disconnect
    ///   4. Rejoin world (triggers sendServerIntroduction → OnServerJoined → EnsureAtLeastXCabins)
    ///   5. Assert available count is >= 1 (replenishment maintains pool)
    ///
    /// Note: total cabin count only increases if available was near the threshold (1).
    /// With many starting cabins, the pool stays above threshold and no new cabins are created.
    /// </summary>
    [Fact]
    public async Task CabinStack_AfterPlayerJoins_CabinReplenished()
    {
        // Record state before join
        var cabinsBefore = await ServerApi.GetCabins(TestContext.Current.CancellationToken);
        Assert.NotNull(cabinsBefore);
        var availableBefore = cabinsBefore.AvailableCount;
        Log($"Before join: total={cabinsBefore.TotalCount}, available={availableBefore}");

        // Join and enter world (consumes one cabin slot via customization)
        var client = await Farmers.ConnectNewAsync(ct: TestContext.Current.CancellationToken);

        // Disconnect and wait for the server to release the farmhand slot
        await Farmers.DisconnectAndWaitForSlotAsync(
            client.JoinResult.UniqueMultiplayerId,
            client.FarmerName,
            TestContext.Current.CancellationToken
        );

        // Rejoin world to trigger cabin replenishment. sendServerIntroduction (and thus
        // OnServerJoined -> EnsureAtLeastXCabins) only fires after farmhand slot selection,
        // not when merely reaching the farmhand screen. A full rejoin is required.
        await Farmers.ReconnectAsync(client.FarmerName, ct: TestContext.Current.CancellationToken);

        // Poll until our farmer's cabin appears as assigned AND at least 1 available
        // (replenishment). Must check for our specific farmer; other tests on a shared
        // server may already have assigned cabins, so AssignedCount >= 1 alone is ambiguous.
        CabinsResponse? cabinsAfter = null;
        CabinInfoResponse? ourCabin = null;
        var cabinsUpdated = await PollingHelper.WaitUntilAsync(
            WaitName.Polling_CabinStrategy_OurCabinAssigned,
            async () =>
            {
                cabinsAfter = await ServerApi.GetCabins(TestContext.Current.CancellationToken);
                ourCabin = cabinsAfter?.Cabins.FirstOrDefault(c =>
                    c.OwnerName.Equals(client.FarmerName, StringComparison.OrdinalIgnoreCase)
                    && c.IsAssigned
                );
                LogDetail(
                    $"Polling cabins: total={cabinsAfter?.TotalCount}, assigned={cabinsAfter?.AssignedCount}, available={cabinsAfter?.AvailableCount}, ourCabin={ourCabin != null}"
                );
                return ourCabin != null && cabinsAfter?.AvailableCount >= 1;
            },
            TestTimings.CabinAssignmentTimeout,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(
            cabinsUpdated,
            $"Cabin replenishment should maintain available cabins after '{client.FarmerName}' joined"
        );
        Log(
            $"After reconnect: total={cabinsAfter!.TotalCount}, assigned={cabinsAfter.AssignedCount}, available={cabinsAfter.AvailableCount}"
        );
        Log($"Our farmer '{client.FarmerName}' has cabin at ({ourCabin!.TileX},{ourCabin.TileY})");
    }

    /// <summary>
    /// Verifies that after a player joins, their cabin shows up in the /cabins list
    /// as assigned with the correct owner info.
    /// </summary>
    [Fact]
    public async Task CabinStack_PlayerJoin_CabinAssignedToPlayer()
    {
        var client = await Farmers.ConnectNewAsync(ct: TestContext.Current.CancellationToken);

        // Poll until cabin ownership appears (server needs time to sync farmhand
        // isCustomized flag and Name after character creation)
        var ownedCabin = await WaitForCabinAssignedAsync(
            client.FarmerName,
            TestContext.Current.CancellationToken
        );

        // Log all cabins for diagnostics
        var cabins = await ServerApi.GetCabins(TestContext.Current.CancellationToken);
        Log($"Cabins after join ({cabins!.Cabins.Count} total):");
        foreach (var c in cabins.Cabins)
        {
            LogDetail(
                $"({c.TileX},{c.TileY}) type={c.Type} hidden={c.IsHidden} assigned={c.IsAssigned} owner='{c.OwnerName}' id={c.OwnerId}"
            );
        }

        Assert.NotNull(ownedCabin);
        Assert.True(ownedCabin.OwnerId != 0, "Owner ID should be set");
        LogSuccess(
            $"Found cabin owned by '{client.FarmerName}' at ({ownedCabin.TileX},{ownedCabin.TileY})"
        );
    }

    /// <summary>
    /// Verifies that our farmer appears as both a customized farmhand (/farmhands)
    /// and an assigned cabin owner (/cabins), confirming cross-endpoint consistency.
    /// </summary>
    [Fact]
    public async Task Cabins_OurFarmerAppearsInBothEndpoints()
    {
        var client = await Farmers.ConnectNewAsync(ct: TestContext.Current.CancellationToken);

        // Poll until our farmer appears in both /cabins and /farmhands
        CabinInfoResponse? ourCabin = null;
        ServerFarmhandInfo? ourFarmhand = null;
        var synced = await PollingHelper.WaitUntilAsync(
            WaitName.Polling_CabinStrategy_FarmerSyncedCabinAndFarmhand,
            async () =>
            {
                var cabins = await ServerApi.GetCabins(TestContext.Current.CancellationToken);
                var farmhands = await ServerApi.GetFarmhands();
                ourCabin = cabins?.Cabins.FirstOrDefault(c =>
                    c.OwnerName.Equals(client.FarmerName, StringComparison.OrdinalIgnoreCase)
                    && c.IsAssigned
                );
                ourFarmhand = farmhands?.Farmhands.FirstOrDefault(f =>
                    f.Name.Equals(client.FarmerName, StringComparison.OrdinalIgnoreCase)
                    && f.IsCustomized
                );
                return ourCabin != null && ourFarmhand != null;
            },
            TestTimings.CabinAssignmentTimeout,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(
            synced,
            $"Farmer '{client.FarmerName}' should appear in both /cabins and /farmhands"
        );
        Log($"Cabin: owner={ourCabin!.OwnerName}, assigned={ourCabin.IsAssigned}");
        Log($"Farmhand: name={ourFarmhand!.Name}, customized={ourFarmhand.IsCustomized}");
    }

    /// <summary>
    /// Verifies that deleting a farmhand frees the cabin slot. The farmhand
    /// is removed from the customized list and a cabin becomes available.
    /// </summary>
    [Fact]
    public async Task Cabins_AfterDeletion_SlotIsFreed()
    {
        // Create, join, disconnect, and wait for persistence
        var client = await Farmers.ConnectNewAsync(ct: TestContext.Current.CancellationToken);
        await Farmers.DisconnectAndWaitForPersistenceAsync(
            client.FarmerName,
            TestContext.Current.CancellationToken
        );

        // Delete the farmhand
        Log($"Deleting farmhand '{client.FarmerName}'...");
        var deleteResult = await ServerApi.WaitForFarmhandDeletedByNameAsync(
            client.FarmerName,
            ct: TestContext.Current.CancellationToken
        );
        Assert.True(
            deleteResult?.Success,
            $"Delete should succeed: {deleteResult?.Error ?? "timeout"}"
        );
        Farmers.CreatedFarmers.RemoveAll(f => f.Uid == client.JoinResult.UniqueMultiplayerId);

        // Poll until deletion is reflected: our farmhand should no longer be customized
        // and at least one available cabin slot should exist.
        // NOTE: We only check our own farmhand and available slots, not global invariants
        // like customizedCount == assignedCount, because other tests on the shared server
        // may be concurrently creating/deleting farmhands.
        CabinsResponse? cabins = null;
        ServerFarmhandsResponse? farmhands = null;
        await PollingHelper.WaitUntilAsync(
            WaitName.Polling_CabinStrategy_FarmerDeletionReflected,
            async () =>
            {
                cabins = await ServerApi.GetCabins(TestContext.Current.CancellationToken);
                farmhands = await ServerApi.GetFarmhands();
                if (cabins == null || farmhands == null)
                    return false;
                // Deletion is reflected when our farmer is gone and an uncustomized slot exists
                var ourFarmerGone = !farmhands.Farmhands.Any(f =>
                    f.IsCustomized
                    && f.Name.Equals(client.FarmerName, StringComparison.OrdinalIgnoreCase)
                );
                return ourFarmerGone && cabins.AvailableCount >= 1;
            },
            TestTimings.NetworkSyncTimeout,
            cancellationToken: TestContext.Current.CancellationToken
        );

        var customizedCount = farmhands!.Farmhands.Count(f => f.IsCustomized);
        var uncustomizedCount = farmhands.Farmhands.Count(f => !f.IsCustomized);

        Log(
            $"Cabins: total={cabins!.TotalCount}, assigned={cabins.AssignedCount}, available={cabins.AvailableCount}"
        );
        Log($"Farmhands: customized={customizedCount}, uncustomized={uncustomizedCount}");

        // Total = Assigned + Available (cabin-side consistency)
        Assert.Equal(cabins.TotalCount, cabins.AssignedCount + cabins.AvailableCount);

        // Our deleted farmhand should not be in the customized list
        Assert.DoesNotContain(
            farmhands.Farmhands,
            f =>
                f.IsCustomized
                && f.Name.Equals(client.FarmerName, StringComparison.OrdinalIgnoreCase)
        );

        // Should have at least one available slot after deletion
        Assert.True(
            cabins.AvailableCount >= 1,
            "After deletion, there should be at least 1 available cabin slot"
        );
    }

    #endregion
}
