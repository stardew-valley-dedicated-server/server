using JunimoServer.Tests.Fixtures;
using JunimoServer.Tests.Helpers;
using JunimoServer.Tests.Schema.Events;

namespace JunimoServer.Tests.Infrastructure.Fixture;

/// <summary>
/// Owns the KeepConnected / persistent-session orchestration: turn lock,
/// exclusive gate, client capacity accounting (<c>_clientSlotsHeld</c>), and
/// the wiring between <see cref="Infrastructure.PersistentSession"/> and the
/// per-test resources held by <see cref="TestBase"/>.
///
/// The coordinator holds a <see cref="TestBase"/> reference so it can reach
/// resource-acquisition surfaces that straddle both KeepConnected and non-
/// KeepConnected paths: <c>AcquireServerAsync</c>, <c>GetClientAsync</c>,
/// <c>MarkActiveAndArmBudget</c>, <c>ExtractMethodName</c>. These are
/// documented exceptions to the README §31 "no callbacks" rule — they are
/// stable, one-direction surfaces; the coordinator calls TestBase, TestBase
/// does not call back in return.
/// </summary>
internal sealed class PersistentSessionCoordinator
{
    private readonly TestBase _testBase;
    private readonly string _displayName;

    // Mutable per-test state owned by this coordinator.
    internal int ClientSlotsHeld { get; private set; }
    internal bool KeepConnected { get; private set; }
    internal SessionGate? Gate { get; private set; }
    internal bool HoldsTurnLock { get; private set; }
    internal bool HoldsExclusive { get; private set; }
    internal Infrastructure.PersistentSession? Session { get; private set; }
    internal bool IsUsingPersistentSession { get; private set; }
    internal bool OwnsLease { get; private set; } = true;
    internal bool SessionBroken { get; private set; }
    internal string? ExpectedServerKey { get; private set; }
    internal string? ConnectedFarmerName { get; private set; }
    internal long? ConnectedFarmerUid { get; private set; }

    // get/set so the Connect helper writes success and the coordinator
    // self-writes break-session reset.
    internal bool DidConnect { get; set; }

    public PersistentSessionCoordinator(TestBase testBase, string displayName)
    {
        _testBase = testBase;
        _displayName = displayName;
    }

    /// <summary>
    /// Records the client slots acquired by TestBase's AcquireServerAsync so
    /// the coordinator can manage capacity ownership across the session
    /// lifecycle. Called from TestBase right after a fresh AcquireAsync.
    /// </summary>
    internal void RecordCapacityAcquired(int slots)
    {
        ClientSlotsHeld = slots;
    }

    /// <summary>
    /// Records the expected server key for the cancellation-fallback decrement.
    /// </summary>
    internal void RecordExpectedServerKey(string? key)
    {
        ExpectedServerKey = key;
    }

