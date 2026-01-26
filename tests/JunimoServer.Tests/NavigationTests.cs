using JunimoServer.Tests.Clients;
using JunimoServer.Tests.Fixtures;
using Xunit;

namespace JunimoServer.Tests;

/// <summary>
/// Integration tests for server navigation and joining.
/// These tests replicate and extend the behavior from tools/run-test-client.ts.
///
/// Uses IntegrationTestFixture which automatically manages:
/// - Server container (via Testcontainers)
/// - Game test client (local Stardew Valley)
/// </summary>
[Collection("Integration")]
public class NavigationTests : IDisposable
{
    private readonly IntegrationTestFixture _fixture;
    private readonly GameTestClient _gameClient;
    private readonly ServerApiClient _serverApi;

    public NavigationTests(IntegrationTestFixture fixture)
    {
        _fixture = fixture;
        _gameClient = new GameTestClient();
        _serverApi = new ServerApiClient(_fixture.ServerBaseUrl);
    }

    /// <summary>
    /// Tests the full flow of joining a server:
    /// 1. Get the invite code from the server API
    /// 2. Navigate to the coop menu
    /// 3. Switch to the join tab
    /// 4. Join the server using the invite code
    ///
    /// This replicates and extends the test from run-test-client.ts.
    /// </summary>
    [Fact]
    public async Task JoinServer_WithInviteCodeFromApi_ShouldSucceed()
    {
        // Ensure we're fully disconnected first
        await _gameClient.Navigate("title");
        await _gameClient.Wait.ForDisconnected(10000);

        // Step 1: Get the invite code from server API
        var status = await _serverApi.GetStatus();
        Assert.NotNull(status);
        Assert.True(status.IsOnline, "Server should be online");
        Assert.False(string.IsNullOrEmpty(status.InviteCode), "Server should have an invite code");

        // Step 2: Navigate to coop menu
        var navigateResponse = await _gameClient.Navigate("coopmenu");
        Assert.NotNull(navigateResponse);
        Assert.True(navigateResponse.Success, navigateResponse.Error ?? "Navigate failed");

        // Wait for CoopMenu to be ready
        var menuWait = await _gameClient.Wait.ForMenu("CoopMenu", 10000);
        Assert.True(menuWait?.Success, menuWait?.Error ?? "Wait for CoopMenu failed");

        // Step 3: Switch to join tab (JOIN_TAB = 0, HOST_TAB = 1)
        var tabResponse = await _gameClient.Coop.Tab(0); // 0 = JOIN_TAB
        Assert.NotNull(tabResponse);
        Assert.True(tabResponse.Success, tabResponse.Error ?? "Tab switch failed");

        // Step 4: Join server with invite code
        var joinResponse = await _gameClient.Coop.JoinInvite(status.InviteCode);
        Assert.NotNull(joinResponse);
        Assert.True(joinResponse.Success, joinResponse.Error ?? "Join failed");
    }

    /// <summary>
    /// Original test from run-test-client.ts:
    /// - Navigate to 'coopmenu'
    /// - Switch to tab 1
    /// </summary>
    [Fact]
    public async Task NavigateToCoopMenuAndSwitchTab_ShouldSucceed()
    {
        // Navigate to coop menu
        var navigateResponse = await _gameClient.Navigate("coopmenu");
        Assert.NotNull(navigateResponse);
        Assert.True(navigateResponse.Success, navigateResponse.Error ?? "Navigate failed");

        // Wait for CoopMenu to be ready
        var menuWait = await _gameClient.Wait.ForMenu("CoopMenu", 10000);
        Assert.True(menuWait?.Success, menuWait?.Error ?? "Wait for CoopMenu failed");

        // Switch to join tab (JOIN_TAB = 0)
        var tabResponse = await _gameClient.Coop.Tab(0);
        Assert.NotNull(tabResponse);
        Assert.True(tabResponse.Success, tabResponse.Error ?? "Tab switch failed");
    }

    [Fact]
    public async Task ServerApi_GetStatus_ShouldReturnValidResponse()
    {
        var status = await _serverApi.GetStatus();

        Assert.NotNull(status);
        Assert.NotNull(status.ServerVersion);
        Assert.NotNull(status.LastUpdated);
    }

    [Fact]
    public async Task ServerApi_GetInviteCode_ShouldReturnCode()
    {
        var response = await _serverApi.GetInviteCode();

        Assert.NotNull(response);
        // Either we have an invite code or an error message
        Assert.True(
            !string.IsNullOrEmpty(response.InviteCode) || !string.IsNullOrEmpty(response.Error),
            "Response should contain either an invite code or an error"
        );
    }

    [Fact]
    public async Task ServerApi_GetHealth_ShouldReturnOk()
    {
        var health = await _serverApi.GetHealth();

        Assert.NotNull(health);
        Assert.Equal("ok", health.Status);
        Assert.False(string.IsNullOrEmpty(health.Timestamp));
    }

    public void Dispose()
    {
        _gameClient.Dispose();
        _serverApi.Dispose();
    }
}
