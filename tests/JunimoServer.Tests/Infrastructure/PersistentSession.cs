using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using JunimoServer.Tests.Clients;
using JunimoServer.Tests.Helpers;
using Xunit;

namespace JunimoServer.Tests.Infrastructure;

/// <summary>
/// How the persistent session should join the server.
/// </summary>
public enum SessionJoinMode
{
    /// <summary>Join and authenticate (auto-login). For tests on no-password servers or tests that need full access.</summary>
    Authenticated,

    /// <summary>Join without authenticating (skip auto-login). For lobby/auth tests that interact while unauthenticated.</summary>
    Unauthenticated,

    /// <summary>Connect only (no world join). For tests that need raw connection control.</summary>
    ConnectOnly,
}

/// <summary>
/// Holds the state of a persistent client session that spans multiple tests within a class.
/// The session keeps the client connected AND capacity held between tests to avoid the
/// 30-60s reconnection cost and prevent resource deadlocks. Capacity is only released
/// when the session is disposed (last test or session death).
/// </summary>
internal sealed class PersistentSession : IAsyncDisposable
{
    public Type OwnerType { get; }
    public ResourceLease Lease { get; }
    public ClientLease ClientLease { get; }
    public ConnectionHelper Connection { get; }
    public ExceptionMonitor ExceptionMonitor { get; }
    public string? FarmerName { get; }
    public long? FarmerUid { get; }
    public int ClientSlotsHeld { get; }
    public bool IsAuthenticated { get; }

    public PersistentSession(
        Type ownerType,
        ResourceLease lease,
        ClientLease clientLease,
        ConnectionHelper connection,
        ExceptionMonitor exceptionMonitor,
        string? farmerName,
        long? farmerUid,
        int clientSlotsHeld,
        bool isAuthenticated
    )
    {
        OwnerType = ownerType;
        Lease = lease;
        ClientLease = clientLease;
        Connection = connection;
        ExceptionMonitor = exceptionMonitor;
        FarmerName = farmerName;
        FarmerUid = farmerUid;
        ClientSlotsHeld = clientSlotsHeld;
        IsAuthenticated = isAuthenticated;
        TestLog.Test(
            $"{ownerType.Name} session created ({CountTestMethods(ownerType)} tests, farmer={farmerName ?? "<none>"}, uid={farmerUid?.ToString() ?? "<none>"})"
        );
        InfrastructureEventLog.Emit(
            "session_created",
            new
            {
                owner = ownerType.Name,
                farmer = farmerName,
                farmerUid,
                clientSlots = clientSlotsHeld,
                tests = CountTestMethods(ownerType),
                authenticated = isAuthenticated,
            }
        );
    }

