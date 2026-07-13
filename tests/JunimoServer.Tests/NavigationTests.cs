using JunimoServer.Tests.Clients;
using JunimoServer.Tests.Helpers;
using JunimoServer.Tests.Infrastructure;
using Xunit;

namespace JunimoServer.Tests;

/// <summary>
/// Integration tests for server navigation and joining.
/// </summary>
[TestServer(Isolation = IsolationMode.SharedAssembly)]
public class NavigationTests : TestBase
{
    public NavigationTests() { }

    /// <summary>
    /// Tests the full flow of joining a server with retry logic.
    /// </summary>
    [Fact]
    [TestServer(WithSteam = true)]
    public async Task JoinServer_WithInviteCodeFromApi_ShouldSucceed()
    {
        // Ensure disconnected
        await Connect.EnsureDisconnectedAsync();

        // Verify server is online and has invite code (Galaxy code arrives asynchronously)
        Assert.NotNull(ServerStatus);
        Assert.True(ServerStatus.IsOnline, "Server should be online");

        var ct = TestCt;
        // InviteCode is read from a file, not from the snapshot — long-poll for
        // any newer snapshot via `since`, then check IsOnline + InviteCode in
        // the response body.
        var hasCode = await PollingHelper.LongPollAsync(
            WaitName.Polling_Navigation_HasInviteCode,
            async (since, remaining) =>
            {
                var status = await ServerApi.WaitForStatusAsync(
                    since: since,
                    timeout: remaining,
                    ct: ct
                );
                if (status == null)
                {
                    return new PollingHelper.LongPollResult(false, since);
                }

                var matched = status.IsOnline && !string.IsNullOrEmpty(status.InviteCode);
                return new PollingHelper.LongPollResult(matched, status.Version);
            },
            TimeSpan.FromSeconds(10),
            cancellationToken: ct
        );
        Assert.True(hasCode, "Server should have an invite code");

        // Connect with retry
        var result = await Connect.WithRetryAsync(ct);
        Connect.AssertConnectionSuccess(result);

        Log($"Successfully joined server after {result.AttemptsUsed} attempt(s)");
        Log($"Found {result.Farmhands?.Farmhands.Count ?? 0} farmhand slots");
    }

    [Fact]
    [TestServer(Clients = 0)]
    public async Task ServerApi_GetStatus_ShouldReturnValidResponse()
    {
        var status = await ServerApi.GetStatus(TestCt);

        Assert.NotNull(status);
        Assert.NotNull(status.ServerVersion);
        Assert.NotNull(status.LastUpdated);

        Log($"Server version: {status.ServerVersion}");
        Log($"Online: {status.IsOnline}, Ready: {status.IsReady}");
    }

    /// <summary>
    /// Verifies that the invite code API returns a valid code.
    /// Requires Steam/Galaxy to be active. Uses WithSteam = true.
    /// </summary>
    [Fact]
    [TestServer(Clients = 0, WithSteam = true)]
    public async Task ServerApi_GetInviteCode_ShouldReturnValidCode()
    {
        // Galaxy invite code may arrive asynchronously; poll briefly
        var ct = TestCt;
        var hasCode = await PollingHelper.WaitUntilAsync(
            WaitName.Polling_Navigation_HasGalaxyInviteCode,
            async () =>
            {
                var r = await ServerApi.GetInviteCode(ct);
                return r != null && !string.IsNullOrEmpty(r.InviteCode);
            },
            TimeSpan.FromSeconds(10),
            cancellationToken: ct
        );

        Assert.True(hasCode, "Server must have a valid invite code (WithSteam=true)");

        var response = await ServerApi.GetInviteCode(ct);
        Assert.NotNull(response);
        Assert.False(
            string.IsNullOrEmpty(response.InviteCode),
            "Should return a non-empty invite code"
        );
        Log($"Invite code: {response.InviteCode}");
    }

    [Fact]
    [TestServer(Clients = 0)]
    public async Task ServerApi_GetHealth_ShouldReturnOk()
    {
        // Long-poll until healthy. /wait/health uses a stateless predicate
        // (no `since` cursor — the server's health bit is "is the game thread
        // ticking now"), so each iteration returns immediately on a healthy
        // server or after up to WaitMaxTimeout (10s) on a stalled one.
        var ct = TestCt;
        HealthResponse? health = null;
        var healthy = await PollingHelper.LongPollAsync(
            WaitName.Polling_Navigation_HealthyOk,
            async (_, remaining) =>
            {
                health = await ServerApi.WaitForHealthAsync(
                    ready: true,
                    timeout: remaining,
                    ct: ct
                );
                // /wait/health is stateless: a 200 always satisfies the predicate,
                // a 408 means the per-tick TCS didn't fire within the server-side
                // window — re-issue without a cursor.
                return new PollingHelper.LongPollResult(health?.Status == "ok", 0);
            },
            TestTimings.ServerReadyBetweenTests,
            cancellationToken: ct
        );

        Assert.NotNull(health);
        Assert.True(
            healthy,
            $"Expected health 'ok' but got '{health.Status}' (lastTickMs={health.LastTickMs}, gameAvailable={health.GameAvailable})"
        );
        Assert.False(string.IsNullOrEmpty(health.Timestamp));

        Log($"Health status: {health.Status}");
    }
}
