using JunimoServer.Tests.Clients;
using JunimoServer.Tests.Containers;
using JunimoServer.Tests.Fixtures;
using JunimoServer.Tests.Helpers;
using JunimoServer.Tests.Infrastructure.Fixture;
using JunimoServer.Tests.Schema.Events;
using Xunit;

namespace JunimoServer.Tests.Infrastructure;

/// <summary>
/// Screenshot capture mode for tests.
/// Controlled by SDVD_TEST_SCREENSHOTS environment variable.
/// </summary>
public enum TestScreenshotMode
{
    None,
    Done,
    All
}

/// <summary>
/// Base class for E2E integration tests. Uses the broker for server lifecycle.
/// </summary>
public abstract class TestBase : IAsyncLifetime, IDisposable
{
    protected internal ResourceLease? Lease { get; private set; }
    protected ServerContainer Server => Lease?.Server
        ?? throw new InvalidOperationException("Server not acquired. Call AcquireServerAsync() first.");
    protected internal ServerApiClient ServerApi => Lease?.Api
        ?? throw new InvalidOperationException("Server not acquired. Call AcquireServerAsync() first.");
    protected ServerStatus? ServerStatus { get; private set; }

    /// <summary>
    /// Server invite code. Available after server acquisition.
    /// </summary>
    protected string InviteCode => ServerStatus?.InviteCode ?? Lease?.InviteCode ?? "";

    // Lazy primary client. NOT allocated until first use.
    private ClientLease? _primaryClientLease;

    /// <summary>
    /// Gets the primary game client. Throws if no client has been leased yet.
    /// </summary>
    protected internal GameTestClient GameClient => _primaryClientLease?.Client
        ?? throw new InvalidOperationException(
            "No client leased. Call GetClientAsync() before using GameClient.");

    // Connection infrastructure (created lazily with first client)
    private ConnectionHelper? _connection;
    private ExceptionMonitor? _exceptions;
    protected ConnectionHelper Connection => _connection
        ?? throw new InvalidOperationException("Call GetClientAsync() first.");
    protected ExceptionMonitor Exceptions => _exceptions
        ?? throw new InvalidOperationException("Call GetClientAsync() first.");

    // Per-concern Fixture/ helpers, eagerly constructed by TestLifecycle.InitializeAsync
    // before any test body runs. Null between construction and InitializeAsync —
    // unreachable in normal use because xUnit calls InitializeAsync before the test method.
    private PersistentSessionCoordinator? _persistentSessionCoordinator;
    internal PersistentSessionCoordinator PersistentSession => _persistentSessionCoordinator!;

    private TestArtifactCollector? _artifacts;
    internal TestArtifactCollector Artifacts => _artifacts!;

    private TestFailureReporter? _failureReporter;
    internal TestFailureReporter FailureReporter => _failureReporter!;

    private ChatTestHelper? _chat;
    internal ChatTestHelper Chat => _chat!;

    private FarmerTestHelper? _farmers;
    internal FarmerTestHelper Farmers => _farmers!;

    private DayChangeWaiter? _dayChange;
    internal DayChangeWaiter DayChange => _dayChange!;

    private ConnectionRetryHelper? _connect;
    internal ConnectionRetryHelper Connect => _connect!;

    // Screenshot mode
    protected internal static TestScreenshotMode ScreenshotMode { get; } = ParseScreenshotMode();

    private static TestScreenshotMode ParseScreenshotMode()
    {
        var value = Environment.GetEnvironmentVariable("SDVD_TEST_SCREENSHOTS");
        return value?.ToLowerInvariant() switch
        {
            "none" or "off" or "false" or "0" => TestScreenshotMode.None,
            "done" or "finish" => TestScreenshotMode.Done,
            "all" or "true" or "1" => TestScreenshotMode.All,
            _ => TestScreenshotMode.Done
        };
    }

