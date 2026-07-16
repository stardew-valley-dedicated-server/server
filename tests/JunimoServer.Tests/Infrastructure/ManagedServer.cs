using System.Collections.Concurrent;
using JunimoServer.Tests.Containers;
using JunimoServer.Tests.Helpers;
using JunimoServer.Tests.Schema.Events;

namespace JunimoServer.Tests.Infrastructure;

/// <summary>
/// Wraps ServerContainer + per-host server-slot reservation with ref counting,
/// health monitoring, and single-initialization guarantee. Owns its slot's release:
/// <see cref="DisposeAsync"/> calls <c>Host.ServerCapacity.Release(1)</c> exactly once,
/// guarded against double-release by <see cref="_slotReleased"/>.
/// </summary>
internal sealed class ManagedServer : IAsyncDisposable
{
    public string Key { get; }
    public ServerContainer Server { get; }

    /// <summary>The Docker host this server runs on. The slot was acquired against
    /// <c>Host.ServerCapacity</c> and is released by this class's disposal path.</summary>
    public DockerHost Host { get; }
    public ResourceRequirements Requirements { get; }

    /// <summary>
    /// Set to 1 by <see cref="ReleaseSlotEarly"/> so subsequent <see cref="DisposeAsync"/>
    /// calls don't double-release the host server-slot. Idempotent via Interlocked.
    /// </summary>
    private int _slotReleased;

    /// <summary>
    /// Steam account index allocated to this server (-1 if none).
    /// Used by the broker to release the account back to the allocator on dispose.
    /// </summary>
    public int SteamAccountIndex { get; set; } = -1;

    private readonly string _displayLabel;

    // Reference counting
    private int _refCount;
    public int RefCount => _refCount;

    // Transient in-flight marker. ServerPool.TryReserveBest increments under
    // ServerPool._lock when handing this instance to an acquirer; AddRef
    // decrements when the acquirer commits, or AcquireSharedCoreAsync calls
    // ReleaseReservation on its failure paths. The pool's ordering key reads
    // RefCount + Reservations so concurrent acquirers see climbing load
    // across the microsecond window between picker and commit. Steady state
    // is always 0.
    private int _reservations;
    internal int Reservations => _reservations;

    /// <summary>
    /// Stakes a transient claim on this instance ahead of <see cref="AddRef"/>.
    /// Called by <see cref="ServerPool.TryReserveBest"/> under that pool's lock.
    /// Must be paired with either <c>AddRef(consumeReservation: true)</c> on
    /// success or <see cref="ReleaseReservation"/> on failure.
    /// </summary>
    internal void ReserveForAcquire()
    {
        Interlocked.Increment(ref _reservations);
    }

    /// <summary>
    /// Releases a reservation taken by <see cref="ReserveForAcquire"/> without
    /// promoting it to a ref. Called by <c>AcquireSharedCoreAsync</c>'s retry
    /// and throw paths when AddRef did not run.
    /// </summary>
    internal void ReleaseReservation()
    {
        Interlocked.Decrement(ref _reservations);
    }

    /// <summary>Instance ID for UI tracking, set during initialization.</summary>
    public string? InstanceId { get; private set; }

    /// <summary>True once initialization has completed successfully.</summary>
    public bool IsInitialized => _initialized;

    public void AddRef(string? testName = null, bool consumeReservation = false)
    {
        if (consumeReservation)
        {
            Interlocked.Decrement(ref _reservations);
        }

        Interlocked.Increment(ref _refCount);
        if (InstanceId != null)
        {
            SetupEventBus.EmitInstanceLeased(InstanceId, testName ?? "");
            InfrastructureEventLog.Emit(
                "server_acquired",
                new
                {
                    server = Key,
                    instanceId = InstanceId,
                    host_id = Host.Id,
                    refCount = _refCount,
                    exclusive = HasExclusiveGate,
                }
            );
        }
    }

    public int Release()
    {
        var remaining = Interlocked.Decrement(ref _refCount);
        if (remaining <= 0 && InstanceId != null)
        {
            SetupEventBus.EmitInstanceReturned(InstanceId);
        }

        if (remaining <= 1)
        {
            _drainSignal?.TrySetResult();
        }

        if (remaining <= 0)
        {
            _poisonDrainSignal?.TrySetResult();
        }

        return remaining;
    }

    // Currently-executing tests on this server. Pushed by TestBase via
    // RegisterRunningTest at the queue→active transition; popped when the
    // returned token is disposed in TestBase.DisposeAsync. Distinct from
    // refcount-tracked leases: under KeepConnected the lease holds one ref
    // for the whole class, but _runningTests cycles per test method so
    // broker-side annotations can attribute events to the test that's
    // actually executing right now.
    private readonly ConcurrentDictionary<string, byte> _runningTests = new();

    /// <summary>
    /// Marks <paramref name="displayName"/> as currently executing on this
    /// server. The returned token must be disposed when the test exits to
    /// remove the registration. Call from TestBase at the queue→active
    /// transition; dispose in TestBase.DisposeAsync after lease release so
    /// <c>server_disposed</c> annotations still resolve to the test.
    /// </summary>
    internal IDisposable RegisterRunningTest(string displayName)
    {
        _runningTests.TryAdd(displayName, 0);
        return new RunningTestToken(this, displayName);
    }

    /// <summary>
    /// Fans an annotation out to every test currently executing on this
    /// server. No-op when <see cref="_runningTests"/> is empty (e.g. an event
    /// fires between leases or during prestart). Co-locate at typed-event
    /// emit sites so annotations and events share a try/catch.
    /// </summary>
    internal void EmitAnnotationToRunningTests(AnnotationLevel level, string message)
    {
        foreach (var displayName in _runningTests.Keys)
        {
            SetupEventBus.EmitTestAnnotation(displayName, level, AnnotationSource.Broker, message);
        }
    }

    private sealed class RunningTestToken : IDisposable
    {
        private readonly ManagedServer _server;
        private readonly string _displayName;
        private int _disposed;

        public RunningTestToken(ManagedServer server, string displayName)
        {
            _server = server;
            _displayName = displayName;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _server._runningTests.TryRemove(_displayName, out _);
        }
    }

    // Disposal guard
    private int _disposed;

    // Error/abort state
    private volatile bool _aborted;
    private string? _abortReason;
    private CancellationTokenSource _errorCts = new();
    private volatile bool _poisoned;

    // Signaled by Release() when _refCount drops to <=1, waking the exclusive drain waiter.
    private volatile TaskCompletionSource? _drainSignal;

