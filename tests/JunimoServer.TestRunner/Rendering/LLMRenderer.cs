using JunimoServer.TestRunner.Rendering.Web;
using JunimoServer.Tests.Helpers;
using JunimoServer.Tests.Schema.Events;
using JunimoServer.Tests.Schema.Json;

namespace JunimoServer.TestRunner.Rendering;

/// <summary>
/// Structured JSONL renderer for LLM/AI agent consumption. Outputs only failures
/// and summary - minimal noise. State mutation, abort-reason, and run-level
/// artifact writing live on <see cref="RunRecorder"/>; this renderer reads the
/// shared state to emit the preliminary <c>run_summary</c> JSONL line.
/// </summary>
public sealed class LLMRenderer : RendererBase
{
    private readonly TextWriter _out;
    private readonly TestRunState _state;
    private RunMetadataEvent? _runMetadata;

    public LLMRenderer(RunRecorder recorder, bool verbose = false)
        : this(recorder, Console.Out, verbose) { }

    public LLMRenderer(RunRecorder recorder, TextWriter output, bool verbose = false)
        : base(verbose)
    {
        _state = recorder.State;
        _out = output;
    }

    public override void OnRunMetadata(RunMetadataEvent e)
    {
        // Capture for the post-OnRunFinished JSONL emit. State mutation +
        // writer re-seed happen in the guard.
        _runMetadata = e;
    }

    public override void OnTestEnrichment(TestEnrichmentEvent e)
    {
        // Sync running counter so the run_finished JSONL line (which reads
        // FailedCount / CanceledCount directly) agrees with summary.json. The
        // guard's ApplyTestEnrichment already returned the reclassified flag
        // and folded the outcome into TestRunState; this is the renderer-side
        // counter sync gated by RendererBase's _classifiedAsCanceled set.
        if (e.Outcome == "failed")
        {
            ReclassifyCanceledAsFailed(e.DisplayName);
        }
    }

    public override void OnTestAnnotation(TestAnnotationEvent e)
    {
        WriteJson(
            new
            {
                Event = "test_annotation",
                e.Timestamp,
                Test = e.DisplayName,
                Level = e.Level.ToString().ToLowerInvariant(),
                Source = e.Source.ToString().ToLowerInvariant(),
                e.Message,
            }
        );
    }

    public override void OnDiscoveryComplete(DiscoveryCompleteEvent e)
    {
        // Not emitted in LLM mode - minimal noise
    }

    public override void OnRunStarted(RunStartedEvent e)
    {
        WriteJson(
            new
            {
                Event = "run_started",
                e.Timestamp,
                TestCount = e.TestCasesToRun,
            }
        );
    }

    public override void OnRunFinished(RunFinishedEvent e)
    {
        // xUnit v3's ExecutionCompleteInfo can undercount failures when StopOnFail
        // cancels the run: TaskCanceledException in IAsyncLifetime.InitializeAsync
        // may be reported via OnTestFailed callback but not counted in the summary.
        // Use the higher of xUnit's summary vs our own accumulated counts.
        // CanceledCount is tracked separately from FailedCount (see RendererBase).
        var passed = Math.Max(e.Passed, PassedCount);
        var failed = Math.Max(e.Failed, FailedCount);
        var skipped = Math.Max(e.Skipped, SkippedCount);
        var canceled = CanceledCount;

        var notExecuted = Math.Max(0, TotalDiscovered - (passed + failed + skipped + canceled));

        // Emit run_aborted before run_finished when stopOnFail cancelled remaining tests.
        if (notExecuted > 0 && (failed > 0 || canceled > 0))
        {
            WriteJson(
                new
                {
                    Event = "run_aborted",
                    e.Timestamp,
                    Reason = "stopOnFail",
                    FailedTests = failed,
                    CanceledTests = canceled,
                    TestsNotExecuted = notExecuted,
                }
            );
        }

        WriteJson(
            new
            {
                Event = "run_finished",
                e.Timestamp,
                Passed = passed,
                Failed = failed,
                Canceled = canceled,
                Skipped = skipped + notExecuted,
                NotExecuted = notExecuted > 0 ? notExecuted : (int?)null,
                DurationMs = (long)e.Duration.TotalMilliseconds,
            }
        );

        // Stdout events fire here so the LLM consumer sees outcomes immediately.
        // Disk artifacts are written by the outer finally in Program.cs after
        // the setup pipe drains.
        if (_runMetadata != null)
        {
            WriteJson(
                new
                {
                    Event = "run_metadata",
                    Timestamp = DateTime.UtcNow,
                    RunDir = _runMetadata.RunDir,
                    Data = _runMetadata.Data,
                }
            );
        }

        // Emit a preliminary run_summary at xUnit's OnRunFinished so LLM consumers
        // see results without waiting for pipe drain. Disk artifacts (which include
        // flakiness) are written later by the outer finally.
        var prelim = _state.GetArtifactView(aborted: false, abortReason: null);
        WriteJson(
            new
            {
                Event = "run_summary",
                Timestamp = DateTime.UtcNow,
                Data = prelim,
            }
        );
    }