    // Per-test wall-clock timeout. Linked to TestContext.Current.CancellationToken
    // on InitializeAsync so it cancels the same token the rest of TestBase already
    // reads — no shadow CTS to plumb separately. Cleanup paths (RunCleanupAsync,
    // recCts, diagCts) keep their independent budgets and must NOT use TestCt —
    // they need to run after a body timeout.
    private static readonly TimeSpan TestTimeout = TestTimings.PerTestTimeout;

    // Test tracking
    private readonly string _testClassName;
    private readonly TestLifecycle _lifecycle;
    private string? _testDisplayName;
    private DateTime _testStartTime;
    private DateTime? _activeStartTime;
    private bool _testFailed;
    private bool _collectArtifacts = true;
    private string? _collectionName;
    private int _screenshotSequence;
    private CancellationTokenSource? _testTimeoutCts;
    private CancellationTokenSource? _budgetCts;
    private bool _budgetArmed;

    // Token returned by Lease.Managed.RegisterRunningTest() at the queue→active
    // transition. Disposed at the very end of DisposeAsync (after lease release)
    // so server_disposed (per_test_release) annotations still resolve to this
    // test. Null when AcquireServerAsync was never reached (dispose-during-init).
    private IDisposable? _runningTestToken;

    /// <summary>
    /// Per-test cancellation token. Linked to xUnit's per-test CancellationToken
    /// plus the per-test timeout. The timeout arms at queue exit (when the test
    /// acquires resources and starts running its own code) — queue time does not
    /// count against it. Use this in test bodies and helpers instead of reading
    /// <c>TestContext.Current.CancellationToken</c> directly so a stuck test fails
    /// within the timeout.
    /// </summary>
    protected CancellationToken TestCt =>
        _testTimeoutCts?.Token ?? TestContext.Current?.CancellationToken ?? CancellationToken.None;

    protected TestBase()
    {
        _testClassName = GetType().Name;
        _lifecycle = new TestLifecycle(this);
    }

    public virtual ValueTask InitializeAsync() => _lifecycle.InitializeAsync();

    public virtual ValueTask DisposeAsync() => _lifecycle.FinalizeAsync();

    /// <summary>
    /// Marks the queue→active transition: stamps <see cref="_activeStartTime"/>,
    /// emits <c>test_running</c> for the UI, and arms the per-test timeout.
    /// Idempotent — only the first call has any effect, so mid-test
    /// re-acquisitions (e.g. via <see cref="EnsureConnectedAsync"/> when
    /// a persistent session was found dead) don't reset the timeout.
    /// Call once per test, at the point where every harness queueing wait
    /// (turn lock, client capacity, server slot, exclusive gate drain) has
    /// completed and the test is about to run its own logic.
    /// </summary>
    private void MarkActiveAndArmBudget()
    {
        if (_budgetArmed) return;
        _budgetArmed = true;

        _activeStartTime = DateTime.UtcNow;
        SetupEventBus.EmitTestRunning(_testClassName, _testClassName,
            ExtractMethodName(_testDisplayName) ?? "unknown",
            _testDisplayName ?? $"{_testClassName}.unknown");

        // Register this test as currently executing on the leased server so
        // broker-side annotations (server_poisoned, server_disposed,
        // mod_phase forwarding) attribute to the running test rather than the
        // test that originally took the lease (which differs under
        // KeepConnected). Lease may be null for dispose-during-init paths;
        // skip silently in that case.
        if (Lease != null && _testDisplayName != null)
            _runningTestToken = Lease.Managed.RegisterRunningTest(_testDisplayName);

        _budgetCts?.CancelAfter(TestTimeout);
    }