    // Signaled by Release() when _refCount drops to 0, waking the poison-drain
    // waiter in DisposeAfterDrainAsync. Distinct from _drainSignal (exclusive
    // access) because that one fires at <=1, not <=0.
    private volatile TaskCompletionSource? _poisonDrainSignal;

    // Exclusive access: non-blocking drain pattern with class-scoped retention.
    //
    // When an exclusive test starts:
    //   1. Wait for any prior exclusive test (from a different class) to finish
    //   2. Set _exclusiveDone + _exclusiveOwnerClass + AddRef atomically (under _exclusiveLock)
    //   3. Wait for other refs to drain (existing tests can finish freely)
    //   4. Once alone, the exclusive test runs
    //   5. On release:
    //      - If same-class waiters are queued → signal the next one via semaphore,
    //        keep the TCS held (non-class tests remain blocked)
    //      - If no same-class waiters → complete TCS, allowing waiting tests to proceed
    //
    // Class-scoped retention: when [TestServer(Exclusive=true)] is on the class, all
    // methods inherit it. The gate stays held between methods of the same class, so
    // non-class tests can't interfere (e.g., a test calling /newgame won't wipe state
    // from another class's in-progress test). Methods within the class serialize via
    // a semaphore (_exclusiveClassTurn); each method waits for the prior to finish
    // before taking its turn.
    //
    // All callers (exclusive and non-exclusive) check _exclusiveDone under
    // _exclusiveLock before adding a ref. This prevents:
    //   - TOCTOU: non-exclusive reads null, exclusive sets TCS, non-exclusive
    //     adds ref → exclusive waits on that ref forever
    //   - Mutual deadlock: two exclusive tests both add refs and wait for
    //     each other to drain
    private volatile TaskCompletionSource? _exclusiveDone;
    private string? _exclusiveOwnerClass;
    private int _exclusiveClassWaiters; // same-class methods waiting to inherit the gate
    private readonly SemaphoreSlim _exclusiveClassTurn = new(0); // serializes same-class inheritance
    private readonly object _exclusiveLock = new();

    // Join serialization: the game loop is single-threaded and can only process one
    // farmhand join at a time. Without this gate, concurrent KeepConnected classes on
    // the same server all call Connect.JoinWithRetryAsync simultaneously, causing the
    // server to bounce clients back to farmhand selection (isGameAvailable() == false).
    private readonly SemaphoreSlim _joinGate = new(1, 1);

    /// <summary>
    /// Serializes farmer join operations on this server instance.
    /// The game loop can only process one farmhand join at a time.
    /// </summary>
    public Task AcquireJoinGateAsync(CancellationToken ct) =>
        WaitTrace.RunAsync(
            WaitName.ManagedServer_JoinGate,
            () => _joinGate.WaitAsync(ct),
            ct,
            snapshot: () => new { server = Key }
        );

    /// <summary>Releases the join gate after a farmer join completes or fails.</summary>
    public void ReleaseJoinGate() => _joinGate.Release();

    /// <summary>True if an exclusive test currently holds the gate on this instance.</summary>
    public bool HasExclusiveGate => _exclusiveDone != null;

    /// <summary>The class name currently holding the exclusive gate, or null.</summary>
    public string? ExclusiveOwnerClass => _exclusiveOwnerClass;

    /// <summary>
    /// Extracts the class name from a fully-qualified test name.
    /// e.g. "JunimoServer.Tests.FarmMapTypeTests.MethodName" -> "FarmMapTypeTests"
    /// </summary>
    internal static string? ExtractClassName(string? testName)
    {
        if (testName == null)
        {
            return null;
        }
        // Strip method name (and any Theory args in parens)
        var parenIdx = testName.IndexOf('(');
        var name = parenIdx >= 0 ? testName[..parenIdx] : testName;
        var parts = name.Split('.');
        return parts.Length >= 2 ? parts[^2] : parts[^1];
    }

    /// <summary>
    /// Adds a ref while respecting exclusive access. If an exclusive test is
    /// active (pending or running), blocks until it finishes.
    /// </summary>
    public async Task AddRefExclusiveAwareAsync(
        string? testName,
        CancellationToken ct,
        Func<Task>? releaseCapacity = null,
        Func<Task>? reacquireCapacity = null,
        bool consumeReservation = false
    )
    {
        // Loop: after waiting for exclusive to finish, re-check under lock.
        // another exclusive test may have started in the meantime.
        var capacityReleased = false;
        while (true)
        {
            // If we released capacity while waiting, reacquire BEFORE AddRef.
            // AddRef with released capacity creates a deadlock: the exclusive test
            // sees our ref and waits for it to drain, but we need capacity to proceed.
            if (capacityReleased && reacquireCapacity != null)
            {
                TestLog.Server(
                    $"{_displayLabel} non-exclusive test reacquiring capacity before AddRef"
                );
                await reacquireCapacity();
                capacityReleased = false;
            }

            TaskCompletionSource? done;
            lock (_exclusiveLock)
            {
                done = _exclusiveDone;
                if (done == null)
                {
                    // No exclusive test; safe to add ref while holding the lock.
                    AddRef(testName, consumeReservation);
                    return;
                }
            }

            // Release client capacity while waiting for the exclusive test to finish.
            // Without this, non-exclusive tests hold capacity slots while blocked here,
            // starving KeepConnected sessions that need capacity to run their next test
            // (bringing the class closer to completion and eventual ref release).
            if (!capacityReleased && releaseCapacity != null)
            {
                TestLog.Server(
                    $"{_displayLabel} non-exclusive test releasing capacity while waiting for exclusive"
                );
                await releaseCapacity();
                capacityReleased = true;
            }

            TestLog.Server(
                $"{_displayLabel} non-exclusive test waiting for exclusive to finish..."
            );
            await WaitForExclusiveAsync(done, ct);
            TestLog.Server($"{_displayLabel} exclusive test finished, proceeding");
        }
    }

