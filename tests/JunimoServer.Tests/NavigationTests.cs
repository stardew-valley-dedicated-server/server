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

    /// <summary>
    /// Verifies that the invite code API returns a valid code when the server is online.
    /// </summary>
    [Fact]
    public async Task ServerApi_GetInviteCode_ShouldReturnValidCode()
    {
        var response = await ServerApi.GetInviteCode();

        Assert.NotNull(response);
        Assert.True(string.IsNullOrEmpty(response.Error), $"Should not have error: {response.Error}");
        Assert.False(string.IsNullOrEmpty(response.InviteCode), "Should return a valid invite code");

        Log($"Invite code: {response.InviteCode}");
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
