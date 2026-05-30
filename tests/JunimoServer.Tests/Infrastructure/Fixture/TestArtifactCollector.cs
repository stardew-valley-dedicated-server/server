using JunimoServer.Tests.Helpers;
using JunimoServer.Tests.Schema.Events;
using Xunit;

namespace JunimoServer.Tests.Infrastructure.Fixture;

/// <summary>
/// Owns per-test artifact collection: end-of-test screenshots (sole disk
/// writer, with the <c>screenshot</c> renderer event co-located with the
/// file write per <c>colocate-event-emit.md</c>) and recording-clip
/// finalization (<see cref="RecordingOrchestrator.FinalizeAsync"/> dispatch,
/// awaited synchronously on failure / deferred onto the broker's background
/// queue for passing tests in mode=all).
/// </summary>
internal sealed class TestArtifactCollector
{
    private readonly TestBase _testBase;
    private readonly string _displayName;
    private readonly string _testClassName;

    private RecordingOrchestrator? _recordingOrchestrator;

    public TestArtifactCollector(TestBase testBase, string displayName)
    {
        _testBase = testBase;
        _displayName = displayName;
        _testClassName = testBase.GetType().Name;
    }

    /// <summary>
    /// Idempotently constructs the recording orchestrator and marks a
    /// container for video clip extraction. Called from the resource-
    /// acquisition paths on TestBase (server acquire, client lease) and from
    /// <see cref="PersistentSessionCoordinator"/> when reusing a session.
    /// </summary>
    internal async Task MarkContainerUsedAsync(string containerName, string kind, CancellationToken ct)
    {
        _recordingOrchestrator ??= new RecordingOrchestrator();
        await _recordingOrchestrator.MarkContainerUsedAsync(containerName, kind, ct);
    }

    /// <summary>
    /// Captures end-of-test screenshots while the client is still connected
    /// and emits <c>test_error</c> with the screenshot path when the test
    /// failed. Called from TestBase's artifacts phase. Returns the wall time
    /// spent capturing.
    /// </summary>
    internal async Task<long> CollectAsync(bool testFailed)
    {
        long screenshotMs = 0;

        if (TestBase.ScreenshotMode != TestScreenshotMode.None)
        {
            var phaseSw = System.Diagnostics.Stopwatch.StartNew();
            var label = testFailed ? "failure" : "result";
            var screenshotPath = await CaptureScreenshotAsync(label);
            if (testFailed)
                InfrastructureEventLog.Emit("test_error", new
                {
                    phase = "artifacts",
                    error = "Test failed",
                    screenshotPath,
                });
            screenshotMs = phaseSw.ElapsedMilliseconds;
        }

        // Container logs stream continuously to containers/{slug}/container.log;
        // use the test window timestamps from `make test-events` to slice context on failure.

        return screenshotMs;
    }

