using JunimoServer.Tests.Clients;
using JunimoServer.Tests.Containers;
using JunimoServer.Tests.Fixtures;
using JunimoServer.Tests.Helpers;
using JunimoServer.Tests.Schema.Events;
using Xunit;

namespace JunimoServer.Tests.Infrastructure.Fixture;

/// <summary>
/// Owns init / dispose orchestration for a single test. Constructs the
/// per-concern Fixture/ helpers eagerly at <see cref="InitializeAsync"/> and
/// drives the explicit dispose ordering chain at <see cref="FinalizeAsync"/>.
///
/// Holds a <see cref="TestBase"/> reference for resource-acquisition surfaces
/// that straddle both KeepConnected and non-KeepConnected paths
/// (<c>AcquireServerAsync</c>, <c>GetClientAsync</c>,
/// <c>MarkActiveAndArmBudget</c>) plus per-test state (CTS pair, lease,
/// outcome flags). These are stable, one-direction surfaces — lifecycle calls
/// TestBase, TestBase does not call back.
/// </summary>
internal sealed class TestLifecycle
{
    private readonly TestBase _testBase;
    private readonly string _testClassName;

    public TestLifecycle(TestBase testBase)
    {
        _testBase = testBase;
        _testClassName = testBase.GetType().Name;
    }

    public async ValueTask InitializeAsync()
    {
        _testBase.SetTestStartTimeInternal(DateTime.UtcNow);
        var displayName = TestContext.Current?.Test?.TestDisplayName;
        _testBase.SetTestDisplayNameInternal(displayName);

        // Construct per-concern helpers eagerly. The lazy-property pattern is
        // dropped here so subclass call-sites that read e.g. `Farmers.*` from
        // a constructor or InitializeAsync override see a non-null helper.
        _testBase.ConstructHelpersInternal();

        // Execution-only budget for the test body. The CTS is constructed here
        // so TestCt has a stable handle that already merges xUnit's upstream
        // CT (StopOnFail cascade), but the timer is NOT started yet — it arms
        // at queue exit via MarkActiveAndArmBudget(). Queue time (turn lock,
        // client capacity, server slot, exclusive gate drain) is harness
        // overhead and does not count against the budget. Cleanup paths use
        // independent CTSes (RunCleanupAsync etc.) so a body timeout does not
        // interrupt recording extraction or container teardown.
        _testBase.InitializeBudgetCtsInternal();

        // Reset the per-test failure-context stash so a previous test's dump
        // can't bleed into this one. AsyncLocal value flows through the
        // test's await chain from here.
        FailureContext.ClearForTest();

        // Test identity is read by every structured emit from
        // TestContext.Current (xUnit's own ambient).
        InfrastructureEventLog.Emit("test_started");

        var methodName = TestBase.ExtractMethodNameInternal(displayName);
        var attr = TestServerAttribute.Resolve(_testBase.GetType(), methodName);
        _testBase.SetCollectArtifactsInternal(attr.Artifacts);

        // Deferred mode: skip acquisition. Test method will call AcquireServerAsync().
        // DeferAcquisition classes are excluded from ServerConfigDiscovery, so no demand tracking needed.
        if (attr.DeferAcquisition) return;

        // Pre-compute server key AND collection name before any await.
        // This ensures: (a) ExpectedServerKey is available for demand
        // decrement on cancellation, (b) the test is registered in the
        // summary fixture even if canceled during acquisition.
        if (attr.Isolation != IsolationMode.PerTest)
        {
            var earlyReqs = ResourceRequirements.FromAttribute(attr, _testBase.GetType().Name, methodName);
            _testBase.PersistentSession.RecordExpectedServerKey(earlyReqs.GetServerKey());
            _testBase.SetCollectionNameInternal(earlyReqs.GetDisplayLabel());
        }
        _testBase.FailureReporter.RecordDispatched(_testBase.CollectionNameInternal ?? _testClassName, _testClassName);

        // When KeepConnected is enabled, serialize test execution via a per-class turn lock.
        // xUnit v3 starts all test instances within a class concurrently, but KeepConnected
        // tests share a single client connection, so only one test can run at a time.
        if (attr.KeepConnected)
        {
            await _testBase.PersistentSession.InitializeKeepConnectedAsync(attr, _testBase.TestCtInternal);
            return;
        }

        var requirements2 = ResourceRequirements.FromAttribute(attr, _testBase.GetType().Name, methodName);
        var priority2 = TestCollectionOrderer.GetPriorityForClass(_testBase.GetType().FullName);
        await _testBase.AcquireServerAsyncInternal(requirements2, _testBase.TestCtInternal, priority2);
    }