    /// <summary>
    /// Acquire a server with the given requirements. Called automatically by
    /// InitializeAsync (default mode), or manually by the test method (deferred mode).
    /// </summary>
    protected async Task AcquireServerAsync(ResourceRequirements requirements,
        CancellationToken ct = default, int priority = 50)
    {
        if (Lease != null)
            throw new InvalidOperationException("Server already acquired.");

        var testName = _testDisplayName ?? GetType().Name;

        var acquireSw = System.Diagnostics.Stopwatch.StartNew();
        // AcquireAsync handles both server readiness AND the global client capacity gate.
        // The gate is inside the broker (after the server is ready but before AddRef),
        // so deferred server creation/eviction proceeds unblocked while waiting tests
        // don't inflate the refcount.
        Lease = await TestResourceBroker.Instance.AcquireAsync(requirements, testName, ct, priority);
        PersistentSession.RecordCapacityAcquired(requirements.Clients);
        acquireSw.Stop();

        // Queue exit: server is owned and capacity acquired. Mark active phase,
        // emit test_running for the UI, and arm the per-test timeout.
        // The follow-on Polling_TestBase_ServerReady poll counts as test body.
        MarkActiveAndArmBudget();

        InfrastructureEventLog.Emit("server_acquired", new
        {
            serverKey = Lease.ServerKey,
            serverInstanceId = Lease.ServerInstanceId,
            durationMs = acquireSw.ElapsedMilliseconds
        });

        // Mark server container for video recording clip extraction
        if (RecordingPolicy.IsEnabled && _collectArtifacts)
        {
            await Artifacts.MarkContainerUsedAsync(Lease.Server.Container.Name, "server", ct);
        }

        if (Lease.IsPoisoned)
            throw new TestRunAbortedException("Server poisoned");

        // Register deferred-acquisition tests (not registered in InitializeAsync)
        if (_collectionName == null)
        {
            _collectionName = requirements.GetDisplayLabel();
            FailureReporter.RecordDispatched(_collectionName, _testClassName);
        }

        // Wait for server to be ready (not mid-transition). We check IsReady
        // (backed by isGameAvailable(), false during newDaySync) AND FarmName
        // non-empty (rejects degraded responses where game state wasn't readable).
        // PlayerCount is NOT checked; other tests may have clients connected.
        var readySw = System.Diagnostics.Stopwatch.StartNew();
        await PollingHelper.LongPollAsync(
            WaitName.Polling_TestBase_ServerReady,
            async (since, remaining) =>
            {
                var status = await Lease.Api.WaitForStatusAsync(since: since, isReady: true, timeout: remaining, ct: ct);
                if (status == null) return new PollingHelper.LongPollResult(false, since);
                if (!string.IsNullOrEmpty(status.FarmName))
                    return new PollingHelper.LongPollResult(true, status.Version);
                // IsReady matched but FarmName empty (degraded snapshot) — advance
                // cursor and wait for the next snapshot.
                return new PollingHelper.LongPollResult(false, status.Version);
            }, TestTimings.ServerReadyBetweenTests, cancellationToken: ct);
        LogTrace($"ServerReady wait: {SetupEventBus.FormatDuration(readySw.Elapsed)}");

        ServerStatus = await Lease.Api.GetStatus();
    }

    /// <summary>
    /// Lazily leases the primary client and sets up connection helpers.
    /// API-only tests can skip this entirely.
    /// </summary>
    protected async Task<GameTestClient> GetClientAsync(CancellationToken ct = default)
    {
        // Copy nullable fields into non-null locals so nullable flow analysis
        // survives the awaits below (field state resets across awaits).
        var serverLease = Lease
            ?? throw new InvalidOperationException("Server not acquired. Call AcquireServerAsync() first.");
        if (_primaryClientLease != null) return _primaryClientLease.Client;

        var lease = _primaryClientLease = await serverLease.LeaseClientAsync(ct);

        // LeaseClientAsync emits instance_leased with the lease's original testName.
        // Re-emit with the current test's name so UsedInstances is correct after BreakSessionAsync.
        if (_testDisplayName != null)
            SetupEventBus.EmitInstanceLeased(lease.InstanceId, _testDisplayName, serverLease.Managed?.InstanceId);

        // Mark client container for video recording clip extraction
        if (RecordingPolicy.IsEnabled)
        {
            await Artifacts.MarkContainerUsedAsync(lease.Container.Container.Name, "client", ct);
        }

        var connectionOptions = new ConnectionOptions
        {
            ServerPassword = serverLease.Password,
            // Galaxy P2P connections can drop and the server needs up to 20s to
            // recreate its lobby. Use more attempts to ride out transient drops.
            MaxAttempts = serverLease.RequiresSteamConnection ? 4 : 2,
        };

        _connection = new ConnectionHelper(
            lease.Client,
            connectionOptions,
            ServerApi);

        _connection.OnCheckpointScreenshot = async (label) =>
        {
            if (ScreenshotMode == TestScreenshotMode.All)
            {
                _screenshotSequence++;
                await Artifacts.CaptureScreenshotAsync($"{_screenshotSequence:D2}_{label}");
            }
        };

        _exceptions = new ExceptionMonitor(
            lease.Client,
            ExceptionMonitorOptions.Default,
            msg => Log(msg));
        WireExceptionMonitorContext(_exceptions);

        lease.Client.CancellationToken = serverLease.ErrorToken;
        return lease.Client;
    }

