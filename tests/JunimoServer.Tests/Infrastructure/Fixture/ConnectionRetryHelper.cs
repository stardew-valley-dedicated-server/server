using JunimoServer.Tests.Helpers;
using JunimoServer.Tests.Schema.Events;
using Xunit;

namespace JunimoServer.Tests.Infrastructure.Fixture;

/// <summary>
/// Connection / join retry helpers and assertion sugar.
///
/// Helpers in this class hold a <see cref="TestBase"/> reference so they can
/// reach properties (<c>Lease</c>, <c>Connection</c>, <c>InviteCode</c>) and a
/// small set of TestBase callbacks: <c>GetClientAsync</c>, <c>ThrowIfServerError</c>.
/// These callbacks are documented exceptions to the README §31 "no callbacks"
/// rule because <c>GetClientAsync</c> straddles both KeepConnected and
/// non-KeepConnected paths and <c>ThrowIfServerError</c> is a sync error-check.
/// They are stable, one-direction surfaces — the helper calls TestBase, TestBase
/// does not call back.
/// </summary>
internal sealed class ConnectionRetryHelper
{
    private readonly TestBase _testBase;
    private readonly string _displayName;

    public ConnectionRetryHelper(TestBase testBase, string displayName)
    {
        _testBase = testBase;
        _displayName = displayName;
    }

    /// <summary>
    /// Connects to the server with automatic retry.
    /// </summary>
    public async Task<ConnectionResult> WithRetryAsync(CancellationToken ct = default)
    {
        await _testBase.GetClientAsyncInternal(ct);
        using var _phase = TestIdentityContext.PushPhase("connect");
        InfrastructureEventLog.Emit(
            "connect_started",
            new { inviteCode = _testBase.InviteCodeInternal }
        );
        try
        {
            var lease = _testBase.LeaseInternal!;
            var result = lease.RequiresSteamConnection
                ? await _testBase.ConnectionInternal.ConnectToServerAsync(
                    _testBase.InviteCodeInternal,
                    ct
                )
                : await _testBase.ConnectionInternal.ConnectViaLanAsync(
                    lease.ServerLanAddress,
                    lease.ServerLanPort,
                    ct
                );
            if (result.Success)
            {
                _testBase.PersistentSession.DidConnect = true;
                InfrastructureEventLog.Emit("connect_completed");
            }
            return result;
        }
        catch (OperationCanceledException ex)
        {
            _testBase.ThrowIfServerErrorInternal(ex, "connecting to server");
            throw;
        }
    }

    /// <summary>
    /// Connects and joins the game world with automatic retry and auto-authentication.
    /// </summary>
    public async Task<JoinWorldResult> JoinWithRetryAsync(
        string farmerName,
        string favoriteThing = "Testing",
        bool preferExistingFarmer = true,
        CancellationToken ct = default
    )
    {
        await _testBase.GetClientAsyncInternal(ct);
        using var _phase = TestIdentityContext.PushPhase("connect");
        var joinSw = System.Diagnostics.Stopwatch.StartNew();

        var lease = _testBase.LeaseInternal!;
        await lease.Managed.AcquireJoinGateAsync(ct);
        try
        {
            var result = lease.RequiresSteamConnection
                ? await _testBase.ConnectionInternal.JoinWorldAsync(
                    _testBase.InviteCodeInternal,
                    farmerName,
                    favoriteThing,
                    preferExistingFarmer,
                    skipAutoLogin: false,
                    ct
                )
                : await _testBase.ConnectionInternal.JoinWorldViaLanAsync(
                    lease.ServerLanAddress,
                    lease.ServerLanPort,
                    farmerName,
                    favoriteThing,
                    preferExistingFarmer,
                    skipAutoLogin: false,
                    ct
                );
            if (result.Success)
            {
                _testBase.PersistentSession.DidConnect = true;
                InfrastructureEventLog.Emit(
                    "connect_completed",
                    new
                    {
                        farmerName,
                        durationMs = joinSw.ElapsedMilliseconds,
                        attempts = result.AttemptsUsed,
                    }
                );
                var primaryClientLease = _testBase.PrimaryClientLeaseInternal;
                if (primaryClientLease != null)
                {
                    SetupEventBus.EmitInstanceConnected(primaryClientLease.InstanceId);
                }
            }
            return result;
        }
        catch (OperationCanceledException ex)
        {
            _testBase.ThrowIfServerErrorInternal(ex, "joining world");
            throw;
        }
        finally
        {
            lease.Managed.ReleaseJoinGate();
        }
    }

