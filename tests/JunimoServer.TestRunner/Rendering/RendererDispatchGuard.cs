using JunimoServer.Tests.Helpers;
using JunimoServer.Tests.Schema.Events;

namespace JunimoServer.TestRunner.Rendering;

/// <summary>
/// Wraps an <see cref="ITestRenderer"/> with strike-based fault isolation: if the
/// inner renderer throws on three consecutive dispatch attempts, subsequent
/// events are counted but not dispatched. The run continues to completion;
/// only live UI / on-disk renderer state goes blind.
///
/// <para>
/// Also the single point of state mutation: each <c>OnX(e)</c> handler invokes
/// <see cref="RunRecorder"/>'s <c>State.ApplyX(e)</c> once, then dispatches the
/// event to the inner renderer via <see cref="Guard"/>. The <c>broadcast</c>
/// callback (set once at construction, non-null for Web mode) receives the
/// returned JSON so the live WebSocket stream gets the data without polluting
/// <see cref="ITestRenderer"/>.
/// </para>
///
/// <para>
/// State mutation is OUTSIDE the <see cref="Guard"/> try/catch. An exception
/// from <c>ApplyX</c> is a run-state bug, not a renderer-display bug: it must
/// be observed separately (logged to <see cref="InfrastructureEventLog"/> as
/// <c>state_apply_failed</c>) without striking the renderer or aborting the
/// xUnit dispatch path.
/// </para>
/// </summary>
public sealed class RendererDispatchGuard : ITestRenderer
{
    private readonly ITestRenderer _inner;
    private readonly RunRecorder _recorder;
    private readonly Action<string?>? _broadcast;
    private readonly int _strikeLimit;
    private int _consecutiveFailures;
    private long _failureCount;
    private bool _disabled;

    /// <summary>
    /// Default strike limit before the guard goes null-mode. Three consecutive
    /// dispatch failures is the canonical threshold from the distributed-mode
    /// inheritance — kept on this guard since it is the primary fault isolator
    /// for renderer bugs.
    /// </summary>
    private const int DefaultStrikeLimit = 3;

    public RendererDispatchGuard(
        ITestRenderer inner,
        RunRecorder recorder,
        Action<string?>? broadcast = null,
        int strikeLimit = DefaultStrikeLimit
    )
    {
        _inner = inner;
        _recorder = recorder;
        _broadcast = broadcast;
        _strikeLimit = strikeLimit;
    }

    public bool IsDegraded => _disabled;

    /// <summary>
    /// Total dispatch failures observed (cumulative; not reset by recovery streaks).
    /// Surfaced into summary.json's degradation block at end-of-run.
    /// </summary>
    public long FailureCount => Volatile.Read(ref _failureCount);

    /// <summary>The wrapped renderer. Exposed so callers can downcast for type-specific operations (e.g. <c>WebRenderer.OpenBrowser</c>).</summary>
    public ITestRenderer Inner => _inner;