    // Plumbs the per-test server-errors source and failure-recording sink into
    // the monitor. Func/Action getters are used (not snapshot values) because
    // PersistentSession reuses one ExceptionMonitor across a class and Lease
    // can be reassigned via AdoptSessionResources / AdoptBrokenSessionLease;
    // the getters always read the *current* test's fields.
    private void WireExceptionMonitorContext(ExceptionMonitor monitor)
    {
        monitor.SetTestContext(
            serverErrorsGetter: () => Lease?.Server?.Errors ?? Array.Empty<string>(),
            recordFailure: (error, phase) => RecordTestFailure(error, phase));
    }

    /// <summary>
    /// Lease an additional client (for multi-client tests like concurrent auth).
    /// </summary>
    protected async Task<ClientLease> LeaseClientAsync(CancellationToken ct = default)
    {
        if (Lease == null)
            throw new InvalidOperationException("Server not acquired.");
        var lease = await Lease.LeaseClientAsync(ct);

        // Mark additional client container for video recording clip extraction
        if (RecordingPolicy.IsEnabled)
        {
            await Artifacts.MarkContainerUsedAsync(lease.Container.Container.Name, "client", ct);
        }

        return lease;
    }

    #region Persistent Session

    /// <summary>
    /// Ensures a persistent client session is active, reusing an existing one if possible.
    /// Creates a new session on first call, or when the previous session was broken/lost.
    /// </summary>
    protected Task EnsureConnectedAsync(
        string farmerPrefix = "Test",
        SessionJoinMode joinMode = SessionJoinMode.Authenticated,
        CancellationToken ct = default)
        => PersistentSession.EnsureConnectedAsync(farmerPrefix, joinMode, ct);

    #endregion

    public void Dispose()
    {
        // Dispose the per-test CTS only after every DisposeAsync path has run
        // (cleanup CTSes are independent so they don't depend on this).
        _testTimeoutCts?.Dispose();
        _testTimeoutCts = null;
        _budgetCts?.Dispose();
        _budgetCts = null;
    }

    #region Helper Access Surface

    // Helpers in `Fixture/` see only `internal`; this region mirrors per-test
    // state plus method callbacks (AcquireServerAsync, GetClientAsync,
    // MarkActiveAndArmBudget, ExtractMethodName, ThrowIfServerError,
    // RecordTestFailure, RecordTestCancellation, EmitCancellationDiagnostic,
    // DisconnectAsync) so a sibling-namespace helper can reach them without
    // widening the protected surface. The README's §31 "helpers do not call
    // back into TestBase" rule is aspirational; the surface above is the
    // documented practical exception. Adoption methods (Adopt*, Clear*)
    // mutate TestBase's resource fields when the persistent-session
    // coordinator hands them across tests.

    internal ResourceLease? LeaseInternal => Lease;
    internal ClientLease? PrimaryClientLeaseInternal => _primaryClientLease;
    internal ConnectionHelper ConnectionInternal => Connection;
    internal ConnectionHelper? ConnectionInternalOrNull => _connection;
    internal ExceptionMonitor ExceptionsInternal => Exceptions;
    internal ExceptionMonitor? ExceptionsInternalOrNull => _exceptions;
    internal string InviteCodeInternal => InviteCode;
    internal bool CollectArtifactsInternal => _collectArtifacts;
    internal CancellationToken TestCtInternal => TestCt;