    /// <summary>
    /// Initializes the KeepConnected block: acquires the per-class turn lock,
    /// reuses an existing session if alive (else acquires fresh resources),
    /// handles the Exclusive gate, and arms the per-test timeout.
    /// Called from <see cref="TestBase.InitializeAsync"/> when the test has
    /// <c>[TestServer(KeepConnected = true)]</c>.
    /// </summary>
    internal async Task InitializeKeepConnectedAsync(TestServerAttribute attr, CancellationToken ct)
    {
        var totalTests = Infrastructure.PersistentSession.CountTestMethods(_testBase.GetType());
        var gate = PersistentSessionStore.GetOrCreateGate(_testBase.GetType(), totalTests);
        Gate = gate;
        KeepConnected = true;

        LogTrace("Waiting for turn lock...");
        await gate.AcquireTurnAsync(ct);
        HoldsTurnLock = true;
        LogTrace("Turn lock acquired");

        // Check for an existing persistent session from a previous test in this class.
        // Capacity is still held (not released between tests), so just wire up.
        var existing = PersistentSessionStore.Get(_testBase.GetType());
        if (existing != null && await existing.IsAliveAsync())
        {
            WireUpPersistentSession(existing);
            LogTrace("Reusing persistent session from previous test");

            // Mark containers for video recording even when reusing persistent session
            if (RecordingPolicy.IsEnabled && _testBase.CollectArtifactsInternal)
            {
                await _testBase.Artifacts.MarkContainerUsedAsync(
                    _testBase.LeaseInternal!.Server.Container.Name, "server", ct);
                var primaryClientLease = _testBase.PrimaryClientLeaseInternal;
                if (primaryClientLease != null)
                    await _testBase.Artifacts.MarkContainerUsedAsync(
                        primaryClientLease.Container.Container.Name, "client", ct);
            }
        }
        else
        {
            // No session or session is dead; acquire our own resources.
            if (existing != null)
            {
                LogTrace("Previous session is dead, cleaning up");
                await PersistentSessionStore.RemoveAndDisposeAsync(_testBase.GetType());
            }
            var methodName = TestBase.ExtractMethodNameInternal(_displayName);
            var requirements = ResourceRequirements.FromAttribute(attr, _testBase.GetType().Name, methodName);
            var priority = TestCollectionOrderer.GetPriorityForClass(_testBase.GetType().FullName);
            await _testBase.AcquireServerAsyncInternal(requirements, ct, priority);
        }

        // KeepConnected exclusive access coordination.
        // When reusing a session, AcquireServerAsync was NOT called. The normal
        // Exclusive handling in the broker is bypassed. Handle it here.
        // When the session is new (acquired fresh above), AcquireServerAsync already
        // called AddRefAndAcquireExclusiveAsync, so we only need to track the flag.
        if (_testBase.LeaseInternal != null)
        {
            var testName = _displayName.Length > 0 ? _displayName : _testBase.GetType().Name;
            if (attr.Exclusive && IsUsingPersistentSession)
            {
                await _testBase.LeaseInternal.Managed.AcquireExclusiveGateOnlyAsync(testName, ct);
                HoldsExclusive = true;
            }
            else if (attr.Exclusive && !IsUsingPersistentSession)
            {
                HoldsExclusive = true;
            }
        }

        _testBase.MarkActiveAndArmBudgetInternal();
    }

    /// <summary>
    /// Wires up this test instance to use an existing persistent session's resources.
    /// Pure resource-rewiring: does NOT mark the queue→active transition.
    /// </summary>
    internal void WireUpPersistentSession(Infrastructure.PersistentSession session)
    {
        Session = session;
        IsUsingPersistentSession = true;
        OwnsLease = false;
        ConnectedFarmerName = session.FarmerName;
        ConnectedFarmerUid = session.FarmerUid;
        _testBase.AdoptSessionResources(
            session.Lease, session.ClientLease, session.Connection, session.ExceptionMonitor);
        ClientSlotsHeld = 0; // Session owns the capacity

        // Emit instance_leased so the UI tracks which instances ran this test
        // (for TPS graph overlay on video recordings, instance history, etc.)
        // We call EmitInstanceLeased directly (NOT AddRef) because the persistent
        // session already holds the server ref.
        var testName = _displayName.Length > 0 ? _displayName : _testBase.GetType().Name;
        if (session.Lease.Managed?.InstanceId != null)
            SetupEventBus.EmitInstanceLeased(session.Lease.Managed.InstanceId, testName);
        if (session.ClientLease != null)
            SetupEventBus.EmitInstanceLeased(session.ClientLease.InstanceId, testName, session.Lease.Managed?.InstanceId);
    }