    /// <summary>
    /// Joins the game world WITHOUT automatic authentication (for lobby/auth tests).
    /// </summary>
    public async Task<JoinWorldResult> JoinWithoutAuthAsync(
        string farmerName,
        string favoriteThing = "Testing",
        bool preferExistingFarmer = true,
        CancellationToken ct = default
    )
    {
        await _testBase.GetClientAsyncInternal(ct);
        var joinSw = System.Diagnostics.Stopwatch.StartNew();
        var lease = _testBase.LeaseInternal!;
        await lease.Managed.AcquireJoinGateAsync(ct);
        try
        {
            var result = lease.RequiresSteamConnection
                ? await _testBase.ConnectionInternal.JoinWorldAsync(
                    _testBase.InviteCodeInternal,
                    farmerName,
                    favoriteThing,
                    preferExistingFarmer,
                    skipAutoLogin: true,
                    ct
                )
                : await _testBase.ConnectionInternal.JoinWorldViaLanAsync(
                    lease.ServerLanAddress,
                    lease.ServerLanPort,
                    farmerName,
                    favoriteThing,
                    preferExistingFarmer,
                    skipAutoLogin: true,
                    ct
                );
            if (result.Success)
            {
                _testBase.PersistentSession.DidConnect = true;
                InfrastructureEventLog.Emit(
                    "connect_completed",
                    new
                    {
                        farmerName,
                        durationMs = joinSw.ElapsedMilliseconds,
                        attempts = result.AttemptsUsed,
                    }
                );
            }
            return result;
        }
        catch (OperationCanceledException ex)
        {
            _testBase.ThrowIfServerErrorInternal(ex, "joining world");
            throw;
        }
        finally
        {
            lease.Managed.ReleaseJoinGate();
        }
    }

    public async Task<bool> EnsureDisconnectedAsync(TimeSpan? timeout = null)
    {
        var connection = _testBase.ConnectionInternalOrNull;
        if (connection == null)
        {
            return true;
        }

        try
        {
            return await connection.EnsureDisconnectedAsync(
                timeout ?? TestTimings.DisconnectedTimeout
            );
        }
        catch (OperationCanceledException ex)
        {
            _testBase.ThrowIfServerErrorInternal(ex, "ensuring disconnected");
            throw;
        }
    }

    public void AssertConnectionSuccess(ConnectionResult result)
    {
        if (!result.Success)
        {
            _testBase.RecordTestFailureInternal(
                $"Connection failed after {result.AttemptsUsed} attempt(s): {result.Error}",
                "connect"
            );
        }

        Assert.True(
            result.Success,
            $"Connection failed after {result.AttemptsUsed} attempt(s): {result.Error}"
        );
    }

    public void AssertJoinSuccess(JoinWorldResult result)
    {
        if (!result.Success)
        {
            _testBase.RecordTestFailureInternal(
                $"Join world failed after {result.AttemptsUsed} attempt(s): {result.Error}",
                "connect"
            );
        }

        Assert.True(
            result.Success,
            $"Join world failed after {result.AttemptsUsed} attempt(s): {result.Error}"
        );
    }

    /// <summary>
    /// Asserts the join succeeded AND, if the player was placed in the lobby,
    /// that auto-login completed and the post-auth warp finished. The warp
    /// check happens inside the connection helper via WaitForAuthWarpAsync, so
    /// callers do not need additional location/cabin polling after this returns.
    /// </summary>
    public void AssertAuthenticated(JoinWorldResult result)
    {
        AssertJoinSuccess(result);
        if (result.WasInLobby && !result.IsAuthenticated)
        {
            var lease = _testBase.LeaseInternal;
            if (
                result.ServerUnhealthy
                || lease?.IsPoisoned == true
                || lease?.ErrorToken.IsCancellationRequested == true
            )
            {
                var reason =
                    lease?.IsPoisoned == true ? $"Server poisoned: {lease.AbortReason ?? "unknown"}"
                    : result.ServerUnhealthy ? "Server API unhealthy during auth"
                    : "Server error detected during auth";
                throw new TestRunAbortedException(reason);
            }

            _testBase.RecordTestFailureInternal(
                "Player was placed in lobby but authentication failed",
                "auth"
            );
            Assert.Fail(
                "Player must be authenticated (was placed in lobby but authentication failed)"
            );
        }
    }
}