    internal DateTime TestStartTimeInternal => _testStartTime;
    internal DateTime? ActiveStartTimeInternal => _activeStartTime;
    internal bool TestFailedInternal => _testFailed;
    internal string? CollectionNameInternal => _collectionName;
    internal string? TestDisplayNameInternal => _testDisplayName;
    internal bool BudgetCtsCancelledInternal => _budgetCts?.IsCancellationRequested == true;
    internal double TestTimeoutSecondsInternal => TestTimeout.TotalSeconds;

    internal void SetTestStartTimeInternal(DateTime t) => _testStartTime = t;
    internal void SetTestDisplayNameInternal(string? name) => _testDisplayName = name;
    internal void SetCollectArtifactsInternal(bool value) => _collectArtifacts = value;
    internal void SetCollectionNameInternal(string? name) => _collectionName = name;

    internal void DisposeRunningTestTokenInternal()
    {
        _runningTestToken?.Dispose();
        _runningTestToken = null;
    }

    /// <summary>
    /// Constructs all per-concern Fixture/ helpers eagerly. Called by
    /// <see cref="TestLifecycle.InitializeAsync"/> before the test body runs.
    /// </summary>
    internal void ConstructHelpersInternal()
    {
        var displayName = _testDisplayName ?? "";
        _persistentSessionCoordinator = new PersistentSessionCoordinator(this, displayName);
        _artifacts = new TestArtifactCollector(this, displayName);
        _failureReporter = new TestFailureReporter(this, displayName);
        _chat = new ChatTestHelper(this, displayName);
        _farmers = new FarmerTestHelper(this, displayName);
        _dayChange = new DayChangeWaiter(this, displayName);
        _connect = new ConnectionRetryHelper(this, displayName);
    }

    /// <summary>
    /// Constructs the per-test timeout CTS pair. The timer is NOT started here —
    /// it arms at queue exit via <see cref="MarkActiveAndArmBudget"/>.
    /// </summary>
    internal void InitializeBudgetCtsInternal()
    {
        var xunitCt = TestContext.Current?.CancellationToken ?? CancellationToken.None;
        _budgetCts = new CancellationTokenSource();
        _testTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(xunitCt, _budgetCts.Token);
    }

    internal Task<GameTestClient> GetClientAsyncInternal(CancellationToken ct = default)
        => GetClientAsync(ct);

    internal Task AcquireServerAsyncInternal(ResourceRequirements requirements,
        CancellationToken ct = default, int priority = 50)
        => AcquireServerAsync(requirements, ct, priority);

    internal void MarkActiveAndArmBudgetInternal() => MarkActiveAndArmBudget();

    internal static string? ExtractMethodNameInternal(string? fullName) => ExtractMethodName(fullName);

    internal static string? ParamsHashInternal(string? displayName) => ParamsHash(displayName);

    internal void ThrowIfServerErrorInternal(OperationCanceledException ex, string? context = null)
        => ThrowIfServerError(ex, context);

    internal void RecordTestFailureInternal(string error, string? phase = null,
        string? screenshotPath = null, string? exceptionType = null)
        => RecordTestFailure(error, phase, screenshotPath, exceptionType);

    internal void RecordTestCancellationInternal() => RecordTestCancellation();

    internal void EmitCancellationDiagnosticInternal(string context) =>
        EmitCancellationDiagnostic(context);

    internal Task AssertNoExceptionsAsyncInternal(string? context = null) =>
        _exceptions?.AssertNoExceptionsAsync(context) ?? Task.CompletedTask;

    internal Task DisconnectAsyncInternal() => DisconnectAsync();