    /// <summary>
    /// Acquires exclusive access: waits for any prior exclusive test to finish,
    /// then adds a ref, signals intent, and waits for all other refs to drain.
    /// New tests block at <see cref="AddRefExclusiveAwareAsync"/> until
    /// <see cref="ReleaseExclusive"/> is called.
    ///
    /// Class-scoped exclusivity: when Exclusive is set at class level, all methods
    /// in that class inherit it. The exclusive gate is held across methods of the
    /// same class. Non-class tests remain blocked between methods, preventing
    /// interference (e.g., /newgame wiping state). Methods within the class still
    /// serialize via the drain-to-1 wait.
    /// </summary>
    public async Task AddRefAndAcquireExclusiveAsync(
        string? testName,
        CancellationToken ct,
        Func<Task>? releaseAndReacquireCapacity = null,
        bool consumeReservation = false
    )
    {
        var callerClass = ExtractClassName(testName);
        var inheritedFromClass = false;
        var reservationConsumed = false;

        while (true)
        {
            TaskCompletionSource? prior;
            lock (_exclusiveLock)
            {
                prior = _exclusiveDone;
                var sameClass =
                    prior != null && callerClass != null && _exclusiveOwnerClass == callerClass;

                if (prior == null)
                {
                    // No prior exclusive; claim the slot atomically.
                    _exclusiveDone = new TaskCompletionSource(
                        TaskCreationOptions.RunContinuationsAsynchronously
                    );
                    _exclusiveOwnerClass = callerClass;
                    // Drain any stale semaphore permits from a prior cancelled session.
                    while (_exclusiveClassTurn.CurrentCount > 0)
                    {
                        _exclusiveClassTurn.Wait(0);
                    }

                    AddRef(testName, consumeReservation);
                    reservationConsumed = consumeReservation;
                    break;
                }

                if (sameClass)
                {
                    // Same class already holds the gate; register as waiter so
                    // ReleaseExclusive knows not to complete the TCS.
                    // Don't AddRef yet; wait for the prior method to finish first
                    // to serialize methods within the class.
                    _exclusiveClassWaiters++;
                    inheritedFromClass = true;
                    break;
                }
            }

            // Different class holds the gate; wait for it to finish.
            TestLog.Server(
                $"{_displayLabel} exclusive test waiting for prior exclusive to finish..."
            );
            await WaitForExclusiveAsync(prior, ct);
        }

        if (inheritedFromClass)
        {
            // Wait for our turn. The semaphore is signaled by ReleaseExclusive
            // (or the prior inherited method's cleanup) one-at-a-time, serializing
            // same-class methods. The TCS stays held + _exclusiveClassWaiters > 0,
            // so non-class tests can't sneak in.
            TestLog.Server(
                $"{_displayLabel} same-class exclusive queued, waiting for turn (refs={_refCount}, waiters={_exclusiveClassWaiters})"
            );
            try
            {
                await WaitTrace.RunAsync(
                    WaitName.ManagedServer_ExclusiveClassTurn,
                    () => _exclusiveClassTurn.WaitAsync(ct),
                    ct,
                    snapshot: () =>
                        new
                        {
                            server = Key,
                            refCount = _refCount,
                            classWaiters = _exclusiveClassWaiters,
                        }
                );
            }
            catch
            {
                lock (_exclusiveLock)
                {
                    _exclusiveClassWaiters--;
                    if (_exclusiveClassWaiters <= 0 && _refCount <= 0)
                    {
                        var done = _exclusiveDone;
                        _exclusiveDone = null;
                        _exclusiveOwnerClass = null;
                        done?.TrySetResult();
                    }
                }
                // Cancellation/error before AddRef ran on this same-class
                // inherited path: release the reservation the caller staked.
                if (consumeReservation && !reservationConsumed)
                {
                    ReleaseReservation();
                }

                throw;
            }

            lock (_exclusiveLock)
            {
                _exclusiveClassWaiters--;
            }
            AddRef(testName, consumeReservation);
            reservationConsumed = consumeReservation;
            TestLog.Server(
                $"{_displayLabel} exclusive access granted (inherited from class '{callerClass}', refs={_refCount})"
            );
            InfrastructureEventLog.Emit(
                "exclusive_acquired",
                new
                {
                    server = Key,
                    instanceId = InstanceId,
                    test = testName,
                    refCount = _refCount,
                    kind = "with_ref",
                    inheritedFromClass = true,
                }
            );
            return;
        }

        TestLog.Server($"{_displayLabel} waiting for refs to drain (current={_refCount})");

        try
        {
            // Release client capacity during drain to prevent deadlock:
            // KeepConnected sessions hold a server ref across tests and need
            // ClientCapacity to run their next test (which releases the ref).
            // The callback atomically enqueues a high-priority reacquire waiter
            // THEN releases, so the drain serves us before other waiters.
            if (_refCount > 1 && releaseAndReacquireCapacity != null)
            {
                TestLog.Server(
                    $"{_displayLabel} releasing capacity during drain wait (atomic reacquire enqueued)"
                );
                await releaseAndReacquireCapacity();
            }

            // Wait for all other tests to finish (their refs to drain to just us).
            // Release() signals _drainSignal when _refCount drops to <=1.
            var drainSignal = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            _drainSignal = drainSignal;

            // Fast path: Release() may have already fired between AddRef and here.
            if (_refCount <= 1)
            {
                _drainSignal = null;
            }
            else
            {
                await WaitTrace.RunAsync(
                    WaitName.ManagedServer_RefDrain,
                    () => drainSignal.Task.WaitAsync(ct),
                    ct,
                    snapshot: () =>
                        new
                        {
                            server = Key,
                            refCount = _refCount,
                            displayLabel = _displayLabel,
                        }
                );
            }
        }
        catch
        {
            _drainSignal = null;
            // Cancelled or error; complete the TCS so waiting tests aren't stuck.
            // The ref is still held and will be released by the caller's DisposeAsync.
            lock (_exclusiveLock)
            {
                if (_refCount <= 1 && _exclusiveClassWaiters <= 0)
                {
                    var done = _exclusiveDone;
                    _exclusiveDone = null;
                    _exclusiveOwnerClass = null;
                    done?.TrySetResult();
                }
            }
            throw;
        }

        _drainSignal = null;
        TestLog.Server($"{_displayLabel} exclusive access granted (refs={_refCount})");
        InfrastructureEventLog.Emit(
            "exclusive_acquired",
            new
            {
                server = Key,
                instanceId = InstanceId,
                test = testName,
                refCount = _refCount,
                kind = "with_ref",
                inheritedFromClass = false,
            }
        );
    }

