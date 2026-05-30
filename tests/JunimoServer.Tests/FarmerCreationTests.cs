using JunimoServer.Tests.Clients;
using JunimoServer.Tests.Helpers;
using JunimoServer.Tests.Infrastructure;
using Xunit;

namespace JunimoServer.Tests;

/// <summary>
/// Integration tests for farmer creation and persistence.
///
/// Uses TestBase which provides:
/// - Automatic retry on connection failures
/// - Exception monitoring with early abort
/// - Server/client log streaming
/// </summary>
[TestServer(Isolation = IsolationMode.SharedAssembly)]
public class FarmerCreationTests : TestBase
{
    public FarmerCreationTests() { }

    /// <summary>
    /// Tests that a newly created farmer persists after disconnecting and reconnecting.
    /// Deliberately uses manual disconnect steps (Exit, EnsureDisconnected, WaitForPlayerRemoved,
    /// WaitForFarmhand) instead of helpers to validate each stage of the persistence lifecycle.
    /// </summary>
    [Fact(Skip = "Redundant: persistence verified by FarmhandManagementTests, reconnect by PasswordProtectionTests")]
    public async Task CreateFarmer_ExitAndReconnect_FarmerPersists()
    {
        // Join with a new farmer (forces new slot, handles auth automatically)
        var client = await Farmers.ConnectNewAsync(
            preferExistingFarmer: false,
            ct: TestContext.Current.CancellationToken);
        await Exceptions.AssertNoExceptionsAsync("initial connect");

        Log($"Created farmer '{client.FarmerName}'");
        await Exceptions.AssertNoExceptionsAsync("after entering world");

        var exitResult = await GameClient.Exit();
        Assert.NotNull(exitResult);
        Assert.True(exitResult.Success, $"Exit failed: {exitResult.Error}");

        var titleWait = await GameClient.Wait.ForTitle(TestTimings.TitleScreenTimeout);
        Assert.NotNull(titleWait);
        Assert.True(titleWait.Success, $"Wait for title failed: {titleWait.Error}");

        await Connect.EnsureDisconnectedAsync();

        // Wait for THIS farmer to fully disconnect from the server.
        // Don't wait for PlayerCount==0; other tests may have clients connected.
        await ServerApi.WaitForPlayerRemovedByIdAsync(client.JoinResult.UniqueMultiplayerId, ct: TestContext.Current.CancellationToken);

        // Wait for the server's farmhandData to reflect the customized farmer.
        // saveFarmhand() clones the live farmer data (including name + isCustomized)
        // into farmhandData on disconnect. Poll until the server API confirms it.
        var farmerPersisted = await ServerApi.WaitForFarmhandByNameAsync(client.FarmerName, requireCustomized: true, ct: TestContext.Current.CancellationToken);
        var serverFarmhands = await ServerApi.GetFarmhands(TestContext.Current.CancellationToken);

        // Log server-side state for diagnostics
        if (serverFarmhands?.Farmhands != null)
        {
            Log($"Server farmhands after disconnect ({serverFarmhands.Farmhands.Count}):");
            foreach (var fh in serverFarmhands.Farmhands)
                Log($"  '{fh.Name}' customized={fh.IsCustomized}");
        }

        if (!farmerPersisted)
        {
            var fhSummary = serverFarmhands?.Farmhands != null
                ? string.Join(", ", serverFarmhands.Farmhands.Select(fh => $"'{fh.Name}' customized={fh.IsCustomized}"))
                : "null";
            Assert.Fail($"Server did not persist farmer '{client.FarmerName}' after disconnect. Server farmhands: [{fhSummary}]");
        }

        await Exceptions.AssertNoExceptionsAsync("after disconnecting");

        // Reconnect with retry
        var reconnectResult = await Connect.WithRetryAsync(TestContext.Current.CancellationToken);
        Connect.AssertConnectionSuccess(reconnectResult);

        // Verify the farmer we created exists
        var reconnectFarmhands = reconnectResult.Farmhands!;

        // Always log slot details for diagnostics
        Log($"Farmhand slots after reconnect ({reconnectFarmhands.Farmhands.Count}):");
        foreach (var slot in reconnectFarmhands.Farmhands)
            Log($"  Slot {slot.Index}: name='{slot.Name}' customized={slot.IsCustomized} empty={slot.IsEmpty}");

        var ourFarmer = reconnectFarmhands.Farmhands.FirstOrDefault(s => s.Name == client.FarmerName && s.IsCustomized);
        if (ourFarmer == null)
        {
            var slotSummary = string.Join(", ", reconnectFarmhands.Farmhands.Select(s =>
                $"[{s.Index}] name='{s.Name}' customized={s.IsCustomized} empty={s.IsEmpty}"));
            Assert.Fail($"Farmer '{client.FarmerName}' not found after reconnect. Slots: {slotSummary}");
        }
        LogSuccess($"Farmer '{client.FarmerName}' persisted at slot {ourFarmer.Index}");
    }

    /// <summary>
    /// Tests that we can join a server and see the farmhand selection screen.
    /// Uses the retry connection helper.
    /// </summary>
    [Fact(Skip = "Redundant: farmhand slots validated by Connect.WithRetryAsync in every client test")]
    public async Task JoinServer_ShouldShowFarmhandSelection()
    {
        // Ensure disconnected
        await Connect.EnsureDisconnectedAsync();

        // Connect with retry
        var connectResult = await Connect.WithRetryAsync(TestContext.Current.CancellationToken);
        Connect.AssertConnectionSuccess(connectResult);

        // Verify we have farmhand slots
        Assert.NotNull(connectResult.Farmhands);
        Assert.NotEmpty(connectResult.Farmhands.Farmhands);

        LogTrace($"Farmhand slots ({connectResult.Farmhands.Farmhands.Count}):");
        foreach (var slot in connectResult.Farmhands.Farmhands)
            LogTrace($"  Slot {slot.Index}: '{slot.Name}' (customized: {slot.IsCustomized})");
    }

    /// <summary>
    /// Demonstrates using Farmers.ConnectNewAsync for a complete join flow.
    /// </summary>
    [Fact(Skip = "Redundant: Farmers.ConnectNewAsync exercised by every client test")]
    public async Task JoinWorld_WithRetry_ShouldEnterGame()
    {
        // This single call handles connecting, selecting slot, character creation, and world ready
        await Farmers.ConnectNewAsync(
            favoriteThing: "RetryTesting",
            preferExistingFarmer: false,
            ct: TestContext.Current.CancellationToken);

        await Exceptions.AssertNoExceptionsAsync("after joining world");
    }

}