    /// <summary>
    /// Checks whether the client session is still alive and connected.
    /// Validates two independent contracts: the test-client still believes it
    /// is connected, AND the server still sees the farmer uid in /players.
    /// These can diverge — the client can believe it is connected after the
    /// server has evicted the peer, which causes GrantAdmin (and any other
    /// getAllFarmers-based lookup) to fail with "player not found". Returning
    /// false here lets the caller fall back to the dead-session path and
    /// rebuild cleanly.
    /// </summary>
    public async Task<bool> IsAliveAsync(CancellationToken ct = default)
    {
        try
        {
            var state = await ClientLease.Client.GetState();
            if (state?.IsConnected != true)
            {
                InfrastructureEventLog.Emit(
                    "session_revalidation_failed",
                    new
                    {
                        owner = OwnerType.Name,
                        farmer = FarmerName,
                        farmerUid = FarmerUid,
                        reason = "client_disconnected",
                    }
                );
                return false;
            }
        }
        catch (Exception ex)
        {
            InfrastructureEventLog.Emit(
                "session_revalidation_failed",
                new
                {
                    owner = OwnerType.Name,
                    farmer = FarmerName,
                    farmerUid = FarmerUid,
                    reason = "client_getstate_" + ex.GetType().Name,
                }
            );
            return false;
        }

        if (FarmerUid is not long uid)
            return true;

        try
        {
            var seen = await Lease.Api.WaitForPlayerByIdAsync(
                uid,
                timeout: TestTimings.SessionRevalidationBudget,
                ct: ct
            );

            if (!seen)
            {
                InfrastructureEventLog.Emit(
                    "session_revalidation_failed",
                    new
                    {
                        owner = OwnerType.Name,
                        farmer = FarmerName,
                        farmerUid = uid,
                        reason = "player_not_in_snapshot",
                    }
                );
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            InfrastructureEventLog.Emit(
                "session_revalidation_failed",
                new
                {
                    owner = OwnerType.Name,
                    farmer = FarmerName,
                    farmerUid = uid,
                    reason = "waitforplayer_" + ex.GetType().Name,
                }
            );
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        TestLog.Test($"{OwnerType.Name} session ending");
        var sw = Stopwatch.StartNew();

        // Every dispose step below must run even if an earlier one throws, or
        // we leak client capacity and server leases. Collect exceptions and
        // rethrow the aggregate at the end so infra failures still surface.
        List<Exception>? errors = null;

        try
        {
            await ClientLease.DisposeAsync();
        }
        catch (Exception ex)
        {
            (errors ??= new()).Add(ex);
        }

        // Wait for the server to confirm the farmer is gone before releasing the lease
        // and returning the client container to the pool. Without this, the next test
        // reusing this container connects before the server processes the disconnect,
        // causing "game didn't see them disconnect" and farmhand rejection loops.
        //
        // If the budget expires, the server is in an unknown state and must not be
        // reused — poison the lease so subsequent tests that would have acquired
        // this server fast-fail cleanly via TestBase's IsPoisoned check.
        if (FarmerUid is long uid)
        {
            try
            {
                var waitSw = Stopwatch.StartNew();
                var removed = await Lease.Api.WaitForPlayerRemovedByIdAsync(
                    uid,
                    timeout: TestTimings.FarmerRemovalBudget
                );
                waitSw.Stop();

                InfrastructureEventLog.Emit(
                    "farmer_removal_waited",
                    new
                    {
                        uid,
                        farmer = FarmerName,
                        waitedMs = waitSw.ElapsedMilliseconds,
                        removed,
                    }
                );

                if (!removed)
                {
                    Lease.Managed.PoisonServer(
                        $"farmer uid={uid} ('{FarmerName}') still visible after {TestTimings.FarmerRemovalBudget.TotalSeconds}s disconnect wait",
                        ManagedServer.PoisonReasonCode.FarmerRemovalTimeout
                    );
                }
            }
            catch (Exception ex)
            {
                (errors ??= new()).Add(ex);
            }
        }

        // Capture the host the broker acquired client slots against, before
        // disposing the lease (which doesn't null Lease.Managed but the read
        // is clearer when scoped to the release site).
        var capacityHost = Lease.Host;

        try
        {
            await Lease.DisposeAsync();
        }
        catch (Exception ex)
        {
            (errors ??= new()).Add(ex);
        }

        if (ClientSlotsHeld > 0)
        {
            capacityHost.ClientCapacity.Release(ClientSlotsHeld);
            TestLog.Test($"{OwnerType.Name} released {ClientSlotsHeld} client(s)");
        }

        InfrastructureEventLog.Emit(
            "session_disposed",
            new
            {
                owner = OwnerType.Name,
                durationMs = sw.ElapsedMilliseconds,
                clientSlotsReleased = ClientSlotsHeld,
            }
        );

        if (errors is { Count: > 0 })
            throw new AggregateException(
                $"{OwnerType.Name} session dispose had {errors.Count} failure(s).",
                errors
            );
    }

    /// <summary>
    /// Counts the number of [Fact] and [Theory] test methods on a class.
    /// </summary>
    internal static int CountTestMethods(Type type)
    {
        return type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Count(m =>
                m.GetCustomAttribute<FactAttribute>() != null
                || m.GetCustomAttribute<TheoryAttribute>() != null
            );
    }
}

/// <summary>
/// Serializes test execution within a KeepConnected class.
/// xUnit v3 starts all test instances within a class concurrently, but KeepConnected
/// tests share a single client connection, so only one test can run at a time.
/// The turn lock ensures sequential execution. The completion counter tracks when
/// all tests have finished so the session can be disposed.
/// </summary>
internal sealed class SessionGate
{
    /// <summary>
    /// Serializes test execution within the class. Only one test holds this at a time.
    /// Acquired in InitializeAsync, released in DisposeAsync.
    /// </summary>
    private readonly SemaphoreSlim _turnLock = new(1, 1);

    private readonly int _totalTests;
    private int _completedTests;

    public SessionGate(int totalTests)
    {
        _totalTests = totalTests;
        TestLog.Test($"Turn gate created ({totalTests} tests)");
    }

    /// <summary>
    /// Acquires the per-class turn lock so the test can run. Released by
    /// <see cref="ReleaseTurn"/> in <c>TestBase.DisposeAsync</c>.
    /// </summary>
    public Task AcquireTurnAsync(CancellationToken ct) =>
        WaitTrace.RunAsync(
            WaitName.SessionGate_TurnLock,
            () => _turnLock.WaitAsync(ct),
            ct,
            snapshot: () =>
                new { totalTests = _totalTests, completed = Volatile.Read(ref _completedTests) }
        );

    /// <summary>Releases the turn lock so the next test in the class can proceed.</summary>
    public void ReleaseTurn() => _turnLock.Release();

    /// <summary>Total number of tests in the class. Read-only snapshot for diagnostics.</summary>
    internal int TotalTests => _totalTests;

    /// <summary>Number of tests that have completed so far. Read with Volatile.Read.</summary>
    internal int CompletedTests => Volatile.Read(ref _completedTests);

    /// <summary>
    /// Increments the completed test counter. Returns true when all tests have finished.
    /// </summary>
    public bool IncrementAndCheckDone()
    {
        var completed = Interlocked.Increment(ref _completedTests);
        TestLog.Test($"Test completed ({completed}/{_totalTests})");
        return completed >= _totalTests;
    }
}

/// <summary>
/// Thread-safe store for persistent sessions, keyed by test class type.
/// </summary>
internal static class PersistentSessionStore
{
    private static readonly ConcurrentDictionary<Type, PersistentSession> Sessions = new();
    private static readonly ConcurrentDictionary<Type, SessionGate> Gates = new();

    public static PersistentSession? Get(Type type)
    {
        Sessions.TryGetValue(type, out var session);
        return session;
    }

    public static void Register(Type type, PersistentSession session)
    {
        Sessions[type] = session;
        TestLog.Test($"Registered session for {type.Name}");
    }

    public static async Task RemoveAndDisposeAsync(Type type)
    {
        if (Sessions.TryRemove(type, out var session))
        {
            TestLog.Test($"Removing and disposing session for {type.Name}");
            await session.DisposeAsync();
        }
    }

    public static void Remove(Type type)
    {
        if (Sessions.TryRemove(type, out _))
            TestLog.Test($"Removed session for {type.Name} (no dispose)");
    }

    /// <summary>
    /// Gets or creates a SessionGate for the given test class type.
    /// </summary>
    public static SessionGate GetOrCreateGate(Type type, int totalTests)
    {
        return Gates.GetOrAdd(
            type,
            t =>
            {
                TestLog.Test($"Created gate for {t.Name} ({totalTests} tests)");
                return new SessionGate(totalTests);
            }
        );
    }
}
