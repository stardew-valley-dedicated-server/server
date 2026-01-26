using JunimoServer.Tests.Clients;
using JunimoServer.Tests.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace JunimoServer.Tests;

/// <summary>
/// Integration tests for farmer creation and persistence.
///
/// Uses IntegrationTestFixture which automatically manages:
/// - Server container (via Testcontainers)
/// - Game test client (local Stardew Valley)
/// </summary>
[Collection("Integration")]
public class FarmerCreationTests : IDisposable, IAsyncLifetime
{
    private readonly IntegrationTestFixture _fixture;
    private readonly GameTestClient _gameClient;
    private readonly ServerApiClient _serverApi;
    private readonly ITestOutputHelper _output;
    private readonly List<string> _createdFarmers = new();

    public FarmerCreationTests(IntegrationTestFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
        _gameClient = new GameTestClient();
        _serverApi = new ServerApiClient(_fixture.ServerBaseUrl);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        // Clean up any farmers created during tests
        foreach (var farmerName in _createdFarmers)
        {
            try
            {
                _output.WriteLine($"Cleaning up farmer: {farmerName}");
                var result = await _serverApi.DeleteFarmhand(farmerName);
                if (result?.Success == true)
                {
                    _output.WriteLine($"  Deleted successfully");
                }
                else if (result?.Error?.Contains("not found", StringComparison.OrdinalIgnoreCase) == true)
                {
                    // Farmer doesn't exist, that's fine (test may have failed before creating it)
                    _output.WriteLine($"  Farmer not found (ok)");
                }
                else
                {
                    _output.WriteLine($"  Delete failed: {result?.Error ?? "unknown error"}");
                }
            }
            catch (Exception ex)
            {
                _output.WriteLine($"  Cleanup error: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Tests that a newly created farmer persists after disconnecting and reconnecting:
    /// 1. Join server via invite code
    /// 2. Select an uncustomized farmhand slot
    /// 3. Create a new farmer (set name and favorite thing)
    /// 4. Enter the game world
    /// 5. Exit to title
    /// 6. Reconnect to the server
    /// 7. Verify the created farmer exists with the correct name
    /// </summary>
    [Fact]
    public async Task CreateFarmer_ExitAndReconnect_FarmerPersists()
    {
        // Ensure we're fully disconnected first (no leftover connection from previous test)
        await _gameClient.Navigate("title");
        var disconnectWait = await _gameClient.Wait.ForDisconnected(10000);
        Assert.True(disconnectWait?.Success, "Should be disconnected before starting test");

        // Keep name short — the game's TextBox UI truncates text exceeding its pixel width.
        // The test-client bypasses this via limitWidth, but we use a short name for safety.
        var farmerName = $"Test{DateTime.UtcNow.Ticks % 1000}";
        var favoriteThing = "Testing";

        // Track for cleanup
        _createdFarmers.Add(farmerName);

        _output.WriteLine($"Creating farmer with name: {farmerName}");

        // Step 1: Get the invite code from server API
        var status = await _serverApi.GetStatus();
        Assert.NotNull(status);
        Assert.True(status.IsOnline, "Server should be online");
        Assert.False(string.IsNullOrEmpty(status.InviteCode), "Server should have an invite code");
        _output.WriteLine($"Server online with invite code: {status.InviteCode}");

        // Step 2: Navigate to coop menu, wait for it to load, then switch to join tab
        var navigateResult = await _gameClient.Navigate("coopmenu");
        Assert.NotNull(navigateResult);
        Assert.True(navigateResult.Success, $"Navigate failed: {navigateResult.Error}");

        // Wait for CoopMenu to be ready
        var menuWait = await _gameClient.Wait.ForMenu("CoopMenu", 10000);
        Assert.True(menuWait?.Success, $"Wait for CoopMenu failed: {menuWait?.Error}");

        var tabResult = await _gameClient.Coop.Tab(0); // 0 = JOIN_TAB
        Assert.NotNull(tabResult);
        Assert.True(tabResult.Success, $"Tab switch failed: {tabResult.Error}");

        // Step 3: Open invite code input dialog
        var openResult = await _gameClient.Coop.OpenInviteCodeMenu();
        Assert.NotNull(openResult);
        Assert.True(openResult.Success, $"Open invite code menu failed: {openResult.Error}");

        // Step 4: Wait for text input menu to appear
        var textInputWait = await _gameClient.Wait.ForTextInput(10000);
        Assert.NotNull(textInputWait);
        Assert.True(textInputWait.Success, $"Wait for text input menu failed: {textInputWait.Error}");
        _output.WriteLine($"Text input menu appeared after {textInputWait.WaitedMs}ms");

        // Step 5: Submit the invite code
        var submitResult = await _gameClient.Coop.SubmitInviteCode(status.InviteCode);
        Assert.NotNull(submitResult);
        Assert.True(submitResult.Success, $"Submit invite code failed: {submitResult.Error}");
        _output.WriteLine("Submitted invite code, waiting for farmhand selection...");

        // Step 6: Wait for farmhand selection screen
        var farmhandWait = await _gameClient.Wait.ForFarmhands(60000);
        Assert.NotNull(farmhandWait);
        Assert.True(farmhandWait.Success, $"Wait for farmhands failed: {farmhandWait.Error}");
        _output.WriteLine($"Farmhand menu appeared after {farmhandWait.WaitedMs}ms");

        // Give the game time to load farmhand data
        await Task.Delay(2000);

        // Step 7: Get available farmhand slots and find an uncustomized one
        var farmhands = await _gameClient.Farmhands.GetSlots();
        Assert.NotNull(farmhands);
        Assert.True(farmhands.Success, $"Get farmhands failed: {farmhands.Error}");
        Assert.NotEmpty(farmhands.Slots);

        var newSlot = farmhands.Slots.FirstOrDefault(s => !s.IsCustomized);
        Assert.NotNull(newSlot);
        _output.WriteLine($"Found uncustomized slot at index {newSlot.Index}");

        // Step 8: Select the uncustomized farmhand slot (opens CharacterCustomization)
        var selectResult = await _gameClient.Farmhands.Select(newSlot.Index);
        Assert.NotNull(selectResult);
        Assert.True(selectResult.Success, $"Select farmhand failed: {selectResult.Error}");

        // Step 9: Wait for character customization menu
        var charWait = await _gameClient.Wait.ForCharacter(30000);
        Assert.NotNull(charWait);
        Assert.True(charWait.Success, $"Wait for character menu failed: {charWait.Error}");
        _output.WriteLine($"Character customization menu appeared after {charWait.WaitedMs}ms");

        // Step 10: Set name and favorite thing
        var customizeResult = await _gameClient.Character.Customize(farmerName, favoriteThing);
        Assert.NotNull(customizeResult);
        Assert.True(customizeResult.Success, $"Customize failed: {customizeResult.Error}");
        _output.WriteLine($"Set character data - Name: {farmerName}, FavoriteThing: {favoriteThing}");

        // Wait for game to sync textbox values to player properties (happens in draw cycle)
        await Task.Delay(200);

        // Step 11: Confirm character creation
        var confirmResult = await _gameClient.Character.Confirm();
        Assert.NotNull(confirmResult);
        Assert.True(confirmResult.Success, $"Confirm failed: {confirmResult.Error}");
        _output.WriteLine("Character confirmed");

        // Step 12: Wait for world to be ready (we're now in the game)
        var worldWait = await _gameClient.Wait.ForWorldReady(60000);
        Assert.NotNull(worldWait);
        Assert.True(worldWait.Success, $"Wait for world ready failed: {worldWait.Error}");
        _output.WriteLine($"World ready after {worldWait.WaitedMs}ms");

        // Wait for network sync to propagate player data to server
        // NetFields sync is asynchronous and may take a few ticks
        await Task.Delay(2000);
        _output.WriteLine("Waited for network sync");

        // Step 13: Exit to title
        var exitResult = await _gameClient.Exit();
        Assert.NotNull(exitResult);
        Assert.True(exitResult.Success, $"Exit failed: {exitResult.Error}");

        // Step 14: Wait for title screen and full disconnection
        var titleWait = await _gameClient.Wait.ForTitle(30000);
        Assert.NotNull(titleWait);
        Assert.True(titleWait.Success, $"Wait for title failed: {titleWait.Error}");
        _output.WriteLine($"Returned to title after {titleWait.WaitedMs}ms");

        // Wait for full disconnection before reconnecting
        var disconnectBeforeReconnect = await _gameClient.Wait.ForDisconnected(10000);
        Assert.True(disconnectBeforeReconnect?.Success, "Should be fully disconnected before reconnecting");
        _output.WriteLine("Fully disconnected, ready to reconnect");

        // Step 15: Reconnect - navigate to coop menu, wait for it, then switch to join tab
        var reconnectNav = await _gameClient.Navigate("coopmenu");
        Assert.NotNull(reconnectNav);
        Assert.True(reconnectNav.Success, $"Reconnect navigate failed: {reconnectNav.Error}");

        // Wait for CoopMenu to be ready
        var reconnectMenuWait = await _gameClient.Wait.ForMenu("CoopMenu", 10000);
        Assert.True(reconnectMenuWait?.Success, $"Wait for CoopMenu on reconnect failed: {reconnectMenuWait?.Error}");

        var reconnectTab = await _gameClient.Coop.Tab(0); // 0 = JOIN_TAB
        Assert.NotNull(reconnectTab);
        Assert.True(reconnectTab.Success, $"Reconnect tab failed: {reconnectTab.Error}");

        // Step 16: Open invite code menu for reconnection
        var reconnectOpenResult = await _gameClient.Coop.OpenInviteCodeMenu();
        Assert.NotNull(reconnectOpenResult);
        Assert.True(reconnectOpenResult.Success, $"Reconnect open invite code menu failed: {reconnectOpenResult.Error}");

        // Step 17: Wait for text input menu
        var reconnectTextInputWait = await _gameClient.Wait.ForTextInput(10000);
        Assert.NotNull(reconnectTextInputWait);
        Assert.True(reconnectTextInputWait.Success, $"Wait for text input on reconnect failed: {reconnectTextInputWait.Error}");

        // Step 18: Submit invite code to rejoin
        var rejoinResult = await _gameClient.Coop.SubmitInviteCode(status.InviteCode);
        Assert.NotNull(rejoinResult);
        Assert.True(rejoinResult.Success, $"Rejoin failed: {rejoinResult.Error}");

        // Step 19: Wait for farmhand selection again
        var reconnectFarmhandWait = await _gameClient.Wait.ForFarmhands(60000);
        Assert.NotNull(reconnectFarmhandWait);
        Assert.True(reconnectFarmhandWait.Success, $"Wait for farmhands on reconnect failed: {reconnectFarmhandWait.Error}");
        _output.WriteLine($"Farmhand menu appeared on reconnect after {reconnectFarmhandWait.WaitedMs}ms");

        // Give the game time to load farmhand data
        await Task.Delay(2000);

        // Step 20: Verify the farmer we created exists with the correct name
        var reconnectFarmhands = await _gameClient.Farmhands.GetSlots();
        Assert.NotNull(reconnectFarmhands);
        Assert.True(reconnectFarmhands.Success, $"Get farmhands on reconnect failed: {reconnectFarmhands.Error}");

        _output.WriteLine($"Found {reconnectFarmhands.Slots.Count} farmhand slots on reconnect:");
        foreach (var slot in reconnectFarmhands.Slots)
        {
            _output.WriteLine($"  Slot {slot.Index}: Name='{slot.Name}', IsCustomized={slot.IsCustomized}, IsEmpty={slot.IsEmpty}");
        }

        var ourFarmer = reconnectFarmhands.Slots.FirstOrDefault(s =>
            s.Name == farmerName && s.IsCustomized);

        Assert.NotNull(ourFarmer);
        _output.WriteLine($"SUCCESS: Found our farmer '{farmerName}' at slot index {ourFarmer.Index}");

        // Return to title and wait for full disconnection
        await _gameClient.Navigate("title");
        await _gameClient.Wait.ForDisconnected(10000);
    }

    /// <summary>
    /// Tests that we can join a server and see the farmhand selection screen.
    /// </summary>
    [Fact]
    public async Task JoinServer_ShouldShowFarmhandSelection()
    {
        // Ensure we're fully disconnected first (no leftover connection from previous test)
        await _gameClient.Navigate("title");
        var disconnectWait = await _gameClient.Wait.ForDisconnected(10000);
        Assert.True(disconnectWait?.Success, "Should be disconnected before starting test");

        // Get invite code
        var status = await _serverApi.GetStatus();
        Assert.NotNull(status);
        Assert.True(status.IsOnline, "Server should be online");

        // Navigate and join (JOIN_TAB = 0)
        var navigateResult = await _gameClient.Navigate("coopmenu");
        Assert.True(navigateResult?.Success, "Navigate failed");

        // Wait for CoopMenu to be ready
        var menuWait = await _gameClient.Wait.ForMenu("CoopMenu", 10000);
        Assert.True(menuWait?.Success, "Wait for CoopMenu failed");

        var tabResult = await _gameClient.Coop.Tab(0); // 0 = JOIN_TAB
        Assert.True(tabResult?.Success, "Tab switch failed");

        // Open invite code menu
        var openResult = await _gameClient.Coop.OpenInviteCodeMenu();
        Assert.True(openResult?.Success, "Open invite code menu failed");

        // Wait for text input menu
        var textInputWait = await _gameClient.Wait.ForTextInput(10000);
        Assert.True(textInputWait?.Success, "Wait for text input failed");

        // Submit invite code
        var submitResult = await _gameClient.Coop.SubmitInviteCode(status.InviteCode);
        Assert.True(submitResult?.Success, "Submit invite code failed");

        // Wait for farmhand selection
        var farmhandWait = await _gameClient.Wait.ForFarmhands(60000);
        Assert.True(farmhandWait?.Success, "Should reach farmhand selection screen");

        // Give the game time to load farmhand data
        await Task.Delay(2000);

        // Verify we can get farmhand slots
        var farmhands = await _gameClient.Farmhands.GetSlots();
        Assert.NotNull(farmhands);
        Assert.True(farmhands.Success, "Should be able to get farmhand slots");
        Assert.NotEmpty(farmhands.Slots);

        _output.WriteLine($"Found {farmhands.Slots.Count} farmhand slots");
        foreach (var slot in farmhands.Slots)
        {
            _output.WriteLine($"  Slot {slot.Index}: {slot.Name} (customized: {slot.IsCustomized}, empty: {slot.IsEmpty})");
        }

        // Return to title and wait for full disconnection
        await _gameClient.Navigate("title");
        await _gameClient.Wait.ForDisconnected(10000);
    }

    public void Dispose()
    {
        _gameClient.Dispose();
        _serverApi.Dispose();
    }
}