    private void Guard(Action action)
    {
        if (_disabled)
        {
            return;
        }

        try
        {
            action();
            Interlocked.Exchange(ref _consecutiveFailures, 0);
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _failureCount);
            var streak = Interlocked.Increment(ref _consecutiveFailures);
            InfrastructureEventLog.Emit(
                "renderer_dispatch_failed",
                new
                {
                    consecutive = streak,
                    error_type = ex.GetType().Name,
                    message = ex.Message,
                }
            );
            if (streak >= _strikeLimit && !_disabled)
            {
                _disabled = true;
                InfrastructureEventLog.Emit(
                    "renderer_degraded",
                    new
                    {
                        reason = $"{_strikeLimit} consecutive dispatch failures",
                        total_failures = FailureCount,
                    }
                );
            }
        }
    }

    /// <summary>
    /// Runs a state-mutation closure outside <see cref="Guard"/>'s try/catch so
    /// a throw cannot strike-degrade the renderer. The exception is logged to
    /// <see cref="InfrastructureEventLog"/> as <c>state_apply_failed</c> with
    /// the method name and continues — the renderer dispatch still runs for
    /// subsequent events, and the single bad event's artifact data is lost.
    /// Returns the closure's result on success, <c>null</c> on throw.
    /// </summary>
    private string? ApplyState(string method, Func<string?> apply)
    {
        try
        {
            return apply();
        }
        catch (Exception ex)
        {
            InfrastructureEventLog.Emit(
                "state_apply_failed",
                new
                {
                    method,
                    error_type = ex.GetType().Name,
                    message = ex.Message,
                }
            );
            return null;
        }
    }

    /// <summary>
    /// Variant for void-returning <see cref="Web.TestRunState"/> Apply methods
    /// (e.g. <c>ApplyRunMetadata</c>). Same swallow-and-log semantics as
    /// <see cref="ApplyState(string, Func{string})"/> but no broadcast JSON is
    /// produced.
    /// </summary>
    private void ApplyState(string method, Action apply)
    {
        try
        {
            apply();
        }
        catch (Exception ex)
        {
            InfrastructureEventLog.Emit(
                "state_apply_failed",
                new
                {
                    method,
                    error_type = ex.GetType().Name,
                    message = ex.Message,
                }
            );
        }
    }

    public ValueTask InitializeAsync() => _inner.InitializeAsync();

    public ValueTask DisposeAsync() => _inner.DisposeAsync();

    public void OnDiscoveryComplete(DiscoveryCompleteEvent e)
    {
        var json = ApplyState(
            nameof(OnDiscoveryComplete),
            () => _recorder.State.ApplyDiscoveryComplete(e)
        );
        _broadcast?.Invoke(json);
        Guard(() => _inner.OnDiscoveryComplete(e));
    }

    public void OnRunStarted(RunStartedEvent e)
    {
        var json = ApplyState(nameof(OnRunStarted), () => _recorder.State.ApplyRunStarted(e));
        _broadcast?.Invoke(json);
        Guard(() => _inner.OnRunStarted(e));
    }

    public void OnRunFinished(RunFinishedEvent e)
    {
        var json = ApplyState(nameof(OnRunFinished), () => _recorder.State.ApplyRunFinished(e));
        _broadcast?.Invoke(json);
        Guard(() => _inner.OnRunFinished(e));
        // Mark AFTER dispatch so a renderer that reads IsRunFinished during
        // OnRunFinished still sees false; only post-dispatch consumers (the
        // artifact writer in the outer finally) observe the flip.
        _recorder.MarkRunFinished();
    }

    public void OnTestStarted(TestStartedEvent e)
    {
        var json = ApplyState(nameof(OnTestStarted), () => _recorder.State.ApplyTestStarted(e));
        _broadcast?.Invoke(json);
        Guard(() => _inner.OnTestStarted(e));
    }

    public void OnTestRunning(TestRunningEvent e)
    {
        var json = ApplyState(nameof(OnTestRunning), () => _recorder.State.ApplyTestRunning(e));
        _broadcast?.Invoke(json);
        Guard(() => _inner.OnTestRunning(e));
    }

    public void OnTestOutput(TestOutputEvent e)
    {
        var json = ApplyState(nameof(OnTestOutput), () => _recorder.State.ApplyTestOutput(e));
        _broadcast?.Invoke(json);
        Guard(() => _inner.OnTestOutput(e));
    }

    public void OnTestAnnotation(TestAnnotationEvent e)
    {
        var json = ApplyState(
            nameof(OnTestAnnotation),
            () => _recorder.State.ApplyTestAnnotation(e)
        );
        _broadcast?.Invoke(json);
        Guard(() => _inner.OnTestAnnotation(e));
    }

    public void OnTestEnrichment(TestEnrichmentEvent e)
    {
        // ApplyTestEnrichment returns a tuple; route the JSON to broadcast but
        // surface the reclassified flag to the inner renderer via its own
        // OnTestEnrichment dispatch (RendererBase handles counter sync).
        string? json = null;
        try
        {
            (json, _) = _recorder.State.ApplyTestEnrichment(e);
        }
        catch (Exception ex)
        {
            InfrastructureEventLog.Emit(
                "state_apply_failed",
                new
                {
                    method = nameof(OnTestEnrichment),
                    error_type = ex.GetType().Name,
                    message = ex.Message,
                }
            );
        }
        _broadcast?.Invoke(json);
        Guard(() => _inner.OnTestEnrichment(e));
    }

    public void OnRunMetadata(RunMetadataEvent e)
    {
        // ApplyRunMetadata is void+idempotent; SerializeRunMetadataEvent
        // produces the broadcast JSON and has the AddEventLog side-effect
        // that contributes to the WS-snapshot history.
        ApplyState(nameof(OnRunMetadata) + ":apply", () => _recorder.State.ApplyRunMetadata(e));
        string? json = null;
        try
        {
            json = _recorder.State.SerializeRunMetadataEvent(e);
        }
        catch (Exception ex)
        {
            InfrastructureEventLog.Emit(
                "state_apply_failed",
                new
                {
                    method = nameof(OnRunMetadata) + ":serialize",
                    error_type = ex.GetType().Name,
                    message = ex.Message,
                }
            );
        }
        _broadcast?.Invoke(json);
        Guard(() => _inner.OnRunMetadata(e));
    }

    public void OnFlakyTests(FlakyTestsEvent e)
    {
        var json = ApplyState(nameof(OnFlakyTests), () => _recorder.State.ApplyFlakyTests(e));
        _broadcast?.Invoke(json);
        Guard(() => _inner.OnFlakyTests(e));
    }

    public void OnTestPassed(TestPassedEvent e)
    {
        var json = ApplyState(nameof(OnTestPassed), () => _recorder.State.ApplyTestPassed(e));
        _broadcast?.Invoke(json);
        Guard(() => _inner.OnTestPassed(e));
    }

    public void OnTestFailed(TestFailedEvent e)
    {
        var json = ApplyState(nameof(OnTestFailed), () => _recorder.State.ApplyTestFailed(e));
        _broadcast?.Invoke(json);
        Guard(() => _inner.OnTestFailed(e));
    }

    public void OnTestSkipped(TestSkippedEvent e)
    {
        var json = ApplyState(nameof(OnTestSkipped), () => _recorder.State.ApplyTestSkipped(e));
        _broadcast?.Invoke(json);
        Guard(() => _inner.OnTestSkipped(e));
    }

    public void OnDiagnostic(DiagnosticEvent e)
    {
        var json = ApplyState(nameof(OnDiagnostic), () => _recorder.State.ApplyDiagnostic(e));
        _broadcast?.Invoke(json);
        Guard(() => _inner.OnDiagnostic(e));
    }

    public void OnError(ErrorEvent e)
    {
        var json = ApplyState(nameof(OnError), () => _recorder.State.ApplyError(e));
        _broadcast?.Invoke(json);
        Guard(() => _inner.OnError(e));
    }

    public void OnSetupPhaseStarted(SetupPhaseStartedEvent e)
    {
        var json = ApplyState(
            nameof(OnSetupPhaseStarted),
            () => _recorder.State.ApplySetupPhaseStarted(e)
        );
        _broadcast?.Invoke(json);
        Guard(() => _inner.OnSetupPhaseStarted(e));
    }

    public void OnSetupPhaseCompleted(SetupPhaseCompletedEvent e)
    {
        var json = ApplyState(
            nameof(OnSetupPhaseCompleted),
            () => _recorder.State.ApplySetupPhaseCompleted(e)
        );
        _broadcast?.Invoke(json);
        Guard(() => _inner.OnSetupPhaseCompleted(e));
    }

    public void OnSetupStep(SetupStepEvent e)
    {
        var json = ApplyState(nameof(OnSetupStep), () => _recorder.State.ApplySetupStep(e));
        _broadcast?.Invoke(json);
        Guard(() => _inner.OnSetupStep(e));
    }

    public void PopulateTests(
        IReadOnlyList<(
            string Collection,
            string ClassName,
            string MethodName,
            string DisplayName
        )> tests
    )
    {
        var json = ApplyState(
            nameof(PopulateTests),
            () => _recorder.State.ApplyPopulateTests(tests)
        );
        _broadcast?.Invoke(json);
        Guard(() => _inner.PopulateTests(tests));
    }

    public void OnScreenshotCaptured(ScreenshotCapturedEvent e)
    {
        var json = ApplyState(
            nameof(OnScreenshotCaptured),
            () => _recorder.State.ApplyScreenshotCaptured(e)
        );
        _broadcast?.Invoke(json);
        Guard(() => _inner.OnScreenshotCaptured(e));
    }

    public void OnRecordingCaptured(RecordingCapturedEvent e)
    {
        var json = ApplyState(
            nameof(OnRecordingCaptured),
            () => _recorder.State.ApplyRecordingCaptured(e)
        );
        _broadcast?.Invoke(json);
        Guard(() => _inner.OnRecordingCaptured(e));
    }

    public void OnRecordingSkipped(RecordingSkippedEvent e)
    {
        var json = ApplyState(
            nameof(OnRecordingSkipped),
            () => _recorder.State.ApplyRecordingSkipped(e)
        );
        _broadcast?.Invoke(json);
        Guard(() => _inner.OnRecordingSkipped(e));
    }

    public void OnVncUrl(VncUrlEvent e) => Guard(() => _inner.OnVncUrl(e));

    public void OnInstanceCreated(InstanceCreatedEvent e)
    {
        var json = ApplyState(
            nameof(OnInstanceCreated),
            () => _recorder.State.ApplyInstanceCreated(e)
        );
        _broadcast?.Invoke(json);
        Guard(() => _inner.OnInstanceCreated(e));
    }

    public void OnInstanceLeased(InstanceLeasedEvent e)
    {
        var json = ApplyState(
            nameof(OnInstanceLeased),
            () => _recorder.State.ApplyInstanceLeased(e)
        );
        _broadcast?.Invoke(json);
        Guard(() => _inner.OnInstanceLeased(e));
    }

    public void OnInstanceClientAttached(InstanceClientAttachedEvent e)
    {
        var json = ApplyState(
            nameof(OnInstanceClientAttached),
            () => _recorder.State.ApplyInstanceClientAttached(e)
        );
        _broadcast?.Invoke(json);
        Guard(() => _inner.OnInstanceClientAttached(e));
    }

    public void OnInstanceReturned(InstanceReturnedEvent e)
    {
        var json = ApplyState(
            nameof(OnInstanceReturned),
            () => _recorder.State.ApplyInstanceReturned(e)
        );
        _broadcast?.Invoke(json);
        Guard(() => _inner.OnInstanceReturned(e));
    }

    public void OnInstanceDisposed(InstanceDisposedEvent e)
    {
        var json = ApplyState(
            nameof(OnInstanceDisposed),
            () => _recorder.State.ApplyInstanceDisposed(e)
        );
        _broadcast?.Invoke(json);
        Guard(() => _inner.OnInstanceDisposed(e));
    }

    public void OnInstanceRecording(InstanceRecordingEvent e)
    {
        var json = ApplyState(
            nameof(OnInstanceRecording),
            () => _recorder.State.ApplyInstanceRecording(e)
        );
        _broadcast?.Invoke(json);
        Guard(() => _inner.OnInstanceRecording(e));
    }

    public void OnInstancePoisoned(InstancePoisonedEvent e)
    {
        var json = ApplyState(
            nameof(OnInstancePoisoned),
            () => _recorder.State.ApplyInstancePoisoned(e)
        );
        _broadcast?.Invoke(json);
        Guard(() => _inner.OnInstancePoisoned(e));
    }

    public void OnInstanceConnected(InstanceConnectedEvent e)
    {
        var json = ApplyState(
            nameof(OnInstanceConnected),
            () => _recorder.State.ApplyInstanceConnected(e)
        );
        _broadcast?.Invoke(json);
        Guard(() => _inner.OnInstanceConnected(e));
    }

    public void OnInstanceDisconnected(InstanceDisconnectedEvent e)
    {
        var json = ApplyState(
            nameof(OnInstanceDisconnected),
            () => _recorder.State.ApplyInstanceDisconnected(e)
        );
        _broadcast?.Invoke(json);
        Guard(() => _inner.OnInstanceDisconnected(e));
    }

    public void OnInstanceStats(InstanceStatsEvent e)
    {
        var json = ApplyState(nameof(OnInstanceStats), () => _recorder.State.ApplyInstanceStats(e));
        _broadcast?.Invoke(json);
        Guard(() => _inner.OnInstanceStats(e));
    }

    public int FailedCount => _inner.FailedCount;
    public int PassedCount => _inner.PassedCount;
    public int SkippedCount => _inner.SkippedCount;
}