    /// <summary>
    /// Adopts an existing persistent session's resources onto this test
    /// instance. Called by <see cref="PersistentSessionCoordinator.WireUpPersistentSession"/>.
    /// </summary>
    internal void AdoptSessionResources(
        ResourceLease lease, ClientLease clientLease,
        ConnectionHelper connection, ExceptionMonitor exceptions)
    {
        Lease = lease;
        _primaryClientLease = clientLease;
        _connection = connection;
        _exceptions = exceptions;
        _exceptions.SetLogOutput(msg => Log(msg));
        WireExceptionMonitorContext(_exceptions);
        ServerStatus = null; // Will be fetched lazily if needed
    }

    /// <summary>
    /// Clears the resource fields after a previously-adopted session was
    /// detected dead, so the coordinator can re-acquire from scratch.
    /// </summary>
    internal void ClearAdoptedResources()
    {
        Lease = null;
        _primaryClientLease = null;
        _connection = null;
        _exceptions = null;
    }

    /// <summary>
    /// Take over a session's server lease for the current test after
    /// <see cref="PersistentSessionCoordinator.BreakSessionAsync"/>.
    /// </summary>
    internal void AdoptBrokenSessionLease(ResourceLease lease)
    {
        Lease = lease;
    }

    /// <summary>
    /// Drop the per-test client connection state after BreakSessionAsync. The
    /// coordinator already disposed the lease.
    /// </summary>
    internal void ClearAdoptedClientState()
    {
        _primaryClientLease = null;
        _connection = null;
        _exceptions = null;
    }

    #endregion

    protected async Task DisconnectAsync()
    {
        var exitResult = await GameClient.Exit();
        Assert.True(exitResult?.Success, $"Exit failed: {exitResult?.Error}");
        await GameClient.Wait.ForTitle(TestTimings.TitleScreenTimeout);
        await GameClient.Wait.ForDisconnected(TestTimings.DisconnectedTimeout);
        Log("Disconnected from server");
    }

    #region Server Lifecycle

    /// <summary>
    /// Creates a new game on the current server with the specified farm type.
    /// Suspends health checks during the transition and waits for the server to come back online.
    /// Only valid when the test has an active server lease.
    /// </summary>
    protected async Task CreateNewGameOnServerAsync(int farmType, string farmName = "Junimo",
        int startingCabins = 1, string cabinStrategy = "CabinStack")
    {
        if (Lease == null)
            throw new InvalidOperationException("No server lease. Call AcquireServerAsync() first.");

        await Lease.CreateNewGameAsync(farmType, farmName, startingCabins, cabinStrategy, TestCt);

        // Refresh the cached server status
        ServerStatus = await ServerApi.GetStatus(TestCt);
    }

    /// <summary>
    /// Re-reads server-settings.json and reloads the current world in-process (no
    /// container restart), re-running the mod's OnSaveLoaded path. Suspends health
    /// checks during the transition and waits for the server to come back online.
    /// Only valid when the test has an active server lease.
    /// </summary>
    protected async Task ReloadServerAsync()
    {
        if (Lease == null)
            throw new InvalidOperationException("No server lease. Call AcquireServerAsync() first.");

        await Lease.ReloadAsync(TestCt);

        // Refresh the cached server status
        ServerStatus = await ServerApi.GetStatus(TestCt);
    }

    /// <summary>
    /// Sleeps the primary client's farmhand and waits for the day transition, which writes the
    /// save to disk. Mandatory before a reload — WriteSaveData and building tileX/tileY (and any
    /// farmhandData mutation) are in-memory until a game save, so a reload without a save proves
    /// nothing. Requires the primary client to be connected and in-world.
    /// </summary>
    protected async Task SleepToSaveAsync(CancellationToken ct)
    {
        var statusBefore = await ServerApi.GetStatus(ct);
        Assert.NotNull(statusBefore);

        var sleep = await GameClient.Actions.Sleep();
        Assert.True(sleep?.Success == true, $"Sleep failed: {sleep?.Error}");

        var dayChanged = await DayChange.WaitAsync(
            statusBefore.Day, statusBefore.Season, statusBefore.Year, ct);
        Assert.True(dayChanged, "Day did not advance; save was not written");
    }

    #endregion

    #region Exception Monitoring

