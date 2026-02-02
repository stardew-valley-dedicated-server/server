using JunimoServer.Tests.Containers;
using JunimoServer.Tests.Helpers;
using Xunit;
using Xunit.Abstractions;

namespace JunimoServer.Tests;

/// <summary>
/// Tests that verify game creation and player joining works for all 8 farm map types.
/// Each test spins up its own dedicated server with a specific farm type.
/// </summary>
public class FarmMapTypeTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private ServerContainerManager? _serverManager;
    private GameClientManager? _clientManager;

    public FarmMapTypeTests(ITestOutputHelper output)
    {
        _output = output;
    }

    public async Task InitializeAsync()
    {
        _serverManager = new ServerContainerManager(
            logCallback: msg => _output.WriteLine($"[ServerManager] {msg}"));

        _clientManager = new GameClientManager(
            logCallback: msg => _output.WriteLine($"[ClientManager] {msg}"));
    }

    public async Task DisposeAsync()
    {
        if (_clientManager != null)
            await _clientManager.DisposeAsync();

        if (_serverManager != null)
            await _serverManager.DisposeAsync();
    }

    [Theory]
    [InlineData(0, "Standard")]
    [InlineData(1, "Riverland")]
    [InlineData(2, "Forest")]
    [InlineData(3, "Hilltop")]
    [InlineData(4, "Wilderness")]
    [InlineData(5, "FourCorners")]
    [InlineData(6, "Beach")]
    [InlineData(7, "MeadowlandsFarm")]
    public async Task NewGame_WithFarmType_CabinsBuiltAndPlayerCanJoin(int farmType, string expectedFarmTypeKey)
    {
        _output.WriteLine($"=== Testing {expectedFarmTypeKey} farm (type {farmType}) ===");

        // Create server with specific farm type (CabinStack strategy manages cabins automatically)
        var server = await _serverManager!.CreateServerForFarmTypeAsync(farmType);

        _output.WriteLine($"Server ready: {server.BaseUrl}");
        _output.WriteLine($"Invite code: {server.InviteCode}");

        // Verify cabins were created via API
        // CabinStack maintains at least 1 available cabin automatically
        using var apiClient = server.CreateApiClient();
        var cabinsResponse = await apiClient.GetCabinsAsync();

        Assert.NotNull(cabinsResponse);
        Assert.True(cabinsResponse.TotalCount >= 1,
            $"Expected at least 1 cabin, got {cabinsResponse.TotalCount}");
        _output.WriteLine($"Cabins created: {cabinsResponse.TotalCount} (strategy: {cabinsResponse.Strategy})");

        // Verify settings were applied
        var settingsResponse = await apiClient.GetSettingsAsync();
        Assert.NotNull(settingsResponse);
        Assert.Equal(farmType, settingsResponse.Game.FarmType);
        _output.WriteLine($"Farm type setting: {settingsResponse.Game.FarmType}");

        // Verify actual loaded farm type via GetFarmTypeKey()
        var statusResponse = await apiClient.GetStatus();
        Assert.NotNull(statusResponse);
        Assert.Equal(expectedFarmTypeKey, statusResponse.FarmTypeKey);
        _output.WriteLine($"Farm type key verified: {statusResponse.FarmTypeKey}");

        // Create a game client and connect
        var client = await _clientManager!.CreateClientAsync(new GameClientOptions
        {
            GameDataVolume = server.Options.GameDataVolume
        });

        _output.WriteLine($"Game client ready: {client.BaseUrl}");

        // Connect via LAN
        var connectionResult = await client.ConnectViaLanAsync(
            "host.docker.internal",
            server.GamePort);

        Assert.True(connectionResult.Success,
            $"Failed to connect: {connectionResult.Error}");

        Assert.NotNull(connectionResult.Farmhands);
        Assert.True(connectionResult.Farmhands.Slots.Count >= 1,
            $"Expected at least 1 farmhand slot, got {connectionResult.Farmhands.Slots.Count}");

        _output.WriteLine($"Connected! Found {connectionResult.Farmhands.Slots.Count} farmhand slots");

        // Find an uncustomized slot
        var availableSlot = connectionResult.Farmhands.Slots.FirstOrDefault(s => !s.IsCustomized);
        Assert.NotNull(availableSlot);

        var selectResult = await client.Client.Farmhands.Select(availableSlot.Index);
        Assert.True(selectResult?.Success == true, $"Failed to select farmhand: {selectResult?.Error}");

        // Wait for character menu and create character
        var charWait = await client.Client.Wait.ForCharacter(TestTimings.CharacterMenuTimeout);
        Assert.True(charWait?.Success == true, $"Character menu didn't appear: {charWait?.Error}");

        var customizeResult = await client.Client.Character.Customize($"Test_{expectedFarmTypeKey}", "Testing");
        Assert.True(customizeResult?.Success == true, $"Failed to customize: {customizeResult?.Error}");

        await Task.Delay(TestTimings.CharacterCreationSyncDelay);

        var confirmResult = await client.Client.Character.Confirm();
        Assert.True(confirmResult?.Success == true, $"Failed to confirm: {confirmResult?.Error}");

        // Wait for world to be ready
        var worldWait = await client.Client.Wait.ForWorldReady(TestTimings.WorldReadyTimeout);
        Assert.True(worldWait?.Success == true, $"World didn't become ready: {worldWait?.Error}");

        _output.WriteLine($"Successfully joined {expectedFarmTypeKey} farm!");

        // Disconnect
        await client.DisconnectAsync();

        // Verify no server errors occurred
        Assert.False(server.HasErrors,
            $"Server errors detected: {string.Join("; ", server.Errors)}");

        _output.WriteLine($"=== {expectedFarmTypeKey} farm test PASSED ===");
    }

    [Fact]
    public async Task NewGame_NoneStrategy_DefaultStartingCabins()
    {
        _output.WriteLine("=== Testing None (vanilla) strategy with default starting cabins ===");

        var options = ServerContainerOptions.ForFarmType(0, "VanillaDefault");
        options.CabinStrategy = "None";
        // Default startingCabins is 1

        var server = await _serverManager!.CreateServerAsync(options);

        _output.WriteLine($"Server ready: {server.BaseUrl}");

        using var apiClient = server.CreateApiClient();
        var cabinsResponse = await apiClient.GetCabinsAsync();

        Assert.NotNull(cabinsResponse);
        Assert.Equal("None", cabinsResponse.Strategy);
        Assert.True(cabinsResponse.TotalCount >= 1,
            $"Expected at least 1 cabin with default settings, got {cabinsResponse.TotalCount}");

        _output.WriteLine($"Cabins created: {cabinsResponse.TotalCount} (strategy: {cabinsResponse.Strategy})");

        Assert.False(server.HasErrors,
            $"Server errors detected: {string.Join("; ", server.Errors)}");

        _output.WriteLine("=== None strategy default cabins test PASSED ===");
    }

    [Fact]
    public async Task NewGame_NoneStrategy_SixStartingCabins()
    {
        _output.WriteLine("=== Testing None (vanilla) strategy with 6 starting cabins ===");

        var options = ServerContainerOptions.ForFarmType(0, "VanillaSix");
        options.CabinStrategy = "None";
        options.StartingCabins = 6;

        var server = await _serverManager!.CreateServerAsync(options);

        _output.WriteLine($"Server ready: {server.BaseUrl}");

        using var apiClient = server.CreateApiClient();
        var cabinsResponse = await apiClient.GetCabinsAsync();

        Assert.NotNull(cabinsResponse);
        Assert.Equal("None", cabinsResponse.Strategy);
        Assert.Equal(6, cabinsResponse.TotalCount);

        _output.WriteLine($"Cabins created: {cabinsResponse.TotalCount} (strategy: {cabinsResponse.Strategy})");

        // Verify all 6 are available (unclaimed)
        Assert.Equal(6, cabinsResponse.AvailableCount);

        Assert.False(server.HasErrors,
            $"Server errors detected: {string.Join("; ", server.Errors)}");

        _output.WriteLine("=== None strategy 6 cabins test PASSED ===");
    }
}