    /// <summary>
    /// Ensures a persistent client session is active, reusing an existing one if possible.
    /// Creates a new session on first call, or when the previous session was broken/lost.
    /// </summary>
    internal async Task EnsureConnectedAsync(
        string farmerPrefix,
        SessionJoinMode joinMode,
        CancellationToken ct)
    {
        // Check for existing session
        var existing = PersistentSessionStore.Get(_testBase.GetType());
        if (existing != null)
        {
            if (await existing.IsAliveAsync())
            {
                // Already wired up from InitializeAsync, or re-wire if BreakSessionAsync was called
                if (!IsUsingPersistentSession)
                    WireUpPersistentSession(existing);
                LogDetail("Reusing existing session");
                return;
            }

            // Session is dead; dispose and remove
            Log("Persistent session lost, re-establishing...");
            await PersistentSessionStore.RemoveAndDisposeAsync(_testBase.GetType());
            IsUsingPersistentSession = false;
            Session = null;

            // Re-acquire our own resources if they were from the dead session
            if (!OwnsLease)
            {
                _testBase.ClearAdoptedResources();
                var methodName = TestBase.ExtractMethodNameInternal(_displayName);
                var attr = TestServerAttribute.Resolve(_testBase.GetType(), methodName);
                var requirements = ResourceRequirements.FromAttribute(attr, _testBase.GetType().Name, methodName);
                var priority = TestCollectionOrderer.GetPriorityForClass(_testBase.GetType().FullName);
                await _testBase.AcquireServerAsyncInternal(requirements, ct, priority);
            }
        }

        // Create a new session
        await _testBase.GetClientAsyncInternal(ct);

        var farmerName = _testBase.Farmers.GenerateName(farmerPrefix);
        bool isAuthenticated = false;

        switch (joinMode)
        {
            case SessionJoinMode.Authenticated:
                var joinResult = await _testBase.Connect.JoinWithRetryAsync(farmerName, ct: ct);
                _testBase.Connect.AssertJoinSuccess(joinResult);
                isAuthenticated = true;
                ConnectedFarmerName = farmerName;
                ConnectedFarmerUid = joinResult.UniqueMultiplayerId;
                break;
            case SessionJoinMode.Unauthenticated:
                var unauthResult = await _testBase.Connect.JoinWithoutAuthAsync(farmerName, ct: ct);
                _testBase.Connect.AssertJoinSuccess(unauthResult);
                ConnectedFarmerName = farmerName;
                ConnectedFarmerUid = unauthResult.UniqueMultiplayerId;
                break;
            case SessionJoinMode.ConnectOnly:
                var connectResult = await _testBase.Connect.WithRetryAsync(ct);
                _testBase.Connect.AssertConnectionSuccess(connectResult);
                // No farmer joined: both session fields stay null.
                break;
        }

        Log($"Session established (name={ConnectedFarmerName ?? "<none>"}, uid={ConnectedFarmerUid?.ToString() ?? "<none>"}, mode={joinMode})");

        // Don't create a persistent session if KeepConnected is off or the session was broken.
        if (!KeepConnected || SessionBroken)
        {
            Log(SessionBroken
                ? "Session broken, connected without creating persistent session"
                : "Non-KeepConnected, connected without creating persistent session");
            DidConnect = true;
            if (ConnectedFarmerName != null && ConnectedFarmerUid is long trackUid)
                _testBase.Farmers.TrackFarmer(ConnectedFarmerName, trackUid);
            return;
        }

        var session = new Infrastructure.PersistentSession(
            ownerType: _testBase.GetType(),
            lease: _testBase.LeaseInternal!,
            clientLease: _testBase.PrimaryClientLeaseInternal!,
            connection: _testBase.ConnectionInternal,
            exceptionMonitor: _testBase.ExceptionsInternal,
            farmerName: ConnectedFarmerName,
            farmerUid: ConnectedFarmerUid,
            clientSlotsHeld: ClientSlotsHeld,
            isAuthenticated: isAuthenticated);

        PersistentSessionStore.Register(_testBase.GetType(), session);
        Session = session;
        IsUsingPersistentSession = true;
        OwnsLease = false; // Session now owns the resources
        ClientSlotsHeld = 0; // Transfer capacity ownership to session
    }

    /// <summary>
    /// Breaks the persistent session: disconnects the client and takes over the session's
    /// server lease and client capacity for this test's own use. Safe because the turn lock
    /// ensures no other test in this class is using the session concurrently.
    /// Subsequent tests will find no session and re-acquire from scratch.
    /// </summary>
    public async Task BreakSessionAsync()
    {
        var session = PersistentSessionStore.Get(_testBase.GetType());
        if (session != null)
        {
            LogTrace("Breaking persistent session (turn lock held, safe)");
            PersistentSessionStore.Remove(_testBase.GetType());
        }

        // Return the client container to the pool. _primaryClientLease holds the
        // same object as session.ClientLease when a session was wired up; when
        // there is no session, the test leased its client directly via
        // GetClientAsync. Either way, this is the single dispose site --
        // ClientLease.DisposeAsync is idempotent.
        var primary = _testBase.PrimaryClientLeaseInternal;
        if (primary != null)
        {
            try { await primary.DisposeAsync(); } catch { }
        }

        if (session != null)
        {
            // Wait for the server to confirm the farmer is gone before reconnecting.
            if (session.FarmerUid is long brokenUid)
            {
                await session.Lease.Api.WaitForPlayerRemovedByIdAsync(brokenUid, timeout: TestTimings.FarmerRemovalBudget);
            }

            // Take over the session's server lease and capacity counter for this
            // test's own use.
            _testBase.AdoptBrokenSessionLease(session.Lease);
            OwnsLease = true;
            ClientSlotsHeld = session.ClientSlotsHeld;

            var testName = _displayName.Length > 0 ? _displayName : _testBase.GetType().Name;
            if (session.Lease.Managed?.InstanceId != null)
                SetupEventBus.EmitInstanceLeased(session.Lease.Managed.InstanceId, testName);
        }

        // Clear session-derived state; this test is now independent.
        Session = null;
        IsUsingPersistentSession = false;
        SessionBroken = true;
        _testBase.ClearAdoptedClientState();
        DidConnect = false;
    }

