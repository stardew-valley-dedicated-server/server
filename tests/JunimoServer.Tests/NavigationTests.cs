using JunimoServer.Tests.Clients;
using JunimoServer.Tests.Fixtures;
using JunimoServer.Tests.Helpers;
using Xunit;
using Xunit.Abstractions;

namespace JunimoServer.Tests;

/// <summary>
/// Integration tests for server navigation and joining.
/// These tests replicate and extend the behavior from tools/run-test-client.ts.
///
/// Uses IntegrationTestBase which provides:
/// - Automatic retry on connection failures
/// - Exception monitoring with early abort
/// - Server/client log streaming
/// </summary>
[Collection("Integration")]
public class NavigationTests : IntegrationTestBase
{
    public NavigationTests(IntegrationTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    /// <summary>
    /// Tests the full flow of joining a server with retry logic.
    /// </summary>
    [Fact]
    public async Task JoinServer_WithInviteCodeFromApi_ShouldSucceed()
    {
        // Ensure disconnected
        await EnsureDisconnectedAsync();

        // Verify server is online
        Assert.NotNull(ServerStatus);
        Assert.True(ServerStatus.IsOnline, "Server should be online");
        Assert.False(string.IsNullOrEmpty(InviteCode), "Server should have an invite code");

        // Connect with retry
        var result = await ConnectWithRetryAsync();
        AssertConnectionSuccess(result);

        Log($"Successfully joined server after {result.AttemptsUsed} attempt(s)");
        Log($"Found {result.Farmhands?.Slots.Count ?? 0} farmhand slots");

        await AssertNoExceptionsAsync("at end of test");
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
        var navigateResponse = await GameClient.Navigate("coopmenu");
        Assert.NotNull(navigateResponse);
        Assert.True(navigateResponse.Success, navigateResponse.Error ?? "Navigate failed");

        // Wait for CoopMenu to be ready
        var menuWait = await GameClient.Wait.ForMenu("CoopMenu", TestTimings.MenuWaitTimeoutMs);
        Assert.True(menuWait?.Success, menuWait?.Error ?? "Wait for CoopMenu failed");

        // Switch to join tab (JOIN_TAB = 0)
        var tabResponse = await GameClient.Coop.Tab(0);
        Assert.NotNull(tabResponse);
        Assert.True(tabResponse.Success, tabResponse.Error ?? "Tab switch failed");

        await AssertNoExceptionsAsync("at end of test");
    }

    [Fact]
    public async Task ServerApi_GetStatus_ShouldReturnValidResponse()
    {
        var status = await ServerApi.GetStatus();

        Assert.NotNull(status);
        Assert.NotNull(status.ServerVersion);
        Assert.NotNull(status.LastUpdated);

        Log($"Server version: {status.ServerVersion}");
        Log($"Online: {status.IsOnline}, Ready: {status.IsReady}");

        await AssertNoExceptionsAsync("at end of test");
    }

    [Fact]
    public async Task ServerApi_GetInviteCode_ShouldReturnCode()
    {
        var response = await ServerApi.GetInviteCode();

        Assert.NotNull(response);
        // Either we have an invite code or an error message
        Assert.True(
            !string.IsNullOrEmpty(response.InviteCode) || !string.IsNullOrEmpty(response.Error),
            "Response should contain either an invite code or an error"
        );

        Log($"Invite code: {response.InviteCode ?? "(none)"}");
        if (response.Error != null)
            Log($"Error: {response.Error}");
    }

    [Fact]
    public async Task ServerApi_GetHealth_ShouldReturnOk()
    {
        var health = await ServerApi.GetHealth();

        Assert.NotNull(health);
        Assert.Equal("ok", health.Status);
        Assert.False(string.IsNullOrEmpty(health.Timestamp));

        Log($"Health status: {health.Status}");
    }
}