    /// <summary>
    /// Snapshots which cancellation tokens are in a cancelled state at the moment
    /// a test is about to fail with an <see cref="OperationCanceledException"/>.
    /// Used to distinguish test-CT cancellation (xUnit) from server ErrorToken
    /// cancellation (server poison) when the proximate exception does not say.
    /// </summary>
    private void EmitCancellationDiagnostic(string context)
    {
        InfrastructureEventLog.Emit("cancellation_detected", new
        {
            test = _testDisplayName,
            context,
            // xUnit cancellation (stopOnFail / Ctrl-C) vs the per-test wall-clock
            // timeout. The linked TestCt is true if either fires; the budget-only
            // field is what tells us "this was a timeout" even when Ctrl-C arrived
            // during the unwind.
            budgetCtsCancelled = _budgetCts?.IsCancellationRequested == true,
            xunitCtCancelled = TestContext.Current?.CancellationToken.IsCancellationRequested == true,
            testCtCancelled = TestCt.IsCancellationRequested,
            errorTokenCancelled = Lease?.ErrorToken.IsCancellationRequested == true,
            leasePoisoned = Lease?.IsPoisoned == true
        });
    }

    protected void ThrowIfServerError(OperationCanceledException ex, string? context = null)
    {
        if (Lease?.Server == null) throw ex;
        var serverErrors = Lease.Server.Errors;
        if (serverErrors.Count > 0)
        {
            var errorList = string.Join("\n", serverErrors);
            var reason = context != null ? $"Server error during: {context}" : "Server error detected";
            var message = $"{reason}\n\n{errorList}";
            RecordTestFailure(message, context);
            throw new ExceptionMonitorException(message, Array.Empty<CapturedException>());
        }
        EmitCancellationDiagnostic(context ?? "server-error-check");
        RecordTestFailure(ex.Message, context);
        throw ex;
    }

    protected void ThrowIfServerError()
    {
        if (Lease?.Server == null) return;
        var serverErrors = Lease.Server.Errors;
        if (serverErrors.Count > 0)
        {
            var errorList = string.Join("\n", serverErrors);
            RecordTestFailure(errorList, "server_error");
            throw new ExceptionMonitorException($"Server errors detected:\n{errorList}", Array.Empty<CapturedException>());
        }
    }

    #endregion

    #region Cabin Helpers

    /// <summary>
    /// Polls /cabins until a cabin owned by the given player UID appears.
    /// Prefer this over the name-based overload: OwnerId (UMI) is set immediately
    /// at AssignFarmhand and avoids the customization-sync race that delays both
    /// OwnerName and IsAssigned for several seconds after a fresh customization.
    /// On timeout dumps <see cref="FailureContext"/>.
    /// </summary>
    protected async Task<CabinInfoResponse?> WaitForCabinAssignedAsync(
        long playerId, CancellationToken ct = default)
    {
        CabinInfoResponse? result = null;
        CabinsResponse? lastCabins = null;
        await PollingHelper.WaitUntilAsync(
            WaitName.Polling_TestBase_WaitForCabinAssignedById,
            async () =>
            {
                lastCabins = await ServerApi.GetCabins(ct);
                result = lastCabins?.Cabins.FirstOrDefault(c => c.OwnerId == playerId);
                return result != null;
            }, TestTimings.CabinAssignmentTimeout, cancellationToken: ct,
           onTimeoutAsync: async () => await FailureContext.DumpAsync(
               ServerApi,
               reason: "WaitForCabinAssignedAsync_timeout",
               extras: new Dictionary<string, object?>
               {
                   ["playerId"] = playerId,
                   ["lastCabinsSnapshot"] = lastCabins?.Cabins
               }));
        return result;
    }

