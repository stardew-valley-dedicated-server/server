using JunimoServer.Tests.Fixtures;
using JunimoServer.Tests.Helpers;
using Xunit;
using Xunit.Abstractions;

namespace JunimoServer.Tests;

/// <summary>
/// Integration tests for farmer creation and persistence.
///
/// Uses IntegrationTestBase which provides:
/// - Automatic retry on connection failures
/// - Exception monitoring with early abort
/// - Server/client log streaming
/// </summary>
[Collection("Integration")]
public class FarmerCreationTests : IntegrationTestBase
{
    public FarmerCreationTests(IntegrationTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    /// <summary>
    /// Tests that a newly created farmer persists after disconnecting and reconnecting.
    /// Uses retry logic for the connection which can sometimes get stuck.
    /// </summary>
    [Fact]
    public async Task CreateFarmer_ExitAndReconnect_FarmerPersists()
    {
        // Ensure we're fully disconnected first
        await EnsureDisconnectedAsync();
        await AssertNoExceptionsAsync("initial disconnect");

        // Generate unique farmer name and track for cleanup
        var farmerName = GenerateFarmerName();
        var favoriteThing = "Testing";
        TrackFarmer(farmerName);

        // Step 1: Connect to server with retry (handles "stuck connecting" issue)
        var connectResult = await ConnectWithRetryAsync();
        AssertConnectionSuccess(connectResult);

        await AssertNoExceptionsAsync("after connecting");

        // Step 2: Find an uncustomized slot
        var farmhands = connectResult.Farmhands!;
        var newSlot = farmhands.Slots.FirstOrDefault(s => !s.IsCustomized);
        Assert.NotNull(newSlot);

        // Step 3: Select the uncustomized farmhand slot
        var selectResult = await GameClient.Farmhands.Select(newSlot.Index);
        Assert.NotNull(selectResult);
        Assert.True(selectResult.Success, $"Select farmhand failed: {selectResult.Error}");

        // Step 4: Wait for character customization menu
        var charWait = await GameClient.Wait.ForCharacter(TestTimings.CharacterMenuTimeout);
        Assert.NotNull(charWait);
        Assert.True(charWait.Success, $"Wait for character menu failed: {charWait.Error}");

        await AssertNoExceptionsAsync("after opening character menu");

        // Step 5: Set name and favorite thing
        var customizeResult = await GameClient.Character.Customize(farmerName, favoriteThing);
        Assert.NotNull(customizeResult);
        Assert.True(customizeResult.Success, $"Customize failed: {customizeResult.Error}");

        // Wait for game to sync textbox values
        await Task.Delay(TestTimings.CharacterCreationSyncDelay);

        // Step 6: Confirm character creation
        var confirmResult = await GameClient.Character.Confirm();
        Assert.NotNull(confirmResult);
        Assert.True(confirmResult.Success, $"Confirm failed: {confirmResult.Error}");

        // Step 7: Wait for world to be ready
        var worldWait = await GameClient.Wait.ForWorldReady(TestTimings.WorldReadyTimeout);
        Assert.NotNull(worldWait);
        Assert.True(worldWait.Success, $"Wait for world ready failed: {worldWait.Error}");

        // Poll until game state confirms we're connected and in-game
        await PollingHelper.WaitUntilAsync(async () =>
        {
            var state = await GameClient.GetState();
            return state?.IsConnected == true && state.IsInGame;
        }, TestTimings.NetworkSyncTimeout);

        Log($"Created farmer '{farmerName}'");
        await AssertNoExceptionsAsync("after entering world");

        // Step 8: Exit to title
        var exitResult = await GameClient.Exit();
        Assert.NotNull(exitResult);
        Assert.True(exitResult.Success, $"Exit failed: {exitResult.Error}");

        var titleWait = await GameClient.Wait.ForTitle(TestTimings.TitleScreenTimeout);
        Assert.NotNull(titleWait);
        Assert.True(titleWait.Success, $"Wait for title failed: {titleWait.Error}");

        // Wait for full disconnection
        await EnsureDisconnectedAsync();
        await AssertNoExceptionsAsync("after disconnecting");

        // Step 9: Reconnect with retry
        var reconnectResult = await ConnectWithRetryAsync();
        AssertConnectionSuccess(reconnectResult);

        // Step 10: Verify the farmer we created exists
        var reconnectFarmhands = reconnectResult.Farmhands!;
        if (VerboseLogging)
        {
            LogDetail($"Farmhand slots ({reconnectFarmhands.Slots.Count}):");
            foreach (var slot in reconnectFarmhands.Slots)
                LogDetail($"  Slot {slot.Index}: '{slot.Name}' (customized: {slot.IsCustomized})");
        }

        var ourFarmer = reconnectFarmhands.Slots.FirstOrDefault(s => s.Name == farmerName && s.IsCustomized);
        Assert.NotNull(ourFarmer);
        LogSuccess($"Farmer '{farmerName}' persisted at slot {ourFarmer.Index}");

        await AssertNoExceptionsAsync("at end of test");
    }

    /// <summary>
    /// Tests that we can join a server and see the farmhand selection screen.
    /// Uses the retry connection helper.
    /// </summary>
    [Fact]
    public async Task JoinServer_ShouldShowFarmhandSelection()
    {
        // Ensure disconnected
        await EnsureDisconnectedAsync();

        // Connect with retry
        var connectResult = await ConnectWithRetryAsync();
        AssertConnectionSuccess(connectResult);

        // Verify we have farmhand slots
        Assert.NotNull(connectResult.Farmhands);
        Assert.NotEmpty(connectResult.Farmhands.Slots);

        if (VerboseLogging)
        {
            LogDetail($"Farmhand slots ({connectResult.Farmhands.Slots.Count}):");
            foreach (var slot in connectResult.Farmhands.Slots)
                LogDetail($"  Slot {slot.Index}: '{slot.Name}' (customized: {slot.IsCustomized})");
        }

        await AssertNoExceptionsAsync("at end of test");
    }

    /// <summary>
    /// Demonstrates using JoinWorldWithRetryAsync for a complete join flow.
    /// </summary>
    [Fact]
    public async Task JoinWorld_WithRetry_ShouldEnterGame()
    {
        await EnsureDisconnectedAsync();

        var farmerName = GenerateFarmerName("Retry");
        TrackFarmer(farmerName);

        // This single call handles connecting, selecting slot, character creation, and world ready
        var joinResult = await JoinWorldWithRetryAsync(
            farmerName,
            favoriteThing: "RetryTesting",
            preferExistingFarmer: false);

        AssertJoinSuccess(joinResult);

        await AssertNoExceptionsAsync("after joining world");
    }

}
