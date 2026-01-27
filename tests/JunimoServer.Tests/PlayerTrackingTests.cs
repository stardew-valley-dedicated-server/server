using JunimoServer.Tests.Clients;
using JunimoServer.Tests.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace JunimoServer.Tests;

/// <summary>
/// Integration tests for player tracking and server status accuracy.
/// Verifies that the server API correctly reflects connected players and game world state.
/// </summary>
[Collection("Integration")]
public class PlayerTrackingTests : IDisposable, IAsyncLifetime
{
    private readonly IntegrationTestFixture _fixture;
    private readonly GameTestClient _gameClient;
    private readonly ServerApiClient _serverApi;
    private readonly ITestOutputHelper _output;
    private readonly List<string> _createdFarmers = new();

    public PlayerTrackingTests(IntegrationTestFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
        _gameClient = new GameTestClient();
        _serverApi = new ServerApiClient(_fixture.ServerBaseUrl);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
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
    /// Verifies that GET /status returns valid game world data when the server is running.
    /// The server should report a farm name, a valid season/day/year, and a game time.
    /// </summary>
    [Fact]
    public async Task ServerStatus_HasValidGameWorldData()
    {
        var status = await _serverApi.GetStatus();

        Assert.NotNull(status);
        Assert.True(status.IsOnline, "Server should be online");

        // Farm name should be set
        Assert.False(string.IsNullOrEmpty(status.FarmName), "FarmName should not be empty");

        // Season must be a valid Stardew Valley season
        var validSeasons = new[] { "spring", "summer", "fall", "winter" };
        Assert.Contains(status.Season, validSeasons);

        // Day must be 1-28
        Assert.InRange(status.Day, 1, 28);

        // Year must be >= 1
        Assert.True(status.Year >= 1, $"Year should be >= 1, got {status.Year}");

        // TimeOfDay should be a valid game time (600 = 6:00 AM is the earliest)
        Assert.True(status.TimeOfDay >= 600, $"TimeOfDay should be >= 600, got {status.TimeOfDay}");

        _output.WriteLine($"Game world: {status.FarmName} Farm, {status.Season} {status.Day}, Year {status.Year}, {status.TimeOfDay}");
    }

    /// <summary>
    /// Verifies that connecting a client updates both GET /players and GET /status,
    /// and that disconnecting removes the client from both endpoints.
    /// </summary>
    [Fact]
    public async Task ConnectedClient_TrackedInPlayersAndStatus()
    {
        // Ensure we're fully disconnected first
        await _gameClient.Navigate("title");
        await _gameClient.Wait.ForDisconnected(10000);

        // Verify no players are connected initially
        var initialPlayers = await _serverApi.GetPlayers();
        Assert.NotNull(initialPlayers);
        Assert.Empty(initialPlayers.Players);

        var initialStatus = await _serverApi.GetStatus();
        Assert.NotNull(initialStatus);
        Assert.Equal(0, initialStatus.PlayerCount);

        _output.WriteLine("Verified: no players connected initially");

        // Join the server and enter the game world
        var farmerName = $"Track{DateTime.UtcNow.Ticks % 1000}";
        _createdFarmers.Add(farmerName);
        await JoinAndEnterWorld(farmerName);

        // Verify the client appears in /players
        var connectedPlayers = await _serverApi.GetPlayers();
        Assert.NotNull(connectedPlayers);
        Assert.NotEmpty(connectedPlayers.Players);

        var ourPlayer = connectedPlayers.Players.FirstOrDefault(p => p.Name == farmerName);
        Assert.NotNull(ourPlayer);
        Assert.True(ourPlayer.IsOnline, "Player should be marked as online");
        _output.WriteLine($"Found player in /players: {ourPlayer.Name} (ID: {ourPlayer.Id})");

        // Verify /status reflects the connection
        var connectedStatus = await _serverApi.GetStatus();
        Assert.NotNull(connectedStatus);
        Assert.True(connectedStatus.PlayerCount >= 1, $"PlayerCount should be >= 1, got {connectedStatus.PlayerCount}");
        _output.WriteLine($"Status playerCount: {connectedStatus.PlayerCount}");

        // Disconnect
        var exitResult = await _gameClient.Exit();
        Assert.True(exitResult?.Success, $"Exit failed: {exitResult?.Error}");

        await _gameClient.Wait.ForTitle(30000);
        await _gameClient.Wait.ForDisconnected(10000);
        _output.WriteLine("Disconnected from server");

        // Wait for server to process the disconnection
        await Task.Delay(3000);

        // Verify the client is removed from /players
        var afterDisconnectPlayers = await _serverApi.GetPlayers();
        Assert.NotNull(afterDisconnectPlayers);
        var stillThere = afterDisconnectPlayers.Players.FirstOrDefault(p => p.Name == farmerName);
        Assert.Null(stillThere);
        _output.WriteLine("Verified: player removed from /players after disconnect");

        // Verify /status reflects the disconnection
        var afterDisconnectStatus = await _serverApi.GetStatus();
        Assert.NotNull(afterDisconnectStatus);
        Assert.Equal(0, afterDisconnectStatus.PlayerCount);
        _output.WriteLine("Verified: playerCount back to 0 after disconnect");
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
