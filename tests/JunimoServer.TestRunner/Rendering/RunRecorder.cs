using JunimoServer.TestRunner.Rendering.Web;

namespace JunimoServer.TestRunner.Rendering;

/// <summary>
/// Process-scope aggregate that owns the run-level artifact lifecycle: the
/// <see cref="TestRunState"/> that all <c>Apply*</c> calls mutate, the
/// <see cref="TestRunArtifactWriter"/> that projects it into <c>summary.json</c>
/// / <c>ctrf-report.json</c> / <c>latest.txt</c> / <c>run-output.json</c>, and
/// the abort-reason / run-finished flags whose values branch the writer's
/// output.
///
/// <para>
/// Lifted out of the renderer base class because run-level artifacts must be
/// written by every exit path — clean run, preflight failure, image-build
/// failure, Ctrl+C, UI Stop — regardless of which renderer is active. Owning
/// the writer on the renderer made the write gated on the renderer's
/// <c>DisposeAsync</c> running cleanly; CI mode never wrote anything.
/// </para>
///
/// <para>
/// Idempotent via the writer's <c>_written</c> latch: the main <c>finally</c>
/// and the abort handlers can both call <see cref="WriteRunArtifacts"/>; only
/// the first one produces files.
/// </para>
/// </summary>
public sealed class RunRecorder
{
    private readonly TestRunArtifactWriter _writer = new();

    // Volatile because OnRunFinished writes from the dispatch thread and
    // BeginAbort's force-kill thread reads from a thread-pool thread. Without it
    // the abort handler can label a graceful run as aborted via a stale read.
    private volatile bool _isRunFinished;

    // Externally-supplied abort reason ("ctrl_c", "stop_on_fail", "preflight",
    // ...). Falls back to the writer-side default "child_process_terminated"
    // when null.
    private string? _externalAbortReason;

    public TestRunState State { get; } = new();

    public bool IsRunFinished => _isRunFinished;

    public void SeedRunIdentity(string runDir, string runId)
        => _writer.OnRunMetadata(runDir, runId);

    public void SetAbortReason(string reason) => _externalAbortReason = reason;

    public void MarkRunFinished() => _isRunFinished = true;

    /// <summary>
    /// Snapshot <see cref="TestRunState"/> and hand it to the artifact writer.
    /// Idempotent — the writer's <c>_written</c> latch under lock swallows the
    /// second call, so the main <c>finally</c> and abort paths can both call
    /// this without coordination. <c>aborted</c> is derived from whether
    /// <see cref="MarkRunFinished"/> has fired.
    /// </summary>
    public void WriteRunArtifacts()
    {
        var aborted = !IsRunFinished;
        var view = State.GetArtifactView(
            aborted: aborted,
            abortReason: aborted ? (_externalAbortReason ?? "child_process_terminated") : null);
        _writer.WriteIfNotWritten(view);
    }
}