    /// <summary>
    /// Polls /cabins until a cabin owned by the given farmer name appears.
    /// Use the UID overload when possible — name lookup races with the
    /// customization sync (OwnerName can be empty briefly after fresh joins).
    /// On timeout dumps <see cref="FailureContext"/>.
    /// </summary>
    protected async Task<CabinInfoResponse?> WaitForCabinAssignedAsync(
        string farmerName, CancellationToken ct = default)
    {
        CabinInfoResponse? result = null;
        CabinsResponse? lastCabins = null;
        await PollingHelper.WaitUntilAsync(
            WaitName.Polling_TestBase_WaitForCabinAssignedByName,
            async () =>
            {
                lastCabins = await ServerApi.GetCabins(ct);
                result = lastCabins?.Cabins.FirstOrDefault(c =>
                    c.OwnerName.Equals(farmerName, StringComparison.OrdinalIgnoreCase) && c.IsAssigned);
                return result != null;
            }, TestTimings.CabinAssignmentTimeout, cancellationToken: ct,
           onTimeoutAsync: async () => await FailureContext.DumpAsync(
               ServerApi,
               reason: "WaitForCabinAssignedAsync_timeout",
               extras: new Dictionary<string, object?>
               {
                   ["farmerName"] = farmerName,
                   ["lastCabinsSnapshot"] = lastCabins?.Cabins
               }));
        return result;
    }

    #endregion

    #region Test Failure Recording

    protected void RecordTestFailure(string error, string? phase = null, string? screenshotPath = null,
        string? exceptionType = null)
    {
        _testFailed = true;
        var collectionName = _collectionName ?? _testClassName;
        FailureReporter.RecordFailure(collectionName, _testClassName, error, phase, screenshotPath,
            Lease?.ServerKey, Lease?.ServerInstanceId, exceptionType);
    }

    protected void RecordTestCancellation()
    {
        var collectionName = _collectionName ?? _testClassName;
        FailureReporter.RecordCancellation(collectionName, _testClassName);
    }

    #endregion

    #region Logging

    protected void Log(string message)
        => SetupEventBus.EmitTestAnnotation(_testDisplayName ?? _testClassName,
            AnnotationLevel.Info, AnnotationSource.Body, message);

    protected void LogSuccess(string message)
        => SetupEventBus.EmitTestAnnotation(_testDisplayName ?? _testClassName,
            AnnotationLevel.Success, AnnotationSource.Body, message);

    protected void LogWarning(string message)
        => SetupEventBus.EmitTestAnnotation(_testDisplayName ?? _testClassName,
            AnnotationLevel.Warning, AnnotationSource.Body, message);

    protected void LogError(string message)
        => SetupEventBus.EmitTestAnnotation(_testDisplayName ?? _testClassName,
            AnnotationLevel.Error, AnnotationSource.Body, message);

    protected void LogDetail(string message)
        => SetupEventBus.EmitTestAnnotation(_testDisplayName ?? _testClassName,
            AnnotationLevel.Detail, AnnotationSource.Body, message);

    protected void LogTrace(string message)
        => SetupEventBus.EmitTestAnnotation(_testDisplayName ?? _testClassName,
            AnnotationLevel.Trace, AnnotationSource.Body, message);

    protected void LogSection(string title)
        => SetupEventBus.EmitTestAnnotation(_testDisplayName ?? _testClassName,
            AnnotationLevel.Section, AnnotationSource.Body, title);

    internal static string? ExtractMethodName(string? fullName)
    {
        if (string.IsNullOrEmpty(fullName)) return null;

        // Strip parameters first. Dots inside parameter values (e.g. "Game.SpawnMonstersAtNight")
        // would otherwise be matched by LastIndexOf('.') instead of the namespace separator.
        var paren = fullName.IndexOf('(');
        var qualifiedName = paren >= 0 ? fullName[..paren] : fullName;

        var lastDot = qualifiedName.LastIndexOf('.');
        return lastDot >= 0 ? qualifiedName[(lastDot + 1)..] : qualifiedName;
    }

    private static string? ParamsHash(string? displayName)
    {
        if (displayName == null) return null;
        var paren = displayName.IndexOf('(');
        if (paren < 0) return null;
        var bytes = System.Text.Encoding.UTF8.GetBytes(displayName[paren..]);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))[..8];
    }

    #endregion
}
