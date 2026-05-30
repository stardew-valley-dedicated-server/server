namespace JunimoServer.TestRunner.Rendering.Web;

/// <summary>
/// Immutable run-level snapshot consumed by TestRunArtifactWriter to project
/// summary.json, ctrf-report.json, run-output.json, and run-metadata.json.
/// Materialized once under TestRunState's lock so the writer never reaches back
/// into mutable state.
///
/// <para>
/// The last five fields carry coordinator-only signals (workers fleet state).
/// Local-mode passes them as <c>null</c>; the writer treats null fields as
/// "no degradation block" and omits them from the JSON. A future distributed
/// coordinator would populate them via the appropriate
/// <see cref="TestRunState.GetArtifactView"/> call.
/// </para>
/// </summary>
public sealed record RunArtifactView(
    DateTime RunStartTime,
    DateTime RunEndTime,
    TimeSpan Duration,
    int ExpectedTestCount,
    int Passed,
    int Failed,
    int Skipped,
    int Canceled,
    bool Aborted,
    string? AbortReason,
    IReadOnlyList<TestArtifactView> Tests,
    System.Text.Json.JsonElement? FlakyTests,
    // Coordinator-only signals (null in local mode).
    IReadOnlyDictionary<string, long>? DroppedEventsByWorker = null,
    IReadOnlyList<string>? LostWorkers = null,
    long? RendererFailures = null,
    IReadOnlyList<string>? MissingArtifacts = null,
    IReadOnlyList<System.Text.Json.JsonElement>? WorkerRunMetadata = null);

/// <summary>Per-test view used by the artifact writer.</summary>
public sealed record TestArtifactView(
    string Collection,
    string ClassName,
    string DisplayName,
    string Status,
    long DurationMs,
    long QueueDurationMs,
    string? ErrorMessage,
    string? ErrorType,
    string? FailureCategory,
    string? ErrorPreview,
    string? Phase,
    string? ReproCommand,
    string? ServerKey,
    string? ServerInstanceId,
    string? ScreenshotPath,
    LifecycleView? Lifecycle,
    string? SkipReason);

/// <summary>
/// Lifecycle phase breakdown.
///
/// <para><see cref="CleanupMs"/> is the wall-clock superset of cleanup-phase work.
/// <see cref="LeaseReleaseMs"/> (synchronous container teardown when this test is
/// the last consumer of its server config) and <see cref="LastKeepDisposeMs"/>
/// (synchronous KeepConnected session dispose) are sequential subset components —
/// subtract from <see cref="CleanupMs"/> to estimate test-specific cleanup work.</para>
/// </summary>
public sealed record LifecycleView(
    long TestMs,
    long CleanupMs,
    long ArtifactsMs,
    long LastKeepDisposeMs,
    long LeaseReleaseMs);