    public async ValueTask FinalizeAsync()
    {
        // Capture once to avoid drift between phases.
        var disposeStart = DateTime.UtcNow;
        var testStartTime = _testBase.TestStartTimeInternal;
        var activeStartTime = _testBase.ActiveStartTimeInternal;

        var queueDuration = activeStartTime != null
            ? TimeSpan.FromTicks(Math.Max(0, (activeStartTime.Value - testStartTime).Ticks))
            : TimeSpan.Zero;

        // Test body = time from acquisition to DisposeAsync entry.
        var testBodyDuration = activeStartTime != null
            ? disposeStart - activeStartTime.Value
            : TimeSpan.Zero;

        var artifactsDuration = TimeSpan.Zero;
        var cleanupDuration = TimeSpan.Zero;
        ArtifactTimings? artifactTimings = null;
        CleanupTimings? cleanupTimings = null;
        TestPhaseBreakdown? breakdown = null;

        if (activeStartTime == null)
            LogWarning("DisposeAsync called without active start time; test may not have acquired a server");

        // If stopOnFail triggered, xUnit cancels the CT for dispatched tests.
        // Skipped (never-dispatched) tests never run DisposeAsync, so their
        // _remainingDemand leaks. Clear it now so deferred servers can start.
        // The per-test timeout can also fire TestCt — same handling either way.
        if (_testBase.TestCtInternal.IsCancellationRequested)
        {
            _testBase.EmitCancellationDiagnosticInternal("dispose");
            TestResourceBroker.Instance.NotifyStopOnFail();
        }

        try
        {
            // Auto-check for uncaught server/client exceptions at end of every test.
            // Only fires when the test passed; avoids masking real failures.
            if (_testBase.LeaseInternal != null
                && !_testBase.TestFailedInternal
                && TestContext.Current?.TestState?.Result != TestResult.Failed
                && !_testBase.TestCtInternal.IsCancellationRequested)
            {
                await _testBase.AssertNoExceptionsAsyncInternal("at end of test (auto)");
            }

            // For KeepConnected tests: check if this is the last test, BEFORE cleanup.
            // We need this flag to decide whether to dispose the session or just notify.
            var isLastKeepConnectedTest = _testBase.PersistentSession.IsLastKeepConnectedTestAndIncrement();

            // ── Artifacts phase: screenshots + video clip extraction ──
            // Runs while containers are still alive and client is connected,
            // so screenshots capture final game state and video clips end cleanly.
            using var _artifactsPhase = TestIdentityContext.PushPhase("artifacts");
            LogDetail("Collecting artifacts...");

            var artifactsSw = System.Diagnostics.Stopwatch.StartNew();
            long screenshotMs = 0;
            long recordingMs = 0;
            try
            {
                var isTestFailing = _testBase.TestFailedInternal
                    || TestContext.Current?.TestState?.Result == TestResult.Failed;

                if (_testBase.CollectArtifactsInternal || isTestFailing)
                {
                    screenshotMs = await _testBase.Artifacts.CollectAsync(_testBase.TestFailedInternal);

                    var recordingSw = System.Diagnostics.Stopwatch.StartNew();

                    // Wait for ffmpeg's next x11grab sample to land in a segment
                    // before extracting the clip. The backbuffer screenshot and
                    // ffmpeg's capture schedule are not phase-locked, so a
                    // screenshot can reference a game draw that ffmpeg hasn't
                    // sampled yet. Sized as three frame intervals: one for the
                    // sample to happen, one for encode, one for segment rotation.
                    // Removing this delay causes final-frame loss at 1 FPS.
                    if ((RecordingPolicy.RecordServerEnabled || RecordingPolicy.RecordClientEnabled)
                        && (RecordingPolicy.Mode == TestRecordingMode.All || isTestFailing))
                    {
                        // Size the delay by the slowest recording type. A type that
                        // isn't recording (fps=0) is excluded so it doesn't dominate.
                        var serverFps = RecordingPolicy.RecordServerEnabled ? RecordingPolicy.ServerFps : int.MaxValue;
                        var clientFps = RecordingPolicy.RecordClientEnabled ? RecordingPolicy.ClientFps : int.MaxValue;
                        var slowestFps = Math.Min(serverFps, clientFps);
                        await Task.Delay(3 * (1000 / slowestFps));
                    }

                    await _testBase.Artifacts.FinalizeRecordingAsync(_testBase.TestFailedInternal);
                    recordingMs = recordingSw.ElapsedMilliseconds;
                }
                else if (RecordingPolicy.IsEnabled)
                {
                    // Class-level [TestServer(Artifacts = false)] passing test:
                    // no screenshot, no clip extraction. The orchestrator never
                    // runs, so the runner-side test never sees a recording event
                    // — the UI would otherwise stay on "Recordings appear after
                    // test cleanup" forever. Emit explicit per-source skips so
                    // the placeholder explains the opt-out.
                    var displayName = _testBase.TestDisplayNameInternal ?? _testClassName;
                    SetupEventBus.EmitRecordingSkipped(_testClassName, _testClassName, displayName,
                        "server", RecordingSkipReason.ArtifactsOptedOut);
                    if (_testBase.PrimaryClientLeaseInternal != null)
                    {
                        SetupEventBus.EmitRecordingSkipped(_testClassName, _testClassName, displayName,
                            "client", RecordingSkipReason.ArtifactsOptedOut);
                    }
                }
            }
            finally
            {
                artifactsDuration = artifactsSw.Elapsed;
                artifactTimings = new ArtifactTimings(screenshotMs, recordingMs);
            }

            // ── Cleanup phase ──
            // Persistent sessions: lightweight (session handoff, broker notification).
            // Normal tests: full disconnect + farmer delete + lease disposal.
            using var _cleanupPhase = TestIdentityContext.PushPhase("cleanup");
            LogDetail("Cleaning up...");

            var cleanupSw = System.Diagnostics.Stopwatch.StartNew();
            long lastKeepDisposeMs = 0;
            long leaseReleaseMs = 0;
            try
            {
                if (_testBase.PersistentSession.IsUsingPersistentSession && _testBase.PersistentSession.Session != null)
                {
                    lastKeepDisposeMs = await _testBase.PersistentSession.FinalizeSessionAsync(isLastKeepConnectedTest, _testBase.TestCtInternal);
                }
                else
                {
                    cleanupTimings = await RunCleanupAsync();

                    // Dispose primary client lease; return container to pool
                    var primaryClientLease = _testBase.PrimaryClientLeaseInternal;
                    if (primaryClientLease != null)
                    {
                        try { await primaryClientLease.DisposeAsync(); } catch { }
                    }

                    // Release per-host client capacity slot early. The client
                    // container is back in the pool, so another test can start
                    // acquiring its client while we finish server-side cleanup
                    // (farmhand deletion, lease release). Routes to the same
                    // host whose ClientCapacity was acquired in the broker.
                    _testBase.PersistentSession.ReleaseClientCapacityIfHeld();

                    // Release server lease. On the last test of an evicted
                    // config this triggers synchronous ManagedServer.DisposeAsync
                    // (container teardown), which dominates the cleanup phase
                    // for that test — surface it separately.
                    if (_testBase.LeaseInternal != null && _testBase.PersistentSession.OwnsLease)
                    {
                        var leaseSw = System.Diagnostics.Stopwatch.StartNew();
                        try { await _testBase.LeaseInternal.DisposeAsync(); } catch { }
                        leaseReleaseMs = leaseSw.ElapsedMilliseconds;
                    }

                    // Fallback: if this test was counted by ServerConfigDiscovery
                    // but never acquired resources (e.g., cancelled during
                    // InitializeAsync), decrement _remainingDemand so the server
                    // can eventually be evicted.
                    if (_testBase.LeaseInternal == null && _testBase.PersistentSession.ExpectedServerKey != null)
                    {
                        TestResourceBroker.Instance.NotifyTestCompleted(_testBase.PersistentSession.ExpectedServerKey);
                        LogTrace($"Fallback demand decrement for cancelled test (key={_testBase.PersistentSession.ExpectedServerKey})");
                    }

                    // If this is the last KeepConnected test AND a session
                    // still exists (e.g., this test broke the session but a
                    // previous test created one that's still registered),
                    // dispose it now.
                    if (isLastKeepConnectedTest)
                    {
                        lastKeepDisposeMs = await _testBase.PersistentSession.DisposeOrphanedSessionAsync();
                    }
                }

                // The per-test timeout fired (our CTS). If our timeout cancelled,
                // that's the cause of the test failure regardless of whether
                // xUnit's upstream CT later cancelled too (Ctrl-C race, StopOnFail
                // cascade). Don't gate on xunitCt being clean — Testcontainers/
                // Docker.DotNet can ignore the timeout CT for tens of seconds,
                // leaving a window where Ctrl-C arrives before the body unwinds
                // and both tokens read cancelled.
                if (_testBase.BudgetCtsCancelledInternal && !_testBase.TestFailedInternal)
                {
                    var budgetSec = _testBase.TestTimeoutSecondsInternal;
                    _testBase.RecordTestFailureInternal(
                        error: $"Test timed out ({budgetSec:F0}s)",
                        phase: "test_body",
                        exceptionType: "System.TimeoutException");
                }

                // Detect uncaught test failures via xUnit's TestContext.
                // TestState is populated after the test method completes but
                // before DisposeAsync. Skip canceled tests (OperationCanceledException
                // from StopOnFail); they're not real failures.
                if (!_testBase.TestFailedInternal)
                {
                    var testState = TestContext.Current?.TestState;
                    if (testState?.Result == TestResult.Failed)
                    {
                        var exType = testState.ExceptionTypes?.FirstOrDefault();
                        var isCancellation = exType != null && (
                            exType.Contains("OperationCanceledException") ||
                            exType.Contains("TaskCanceledException"));

                        if (isCancellation)
                        {
                            _testBase.RecordTestCancellationInternal();
                        }
                        else
                        {
                            var errorMessage = testState.ExceptionMessages?.FirstOrDefault()
                                ?? "Test failed (no message)";
                            _testBase.RecordTestFailureInternal(errorMessage, phase: "test_body", exceptionType: exType);
                            LogError(errorMessage);
                        }
                    }
                }

                var preliminaryCleanup = cleanupSw.Elapsed;
                var activeDuration = testBodyDuration + artifactsDuration + preliminaryCleanup;

                breakdown = new TestPhaseBreakdown(
                    TestBodyMs: (long)testBodyDuration.TotalMilliseconds,
                    ArtifactsMs: (long)artifactsDuration.TotalMilliseconds,
                    CleanupMs: (long)preliminaryCleanup.TotalMilliseconds,
                    LastKeepDisposeMs: lastKeepDisposeMs,
                    LeaseReleaseMs: leaseReleaseMs);

                // Report completion to assembly-level test summary
                var collectionName = _testBase.CollectionNameInternal ?? _testClassName;
                var displayName = _testBase.TestDisplayNameInternal;
                var lease = _testBase.LeaseInternal;
                TestSummaryFixture.Instance?.SetServerContext(
                    collectionName, _testClassName, displayName,
                    lease?.ServerKey, lease?.ServerInstanceId);
                TestSummaryFixture.Instance?.MarkCompleted(
                    collectionName, _testClassName, displayName,
                    activeDuration, queueDuration, breakdown);

                InfrastructureEventLog.Emit("test_completed", new
                {
                    passed = !_testBase.TestFailedInternal,
                    activeDurationMs = (long)activeDuration.TotalMilliseconds,
                    queueDurationMs = (long)queueDuration.TotalMilliseconds,
                    testBodyMs = breakdown.TestBodyMs,
                    artifactsMs = breakdown.ArtifactsMs,
                    cleanupMs = breakdown.CleanupMs,
                    lastKeepDisposeMs = breakdown.LastKeepDisposeMs,
                    leaseReleaseMs = breakdown.LeaseReleaseMs
                });

                var doneTree = BuildDoneTree(activeDuration, queueDuration, testBodyDuration,
                    artifactsDuration, artifactTimings, preliminaryCleanup, cleanupTimings,
                    lastKeepDisposeMs, leaseReleaseMs,
                    failed: _testBase.TestFailedInternal);
                if (_testBase.TestFailedInternal)
                    LogError(doneTree);
                else
                    LogSuccess(doneTree);
            }
            finally
            {
                cleanupDuration = cleanupSw.Elapsed;
            }

            // Emit per-test enrichment: failure category/phase/repro, server
            // context, and the full lifecycle phase breakdown. The runner
            // correlates by displayName and patches the existing TestSnapshot.
            // Uses the final cleanupDuration which includes all finalization
            // work above, so the timeline covers every log entry including
            // the Done line.
            if (_testBase.TestDisplayNameInternal != null)
            {
                var enrichLease = _testBase.LeaseInternal;
                _testBase.FailureReporter.EmitEnrichment(
                    _testBase.CollectionNameInternal ?? _testClassName,
                    _testClassName,
                    enrichLease?.ServerKey,
                    enrichLease?.ServerInstanceId,
                    breakdown,
                    testBodyDuration,
                    artifactsDuration,
                    cleanupDuration,
                    lastKeepDisposeMs,
                    leaseReleaseMs);
            }

            // Flush the infrastructure event log so failure_context, screenshot,
            // and recording events for this test are on disk before xUnit's
            // pipeline emits the test_failed / test_passed result. Bounded; a
            // hung writer must not block the broker's turn-lock release below.
            try { await InfrastructureEventLog.FlushAsync().WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (TimeoutException) { /* writer slow; skip rather than block */ }
            catch (Exception ex) { LogWarning($"InfrastructureEventLog.FlushAsync threw: {ex.Message}"); }
        }
        finally
        {
            // Deregister from the server's currently-running set now that the
            // lease has been released and any server_disposed annotation has
            // already fired against this test's display name. Done before the
            // turn-lock release so the next KeepConnected test in this class
            // sees a clean running-tests set when it registers.
            _testBase.DisposeRunningTestTokenInternal();

            // Release the exclusive gate before the turn lock. The next test
            // in this class (or another class) can then proceed without the
            // gate blocking it.
            _testBase.PersistentSession.ReleaseExclusiveGate();

            // Release the turn lock so the next KeepConnected test in this
            // class can proceed. Must be the very last thing in DisposeAsync,
            // after all cleanup and logging.
            _testBase.PersistentSession.ReleaseTurnLock();
        }
    }

    private async Task<CleanupTimings?> RunCleanupAsync()
    {
        using var cts = new CancellationTokenSource(TestTimings.CleanupTimeout);
        try
        {
            return await RunCleanupCoreAsync(cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            LogWarning($"Cleanup timed out after {TestTimings.CleanupTimeout.TotalSeconds}s, aborting cleanup");
            return null;
        }
    }

    private async Task<CleanupTimings> RunCleanupCoreAsync(CancellationToken ct)
    {
        var cleanupSw = System.Diagnostics.Stopwatch.StartNew();
        long disconnectMs = 0, farmerDeleteMs = 0;

        if (_testBase.PersistentSession.DidConnect && _testBase.PrimaryClientLeaseInternal != null)
        {
            var phaseSw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                await _testBase.GameClient.Navigate("title");
                await _testBase.GameClient.Wait.ForDisconnected(TestTimings.DisconnectedTimeout);
                SetupEventBus.EmitInstanceDisconnected(_testBase.PrimaryClientLeaseInternal.InstanceId);
                _testBase.PrimaryClientLeaseInternal.AlreadyDisconnected = true;

                // Wait for THIS test's farmer(s) to leave. Don't wait for all
                // players to be gone; other tests may have clients connected
                // concurrently. Cleanup is graceful — a timeout here logs a
                // warning and continues (not a test failure). The helper
                // emits a `failure_context` event on timeout, which the
                // runner-side aggregator surfaces in summary.json.
                if (_testBase.Farmers.CreatedFarmers.Count > 0)
                {
                    var myUids = _testBase.Farmers.CreatedFarmers.Select(f => f.Uid).ToHashSet();
                    var ok = await _testBase.ServerApi.WaitForPlayersRemovedByIdAsync(myUids, ct: ct);
                    if (!ok && !ct.IsCancellationRequested)
                        LogWarning($"Cleanup disconnect wait timed out; {myUids.Count} farmer(s) still online");
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                LogWarning($"Failed to return to title [{ex.GetType().Name}]: {ex}");
            }
            disconnectMs = phaseSw.ElapsedMilliseconds;
        }

        if (_testBase.Farmers.CreatedFarmers.Count > 0)
        {
            var phaseSw = System.Diagnostics.Stopwatch.StartNew();
            var failedCount = 0;
            var processed = 0;
            try
            {
                foreach (var farmer in _testBase.Farmers.CreatedFarmers)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!await DeleteFarmerAsync(farmer))
                        failedCount++;
                    processed++;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Cancellation mid-loop: farmers not yet processed are leaked.
                // Count them as failures so the lease is poisoned, then
                // re-throw so the outer cleanup-timeout handling at
                // RunCleanupAsync still records it.
                failedCount += _testBase.Farmers.CreatedFarmers.Count - processed;
                PoisonOnCleanupFailureIfNeeded(failedCount);
                throw;
            }
            farmerDeleteMs = phaseSw.ElapsedMilliseconds;
            PoisonOnCleanupFailureIfNeeded(failedCount);
        }

        var exceptions = _testBase.ExceptionsInternalOrNull;
        if (exceptions != null)
        {
            // Diagnostic check uses its own unlinked CTS: the whole purpose
            // is to run even when cleanup's ct or the client's ErrorToken are
            // already cancelled.
            using var diagCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                await exceptions.CheckGameClientErrorsAsync(diagCts.Token);
                var captured = exceptions.GetExceptions();
                InfrastructureEventLog.Emit("exception_check", new
                {
                    capturedCount = captured.Count,
                    exceptions = captured.Count > 0 ? captured : null,
                    error = (string?)null
                });
                if (captured.Count > 0)
                {
                    LogWarning($"{captured.Count} exception(s) occurred during test:");
                    foreach (var ex in captured)
                        LogDetail(ex.ToString());
                }
            }
            catch (OperationCanceledException) when (diagCts.IsCancellationRequested)
            {
                InfrastructureEventLog.Emit("exception_check", new
                {
                    capturedCount = -1,
                    error = "timeout_10s"
                });
                LogWarning("[ExceptionMonitor] Diagnostic check timed out after 10s");
            }
            catch (Exception ex)
            {
                InfrastructureEventLog.Emit("exception_check", new
                {
                    capturedCount = -1,
                    error = $"{ex.GetType().Name}: {ex.Message}"
                });
                LogWarning($"Failed to check for exceptions during cleanup: {ex.Message}");
            }
        }

        return new CleanupTimings(cleanupSw.Elapsed, disconnectMs, farmerDeleteMs);
    }

