using JunimoServer.Tests.Fixtures;
using JunimoServer.Tests.Helpers;
using Xunit;
using Xunit.Abstractions;
using static JunimoServer.Tests.Helpers.TestTimings;

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

        Log($"Creating farmer with name: {farmerName}");

        // Verify server is online
        Assert.NotNull(ServerStatus);
        Assert.True(ServerStatus.IsOnline, "Server should be online");
        Assert.False(string.IsNullOrEmpty(InviteCode), "Server should have an invite code");
        Log($"Server online with invite code: {InviteCode}");

        // Step 1: Connect to server with retry (handles "stuck connecting" issue)
        LogSection("Connecting to server");
        var connectResult = await ConnectWithRetryAsync();
        AssertConnectionSuccess(connectResult);
        Log($"Connected after {connectResult.AttemptsUsed} attempt(s)");

        await AssertNoExceptionsAsync("after connecting");

        // Step 2: Find an uncustomized slot
        var farmhands = connectResult.Farmhands!;
        var newSlot = farmhands.Slots.FirstOrDefault(s => !s.IsCustomized);
        Assert.NotNull(newSlot);
        Log($"Found uncustomized slot at index {newSlot.Index}");

        // Step 3: Select the uncustomized farmhand slot
        LogSection("Creating character");
        var selectResult = await GameClient.Farmhands.Select(newSlot.Index);
        Assert.NotNull(selectResult);
        Assert.True(selectResult.Success, $"Select farmhand failed: {selectResult.Error}");

        // Step 4: Wait for character customization menu
        var charWait = await GameClient.Wait.ForCharacter(CharacterMenuTimeoutMs);
        Assert.NotNull(charWait);
        Assert.True(charWait.Success, $"Wait for character menu failed: {charWait.Error}");
        Log($"Character customization menu appeared after {charWait.WaitedMs}ms");

        await AssertNoExceptionsAsync("after opening character menu");

        // Step 5: Set name and favorite thing
        var customizeResult = await GameClient.Character.Customize(farmerName, favoriteThing);
        Assert.NotNull(customizeResult);
        Assert.True(customizeResult.Success, $"Customize failed: {customizeResult.Error}");
        Log($"Set character data - Name: {farmerName}, FavoriteThing: {favoriteThing}");

        // Wait for game to sync textbox values
        await Task.Delay(CharacterCreationSyncDelayMs);

        // Step 6: Confirm character creation
        var confirmResult = await GameClient.Character.Confirm();
        Assert.NotNull(confirmResult);
        Assert.True(confirmResult.Success, $"Confirm failed: {confirmResult.Error}");
        Log("Character confirmed");

        // Step 7: Wait for world to be ready
        LogSection("Entering world");
        var worldWait = await GameClient.Wait.ForWorldReady(WorldReadyTimeoutMs);
        Assert.NotNull(worldWait);
        Assert.True(worldWait.Success, $"Wait for world ready failed: {worldWait.Error}");
        Log($"World ready after {worldWait.WaitedMs}ms");

        // Wait for network sync
        await Task.Delay(NetworkSyncDelayMs);
        Log("Waited for network sync");

        await AssertNoExceptionsAsync("after entering world");

        // Step 8: Exit to title
        LogSection("Disconnecting");
        var exitResult = await GameClient.Exit();
        Assert.NotNull(exitResult);
        Assert.True(exitResult.Success, $"Exit failed: {exitResult.Error}");

        var titleWait = await GameClient.Wait.ForTitle(TitleScreenTimeoutMs);
        Assert.NotNull(titleWait);
        Assert.True(titleWait.Success, $"Wait for title failed: {titleWait.Error}");
        Log($"Returned to title after {titleWait.WaitedMs}ms");

        // Wait for full disconnection
        await EnsureDisconnectedAsync();
        Log("Fully disconnected, ready to reconnect");

        await AssertNoExceptionsAsync("after disconnecting");

        // Step 9: Reconnect with retry
        LogSection("Reconnecting");
        var reconnectResult = await ConnectWithRetryAsync();
        AssertConnectionSuccess(reconnectResult);
        Log($"Reconnected after {reconnectResult.AttemptsUsed} attempt(s)");

        // Step 10: Verify the farmer we created exists
        LogSection("Verifying farmer");
        var reconnectFarmhands = reconnectResult.Farmhands!;
        Log($"Found {reconnectFarmhands.Slots.Count} farmhand slots on reconnect:");
        foreach (var slot in reconnectFarmhands.Slots)
        {
            Log($"  Slot {slot.Index}: Name='{slot.Name}', IsCustomized={slot.IsCustomized}, IsEmpty={slot.IsEmpty}");
        }

        var ourFarmer = reconnectFarmhands.Slots.FirstOrDefault(s =>
            s.Name == farmerName && s.IsCustomized);

        Assert.NotNull(ourFarmer);
        Log($"SUCCESS: Found our farmer '{farmerName}' at slot index {ourFarmer.Index}");

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

        // Verify server is online
        Assert.NotNull(ServerStatus);
        Assert.True(ServerStatus.IsOnline, "Server should be online");

        // Connect with retry
        var connectResult = await ConnectWithRetryAsync();
        AssertConnectionSuccess(connectResult);

        // Verify we have farmhand slots
        Assert.NotNull(connectResult.Farmhands);
        Assert.NotEmpty(connectResult.Farmhands.Slots);

        Log($"Found {connectResult.Farmhands.Slots.Count} farmhand slots (connected after {connectResult.AttemptsUsed} attempt(s))");
        foreach (var slot in connectResult.Farmhands.Slots)
        {
            Log($"  Slot {slot.Index}: {slot.Name} (customized: {slot.IsCustomized}, empty: {slot.IsEmpty})");
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

        Log($"Joining world with farmer: {farmerName}");

        // This single call handles:
        // - Connecting to server (with retry)
        // - Selecting farmhand slot
        // - Character creation (if needed)
        // - Waiting for world ready
        var joinResult = await JoinWorldWithRetryAsync(
            farmerName,
            favoriteThing: "RetryTesting",
            preferExistingFarmer: false);

        AssertJoinSuccess(joinResult);
        Log($"Successfully joined world at slot {joinResult.SlotIndex} after {joinResult.AttemptsUsed} attempt(s)");

        // Verify we're in the game
        var status = await GameClient.GetMenu();
        Log($"Current menu after join: {status?.Type ?? "none"}");

        await AssertNoExceptionsAsync("after joining world");
    }

    /// <summary>
    /// Test demonstrating how to temporarily suppress exception abort
    /// for scenarios where exceptions are expected.
    /// </summary>
    [Fact]
    public async Task ExceptionSuppression_CanBeTemporarilyDisabled()
    {
        await EnsureDisconnectedAsync();

        // Connect normally
        var connectResult = await ConnectWithRetryAsync();
        AssertConnectionSuccess(connectResult);

        // Temporarily suppress exception abort for a section where
        // we might see expected errors (e.g., testing error handling)
        using (SuppressExceptionAbort())
        {
            Log("Exception abort suppressed for this section");

            // Even if exceptions occur here, they won't fail the test immediately
            // (In a real scenario, you might trigger expected errors here)
        }

        Log("Exception abort re-enabled");

        // Check for exceptions at the end (this will fail if there were any)
        await AssertNoExceptionsAsync("at end of test");
    }
}