    public override void OnTestStarted(TestStartedEvent e)
    {
        // Not emitted in LLM mode - only failures matter
    }

    protected override void OnTestPassedCore(TestPassedEvent e)
    {
        if (!Verbose)
        {
            return;
        }

        WriteJson(
            new
            {
                Event = "test_passed",
                e.Timestamp,
                Test = e.DisplayName,
                DurationMs = (long)e.Duration.TotalMilliseconds,
            }
        );
    }

    protected override void OnTestFailedCore(TestFailedEvent e)
    {
        WriteJson(
            new
            {
                Event = "test_failed",
                e.Timestamp,
                Test = e.DisplayName,
                DurationMs = (long)e.Duration.TotalMilliseconds,
                Error = e.Message,
                e.ExceptionType,
                StackTrace = e.StackTrace != null ? SanitizeStackTrace(e.StackTrace) : null,
                e.ScreenshotPath,
                e.ArtifactId,
            }
        );
    }

    protected override void OnTestSkippedCore(TestSkippedEvent e)
    {
        // Skipped tests might be relevant for LLMs to understand test coverage
        WriteJson(
            new
            {
                Event = "test_skipped",
                e.Timestamp,
                Test = e.DisplayName,
                e.Reason,
            }
        );
    }

    public override void OnDiagnostic(DiagnosticEvent e)
    {
        // In verbose mode, include warnings too; otherwise only errors
        var minLevel = Verbose ? LogLevel.Warning : LogLevel.Error;
        if (e.Level < minLevel)
        {
            return;
        }

        WriteJson(
            new
            {
                Event = "error",
                e.Timestamp,
                Source = e.Source.ToString().ToLowerInvariant(),
                e.Message,
            }
        );
    }

    public override void OnError(ErrorEvent e)
    {
        WriteJson(
            new
            {
                Event = "error",
                e.Timestamp,
                e.Message,
                StackTrace = e.StackTrace != null ? SanitizeStackTrace(e.StackTrace) : null,
            }
        );
    }

    public override void OnSetupPhaseStarted(SetupPhaseStartedEvent e)
    {
        WriteJson(
            new
            {
                Event = "setup_started",
                e.Timestamp,
                e.Category,
                Phase = e.PhaseName,
                Collection = e.CollectionName,
            }
        );
    }

    public override void OnSetupPhaseCompleted(SetupPhaseCompletedEvent e)
    {
        WriteJson(
            new
            {
                Event = "setup_completed",
                e.Timestamp,
                e.Category,
                Phase = e.PhaseName,
                e.Success,
                Error = e.ErrorMessage,
                Collection = e.CollectionName,
            }
        );
    }

    public override void OnSetupStep(SetupStepEvent e)
    {
        WriteJson(
            new
            {
                Event = "setup_step",
                e.Timestamp,
                e.Category,
                Step = e.StepName,
                Status = e.Status.ToSnakeCase(),
                e.Details,
                Collection = e.CollectionName,
            }
        );
    }

    private void WriteJson<T>(T obj)
    {
        var json = DiagnosticEmitJson.Serialize(obj);
        _out.WriteLine(json);
        _out.Flush();
    }
}