    /// <summary>
    /// Releases exclusive access. If other methods from the same class still hold
    /// refs, the gate stays held and non-class tests remain blocked. Only when the
    /// last same-class ref releases does the TCS complete.
    /// </summary>
    public void ReleaseExclusive()
    {
        TaskCompletionSource? done;
        lock (_exclusiveLock)
        {
            if (_exclusiveDone == null)
            {
                return;
            }

            // Same-class methods are waiting; signal the next one via semaphore.
            // The TCS stays held so non-class tests remain blocked.
            if (_exclusiveClassWaiters > 0)
            {
                TestLog.Server(
                    $"{_displayLabel} exclusive access passing to next same-class method ({_exclusiveClassWaiters} waiter(s))"
                );
                InfrastructureEventLog.Emit(
                    "exclusive_released",
                    new
                    {
                        server = Key,
                        instanceId = InstanceId,
                        kind = "passed_to_same_class",
                        ownerClass = _exclusiveOwnerClass,
                        waiters = _exclusiveClassWaiters,
                    }
                );
                _exclusiveClassTurn.Release();
                return;
            }

            done = _exclusiveDone;
            _exclusiveDone = null;
            _exclusiveOwnerClass = null;
        }
        done.TrySetResult();
        TestLog.Server($"{_displayLabel} exclusive access released");
        InfrastructureEventLog.Emit(
            "exclusive_released",
            new
            {
                server = Key,
                instanceId = InstanceId,
                kind = "ended",
            }
        );
    }

    /// <summary>
    /// Acquires the exclusive gate for a KeepConnected test that reuses an existing
    /// persistent session. Unlike <see cref="AddRefAndAcquireExclusiveAsync"/>, this
    /// does NOT add a ref (the persistent session already holds one). After setting
    /// the gate, waits for all other classes' refs to drain naturally (their classes
    /// finish all tests, dispose sessions, and release refs).
    ///
    /// For KeepConnected classes, same-class serialization is handled by the turn lock,
    /// not the exclusive class semaphore. If another exclusive from the same class
    /// already holds the gate, this returns immediately (the turn lock guarantees
    /// the prior method has already finished).
    /// </summary>
    public async Task AcquireExclusiveGateOnlyAsync(string? testName, CancellationToken ct)
    {
        var callerClass = ExtractClassName(testName);

        while (true)
        {
            TaskCompletionSource? prior;
            lock (_exclusiveLock)
            {
                prior = _exclusiveDone;

                if (prior == null)
                {
                    // No prior exclusive; claim the slot.
                    _exclusiveDone = new TaskCompletionSource(
                        TaskCreationOptions.RunContinuationsAsynchronously
                    );
                    _exclusiveOwnerClass = callerClass;
                    break;
                }

                // Same class already owns the gate. For KeepConnected, the turn lock
                // ensures the prior exclusive method has finished (and released the gate
                // in its DisposeAsync). If the gate is still set, it means a different
                // method in the same class set it in this very turn, which can't happen
                // because the turn lock serializes methods. Safe to treat as a no-op race.
                if (callerClass != null && _exclusiveOwnerClass == callerClass)
                {
                    return;
                }
            }

            // Different class holds the gate; wait for it to finish.
            TestLog.Server(
                $"{_displayLabel} exclusive gate-only waiting for prior exclusive to finish..."
            );
            await WaitForExclusiveAsync(prior, ct);
        }

        // Wait for other refs to drain. KeepConnected sessions hold their refs until
        // the last test in the class finishes and DisposeAsync releases them.
        // Non-KeepConnected tests block at AddRefExclusiveAwareAsync during acquisition.
        TestLog.Server(
            $"{_displayLabel} exclusive gate set, waiting for other refs to drain (current={_refCount})"
        );
        await WaitTrace.RunAsync(
            WaitName.ManagedServer_GateOnlyRefPoll,
            async () =>
            {
                while (_refCount > 1 && !ct.IsCancellationRequested)
                {
                    await Task.Delay(100, ct);
                }

                ct.ThrowIfCancellationRequested();
            },
            ct,
            snapshot: () => new { server = Key, refCount = _refCount }
        );

        TestLog.Server(
            $"{_displayLabel} exclusive gate-only acquired by '{testName}' (refs={_refCount})"
        );
        InfrastructureEventLog.Emit(
            "exclusive_acquired",
            new
            {
                server = Key,
                instanceId = InstanceId,
                test = testName,
                refCount = _refCount,
                kind = "gate_only",
                inheritedFromClass = false,
            }
        );
    }

    public bool IsAborted => _aborted;
    public bool IsPoisoned => _poisoned;
    public string? AbortReason => _abortReason;
    public CancellationToken ErrorToken => _errorCts.Token;

    // Single initialization guarantee
    private readonly Lazy<Task> _initTask;
    private volatile bool _initialized;

    // Health watchdog
    private CancellationTokenSource? _healthCts;
    private Task? _healthTask;
    private volatile bool _healthSuspended;

    private Action<ManagedServer>? _onPoisoned;

    /// <summary>
    /// Registers a callback invoked when this server is poisoned.
    /// Used by the broker to trigger automatic replacement.
    /// </summary>
    public void SetPoisonCallback(Action<ManagedServer> callback) => _onPoisoned = callback;

    public ManagedServer(
        string key,
        ServerContainer server,
        DockerHost host,
        ResourceRequirements requirements
    )
    {
        Key = key;
        Server = server;
        Host = host;
        Requirements = requirements;
        _displayLabel = requirements.GetDisplayLabel();
        _initTask = new Lazy<Task>(() => InitializeCoreAsync(_initCts));
    }

    /// <summary>
    /// Releases the host server-slot synchronously, ahead of full disposal. Used by
    /// the broker's eviction path to unblock a waiter that needs the slot now while
    /// the heavy container teardown (Docker stop-grace + recording extraction) runs
    /// in the background. Idempotent: <see cref="DisposeAsync"/> sees
    /// <see cref="_slotReleased"/> set and skips the second release.
    /// </summary>
    public void ReleaseSlotEarly()
    {
        if (Interlocked.Exchange(ref _slotReleased, 1) == 0)
        {
            Host.ServerCapacity.Release(1);
        }
    }

    private CancellationToken _initCts;

    /// <summary>
    /// Ensures server is started, ready, and health watchdog running.
    /// Safe to call from multiple threads. Initialization runs exactly once.
    /// </summary>
    public Task EnsureInitializedAsync(CancellationToken ct)
    {
        _initCts = ct;
        return WaitTrace.RunAsync(
            WaitName.ManagedServer_EnsureInitialized,
            () => _initTask.Value,
            ct,
            snapshot: () => new { server = Key, alreadyComplete = _initialized }
        );
    }