    private void PoisonOnCleanupFailureIfNeeded(int failedCount)
    {
        if (failedCount <= 0) return;
        if (_testBase.LeaseInternal is not { IsPoisoned: false } lease) return;
        if (ShutdownCoordinator.IsShuttingDown) return;

        lease.Managed.PoisonServer(
            $"DeleteFarmerAsync failed for {failedCount} farmer(s) in cleanup",
            ManagedServer.PoisonReasonCode.CleanupFarmerDeleteFailed);
    }

    private async Task<bool> DeleteFarmerAsync(TrackedFarmer farmer)
    {
        if (_testBase.LeaseInternal == null) return true;
        var deadline = DateTime.UtcNow + TestTimings.FarmerDeleteTimeout;
        var label = $"'{farmer.Name}' (uid={farmer.Uid})";

        while (true)
        {
            try
            {
                var result = await _testBase.ServerApi.DeleteFarmhandById(farmer.Uid);
                if (result?.Success == true)
                {
                    LogDetail($"Deleted: {label}");
                    return true;
                }
                if (result?.Error?.Contains("not found", StringComparison.OrdinalIgnoreCase) == true)
                {
                    LogDetail($"Not found (ok): {label}");
                    return true;
                }
                var isRetryable = result?.Error?.Contains("online", StringComparison.OrdinalIgnoreCase) == true
                    || result?.Error?.Contains("save", StringComparison.OrdinalIgnoreCase) == true;
                if (isRetryable && DateTime.UtcNow < deadline)
                {
                    await Task.Delay(TestTimings.FastPollInterval);
                    continue;
                }
                LogWarning($"Failed to delete {label}: {result?.Error ?? "unknown error"}");
                return false;
            }
            catch (Exception ex)
            {
                LogWarning($"Error deleting {label} [{ex.GetType().Name}]: {ex}");
                return false;
            }
        }
    }

