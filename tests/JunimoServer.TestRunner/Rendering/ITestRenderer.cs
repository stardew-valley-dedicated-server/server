using JunimoServer.Tests.Schema.Events;

namespace JunimoServer.TestRunner.Rendering;

/// <summary>
/// Interface for test output renderers.
/// Implementations handle different output modes (CI, Dev, LLM).
/// </summary>
public interface ITestRenderer : IAsyncDisposable
{
    /// <summary>Initialize the renderer before test execution begins.</summary>
    ValueTask InitializeAsync();

    void OnDiscoveryComplete(DiscoveryCompleteEvent e);
    void OnRunStarted(RunStartedEvent e);
    void OnRunFinished(RunFinishedEvent e);
    void OnTestStarted(TestStartedEvent e);

    /// <summary>Handle test transitioned from queued to actively running.</summary>
    void OnTestRunning(TestRunningEvent e) { }

    /// <summary>Handle a real-time test output line (streamed via named pipe, bypasses xUnit buffering).</summary>
    void OnTestOutput(TestOutputEvent e) { }

    /// <summary>
    /// Handle a per-test free-form annotation (Plane C). Carries level + source so
    /// renderers can format consistently. Single sink for <c>TestBase.Log*</c> calls
    /// and curated infrastructure narration (broker, recording, mod-forwarded).
    /// </summary>
    void OnTestAnnotation(TestAnnotationEvent e) { }

    /// <summary>
    /// Handle per-test enrichment carrying failure metadata, server context, and
    /// the full lifecycle phase breakdown. Correlates with the xUnit-native
    /// test_passed/failed event by displayName; may arrive slightly after it.
    /// </summary>
    void OnTestEnrichment(TestEnrichmentEvent e) { }

    /// <summary>
    /// Handle the once-per-run identity announcement.
    /// </summary>
    void OnRunMetadata(RunMetadataEvent e) { }

    /// <summary>Handle per-test flakiness over the last 20 runs.</summary>
    void OnFlakyTests(FlakyTestsEvent e) { }

    void OnTestPassed(TestPassedEvent e);
    void OnTestFailed(TestFailedEvent e);
    void OnTestSkipped(TestSkippedEvent e);

    void OnDiagnostic(DiagnosticEvent e);
    void OnError(ErrorEvent e);

    void OnSetupPhaseStarted(SetupPhaseStartedEvent e);
    void OnSetupPhaseCompleted(SetupPhaseCompletedEvent e);
    void OnSetupStep(SetupStepEvent e);

    /// <summary>
    /// Populate the test tree with discovered tests (before execution starts).
    /// Tests will be shown as pending until they run.
    /// </summary>
    void PopulateTests(IReadOnlyList<(string Collection, string ClassName, string MethodName, string DisplayName)> tests);

    /// <summary>Handle a screenshot captured for a test (may arrive after the test result event).</summary>
    void OnScreenshotCaptured(ScreenshotCapturedEvent e);

    /// <summary>Handle a video recording captured for a test (may arrive after the test result event).</summary>
    void OnRecordingCaptured(RecordingCapturedEvent e) { }

    /// <summary>
    /// Handle a per-test recording skip (no clip produced for a given source).
    /// Carries the same attribution surface as <see cref="OnRecordingCaptured"/>;
    /// fires both at orchestrator skip sites and from upstream gates that prevent
    /// the orchestrator from running (e.g. <c>[TestServer(Artifacts = false)]</c>).
    /// </summary>
    void OnRecordingSkipped(RecordingSkippedEvent e) { }

    /// <summary>Handle a VNC URL becoming available (displayed as clickable link in the web UI).</summary>
    void OnVncUrl(VncUrlEvent e) { }

    // ── Instance lifecycle events ──

    void OnInstanceCreated(InstanceCreatedEvent e) { }
    void OnInstanceLeased(InstanceLeasedEvent e) { }
    void OnInstanceClientAttached(InstanceClientAttachedEvent e) { }
    void OnInstanceReturned(InstanceReturnedEvent e) { }
    void OnInstanceDisposed(InstanceDisposedEvent e) { }
    void OnInstanceRecording(InstanceRecordingEvent e) { }
    void OnInstancePoisoned(InstancePoisonedEvent e) { }
    void OnInstanceConnected(InstanceConnectedEvent e) { }
    void OnInstanceDisconnected(InstanceDisconnectedEvent e) { }
    void OnInstanceStats(InstanceStatsEvent e) { }

    /// <summary>Number of failed tests.</summary>
    int FailedCount { get; }

    /// <summary>Number of passed tests.</summary>
    int PassedCount { get; }

    /// <summary>Number of skipped tests.</summary>
    int SkippedCount { get; }
}