    private async Task InitializeCoreAsync(CancellationToken ct)
    {
        // Register instance in UI immediately (before container starts).
        // VNC URL is null until StartAsync publishes ports. The UI shows
        // a "Starting" placeholder that gets replaced once VNC is available.
        // Instance ID is owned by the ServerContainer (set at construction by the broker)
        // so the container can emit instance_recording with a matching ID at dispose time.
        InstanceId = Server.InstanceId ?? $"server-{Key}-{Server.ServerIndex}";
        SetupEventBus.EmitInstanceCreated(InstanceId, "server", Key, null, _displayLabel, Host.Id);

        SetupEventBus.EmitPhaseStarted("Setup", _displayLabel, Key);
        SetupEventBus.EmitStep(
            "Setup",
            "Creating server container",
            SetupStepStatus.Started,
            collectionName: Key
        );

        await Host.StartLimiter.WaitAsync(StartPriority.High, ct);

        SetupEventBus.EmitStep(
            "Setup",
            "Creating server container",
            SetupStepStatus.Completed,
            collectionName: Key
        );
        SetupEventBus.EmitStep(
            "Setup",
            "Starting server container",
            SetupStepStatus.Started,
            collectionName: Key
        );
        var startedSw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await Server.StartAsync(onProgress: detail =>
                SetupEventBus.EmitStep(
                    "Setup",
                    "Starting server container",
                    SetupStepStatus.InProgress,
                    detail,
                    Key
                )
            );
        }
        finally
        {
            Host.StartLimiter.Release();
        }
        // Cold-start phase split: container-up. Failures before here go through
        // the existing server_creation_failed path (TestResourceBroker.cs:580).
        InfrastructureEventLog.Emit(
            "server_started",
            new
            {
                server = Key,
                instanceId = InstanceId,
                durationMs = startedSw.ElapsedMilliseconds,
            }
        );
        SetupEventBus.EmitStep(
            "Setup",
            "Starting server container",
            SetupStepStatus.Completed,
            collectionName: Key
        );

        // Start recording AFTER releasing the start-limiter slot: recording is exec-only
        // against the already-running container, so holding a create+start slot during it
        // would serialize other containers' starts for no reason.
        await Server.StartRecordingAsync(ct);

        // Show video recording status in container setup steps
        if (Server.IsRecording)
        {
            SetupEventBus.EmitStep(
                "Setup",
                "Video recording",
                SetupStepStatus.Completed,
                "recording active",
                collectionName: Key
            );
        }

        // Update UI with VNC URL now that ports are published
        SetupEventBus.EmitInstanceCreated(
            InstanceId,
            "server",
            Key,
            Server.VncUrl,
            _displayLabel,
            Host.Id
        );

        // Register for stats tracking now that the container is running
        ContainerStatsCollector.Register(
            InstanceId,
            Server.Container.Id,
            Server.Container.Name,
            Host,
            Server.BaseUrl
        );

        SetupEventBus.EmitStep(
            "Setup",
            "Waiting for server ready",
            SetupStepStatus.Started,
            collectionName: Key
        );
        // Switch log callback to target the "Waiting for server ready" step
        Server.SetStartupLogCallback(detail =>
            SetupEventBus.EmitStep(
                "Setup",
                "Waiting for server ready",
                SetupStepStatus.InProgress,
                detail,
                Key
            )
        );
        var readySw = System.Diagnostics.Stopwatch.StartNew();
        var ready = await Server.WaitForReadyAsync(onProgress: detail =>
            SetupEventBus.EmitStep(
                "Setup",
                "Waiting for server ready",
                SetupStepStatus.InProgress,
                detail,
                Key
            )
        );
        // Stop emitting log lines to UI (log streaming continues for error detection)
        Server.SetStartupLogCallback(null);
        // Cold-start phase split: game-ready. Emitted before the !ready throw
        // so slow-failure cases (e.g. cold-start hitting the WaitForReady timeout)
        // are visible in infrastructure.jsonl.
        InfrastructureEventLog.Emit(
            "server_ready",
            new
            {
                server = Key,
                instanceId = InstanceId,
                durationMs = readySw.ElapsedMilliseconds,
                success = ready,
            }
        );
        if (!ready)
        {
            SetupEventBus.EmitStep(
                "Setup",
                "Waiting for server ready",
                SetupStepStatus.Failed,
                collectionName: Key
            );
            SetupEventBus.EmitPhaseCompleted(
                "Setup",
                _displayLabel,
                false,
                "Server failed to become ready",
                Key
            );
            throw new TimeoutException($"Server {_displayLabel} failed to become ready");
        }
        SetupEventBus.EmitStep(
            "Setup",
            "Waiting for server ready",
            SetupStepStatus.Completed,
            collectionName: Key
        );

        SetupEventBus.EmitPhaseCompleted("Setup", _displayLabel, true, collectionName: Key);
        _initialized = true;
        StartHealthWatchdog();