    internal record ArtifactTimings(long ScreenshotMs, long RecordingMs);
    internal record CleanupTimings(TimeSpan Total, long DisconnectMs, long FarmerDeleteMs);

    private static string FormatSec(TimeSpan d) =>
        string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:F2}s", d.TotalSeconds);

    private static string FormatSec(long ms) => FormatSec(TimeSpan.FromMilliseconds(ms));

    private static string FormatQueued(TimeSpan d) =>
        d.TotalSeconds >= 60
            ? string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:F1}m", d.TotalMinutes)
            : FormatSec(d);

    private static string BuildDoneTree(
        TimeSpan active, TimeSpan queued, TimeSpan test,
        TimeSpan artifacts, ArtifactTimings? at,
        TimeSpan cleanup, CleanupTimings? ct,
        long lastKeepDisposeMs, long leaseReleaseMs,
        bool failed = false)
    {
        var items = new List<(int depth, string dur, string label)>();

        if (queued.TotalSeconds >= 1)
            items.Add((0, FormatQueued(queued), "queued"));

        items.Add((0, FormatSec(test), "test"));

        if (artifacts.TotalMilliseconds > 0)
        {
            items.Add((0, FormatSec(artifacts), "artifacts"));
            if (at != null)
            {
                if (at.ScreenshotMs > 0) items.Add((1, FormatSec(at.ScreenshotMs), "screenshot"));
                if (at.RecordingMs > 0) items.Add((1, FormatSec(at.RecordingMs), "recording"));
            }
        }

        if (cleanup.TotalMilliseconds > 0)
        {
            items.Add((0, FormatSec(cleanup), "cleanup"));
            if (ct != null)
            {
                if (ct.DisconnectMs > 0) items.Add((1, FormatSec(ct.DisconnectMs), "disconnect"));
                if (ct.FarmerDeleteMs > 0) items.Add((1, FormatSec(ct.FarmerDeleteMs), "farmerDelete"));
            }
            if (leaseReleaseMs > 0) items.Add((1, FormatSec(leaseReleaseMs), "leaseRelease"));
            if (lastKeepDisposeMs > 0) items.Add((1, FormatSec(lastKeepDisposeMs), "lastKeepDispose"));
        }

        var sb = new System.Text.StringBuilder();
        sb.Append($"{(failed ? "Failed" : "Done")} ({FormatSec(active)})");

        var isLast = new bool[items.Count];
        for (var i = items.Count - 1; i >= 0; i--)
        {
            var foundLaterSibling = false;
            for (var j = i + 1; j < items.Count; j++)
            {
                if (items[j].depth == items[i].depth) { foundLaterSibling = true; break; }
                if (items[j].depth < items[i].depth) break;
            }
            isLast[i] = !foundLaterSibling;
        }

        var maxDurLen = 0;
        foreach (var item in items)
            if (item.dur.Length > maxDurLen) maxDurLen = item.dur.Length;

        for (var i = 0; i < items.Count; i++)
        {
            var (depth, dur, label) = items[i];
            var connector = isLast[i] ? "└" : "├";
            var prefix = depth == 0
                ? $"  {dur.PadRight(maxDurLen)} {connector} "
                : $"  {dur.PadRight(maxDurLen)} │  {connector} ";
            sb.Append('\n');
            sb.Append(prefix);
            sb.Append(label);
        }

        return sb.ToString();
    }

    private string DisplayNameOrFallback =>
        _testBase.TestDisplayNameInternal ?? _testClassName;

    private void LogSuccess(string message) =>
        SetupEventBus.EmitTestAnnotation(DisplayNameOrFallback,
            AnnotationLevel.Success, AnnotationSource.Body, message);

    private void LogWarning(string message) =>
        SetupEventBus.EmitTestAnnotation(DisplayNameOrFallback,
            AnnotationLevel.Warning, AnnotationSource.Body, message);

    private void LogError(string message) =>
        SetupEventBus.EmitTestAnnotation(DisplayNameOrFallback,
            AnnotationLevel.Error, AnnotationSource.Body, message);

    private void LogDetail(string message) =>
        SetupEventBus.EmitTestAnnotation(DisplayNameOrFallback,
            AnnotationLevel.Detail, AnnotationSource.Body, message);

    private void LogTrace(string message) =>
        SetupEventBus.EmitTestAnnotation(DisplayNameOrFallback,
            AnnotationLevel.Trace, AnnotationSource.Body, message);
}