    /// <summary>
    /// Returns true if this is the last KeepConnected test in the class
    /// (also increments the gate's completion counter).
    /// </summary>
    internal bool IsLastKeepConnectedTestAndIncrement()
    {
        return KeepConnected && Gate != null && Gate.IncrementAndCheckDone();
    }

    /// <summary>
    /// Disposes the persistent session if this is the last test (or an
    /// orphaned session still exists), or notifies the broker for demand
    /// tracking. Returns the wall-time spent disposing the session, so
    /// TestBase's <c>test_completed</c> event payload still includes
    /// <c>lastKeepDisposeMs</c>.
    /// </summary>
    internal async Task<long> FinalizeSessionAsync(bool isLastKeepConnectedTest, CancellationToken ct)
    {
        long lastKeepDisposeMs = 0;

        if (IsUsingPersistentSession && Session != null)
        {
            // Persistent session mode: lightweight cleanup, keep client connected.
            if (isLastKeepConnectedTest)
            {
                LogTrace("Last KeepConnected test in class, disposing persistent session");
                var disposeSw = System.Diagnostics.Stopwatch.StartNew();
                await PersistentSessionStore.RemoveAndDisposeAsync(_testBase.GetType());
                lastKeepDisposeMs = disposeSw.ElapsedMilliseconds;
            }
            else
            {
                // Not the last test: keep session alive, just notify broker for demand tracking.
                var serverKey = _testBase.LeaseInternal?.ServerKey;
                if (serverKey != null)
                    TestResourceBroker.Instance.NotifyTestCompleted(serverKey);
            }
        }

        return lastKeepDisposeMs;
    }

    /// <summary>
    /// Disposes any orphaned session (after the test broke its session but a
    /// previous test had registered one). Mirrors the secondary dispose call
    /// in TestBase's non-KeepConnected branch when isLastKeepConnectedTest is
    /// still true.
    /// </summary>
    internal async Task<long> DisposeOrphanedSessionAsync()
    {
        LogTrace("Last KeepConnected test, disposing any remaining session");
        var disposeSw = System.Diagnostics.Stopwatch.StartNew();
        await PersistentSessionStore.RemoveAndDisposeAsync(_testBase.GetType());
        return disposeSw.ElapsedMilliseconds;
    }

    /// <summary>Synchronous, idempotent. Releases the exclusive gate.</summary>
    internal void ReleaseExclusiveGate()
    {
        if (HoldsExclusive)
        {
            HoldsExclusive = false;
            _testBase.LeaseInternal?.Managed.ReleaseExclusive();
        }
    }

    /// <summary>Synchronous, idempotent. Releases the per-class turn lock.</summary>
    internal void ReleaseTurnLock()
    {
        if (HoldsTurnLock && Gate != null)
        {
            HoldsTurnLock = false;
            LogTrace("Turn lock released");
            Gate.ReleaseTurn();
        }
    }

    /// <summary>
    /// Releases the per-host client capacity slot held by this test (used in
    /// the non-KeepConnected branch of DisposeAsync).
    /// </summary>
    internal void ReleaseClientCapacityIfHeld()
    {
        if (ClientSlotsHeld > 0 && _testBase.LeaseInternal != null)
        {
            _testBase.LeaseInternal.Host.ClientCapacity.Release(ClientSlotsHeld);
            ClientSlotsHeld = 0;
        }
    }

    private void Log(string message) =>
        SetupEventBus.EmitTestAnnotation(_displayName, AnnotationLevel.Info, AnnotationSource.Body, message);

    private void LogDetail(string message) =>
        SetupEventBus.EmitTestAnnotation(_displayName, AnnotationLevel.Detail, AnnotationSource.Body, message);

    private void LogTrace(string message) =>
        SetupEventBus.EmitTestAnnotation(_displayName, AnnotationLevel.Trace, AnnotationSource.Body, message);
}