    public async Task<string?> CaptureScreenshotAsync(string label)
    {
        var lease = _testBase.LeaseInternal;
        if (lease?.Server?.Container == null) return null;
        var method = TestBase.ExtractMethodNameInternal(_displayName)!;
        var hash = TestBase.ParamsHashInternal(_displayName);
        var testMethod = hash != null ? $"{method}_{hash}" : method;
        var displayName = _displayName.Length > 0 ? _displayName : $"{_testClassName}.{method}";
        string? serverPath = null;

        // Server screenshot via API (captures from game's backbuffer directly)
        // Retry up to 3 times; game thread may be blocked by day transition/save.
        // Per-attempt timeout caps each try at 8s to avoid runaway 503 retry chains.
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                using var attemptCts = new CancellationTokenSource(TestTimings.ScreenshotAttemptTimeout);
                var result = await _testBase.ServerApi.GetScreenshot(attemptCts.Token);
                if (result?.Success == true && result.Base64Png != null)
                {
                    var dir = TestArtifacts.GetTestScreenshotDir(_testClassName, testMethod);
                    serverPath = Path.Combine(dir, $"{label}.png");
                    var bytes = Convert.FromBase64String(result.Base64Png);
                    await File.WriteAllBytesAsync(serverPath, bytes);
                    SetupEventBus.EmitScreenshot(_testClassName, _testClassName, displayName, serverPath, "server");
                    break;
                }
                else if (attempt < 3)
                {
                    LogWarning($"Server screenshot attempt {attempt}/3 failed: {result?.Error ?? "null response"}, retrying in {TestTimings.ScreenshotRetryDelay.TotalSeconds}s");
                    await Task.Delay(TestTimings.ScreenshotRetryDelay);
                }
                else
                {
                    LogWarning($"Server screenshot failed after 3 attempts: {result?.Error ?? "null response"}");
                    InfrastructureEventLog.Emit("screenshot_failed", new
                    {
                        source = "server",
                        label,
                        reason = result?.Error ?? "null response",
                        attempts = 3
                    });
                }
            }
            catch (Exception ex) when (attempt < 3)
            {
                LogWarning($"Server screenshot attempt {attempt}/3 failed: {ex.Message}, retrying in {TestTimings.ScreenshotRetryDelay.TotalSeconds}s");
                await Task.Delay(TestTimings.ScreenshotRetryDelay);
            }
            catch (Exception ex)
            {
                LogWarning($"Server screenshot failed after 3 attempts: {ex.Message}");
                InfrastructureEventLog.Emit("screenshot_failed", new
                {
                    source = "server",
                    label,
                    reason = "exception",
                    exceptionType = ex.GetType().Name,
                    message = ex.Message,
                    attempts = 3
                });
            }
        }

        // Client screenshot
        if (_testBase.PrimaryClientLeaseInternal != null)
        {
            try
            {
                var result = await _testBase.GameClient.CaptureScreenshot();
                if (result?.Success == true && result.Base64Png != null)
                {
                    var dir = TestArtifacts.GetTestScreenshotDir(_testClassName, testMethod);
                    var clientPath = Path.Combine(dir, $"client_{label}.png");
                    var bytes = Convert.FromBase64String(result.Base64Png);
                    await File.WriteAllBytesAsync(clientPath, bytes);
                    SetupEventBus.EmitScreenshot(_testClassName, _testClassName, displayName, clientPath, "client");
                }
            }
            catch (Exception ex)
            {
                LogWarning($"Failed to capture client screenshot: {ex.Message}");
                InfrastructureEventLog.Emit("screenshot_failed", new
                {
                    source = "client",
                    label,
                    reason = "exception",
                    exceptionType = ex.GetType().Name,
                    message = ex.Message
                });
            }
        }

        if (serverPath != null)
            InfrastructureEventLog.Emit("screenshot_captured", new { label, path = serverPath });

        return serverPath;
    }

    /// <summary>
    /// Extracts per-test recording clips. Behavior splits on test outcome and
    /// configured policy:
    ///
    /// <list type="bullet">
    ///   <item>Failed test → await synchronously. The clip must be on disk
    ///   before the <c>test_failed</c> event so the runbook can find it.</item>
    ///   <item>Passing test, mode=all → defer onto the broker's background
    ///   task queue. ffmpeg work no longer sits on the test critical path;
    ///   the broker drains it during DisposeAsync. Segments are append-only
    ///   inside the container, so a deferred extract on a reused server is
    ///   safe — RecordingOrchestrator.FinalizeAsync only reads finalized
    ///   segments.</item>
    ///   <item>Passing test, mode=failure → noop inside FinalizeAsync (gated
    ///   there by retention_passed).</item>
    /// </list>
    /// </summary>
    internal async Task FinalizeRecordingAsync(bool testFailed)
    {
        if (_recordingOrchestrator == null) return;

        var recordingTestFailed = testFailed
            || TestContext.Current?.TestState?.Result == TestResult.Failed;

        // Capture every value the deferred lambda needs into locals at the
        // deferral boundary. The background pump runs without ExecutionContext
        // flow into this AsyncLocal-bearing path (per asynclocal-pitfalls.md);
        // moving any of these reads inside the lambda silently misattributes
        // the deferred clip.
        var method = TestBase.ExtractMethodNameInternal(_displayName) ?? "unknown";
        var hash = TestBase.ParamsHashInternal(_displayName);
        var testMethod = hash != null ? $"{method}_{hash}" : method;
        var displayName = _displayName.Length > 0 ? _displayName : $"{_testClassName}.{method}";

        var orchestrator = _recordingOrchestrator;
        var className = _testClassName;
        // Capture client-presence at the deferral boundary on the test thread.
        // PrimaryClientLeaseInternal is non-null only if the test went through
        // GetClientAsync / LeaseClientAsync (a server-only test never marks a
        // client). The lambda runs on the broker's background pump after the
        // lease may already be released, so reading _testBase inside the catch
        // would be racy and would also fire a spurious "client" UI placeholder
        // for tests that never had one.
        var hadClient = _testBase.PrimaryClientLeaseInternal != null;

        // Defer extraction off the critical path for passing tests in "all"
        // mode. Failing tests still await — the clip must land before the
        // failure event so the runbook can find it. In "failure" mode for a
        // passing test, FinalizeAsync short-circuits via retention_passed
        // so the await is essentially free; deferring would gain nothing.
        var canDefer = !recordingTestFailed
            && RecordingPolicy.IsEnabled
            && RecordingPolicy.Mode == TestRecordingMode.All;

        if (canDefer)
        {
            TestResourceBroker.Instance.EnqueueBackgroundTask(async () =>
            {
                // 300s safety net. Per-clip extraction has its own budget
                // (max(30, 5*durationSec) inside ContainerRecorder) and the orchestrator
                // runs clip extractions in parallel, so total wall time ≈ measurement +
                // max(per-clip-budget). The cap exists because Docker.DotNet's defaultTimeout
                // is Infinite and the broker's _backgroundDisposeTasks drain has no timeout
                // either — without it, a stuck exec would block broker shutdown indefinitely.
                using var recCts = new CancellationTokenSource(TimeSpan.FromSeconds(300));
                try
                {
                    await orchestrator.FinalizeAsync(
                        className, testMethod, displayName, testFailed: false, recCts.Token);
                }
                catch (Exception ex)
                {
                    InfrastructureEventLog.Emit("recording_finalize_deferred_failed",
                        new { test = displayName, error = ex.Message });
                    // Emit per-source skip so the UI shows a real placeholder
                    // instead of an eternal "Recordings appear after test
                    // cleanup". Source is the un-indexed "client" — this skip
                    // is class-level (the orchestrator never reached the
                    // per-clip indexing loop). Server is always marked
                    // (every test goes through AcquireServerAsync), client is
                    // only emitted if the test actually leased one.
                    try
                    {
                        SetupEventBus.EmitRecordingSkipped(className, className, displayName,
                            "server", RecordingSkipReason.FinalizeDeferredFailed);
                        if (hadClient)
                        {
                            SetupEventBus.EmitRecordingSkipped(className, className, displayName,
                                "client", RecordingSkipReason.FinalizeDeferredFailed);
                        }
                    }
                    catch { /* IPC sink already swallows; defensive */ }
                }
            });
            return;
        }

        // Same 300s safety net as the deferred path above. The failing test is waiting
        // on this finalize before test_failed is emitted, so we want it to complete
        // rather than abort half-way; per-clip budgets bound runaway extractions.
        using var syncCts = new CancellationTokenSource(TimeSpan.FromSeconds(300));
        try
        {
            await orchestrator.FinalizeAsync(
                className, testMethod, displayName, recordingTestFailed, syncCts.Token);
        }
        catch (Exception ex)
        {
            LogWarning($"Recording finalization failed: {ex.Message}");
        }
    }

    private void LogWarning(string message) =>
        SetupEventBus.EmitTestAnnotation(_displayName.Length > 0 ? _displayName : _testClassName,
            AnnotationLevel.Warning, AnnotationSource.Body, message);
}