        // Wire ServerContainer's error detection (SMAPI ERROR/FATAL, Docker API failures)
        // to ManagedServer's poison mechanism so tests abort immediately. SuspendHealthChecks
        // gates this too: an intentional transition (new game, reload, network outage) is
        // expected to emit server ERRORs, and treating those as a poison-worthy crash would
        // dispose the container mid-test — so one suspend bracket covers both the watchdog and
        // this log-error scan (see NetworkOutageHelper's class doc).
        Server
            .GetErrorCancellationToken()
            .Register(() =>
            {
                if (!_poisoned && !_healthSuspended && !ShutdownCoordinator.IsShuttingDown)
                {
                    var errors = Server.Errors;
                    var reason =
                        errors.Count > 0 ? errors[0] : "Server error detected via log stream";
                    PoisonServer(reason, PoisonReasonCode.ServerLogError);
                }
            });
    }

    /// <summary>
    /// Suspends health checks during intentional server transitions (e.g., new game creation).
    /// </summary>
    public void SuspendHealthChecks() => _healthSuspended = true;

    /// <summary>
    /// Resumes health checks after a server transition completes.
    /// </summary>
    public void ResumeHealthChecks() => _healthSuspended = false;

    private void StartHealthWatchdog()
    {
        _healthCts = new CancellationTokenSource();
        // SuppressFlow: health watchdog outlives the test that triggered server
        // creation and emits events for many subsequent tests. Without this every
        // /health probe and poison event would be attributed to the starting test.
        // See .claude/rules/asynclocal-pitfalls.md.
        using (ExecutionContext.SuppressFlow())
        {
            _healthTask = Task.Run(() => HealthWatchdogLoop(_healthCts.Token));
        }
    }

    /// <summary>
    /// Monitors server liveness via /health (not /status). The /health endpoint uses
    /// Interlocked.Read for tick timestamps and never blocks on the game thread, so it
    /// responds instantly even during 30s+ game thread stalls. This prevents false
    /// poisoning caused by game thread contention on Docker hosts.
    /// </summary>
    private async Task HealthWatchdogLoop(CancellationToken ct)
    {
        var consecutiveFailures = 0;
        var maxFailures = ParseEnvInt("SDVD_HEALTH_CHECK_MAX_FAILURES", 5);
        var intervalMs = ParseEnvInt("SDVD_HEALTH_CHECK_INTERVAL_MS", 5000);

        // Tracks the *last* failure kind in the current streak so the final
        // poison event gets a reason code that actually matches the cause.
        // Null-response and stuck-tick are "timeout"; any thrown exception
        // (TaskCanceled, HttpRequest, JSON parse, etc.) is "error".
        string lastFailureCode = PoisonReasonCode.HealthCheckTimeout;
        string lastFailureReason = "Health check failed, server unresponsive";

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(intervalMs, ct);

                if (ShutdownCoordinator.IsShuttingDown)
                {
                    break;
                }

                // A poisoned host means every probe goes through a dead daemon
                // or tunnel. Poison the server now (accurate reason, immediate
                // ErrorToken cascade) instead of burning maxFailures probes to
                // reach the same verdict with a misleading "health check" reason.
                if (Host.IsPoisoned)
                {
                    PoisonServer(
                        $"Host {Host.Id} poisoned: {Host.PoisonReason}",
                        PoisonReasonCode.HostPoisoned
                    );
                    break;
                }

                if (_healthSuspended)
                {
                    consecutiveFailures = 0;
                    continue;
                }

                using var client = Server.CreateApiClient();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                // Per-probe deadline. ServerApiClient's own HttpClient.Timeout is 5 min
                // (sized for long-polls), so without this a -L forward that wedges
                // MID-STREAM (bytes stop flowing on an established channel — no exception
                // thrown, so ForwardHealingHandler never engages) freezes THIS probe for
                // 5 min; the loop never reaches the failure counter, no health.check_*
                // fires, and the server never poisons (observed: run 02-12-57Z,
                // host-poison-deadlocks-run.md fu6 — zero health events through the wedge).
                //
                // The deadline MUST sit ABOVE ForwardHealingHandler's heal-retry budget
                // (45s): the watchdog's GetHealth() is wrapped by that handler, so a
                // CATCHABLE forward blip (listener gone → SocketException) is healed in
                // place within 45s and the probe then succeeds. Cutting the probe at <45s
                // would abort a legitimate heal and miscount it as a wedge (real /health
                // calls in run 01-51 took up to ~160s riding a blip+heal to a 200, with a
                // ~0.5s-fresh snapshot — the server was alive the whole time). 50s clears
                // the heal budget with margin while still bounding the mid-stream hang far
                // below 5 min. A probe deadline is NOT shutdown: surface it as
                // TimeoutException so it flows through the heal/poison-count path below,
                // not the outer-ct `break`.
                using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                probeCts.CancelAfter(ParseEnvInt("SDVD_HEALTH_CHECK_PROBE_TIMEOUT_MS", 50_000));
                Clients.HealthResponse? health;
                try
                {
                    health = await client.GetHealth(probeCts.Token);
                }
                catch (OperationCanceledException)
                    when (probeCts.IsCancellationRequested && !ct.IsCancellationRequested)
                {
                    throw new TimeoutException(
                        $"/health probe exceeded {ParseEnvInt("SDVD_HEALTH_CHECK_PROBE_TIMEOUT_MS", 50_000)}ms "
                            + "(forward wedged mid-stream or server unresponsive)"
                    );
                }
                sw.Stop();

                if (health == null)
                {
                    consecutiveFailures++;
                    lastFailureCode = PoisonReasonCode.HealthCheckTimeout;
                    lastFailureReason = "Health check failed, server returned null";
                    InfrastructureEventLog.Emit(
                        "health.check_failed",
                        new
                        {
                            server = Key,
                            instanceId = InstanceId,
                            responseMs = sw.ElapsedMilliseconds,
                            consecutiveFailures,
                            maxFailures,
                            reason = "null response",
                        }
                    );
                    TestLog.Server(
                        $"{_displayLabel} health check failed (null response, "
                            + $"{consecutiveFailures}/{maxFailures}, {sw.ElapsedMilliseconds}ms)"
                    );
                }
                else if (health.LastTickMs == null)
                {
                    // Server is booting; no game tick has fired yet. Don't count as failure.
                    consecutiveFailures = 0;
                }
                else if (health.LastTickMs < 30_000)
                {
                    // Game thread ticked within 30s. Healthy (even if degraded).
                    if (health.LastTickMs > 5000)
                    {
                        InfrastructureEventLog.Emit(
                            "health.slow_tick",
                            new
                            {
                                server = Key,
                                instanceId = InstanceId,
                                responseMs = sw.ElapsedMilliseconds,
                                lastTickMs = health.LastTickMs,
                                pendingActions = health.PendingActions,
                                gameAvailable = health.GameAvailable,
                                healthStatus = health.Status,
                            }
                        );
                        TestLog.Server(
                            $"{_displayLabel} slow tick ({health.LastTickMs}ms): "
                                + $"pending={health.PendingActions}, gameAvailable={health.GameAvailable}"
                        );
                    }

                    consecutiveFailures = 0;
                }
                else
                {
                    // Game thread hasn't ticked in 30+ seconds. Likely stuck.
                    consecutiveFailures++;
                    lastFailureCode = PoisonReasonCode.HealthCheckTimeout;
                    lastFailureReason = $"Game thread stalled (lastTickMs={health.LastTickMs})";
                    InfrastructureEventLog.Emit(
                        "health.check_failed",
                        new
                        {
                            server = Key,
                            instanceId = InstanceId,
                            responseMs = sw.ElapsedMilliseconds,
                            lastTickMs = health.LastTickMs,
                            pendingActions = health.PendingActions,
                            consecutiveFailures,
                            maxFailures,
                            healthStatus = health.Status,
                        }
                    );
                    TestLog.Server(
                        $"{_displayLabel} health check failed (lastTickMs={health.LastTickMs}, "
                            + $"{consecutiveFailures}/{maxFailures})"
                    );
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                if (_healthSuspended)
                {
                    consecutiveFailures = 0;
                    continue;
                }

                // Forward-scoped fault (loopback ConnectionRefused): the per-server
                // -L forward's listener is gone, which a transient shared-master
                // keepalive blip drops for every forward at once while the host stays
                // reachable. If `ssh -O check` confirms the master is alive, re-open
                // this server's forward in place and DON'T count it toward poison —
                // healing a reused/live server instead of cascading into a host
                // poison (the 2026-06-25 13-test cascade). Master dead ⇒ fall through
                // to normal failure counting (the host really is gone).
                if (await TryHealForwardScopedFaultAsync(ex, ct))
                {
                    consecutiveFailures = 0;
                    continue;
                }

                consecutiveFailures++;
                lastFailureCode = PoisonReasonCode.HealthCheckError;
                lastFailureReason = $"Health check threw {ex.GetType().Name}: {ex.Message}";
                InfrastructureEventLog.Emit(
                    "health.check_error",
                    new
                    {
                        server = Key,
                        instanceId = InstanceId,
                        error = $"{ex.GetType().Name}: {ex.Message}",
                        consecutiveFailures,
                        maxFailures,
                    }
                );
                TestLog.Server(
                    $"{_displayLabel} health check error ({ex.GetType().Name}: {ex.Message}, "
                        + $"{consecutiveFailures}/{maxFailures})"
                );
            }

            if (consecutiveFailures >= maxFailures)
            {
                InfrastructureEventLog.Emit(
                    "health.poison",
                    new
                    {
                        server = Key,
                        instanceId = InstanceId,
                        reason = lastFailureReason,
                        reasonCode = lastFailureCode,
                        consecutiveFailures,
                    }
                );
                PoisonServer(lastFailureReason, lastFailureCode);
                break;
            }
        }
    }

    /// <summary>
    /// When a health probe throws a <i>forward-scoped</i> transport fault (loopback
    /// ConnectionRefused — the per-server <c>ssh -L</c> listener is gone), corroborate
    /// the host is still up via <c>ssh -O check</c> and, if so, re-open this server's
    /// API forward in place. Returns true when the fault was forward-scoped AND
    /// healed — the caller then resets the failure streak instead of advancing toward
    /// poison. Returns false for non-forward faults, local hosts, a dead master, or a
    /// failed re-open (all of which should count normally).
    /// </summary>
    private async Task<bool> TryHealForwardScopedFaultAsync(Exception ex, CancellationToken ct)
    {
        var (_, forwardScoped) = TransportFaultClassifier.Classify(ex);
        if (!forwardScoped || Host.SshDestination is null)
        {
            return false;
        }

        // Establish the master is usable before re-opening the forward (retries -O check +
        // respawns once — see TunnelManager.EnsureMasterUsableAsync). This runs every
        // health-watchdog cycle while the forward is dead, so even a longer outage heals on
        // a later cycle rather than the test eating the whole blip.
        if (!await TunnelManager.Default.EnsureMasterUsableAsync(Host.Id, ct))
        {
            return false;
        }

        try
        {
            var reopened = await Server.ReopenApiForwardAsync(ct);
            if (reopened)
            {
                TestLog.Server(
                    $"{_displayLabel} healed forward-scoped fault "
                        + $"({ex.GetType().Name}) — re-opened API forward, host kept"
                );
            }
            return reopened;
        }
        catch (Exception healEx)
        {
            TestLog.Server(
                $"{_displayLabel} forward re-open failed after {ex.GetType().Name}: {healEx.Message}"
            );
            return false;
        }
    }

    /// <summary>
    /// Enumerated poison cause codes. Paired with the free-form <c>reason</c> string
    /// so analytics can bucket failures without parsing it. Add new codes sparingly —
    /// each one should map to a distinct recovery or investigation path.
    /// </summary>
    public static class PoisonReasonCode
    {
        public const string HealthCheckTimeout = "health_check_timeout";
        public const string HealthCheckError = "health_check_error";
        public const string HostPoisoned = "host_poisoned";
        public const string ServerLogError = "server_log_error";
        public const string FarmerRemovalTimeout = "farmer_removal_timeout";
        public const string CleanupFarmerDeleteFailed = "cleanup_farmer_delete_failed";

        /// <summary>A test intentionally retired a server it left unusable (e.g. a deliberate
        /// network-outage test whose server's API reachability can't be restored), so it must not be
        /// reused. Not a defect — distinguishes intentional retirement from real poison in reports.</summary>
        public const string TestRetiredServer = "test_retired_server";
        public const string Other = "other";
    }

    /// <summary>
    /// Marks server as permanently unusable. Cancels error token.
    /// All active and future leases will fail.
    /// </summary>
    public void PoisonServer(string reason, string reasonCode = PoisonReasonCode.Other)
    {
        _poisoned = true;
        _aborted = true;
        _abortReason = reason;
        TestLog.Server($"{_displayLabel} POISONED [{reasonCode}]: {reason}");
        SetupEventBus.EmitInstancePoisoned(
            InstanceId ?? $"server-{Key}-{Server.ServerIndex}",
            reason
        );
        InfrastructureEventLog.Emit(
            "server_poisoned",
            new
            {
                server = Key,
                instanceId = InstanceId,
                reason,
                reasonCode,
                refCount = _refCount,
            }
        );
        EmitAnnotationToRunningTests(AnnotationLevel.Warning, $"Server poisoned: {reasonCode}");
        try
        {
            _errorCts.Cancel();
        }
        catch { }

        // Wake same-class exclusive methods queued on _exclusiveClassTurn. That gate is
        // released only by the prior method's normal cleanup (ReleaseExclusive), which a
        // poison-killed method never reaches — so its siblings would WaitAsync forever on
        // the per-test ct (a host/server poison fires no run-wide cancellation). Releasing
        // the semaphore lets each sibling proceed past the gate and fail fast on the now-
        // aborted server. See host-poison-deadlocks-run.md.
        DrainExclusiveGateOnPoison();

        try
        {
            _onPoisoned?.Invoke(this);
        }
        catch (Exception ex)
        {
            TestLog.Server($"{_displayLabel} poison callback failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Unblocks every same-class exclusive waiter when the server is poisoned: releases
    /// the turn semaphore once per queued waiter and resolves any pending exclusive-done
    /// TCS. Woken waiters re-check the server state (now <c>_aborted</c>) and fail fast
    /// via the normal poisoned-lease path instead of hanging on the gate. Idempotent and
    /// best-effort — a double poison or an already-drained gate is a no-op.
    /// </summary>
    private void DrainExclusiveGateOnPoison()
    {
        TaskCompletionSource? done;
        int waiters;
        lock (_exclusiveLock)
        {
            waiters = _exclusiveClassWaiters;
            done = _exclusiveDone;
            _exclusiveDone = null;
            _exclusiveOwnerClass = null;
        }

        // One permit per queued sibling; each decrements _exclusiveClassWaiters as it
        // wakes. SemaphoreSlim.Release(0) throws, so guard the count.
        if (waiters > 0)
        {
            try
            {
                _exclusiveClassTurn.Release(waiters);
            }
            catch (SemaphoreFullException) { }
            catch (ObjectDisposedException) { }
        }

        // A non-class exclusive holder waits on this TCS; resolve it so it stops waiting
        // on a server that will never drain. TrySetResult is a no-op if already resolved.
        done?.TrySetResult();
    }

    /// <summary>
    /// Awaits a prior exclusive holder's TCS, observing the caller's cancellation token.
    /// Centralizes the WhenAny+CT pattern used by every exclusive-acquisition path.
    /// Instrumented as <see cref="WaitName.ManagedServer_PriorExclusiveDrain"/>.
    /// </summary>
    private Task WaitForExclusiveAsync(TaskCompletionSource prior, CancellationToken ct) =>
        WaitTrace.RunAsync(
            WaitName.ManagedServer_PriorExclusiveDrain,
            async () =>
            {
                var ctTask = ct.IsCancellationRequested
                    ? Task.FromCanceled(ct)
                    : Task.Delay(Timeout.Infinite, ct);
                await Task.WhenAny(prior.Task, ctTask);
                ct.ThrowIfCancellationRequested();
            },
            ct,
            snapshot: () =>
                new
                {
                    server = Key,
                    ownerClass = _exclusiveOwnerClass,
                    refCount = _refCount,
                    classWaiters = _exclusiveClassWaiters,
                }
        );

    /// <summary>
    /// Requests a new game creation via the API, suspending health checks during the transition.
    /// </summary>
    public async Task CreateNewGameAsync(
        FarmTypeSetting farmType,
        string farmName = "Junimo",
        int startingCabins = 1,
        string cabinStrategy = "CabinStack",
        CancellationToken ct = default
    )
    {
        SuspendHealthChecks();
        try
        {
            using var api = Server.CreateApiClient();

            var result = await api.CreateNewGameAsync(
                farmType,
                farmName,
                startingCabins,
                cabinStrategy,
                ct: ct
            );
            if (result?.Success != true)
            {
                throw new InvalidOperationException(
                    $"New game creation failed: {result?.Error ?? "unknown"}"
                );
            }

            // Verify the server is actually back online
            var status = await api.WaitForServerOnline(
                timeout: TimeSpan.FromSeconds(120),
                pollInterval: TimeSpan.FromSeconds(2),
                cancellationToken: ct,
                requireInviteCode: Server.Options.WithSteam
            );

            if (status == null)
            {
                ct.ThrowIfCancellationRequested();
                throw new TimeoutException(
                    "Server did not come back online after new game creation"
                );
            }

            Server.ClearErrors();
            TestLog.Server($"{_displayLabel} new game created (farmType={farmType})");
        }
        finally
        {
            ResumeHealthChecks();
        }
    }

    /// <summary>
    /// Re-reads server-settings.json and reloads the active world via the API,
    /// suspending health checks during the title→reload transition. Used by tests
    /// that need OnSaveLoaded to re-run (e.g. cabin-position persistence).
    /// </summary>
    public async Task ReloadAsync(CancellationToken ct = default)
    {
        SuspendHealthChecks();
        try
        {
            using var api = Server.CreateApiClient();

            var result = await api.ReloadAsync(ct);
            if (result?.Success != true)
            {
                throw new InvalidOperationException($"Reload failed: {result?.Error ?? "unknown"}");
            }

            // Verify the server is actually back online
            var status = await api.WaitForServerOnline(
                timeout: TimeSpan.FromSeconds(120),
                pollInterval: TimeSpan.FromSeconds(2),
                cancellationToken: ct,
                requireInviteCode: Server.Options.WithSteam
            );

            if (status == null)
            {
                ct.ThrowIfCancellationRequested();
                throw new TimeoutException("Server did not come back online after reload");
            }

            Server.ClearErrors();
            TestLog.Server($"{_displayLabel} world reloaded");
        }
        finally
        {
            ResumeHealthChecks();
        }
    }

    /// <summary>
    /// Disposal path for poisoned servers. Releases the environment slot
    /// immediately so a replacement can start, then waits for active leases
    /// to drain (so tests can finish their artifacts phase against the
    /// still-alive container), then disposes the container.
    /// </summary>
    public async Task DisposeAfterDrainAsync(TimeSpan drainTimeout)
    {
        // Release the server slot now so ReplaceServerInBackgroundAsync can create
        // its replacement without waiting for the leasing test to finish.
        // ReleaseSlotEarly is idempotent (guards via _slotReleased flag).
        try
        {
            ReleaseSlotEarly();
        }
        catch (Exception ex)
        {
            TestLog.Server($"{_displayLabel} early slot release failed: {ex.Message}");
        }

        if (Interlocked.CompareExchange(ref _refCount, 0, 0) > 0)
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _poisonDrainSignal = tcs;

            // Recheck after publishing the TCS to close the race where Release()
            // fired before we assigned _poisonDrainSignal.
            if (Interlocked.CompareExchange(ref _refCount, 0, 0) > 0)
            {
                try
                {
                    using var cts = new CancellationTokenSource(drainTimeout);
                    await tcs.Task.WaitAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    TestLog.Server(
                        FormattableString.Invariant(
                            $"{_displayLabel} poison drain timed out after {drainTimeout.TotalSeconds:F0}s (refs={_refCount}); disposing anyway"
                        )
                    );
                }
            }
            _poisonDrainSignal = null;
        }

        await DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return; // Already disposed (safe against concurrent calls from poison + release)
        }

        // Order matters: cancel health first, then dispose server.
        // Use shutdown token so we don't block if the health loop is stuck.
        _healthCts?.Cancel();
        if (_healthTask != null)
        {
            try
            {
                await _healthTask.WaitAsync(ShutdownCoordinator.Token);
            }
            catch { }
        }

        // Unregister from stats tracking before disposal
        ContainerStatsCollector.Unregister(InstanceId ?? "");

        // Notify UI that this server instance is gone
        SetupEventBus.EmitInstanceDisposed(InstanceId ?? $"server-{Key}-{Server.ServerIndex}");

        // Recording emit lives inside ServerContainer.DisposeAsync (scoped to the
        // point where FullRecordingPath is written). Wrap the dispose calls so a
        // failure in one doesn't skip the other; order matters — inner container
        // first, then host server-slot release.
        try
        {
            await Server.DisposeAsync();
        }
        catch (Exception ex)
        {
            TestLog.Server($"{_displayLabel} Server dispose failed: {ex.Message}");
        }

        try
        {
            ReleaseSlotEarly();
        }
        catch (Exception ex)
        {
            TestLog.Server($"{_displayLabel} slot release failed: {ex.Message}");
        }
    }

    private static int ParseEnvInt(string name, int defaultValue)
    {
        var env = Environment.GetEnvironmentVariable(name);
        return int.TryParse(env, out var v) && v > 0 ? v : defaultValue;
    }
}
