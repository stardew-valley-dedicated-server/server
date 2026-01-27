using JunimoServer.Tests.Clients;
using JunimoServer.Tests.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace JunimoServer.Tests;

/// <summary>
/// Integration tests for farmhand management via the server API.
/// Verifies farmhand listing, customization state, and deletion behavior.
/// </summary>
[Collection("Integration")]
public class FarmhandManagementTests : IDisposable, IAsyncLifetime
{
    private readonly IntegrationTestFixture _fixture;
    private readonly GameTestClient _gameClient;
    private readonly ServerApiClient _serverApi;
    private readonly ITestOutputHelper _output;
    private readonly List<string> _createdFarmers = new();

    public FarmhandManagementTests(IntegrationTestFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
        _gameClient = new GameTestClient();
        _serverApi = new ServerApiClient(_fixture.ServerBaseUrl);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        // Ensure we're disconnected so online farmers can be deleted
        try
        {
            await _gameClient.Navigate("title");
            await _gameClient.Wait.ForDisconnected(10000);
        }
        catch (Exception ex)
        {
            _output.WriteLine($"  Error disconnecting during cleanup: {ex.Message}");
        }

        // Wait for server to process disconnection
        await Task.Delay(3000);

        foreach (var farmerName in _createdFarmers)
        {
            try
            {
                _output.WriteLine($"Cleaning up farmer: {farmerName}");
                var result = await _serverApi.DeleteFarmhand(farmerName);
                if (result?.Success == true)
                    _output.WriteLine($"  Deleted successfully");
                else if (result?.Error?.Contains("not found", StringComparison.OrdinalIgnoreCase) == true)
                    _output.WriteLine($"  Farmer not found (ok)");
                else
                    _output.WriteLine($"  Delete failed: {result?.Error ?? "unknown error"}");
            }
            catch (Exception ex)
            {
                _output.WriteLine($"  Cleanup error: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Verifies that GET /farmhands (server API) correctly reflects which farmhand
    /// slots are customized vs uncustomized after a player creates a character.
    /// </summary>
    [Fact]
    public async Task ServerFarmhands_ReflectCustomizationState()
    {
        await EnsureDisconnected();

        var farmerName = $"Cust{DateTime.UtcNow.Ticks % 1000}";
        _createdFarmers.Add(farmerName);
        await JoinAndEnterWorld(farmerName);

        // Check server-side farmhand list
        var farmhands = await _serverApi.GetFarmhands();
        Assert.NotNull(farmhands);
        Assert.NotEmpty(farmhands.Farmhands);

        _output.WriteLine($"Server reports {farmhands.Farmhands.Count} farmhand(s):");
        foreach (var fh in farmhands.Farmhands)
        {
            _output.WriteLine($"  Name='{fh.Name}', IsCustomized={fh.IsCustomized}, Id={fh.Id}");
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
        _output.WriteLine($"Found {uncustomized.Count} uncustomized slot(s) as expected");

        await DisconnectFromServer();
    }

    /// <summary>
    /// Verifies that deleting an offline farmhand via DELETE /farmhands?name=X
    /// succeeds and the farmhand no longer appears in the list.
    /// </summary>
    [Fact]
    public async Task DeleteFarmhand_WhenOffline_Succeeds()
    {
        await EnsureDisconnected();

        var farmerName = $"Del{DateTime.UtcNow.Ticks % 1000}";
        _createdFarmers.Add(farmerName);
        await JoinAndEnterWorld(farmerName);

        // Disconnect so the farmhand is offline
        await DisconnectFromServer();

        // Wait for server to fully process the disconnection
        await Task.Delay(3000);

        // Delete the farmhand
        _output.WriteLine($"Deleting offline farmhand '{farmerName}'...");
        var deleteResult = await _serverApi.DeleteFarmhand(farmerName);
        Assert.NotNull(deleteResult);
        Assert.True(deleteResult.Success, $"Delete should succeed: {deleteResult.Error}");
        _output.WriteLine($"Delete response: {deleteResult.Message}");

        // Verify the farmhand is gone
        var farmhands = await _serverApi.GetFarmhands();
        Assert.NotNull(farmhands);
        var deleted = farmhands.Farmhands.FirstOrDefault(f =>
            f.Name.Equals(farmerName, StringComparison.OrdinalIgnoreCase));
        Assert.Null(deleted);
        _output.WriteLine($"Verified: farmhand '{farmerName}' no longer in list");

        // Remove from cleanup list since we already deleted it
        _createdFarmers.Remove(farmerName);
    }

    /// <summary>
    /// Verifies that attempting to delete a farmhand who is currently connected
    /// returns an error and does not remove them.
    /// </summary>
    [Fact]
    public async Task DeleteFarmhand_WhenOnline_Fails()
    {
        await EnsureDisconnected();

        var farmerName = $"Onl{DateTime.UtcNow.Ticks % 1000}";
        _createdFarmers.Add(farmerName);
        await JoinAndEnterWorld(farmerName);

        // Try to delete while still connected — should fail
        _output.WriteLine($"Attempting to delete online farmhand '{farmerName}'...");
        var deleteResult = await _serverApi.DeleteFarmhand(farmerName);
        Assert.NotNull(deleteResult);
        Assert.False(deleteResult.Success, "Delete should fail for an online farmhand");
        Assert.NotNull(deleteResult.Error);
        Assert.Contains("online", deleteResult.Error, StringComparison.OrdinalIgnoreCase);
        _output.WriteLine($"Delete correctly refused: {deleteResult.Error}");

        // Verify the farmhand still exists
        var farmhands = await _serverApi.GetFarmhands();
        Assert.NotNull(farmhands);
        var stillExists = farmhands.Farmhands.FirstOrDefault(f =>
            f.Name.Equals(farmerName, StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(stillExists);
        _output.WriteLine($"Verified: farmhand '{farmerName}' still exists after failed delete");

        await DisconnectFromServer();
    }

    private async Task EnsureDisconnected()
    {
        await _gameClient.Navigate("title");
        var disconnectWait = await _gameClient.Wait.ForDisconnected(10000);
        Assert.True(disconnectWait?.Success, "Should be disconnected before starting test");
    }

    private async Task DisconnectFromServer()
    {
        var exitResult = await _gameClient.Exit();
        Assert.True(exitResult?.Success, $"Exit failed: {exitResult?.Error}");

        await _gameClient.Wait.ForTitle(30000);
        await _gameClient.Wait.ForDisconnected(10000);
        _output.WriteLine("Disconnected from server");
    }

    /// <summary>
    /// Joins the server with a new farmer and enters the game world.
    /// </summary>
    private async Task JoinAndEnterWorld(string farmerName)
    {
        var status = await _serverApi.GetStatus();
        Assert.NotNull(status);
        Assert.True(status.IsOnline, "Server should be online");
        Assert.False(string.IsNullOrEmpty(status.InviteCode), "Server should have an invite code");

        var navigateResult = await _gameClient.Navigate("coopmenu");
        Assert.True(navigateResult?.Success, $"Navigate failed: {navigateResult?.Error}");

        var menuWait = await _gameClient.Wait.ForMenu("CoopMenu", 10000);
        Assert.True(menuWait?.Success, $"Wait for CoopMenu failed: {menuWait?.Error}");

        var tabResult = await _gameClient.Coop.Tab(0);
        Assert.True(tabResult?.Success, $"Tab switch failed: {tabResult?.Error}");

        var openResult = await _gameClient.Coop.OpenInviteCodeMenu();
        Assert.True(openResult?.Success, $"Open invite code menu failed: {openResult?.Error}");

        var textInputWait = await _gameClient.Wait.ForTextInput(10000);
        Assert.True(textInputWait?.Success, $"Wait for text input failed: {textInputWait?.Error}");

        var submitResult = await _gameClient.Coop.SubmitInviteCode(status.InviteCode);
        Assert.True(submitResult?.Success, $"Submit invite code failed: {submitResult?.Error}");

        var farmhandWait = await _gameClient.Wait.ForFarmhands(60000);
        Assert.True(farmhandWait?.Success, $"Wait for farmhands failed: {farmhandWait?.Error}");

        await Task.Delay(2000);

        var farmhands = await _gameClient.Farmhands.GetSlots();
        Assert.NotNull(farmhands);
        Assert.True(farmhands.Success, $"Get farmhands failed: {farmhands.Error}");
        Assert.NotEmpty(farmhands.Slots);

        var newSlot = farmhands.Slots.FirstOrDefault(s => !s.IsCustomized);
        Assert.NotNull(newSlot);

        var selectResult = await _gameClient.Farmhands.Select(newSlot.Index);
        Assert.True(selectResult?.Success, $"Select farmhand failed: {selectResult?.Error}");

        var charWait = await _gameClient.Wait.ForCharacter(30000);
        Assert.True(charWait?.Success, $"Wait for character menu failed: {charWait?.Error}");

        var customizeResult = await _gameClient.Character.Customize(farmerName, "Testing");
        Assert.True(customizeResult?.Success, $"Customize failed: {customizeResult?.Error}");

        await Task.Delay(200);

        var confirmResult = await _gameClient.Character.Confirm();
        Assert.True(confirmResult?.Success, $"Confirm failed: {confirmResult?.Error}");

        var worldWait = await _gameClient.Wait.ForWorldReady(60000);
        Assert.True(worldWait?.Success, $"Wait for world ready failed: {worldWait?.Error}");

        // Wait for network sync to propagate player data to server
        await Task.Delay(2000);
        _output.WriteLine($"Entered world as '{farmerName}'");
    }

    public void Dispose()
    {
        _gameClient.Dispose();
        _serverApi.Dispose();
    }
}
