using System.Text.Json;
using JunimoServer.Tests.Fixtures;
using JunimoServer.Tests.Helpers;
using JunimoServer.Tests.Schema.Events;
using JunimoServer.Tests.Schema.Json;

namespace JunimoServer.TestRunner.Rendering.Web;

/// <summary>
/// Accumulates all test runner events into a queryable state model.
/// Thread-safe via simple lock: single writer (xUnit runner thread),
/// rare readers (GET /api/state, WS snapshot on connect).
/// </summary>
public sealed class TestRunState
{
    private readonly object _lock = new();

    // Run-level state
    private string _status = "pending"; // pending | running | finished
    private DateTime? _runStartTime;
    private DateTime? _runEndTime;
    private int _totalTests;
    private int _passed;
    private int _failed;
    private int _canceled;
    private int _skipped;
    private TimeSpan? _duration;

    // Ordering counters for timeline view
    private int _nextDiscoveryOrder = 0;
    private int _nextExecutionOrder = 0;

    // Setup phases: key = "category:phaseName" or "collectionName:category:phaseName"
    private readonly List<SetupPhaseState> _setupPhases = new();
    private readonly Dictionary<string, SetupPhaseState> _activePhases = new();

    // Test tree: collection -> class -> tests
    private readonly Dictionary<string, CollectionState> _collections = new();

    // Maps className -> pre-populated collection name.
    // Used to resolve xUnit v3's verbose runtime collection names
    // (e.g. "Test collection for Namespace.Class (id: hash)") back to the
    // short names from reflection (e.g. "Test collection for Class").
    private readonly Dictionary<string, string> _classToCollection = new();

    // Event log (bounded)
    private const int MaxEventLogSize = 5000;
    private readonly List<object> _eventLog = new();

    // Errors
    private readonly List<ErrorState> _errors = new();

    // VNC endpoints projected into the snapshot's `vncUrls` field.
    private readonly List<VncEndpointState> _vncUrls = new();

    // Per-instance state including VNC, status, recording paths.
    private readonly Dictionary<string, InstanceState> _instances = new();

    // Run identity (set once via ApplyRunMetadata; carried in snapshots and forwarded
    // to late-connecting clients).
    private string? _runMetadataRunDir;
    private JsonElement? _runMetadataData;

    // Flakiness over the last 20 runs (populated once at run end).
    private JsonElement? _flakyTests;

    #region Apply events (called from renderer thread, serializes JSON under lock)

    public string ApplyPopulateTests(
        IReadOnlyList<(
            string Collection,
            string ClassName,
            string MethodName,
            string DisplayName
        )> tests
    )
    {
        lock (_lock)
        {
            foreach (var (collection, className, methodName, displayName) in tests)
            {
                var col = GetOrCreateCollection(collection);
                var cls = col.GetOrCreateClass(className);
                if (!cls.Tests.ContainsKey(displayName))
                {
                    cls.Tests[displayName] = new TestState
                    {
                        Collection = collection,
                        ClassName = className,
                        MethodName = methodName,
                        DisplayName = displayName,
                        Status = "pending",
                        DiscoveryOrder = _nextDiscoveryOrder++,
                    };
                }
                _classToCollection.TryAdd(className, collection);
            }

            _totalTests = tests.Count;

            // Include the full collections tree so late-connecting clients can
            // hydrate the test list from this event (not just from the snapshot).
            var collectionsData = _collections
                .Values.Select(col => new
                {
                    col.Name,
                    Classes = col
                        .Classes.Values.Select(cls => new
                        {
                            cls.Name,
                            Tests = cls
                                .Tests.Values.Select(t => new
                                {
                                    t.ClassName,
                                    t.DisplayName,
                                    t.Status,
                                    t.DiscoveryOrder,
                                })
                                .ToList(),
                        })
                        .ToList(),
                })
                .ToList();

            var evt = new
            {
                Event = "populate_tests",
                TestCount = tests.Count,
                Collections = collectionsData,
                Timestamp = DateTime.UtcNow,
            };
            AddEventLog(evt);
            return Serialize(evt);
        }
    }

    public string ApplyRunStarted(RunStartedEvent e)
    {
        lock (_lock)
        {
            _status = "running";
            _runStartTime = e.Timestamp;
            if (e.TestCasesToRun > 0)
            {
                _totalTests = e.TestCasesToRun;
            }

            var evt = new
            {
                Event = "run_started",
                e.Timestamp,
                TestCount = _totalTests,
            };
            AddEventLog(evt);
            return Serialize(evt);
        }
    }

    public string ApplyDiscoveryComplete(DiscoveryCompleteEvent e)
    {
        lock (_lock)
        {
            // Don't overwrite _totalTests. ApplyPopulateTests already set the correct
            // expanded count. Discovery reports unexpanded method count.
            var evt = new
            {
                Event = "discovery_complete",
                e.Timestamp,
                e.TestCasesDiscovered,
                e.TestCasesToRun,
            };
            AddEventLog(evt);
            return Serialize(evt);
        }
    }

    public string ApplyRunFinished(RunFinishedEvent e)
    {
        lock (_lock)
        {
            _status = "finished";
            _runEndTime = e.Timestamp;
            _duration = e.Duration;

            // Mark remaining pending/queued tests as skipped (run ended before
            // they executed). Tests stuck in "running" mean xUnit's typed
            // callback (Passed/Failed/Skipped/NotRun) never fired AND the
            // generic OnTestFinished safety-net (Change A) didn't classify
            // them either — in that case fall back to the test-process
            // verdict via EnrichmentOutcome, which TestBase.DisposeAsync emits
            // before the test process exits. Without enrichment, default to
            // canceled (genuine interruption: run aborted, Ctrl-C, etc.).
            foreach (var col in _collections.Values)
            {
                foreach (var cls in col.Classes.Values)
                {
                    foreach (var test in cls.Tests.Values)
                    {
                        if (test.Status is "pending" or "queued")
                        {
                            SetOutcome(test, "skipped", source: "sweep");
                            test.SkipReason = "Not executed";
                        }
                        else if (test.Status == "running")
                        {
                            if (test.EnrichmentOutcome != null)
                            {
                                SetOutcome(test, test.EnrichmentOutcome, source: "enrichment");
                            }
                            else
                            {
                                SetOutcome(test, "canceled", source: "sweep");
                                test.ErrorMessage = "Test was interrupted when the run ended";
                            }
                        }
                    }
                }
            }

            // Recompute counts from the finalized tree; don't trust xUnit's summary
            // because it double-counts skipped tests that overlap with implicit skips.
            _passed = 0;
            _failed = 0;
            _canceled = 0;
            _skipped = 0;
            foreach (var col in _collections.Values)
            {
                foreach (var cls in col.Classes.Values)
                {
                    foreach (var test in cls.Tests.Values)
                    {
                        switch (test.Status)
                        {
                            case "passed":
                                _passed++;
                                break;
                            case "failed":
                                _failed++;
                                break;
                            case "canceled":
                                _canceled++;
                                break;
                            case "skipped":
                                _skipped++;
                                break;
                        }
                    }
                }
            }

            // Keep the larger of execution total and discovered total
            // (StopOnFail means xUnit's TotalTests only counts tests it actually ran)
            if (e.TotalTests > _totalTests)
            {
                _totalTests = e.TotalTests;
            }

            // Finalize any still-running setup phases and steps
            var finalStatus = _failed > 0 ? "failed" : "completed";
            foreach (var phase in _setupPhases)
            {
                if (phase.Status == "running")
                {
                    phase.Status = finalStatus;
                    phase.EndTime = e.Timestamp;
                    foreach (var step in phase.Steps)
                    {
                        if (step.Status is "started" or "in_progress")
                        {
                            step.Status = "failed";
                        }
                    }
                }
            }
            _activePhases.Clear();

            var evt = new
            {
                Event = "run_finished",
                e.Timestamp,
                TotalTests = _totalTests,
                Passed = _passed,
                Failed = _failed,
                Canceled = _canceled,
                Skipped = _skipped,
                DurationMs = (long)e.Duration.TotalMilliseconds,
            };
            AddEventLog(evt);
            return Serialize(evt);
        }
    }

    public string ApplyTestStarted(TestStartedEvent e)
    {
        lock (_lock)
        {
            var test = GetOrCreateTest(e.TestCollection, e.TestClass, e.TestMethod, e.DisplayName);
            test.Status = "queued";
            test.StartTime = e.Timestamp;

            var evt = new
            {
                Event = "test_started",
                e.Timestamp,
                e.TestCollection,
                e.TestClass,
                e.TestMethod,
                e.DisplayName,
            };
            AddEventLog(evt);
            return Serialize(evt);
        }
    }

    public string ApplyTestRunning(TestRunningEvent e)
    {
        lock (_lock)
        {
            var test = GetOrCreateTest(e.TestCollection, e.TestClass, e.TestMethod, e.DisplayName);
            test.Status = "running";
            test.RunningStartTime = e.Timestamp;
            test.ExecutionOrder = _nextExecutionOrder++;

            var evt = new
            {
                Event = "test_running",
                e.Timestamp,
                e.TestCollection,
                e.TestClass,
                e.TestMethod,
                e.DisplayName,
                test.ExecutionOrder,
            };
            AddEventLog(evt);
            return Serialize(evt);
        }
    }

    public string? ApplyTestOutput(TestOutputEvent e)
    {
        lock (_lock)
        {
            // Parse class/method from display name so we can create the test
            // on-the-fly if it hasn't been registered yet (pipe output arrives
            // before xUnit's OnTestStarting callback).
            var (className, methodName) = ParseDisplayName(e.DisplayName);
            var test = GetOrCreateTest(className, className, methodName, e.DisplayName);

            test.Output.Add(
                new
                {
                    Type = "line",
                    Ts = e.Timestamp,
                    Text = e.Line,
                }
            );

            var evt = new
            {
                Event = "test_output",
                e.Timestamp,
                TestCollection = test.Collection,
                TestClass = test.ClassName,
                e.DisplayName,
                e.Line,
            };
            AddEventLog(evt);
            return Serialize(evt);
        }
    }

    public string? ApplyTestAnnotation(TestAnnotationEvent e)
    {
        lock (_lock)
        {
            var (className, methodName) = ParseDisplayName(e.DisplayName);
            var test = GetOrCreateTest(className, className, methodName, e.DisplayName);

            var level = e.Level.ToString().ToLowerInvariant();
            var source = e.Source.ToString().ToLowerInvariant();
            test.Output.Add(
                new
                {
                    Type = "annotation",
                    Ts = e.Timestamp,
                    Level = level,
                    Source = source,
                    Message = e.Message,
                }
            );

            var evt = new
            {
                Event = "test_annotation",
                e.Timestamp,
                TestCollection = test.Collection,
                TestClass = test.ClassName,
                e.DisplayName,
                Level = level,
                Source = source,
                Message = e.Message,
            };
            AddEventLog(evt);
            return Serialize(evt);
        }
    }

    /// <summary>
    /// Funnel for every <see cref="TestState.Status"/> write that sets a
    /// terminal value (<c>passed</c>/<c>failed</c>/<c>skipped</c>/<c>canceled</c>).
    /// Detects cross-source disagreement (a later write differs from an
    /// earlier terminal write, possibly from a different source) and emits
    /// the <c>outcome_source_disagreement</c> diagnostic so the rate of each
    /// disagreement type is queryable post-deploy.
    ///
    /// <para>Source policy: <c>"enrichment"</c> (test-process verdict) wins
    /// over <c>"xunit"</c> (runner-side classification) when both fire and
    /// disagree — the test process observes the test method's actual outcome
    /// directly. <c>"sweep"</c> (run-finalize fallback in
    /// <see cref="ApplyRunFinished"/>) only writes when no other source has
    /// set the outcome, so it can never lose a disagreement at this site.
    /// </para>
    ///
    /// Caller must hold <c>_lock</c>.
    /// </summary>
    private void SetOutcome(TestState test, string outcome, string source)
    {
        var prior = test.Status;
        var priorSource = test.OutcomeSource;

        if (prior == outcome)
        {
            return;
        }

        // First terminal set, or normal lifecycle progression. The Status
        // before reaching here is one of "pending"/"queued"/"running" which
        // are non-terminal — no disagreement to log.
        var priorIsTerminal = prior is "passed" or "failed" or "canceled" or "skipped";
        if (!priorIsTerminal)
        {
            test.Status = outcome;
            test.OutcomeSource = source;
            return;
        }

        // Two terminal writes disagree. Resolve by source policy and emit diagnostic.
        var enrichmentWins = priorSource == "enrichment";
        var winningOutcome = enrichmentWins ? prior : outcome;
        var winningSource = enrichmentWins ? priorSource : source;

        EmitOutcomeSourceDisagreement(
            displayName: test.DisplayName,
            priorOutcome: prior,
            priorSource: priorSource ?? "(unset)",
            newOutcome: outcome,
            newSource: source,
            winningOutcome: winningOutcome ?? outcome
        );

        test.Status = winningOutcome ?? outcome;
        test.OutcomeSource = winningSource;
    }

    private static void EmitOutcomeSourceDisagreement(
        string displayName,
        string priorOutcome,
        string priorSource,
        string newOutcome,
        string newSource,
        string winningOutcome
    )
    {
        JunimoServer.Tests.Helpers.InfrastructureEventLog.Emit(
            "outcome_source_disagreement",
            new
            {
                testDisplayName = displayName,
                priorOutcome,
                priorSource,
                newOutcome,
                newSource,
                winningOutcome,
            }
        );
    }

    public string ApplyTestPassed(TestPassedEvent e)
    {
        lock (_lock)
        {
            var test = GetOrCreateTest(e.TestCollection, e.TestClass, e.TestMethod, e.DisplayName);
            SetOutcome(test, "passed", source: "xunit");
            ApplyTestDuration(test, e.Duration);
            // Output already accumulated via ApplyTestOutput; don't overwrite.

            var evt = new
            {
                Event = "test_passed",
                e.Timestamp,
                e.TestCollection,
                e.TestClass,
                e.DisplayName,
                DurationMs = test.DurationMs,
                QueueDurationMs = test.QueueDurationMs,
                Output = test.Output,
            };
            AddEventLog(evt);
            return Serialize(evt);
        }
    }

    public string ApplyTestFailed(TestFailedEvent e)
    {
        lock (_lock)
        {
            var test = GetOrCreateTest(e.TestCollection, e.TestClass, e.TestMethod, e.DisplayName);
            var isCanceled =
                e.ExceptionType
                is "System.OperationCanceledException"
                    or "System.Threading.Tasks.TaskCanceledException";
            // The xUnit-side classifier reads the exception type to decide
            // canceled-vs-failed; if enrichment arrived first and disagrees,
            // SetOutcome's source policy (enrichment > xunit) overrides this
            // write to whatever the test process said. That's the budget-
            // timeout vs StopOnFail-cascade reconciliation: indistinguishable
            // from xUnit's exception type, but the test process knows which.
            SetOutcome(test, isCanceled ? "canceled" : "failed", source: "xunit");
            ApplyTestDuration(test, e.Duration);
            // Output already accumulated via ApplyTestOutput; don't overwrite.
            // Only write error metadata if Status actually became a failure
            // outcome — SetOutcome's source policy can keep a prior enrichment
            // verdict (e.g. enrichment said "passed", xunit fired ITestFailed
            // afterwards: protocol-forbidden but defended structurally).
            // Leaving error fields blank for a passed test keeps the snapshot
            // self-consistent.
            if (test.Status is "failed" or "canceled")
            {
                test.ErrorMessage = e.Message;
                test.ErrorType = e.ExceptionType;
                test.StackTrace = e.StackTrace;
                test.FailedAt = e.Timestamp;
            }
            // ScreenshotPath is delivered separately via the screenshot event and
            // appended to test.Output. The xUnit-native event carries it for the
            // wire (runner → UI) but we don't store a top-level field on the snapshot.

            var evt = new
            {
                Event = "test_failed",
                e.Timestamp,
                e.TestCollection,
                e.TestClass,
                e.DisplayName,
                DurationMs = test.DurationMs,
                QueueDurationMs = test.QueueDurationMs,
                Error = e.Message,
                e.ExceptionType,
                StackTrace = RendererBase.SanitizeStackTrace(e.StackTrace ?? ""),
                e.ScreenshotPath,
                Output = test.Output,
            };
            AddEventLog(evt);
            return Serialize(evt);
        }
    }

    public string ApplyTestSkipped(TestSkippedEvent e)
    {
        lock (_lock)
        {
            var test = GetOrCreateTest(e.TestCollection, e.TestClass, e.TestMethod, e.DisplayName);
            SetOutcome(test, "skipped", source: "xunit");
            test.SkipReason = e.Reason;

            var evt = new
            {
                Event = "test_skipped",
                e.Timestamp,
                e.TestCollection,
                e.TestClass,
                e.DisplayName,
                e.Reason,
            };
            AddEventLog(evt);
            return Serialize(evt);
        }
    }

    public string ApplyDiagnostic(DiagnosticEvent e)
    {
        lock (_lock)
        {
            var evt = new
            {
                Event = "diagnostic",
                e.Timestamp,
                Source = e.Source.ToString().ToLowerInvariant(),
                Level = e.Level.ToString().ToLowerInvariant(),
                e.Message,
            };
            AddEventLog(evt);
            return Serialize(evt);
        }
    }

    public string ApplyError(ErrorEvent e)
    {
        lock (_lock)
        {
            _errors.Add(
                new ErrorState
                {
                    Message = e.Message,
                    StackTrace =
                        e.StackTrace != null ? RendererBase.SanitizeStackTrace(e.StackTrace) : null,
                    Timestamp = e.Timestamp,
                }
            );

            var evt = new
            {
                Event = "error",
                e.Timestamp,
                e.Message,
                StackTrace = e.StackTrace != null
                    ? RendererBase.SanitizeStackTrace(e.StackTrace)
                    : null,
            };
            AddEventLog(evt);
            return Serialize(evt);
        }
    }

    public string ApplySetupPhaseStarted(SetupPhaseStartedEvent e)
    {
        lock (_lock)
        {
            var key = MakePhaseKey(e.Category, e.PhaseName, e.CollectionName);
            var phase = new SetupPhaseState
            {
                Category = e.Category,
                PhaseName = e.PhaseName,
                CollectionName = e.CollectionName,
                Status = "running",
                StartTime = e.Timestamp,
            };
            _setupPhases.Add(phase);
            _activePhases[key] = phase;

            var evt = new
            {
                Event = "setup_phase_started",
                e.Timestamp,
                e.Category,
                Phase = e.PhaseName,
                Collection = e.CollectionName,
            };
            AddEventLog(evt);
            return Serialize(evt);
        }
    }

    public string ApplySetupPhaseCompleted(SetupPhaseCompletedEvent e)
    {
        lock (_lock)
        {
            var key = MakePhaseKey(e.Category, e.PhaseName, e.CollectionName);
            if (_activePhases.TryGetValue(key, out var phase))
            {
                phase.Status = e.Success ? "completed" : "failed";
                phase.EndTime = e.Timestamp;
                phase.ErrorMessage = e.ErrorMessage;

                // When the phase failed, finalize any still-running steps
                if (!e.Success)
                {
                    foreach (var step in phase.Steps)
                    {
                        if (step.Status is "started" or "in_progress")
                        {
                            step.Status = "failed";
                        }
                    }
                }

                _activePhases.Remove(key);
            }

            // Update matching instances' setup status
            if (e.CollectionName != null)
            {
                foreach (var inst in _instances.Values)
                {
                    if (inst.ServerKey == e.CollectionName || inst.InstanceId == e.CollectionName)
                    {
                        inst.SetupStatus = e.Success ? "completed" : "failed";
                    }
                }
            }

            var evt = new
            {
                Event = "setup_phase_completed",
                e.Timestamp,
                e.Category,
                Phase = e.PhaseName,
                e.Success,
                Error = e.ErrorMessage,
                Collection = e.CollectionName,
            };
            AddEventLog(evt);
            return Serialize(evt);
        }
    }

    public string ApplySetupStep(SetupStepEvent e)
    {
        lock (_lock)
        {
            // Find the active phase for this category (with optional collection)
            var key = FindActivePhaseKey(e.Category, e.CollectionName);
            if (key != null && _activePhases.TryGetValue(key, out var phase))
            {
                var existing = phase.Steps.Find(s => s.StepName == e.StepName);
                if (existing != null)
                {
                    existing.Status = e.Status.ToSnakeCase();
                    existing.Details = e.Details;
                    existing.Timestamp = e.Timestamp;
                    // Accumulate InProgress detail lines as output history
                    if (e.Status == SetupStepStatus.InProgress && !string.IsNullOrEmpty(e.Details))
                    {
                        existing.Output.Add(
                            new
                            {
                                Type = "line",
                                Ts = e.Timestamp,
                                Text = e.Details,
                            }
                        );
                    }
                }
                else
                {
                    phase.Steps.Add(
                        new SetupStepState
                        {
                            StepName = e.StepName,
                            Status = e.Status.ToSnakeCase(),
                            Details = e.Details,
                            Timestamp = e.Timestamp,
                        }
                    );
                }
            }

            // Route setup steps to matching instances (server_key or instance_id matches collection)
            if (e.CollectionName != null)
            {
                foreach (var inst in _instances.Values)
                {
                    if (inst.ServerKey != e.CollectionName && inst.InstanceId != e.CollectionName)
                    {
                        continue;
                    }

                    inst.SetupStatus ??= "running";
                    var existingInstStep = inst.SetupSteps.Find(s => s.StepName == e.StepName);
                    if (existingInstStep != null)
                    {
                        existingInstStep.Status = e.Status.ToSnakeCase();
                        existingInstStep.Details = e.Details;
                        existingInstStep.Timestamp = e.Timestamp;
                        if (
                            e.Status == SetupStepStatus.InProgress
                            && !string.IsNullOrEmpty(e.Details)
                        )
                        {
                            existingInstStep.Output.Add(
                                new
                                {
                                    Type = "line",
                                    Ts = e.Timestamp,
                                    Text = e.Details,
                                }
                            );
                        }
                    }
                    else
                    {
                        inst.SetupSteps.Add(
                            new SetupStepState
                            {
                                StepName = e.StepName,
                                Status = e.Status.ToSnakeCase(),
                                Details = e.Details,
                                Timestamp = e.Timestamp,
                            }
                        );
                    }
                }
            }

            var evt = new
            {
                Event = "setup_step",
                e.Timestamp,
                e.Category,
                Step = e.StepName,
                Status = e.Status.ToSnakeCase(),
                e.Details,
                Collection = e.CollectionName,
            };
            AddEventLog(evt);
            return Serialize(evt);
        }
    }

    /// <summary>
    /// Records run identity (runDir, git, env, runtime, server-config plan).
    /// Idempotent — first emit wins.
    /// </summary>
    public void ApplyRunMetadata(RunMetadataEvent e)
    {
        lock (_lock)
        {
            if (_runMetadataData != null)
            {
                return;
            }

            _runMetadataRunDir = e.RunDir;
            _runMetadataData = e.Data;
        }
    }

    /// <summary>
    /// Records flakiness data and emits the live event for the UI.
    /// </summary>
    public string ApplyFlakyTests(FlakyTestsEvent e)
    {
        lock (_lock)
        {
            _flakyTests = e.Tests;
            var evt = new
            {
                Event = "flaky_tests",
                e.Timestamp,
                Tests = e.Tests,
            };
            AddEventLog(evt);
            return Serialize(evt);
        }
    }

    /// <summary>
    /// Builds the WebSocket payload for a run_metadata event so late-connecting
    /// clients and live clients see the same shape.
    /// </summary>
    public string SerializeRunMetadataEvent(RunMetadataEvent e)
    {
        var evt = new
        {
            Event = "run_metadata",
            e.Timestamp,
            RunDir = e.RunDir,
            Data = e.Data,
        };
        AddEventLog(evt);
        return Serialize(evt);
    }

    /// <summary>
    /// Folds per-test enrichment from the test child process into the existing TestSnapshot.
    /// Order-independent vs. the xUnit-native test_passed/failed event: stores the
    /// test-process outcome on <see cref="TestState.EnrichmentOutcome"/> and routes
    /// the Status write through <see cref="SetOutcome"/>, whose source policy
    /// (enrichment > xunit) ensures late-arriving xUnit callbacks respect the
    /// test-process verdict and pre-existing terminal Status writes are overridden
    /// when enrichment disagrees. Both orderings converge correctly.
    /// </summary>
    /// <returns>
    /// <para><c>Json</c>: the serialised event payload to broadcast (or null when no
    /// broadcast is needed).</para>
    /// <para><c>ReclassifiedCanceledAsFailed</c>: true when this call flipped the
    /// existing <c>Status</c> from <c>"canceled"</c> to <c>"failed"</c>. Renderers
    /// use this to decrement their canceled-counter and increment the failed-counter
    /// (the xUnit-native <see cref="ApplyTestFailed"/> already incremented canceled).
    /// Other transitions (e.g. canceled→passed, the bug-fix path) do NOT set this
    /// flag because those tests' typed callbacks never fired, so the renderer's
    /// canceled counter was never incremented.</para>
    /// </returns>
    public (string? Json, bool ReclassifiedCanceledAsFailed) ApplyTestEnrichment(
        TestEnrichmentEvent e
    )
    {
        lock (_lock)
        {
            var (className, methodName) = ParseDisplayName(e.DisplayName);
            var test = GetOrCreateTest(className, className, methodName, e.DisplayName);

            // Fold failure metadata + server context onto the existing snapshot.
            test.FailureCategory = e.FailureCategory;
            test.ErrorPreview = e.ErrorPreview;
            test.Phase = e.Phase;
            test.ReproCommand = e.ReproCommand;
            test.ServerKey = e.ServerKey;
            test.ServerInstanceId = e.ServerInstanceId;
            test.FailureContext = e.FailureContext;
            // Screenshots live only in test.Output (appended by ApplyScreenshotCaptured).
            // The wire payload carries e.ScreenshotPath for the eager screenshot cache
            // on the UI side, but no per-test top-level field is stored on the snapshot.

            // Lifecycle: full breakdown including the two teardown-specific fields.
            test.Lifecycle = new LifecycleInfo(
                TestMs: e.TestBodyMs,
                CleanupMs: e.CleanupMs,
                ArtifactsMs: e.ArtifactsMs,
                LastKeepDisposeMs: e.LastKeepDisposeMs,
                LeaseReleaseMs: e.LeaseReleaseMs
            );

            // Cache the test-process outcome and route through SetOutcome.
            // SetOutcome's source policy makes enrichment authoritative when it
            // disagrees with a prior xunit-source write, surfacing the
            // disagreement via the outcome_source_disagreement diagnostic.
            // The reclassified return signals the renderer-side counter sync
            // for the specific canceled→failed transition (the only transition
            // where RendererBase._canceled was incremented and needs decrement;
            // see RendererBase.OnTestFailed body-OCE classifier).
            var reclassified = false;
            if (e.Outcome is "passed" or "failed" or "canceled")
            {
                test.EnrichmentOutcome = e.Outcome;
                var priorStatus = test.Status;
                SetOutcome(test, e.Outcome, source: "enrichment");
                reclassified = priorStatus == "canceled" && e.Outcome == "failed";
            }

            var evt = new
            {
                Event = "test_enrichment",
                e.Timestamp,
                TestCollection = test.Collection,
                TestClass = test.ClassName,
                e.DisplayName,
                e.Outcome,
                e.FailureCategory,
                e.ErrorPreview,
                e.Phase,
                e.ReproCommand,
                e.ServerKey,
                e.ServerInstanceId,
                e.ScreenshotPath,
                e.TestBodyMs,
                e.ArtifactsMs,
                e.CleanupMs,
                e.LastKeepDisposeMs,
                e.LeaseReleaseMs,
                e.FailureContext,
            };
            AddEventLog(evt);
            return (Serialize(evt), reclassified);
        }
    }

    /// <summary>
    /// Applies a screenshot captured event. Updates the test's screenshot path
    /// and broadcasts to clients. This handles screenshots captured after the
    /// TestFailedEvent was already emitted (e.g., during DisposeAsync).
    /// </summary>
    public string ApplyScreenshotCaptured(ScreenshotCapturedEvent e)
    {
        lock (_lock)
        {
            // Find the test and update its screenshot path + append inline entry to output.
            foreach (var col in _collections.Values)
            {
                foreach (var cls in col.Classes.Values)
                {
                    if (cls.Tests.TryGetValue(e.DisplayName, out var test))
                    {
                        test.Output.Add(
                            new
                            {
                                Type = "screenshot",
                                Ts = e.Timestamp,
                                Source = e.Source,
                                Path = e.ScreenshotPath,
                            }
                        );
                        test.LatestScreenshotPath = e.ScreenshotPath;
                        break;
                    }
                }
            }

            var evt = new
            {
                Event = "screenshot",
                e.Timestamp,
                e.TestCollection,
                e.TestClass,
                e.DisplayName,
                e.ScreenshotPath,
                e.Source,
            };
            AddEventLog(evt);
            return Serialize(evt);
        }
    }

    /// <summary>
    /// Applies a recording captured event. Updates the test's recording paths
    /// and broadcasts to clients. Mirrors ApplyScreenshotCaptured for video recordings.
    /// </summary>
    public string ApplyRecordingCaptured(RecordingCapturedEvent e)
    {
        lock (_lock)
        {
            foreach (var col in _collections.Values)
            {
                foreach (var cls in col.Classes.Values)
                {
                    if (cls.Tests.TryGetValue(e.DisplayName, out var test))
                    {
                        test.Recordings.Add(
                            new RecordingInfo(
                                e.RecordingPath,
                                e.Source,
                                e.TimelineOffset,
                                e.WallClockDuration
                            )
                        );
                        break;
                    }
                }
            }

            var evt = new
            {
                Event = "recording",
                e.Timestamp,
                e.TestCollection,
                e.TestClass,
                e.DisplayName,
                e.RecordingPath,
                e.Source,
                e.TimelineOffset,
                e.WallClockDuration,
            };
            AddEventLog(evt);
            return Serialize(evt);
        }
    }

    /// <summary>
    /// Applies a recording-skipped event. Updates the test's per-source
    /// <see cref="TestState.RecordingSkipReasons"/> map and broadcasts to clients.
    /// Mirrors <see cref="ApplyRecordingCaptured"/> for skip events.
    /// </summary>
    public string ApplyRecordingSkipped(RecordingSkippedEvent e)
    {
        // Reason is serialized as snake_case on the wire; mirror that on the
        // store-side dict so the live `evt`, the BuildSnapshot projection, and
        // the UI's per-source lookup all use the same key shape.
        var reason = JsonNamingPolicy.SnakeCaseLower.ConvertName(e.Reason.ToString());
        lock (_lock)
        {
            foreach (var col in _collections.Values)
            {
                foreach (var cls in col.Classes.Values)
                {
                    if (cls.Tests.TryGetValue(e.DisplayName, out var test))
                    {
                        test.RecordingSkipReasons[e.Source] = reason;
                        break;
                    }
                }
            }

            var evt = new
            {
                Event = "recording_skipped",
                e.Timestamp,
                e.TestCollection,
                e.TestClass,
                e.DisplayName,
                e.Source,
                Reason = reason,
            };
            AddEventLog(evt);
            return Serialize(evt);
        }
    }

    /// <summary>
    /// Adds a VNC endpoint URL for visual observation.
    /// Returns a JSON event string for broadcasting.
    /// </summary>
    public string AddVncUrl(string label, string url, string? collection = null)
    {
        lock (_lock)
        {
            // Avoid duplicates
            if (!_vncUrls.Any(v => v.Url == url))
            {
                _vncUrls.Add(
                    new VncEndpointState
                    {
                        Label = label,
                        Url = url,
                        Collection = collection,
                    }
                );
            }

            var evt = new
            {
                Event = "vnc_url",
                Label = label,
                Url = url,
                Collection = collection,
                Timestamp = DateTime.UtcNow,
            };
            AddEventLog(evt);
            return Serialize(evt);
        }
    }

    // ── Instance lifecycle events ──

    /// <summary>
    /// Applies an instance_created event and returns JSON for broadcasting.
    /// HostId is the Docker host the container runs on, set by the producer
    /// (broker) and threaded through unchanged for UI badge display.
    /// </summary>
    public string ApplyInstanceCreated(InstanceCreatedEvent e)
    {
        lock (_lock)
        {
            // Preserve accumulating state when re-creating (e.g., VNC URL update fires a second instance_created)
            List<InstanceHistoryEntry>? existingHistory = null;
            List<SetupStepState>? existingSteps = null;
            string? existingSetupStatus = null;
            bool existingDisposed = false;
            string? existingRecordingPath = null;
            if (_instances.TryGetValue(e.InstanceId, out var existing))
            {
                existingHistory = existing.History;
                existingSteps = existing.SetupSteps;
                existingSetupStatus = existing.SetupStatus;
                existingDisposed = existing.Disposed;
                existingRecordingPath = existing.RecordingPath;
            }

            _instances[e.InstanceId] = new InstanceState
            {
                InstanceId = e.InstanceId,
                HostId = e.HostId,
                InstanceType = e.InstanceType,
                ServerKey = e.ServerKey,
                VncUrl = e.VncUrl,
                Label = e.Label ?? "",
                Status = e.VncUrl != null ? "idle" : "starting",
                Connected = false,
                Disposed = existingDisposed,
                CurrentTest = null,
                History = existingHistory ?? new List<InstanceHistoryEntry>(),
                SetupSteps = existingSteps ?? new List<SetupStepState>(),
                SetupStatus = existingSetupStatus,
                RecordingPath = existingRecordingPath,
            };

            // Only add "created" history entry on first creation (not VNC URL updates)
            if (existingHistory == null)
            {
                _instances[e.InstanceId]
                    .History.Add(
                        new InstanceHistoryEntry { Timestamp = DateTime.UtcNow, Event = "created" }
                    );
            }

            var evt = new
            {
                Event = "instance_created",
                Timestamp = DateTime.UtcNow,
                e.InstanceId,
                e.HostId,
                e.InstanceType,
                e.ServerKey,
                e.VncUrl,
                e.Label,
            };
            AddEventLog(evt);
            return Serialize(evt);
        }
    }

    public string ApplyInstanceLeased(InstanceLeasedEvent e) =>
        ApplyInstanceStatus(
            "instance_leased",
            e.InstanceId,
            testName: e.TestName,
            serverInstanceId: e.ServerInstanceId
        );

    public string ApplyInstanceClientAttached(InstanceClientAttachedEvent e) =>
        ApplyInstanceStatus(
            "instance_client_attached",
            e.ServerInstanceId,
            clientInstanceId: e.ClientInstanceId
        );

    public string ApplyInstanceReturned(InstanceReturnedEvent e) =>
        ApplyInstanceStatus("instance_returned", e.InstanceId);

    public string ApplyInstanceDisposed(InstanceDisposedEvent e) =>
        ApplyInstanceStatus("instance_disposed", e.InstanceId);

    public string ApplyInstancePoisoned(InstancePoisonedEvent e) =>
        ApplyInstanceStatus("instance_poisoned", e.InstanceId, reason: e.Reason);

    public string ApplyInstanceConnected(InstanceConnectedEvent e) =>
        ApplyInstanceStatus("instance_connected", e.InstanceId);

    public string ApplyInstanceDisconnected(InstanceDisconnectedEvent e) =>
        ApplyInstanceStatus("instance_disconnected", e.InstanceId);

    /// <summary>
    /// Applies a generic instance status event (leased, returned, disposed, poisoned, connected, disconnected).
    /// </summary>
    private string ApplyInstanceStatus(
        string eventName,
        string instanceId,
        string? testName = null,
        string? reason = null,
        string? serverInstanceId = null,
        string? clientInstanceId = null
    )
    {
        lock (_lock)
        {
            if (_instances.TryGetValue(instanceId, out var inst))
            {
                // Record history before applying state changes (so disposed captures before removal)
                inst.History.Add(
                    new InstanceHistoryEntry
                    {
                        Timestamp = DateTime.UtcNow,
                        Event = eventName.Replace("instance_", ""),
                        TestName = testName,
                        Reason = reason,
                        ServerInstanceId = serverInstanceId,
                        ClientInstanceId = clientInstanceId,
                    }
                );

                switch (eventName)
                {
                    case "instance_leased":
                        inst.Status = "in_use";
                        inst.CurrentTest = testName;
                        inst.ConnectedServerId = serverInstanceId;
                        // Track which instances ran this test
                        if (testName != null)
                        {
                            foreach (var col in _collections.Values)
                            {
                                foreach (var cls in col.Classes.Values)
                                {
                                    if (cls.Tests.TryGetValue(testName, out var test))
                                    {
                                        if (!test.UsedInstances.Contains(instanceId))
                                        {
                                            test.UsedInstances.Add(instanceId);
                                        }
                                    }
                                }
                            }
                        }
                        break;
                    case "instance_returned":
                        inst.Status = "idle";
                        inst.Connected = false;
                        inst.CurrentTest = null;
                        inst.ConnectedServerId = null;
                        break;
                    case "instance_disposed":
                        // Flag the container as torn down, but do NOT overwrite Status.
                        // A poisoned container draining for recording extraction should
                        // still read as "poisoned" in the UI; Disposed just gates the
                        // retained-for-recording overlay.
                        inst.Disposed = true;
                        inst.Connected = false;
                        inst.CurrentTest = null;
                        inst.ConnectedServerId = null;
                        break;
                    case "instance_poisoned":
                        inst.Status = "poisoned";
                        inst.PoisonReason = reason;
                        // CurrentTest is cleared because a poisoned instance is no longer
                        // running the test that triggered the poison — leaving it set
                        // implied a still-running test.
                        inst.CurrentTest = null;
                        break;
                    case "instance_connected":
                        inst.Connected = true;
                        break;
                    case "instance_disconnected":
                        inst.Connected = false;
                        break;
                }
            }

            var evt = new
            {
                Event = eventName,
                Timestamp = DateTime.UtcNow,
                InstanceId = instanceId,
                TestName = testName,
                Reason = reason,
                ServerInstanceId = serverInstanceId,
                ClientInstanceId = clientInstanceId,
            };
            AddEventLog(evt);
            return Serialize(evt);
        }
    }

    /// <summary>
    /// Applies an instance recording event. Sets the full recording path on the instance.
    /// </summary>
    public string ApplyInstanceRecording(InstanceRecordingEvent e)
    {
        lock (_lock)
        {
            if (_instances.TryGetValue(e.InstanceId, out var inst))
            {
                inst.RecordingPath = e.RecordingPath;
            }

            var evt = new
            {
                Event = "instance_recording",
                Timestamp = DateTime.UtcNow,
                e.InstanceId,
                e.RecordingPath,
            };
            AddEventLog(evt);
            return Serialize(evt);
        }
    }

    /// <summary>
    /// Applies a container stats event. Stored in InstanceState.StatsHistory so
    /// late-connecting clients receive full history via the snapshot. Returns
    /// null if the instance no longer exists (disposed between poll and emit).
    /// </summary>
    public string? ApplyInstanceStats(InstanceStatsEvent e)
    {
        lock (_lock)
        {
            if (!_instances.TryGetValue(e.InstanceId, out var inst))
            {
                return null;
            }
            // Backfill HostId on the existing InstanceState so late-connecting WS
            // clients receive correct placement info via BuildSnapshot, even if
            // they missed the original instance_created event.
            if (!string.IsNullOrEmpty(e.HostId))
            {
                inst.HostId = e.HostId;
            }

            var d = e.Data;
            var timestamp = DateTime.UtcNow;
            var roundedCpu = Math.Round(d.CpuPercent, 1);
            var roundedMem = Math.Round(d.MemoryMb, 1);
            var roundedTotalMem = Math.Round(d.TotalMemoryMb, 0);
            var roundedFps = d.Fps.HasValue ? (double?)Math.Round(d.Fps.Value, 1) : null;
            var roundedTps = d.Tps.HasValue ? (double?)Math.Round(d.Tps.Value, 1) : null;
            var roundedAvgTickMs = d.AvgTickMs.HasValue
                ? (double?)Math.Round(d.AvgTickMs.Value, 2)
                : null;
            var roundedGameMem = d.GameMemoryMb.HasValue
                ? (double?)Math.Round(d.GameMemoryMb.Value, 1)
                : null;
            var roundedGcRate = d.GcRate.HasValue ? (double?)Math.Round(d.GcRate.Value, 1) : null;
            var roundedWait = d.GameThreadWaitMs.HasValue
                ? (double?)Math.Round(d.GameThreadWaitMs.Value, 2)
                : null;
            var roundedNetRx = d.NetRxBytesPerSec.HasValue
                ? (double?)Math.Round(d.NetRxBytesPerSec.Value, 0)
                : null;
            var roundedNetTx = d.NetTxBytesPerSec.HasValue
                ? (double?)Math.Round(d.NetTxBytesPerSec.Value, 0)
                : null;
            var roundedBlkRead = d.BlkReadBytesPerSec.HasValue
                ? (double?)Math.Round(d.BlkReadBytesPerSec.Value, 0)
                : null;
            var roundedBlkWrite = d.BlkWriteBytesPerSec.HasValue
                ? (double?)Math.Round(d.BlkWriteBytesPerSec.Value, 0)
                : null;
            var roundedMemLimit = Math.Round(d.MemoryLimitMb, 0);

            inst.StatsHistory.Add(
                new InstanceStatsEntry
                {
                    Timestamp = timestamp,
                    CpuPercent = roundedCpu,
                    MemoryMb = roundedMem,
                    CpuCount = d.CpuCount,
                    TotalMemoryMb = roundedTotalMem,
                    Fps = roundedFps,
                    Tps = roundedTps,
                    AvgTickMs = roundedAvgTickMs,
                    GameMemoryMb = roundedGameMem,
                    TargetTps = d.TargetTps,
                    TargetFps = d.TargetFps,
                    GcRate = roundedGcRate,
                    PendingActions = d.PendingActions,
                    GameThreadWaitMs = roundedWait,
                    NetRxBytesPerSec = roundedNetRx,
                    NetTxBytesPerSec = roundedNetTx,
                    BlkReadBytesPerSec = roundedBlkRead,
                    BlkWriteBytesPerSec = roundedBlkWrite,
                    MemoryLimitMb = roundedMemLimit,
                }
            );

            var evt = new
            {
                Event = "instance_stats",
                Timestamp = timestamp,
                e.InstanceId,
                e.HostId,
                CpuPercent = roundedCpu,
                MemoryMb = roundedMem,
                d.CpuCount,
                TotalMemoryMb = roundedTotalMem,
                Fps = roundedFps,
                Tps = roundedTps,
                AvgTickMs = roundedAvgTickMs,
                GameMemoryMb = roundedGameMem,
                d.TargetTps,
                d.TargetFps,
                GcRate = roundedGcRate,
                d.PendingActions,
                GameThreadWaitMs = roundedWait,
                NetRxBytesPerSec = roundedNetRx,
                NetTxBytesPerSec = roundedNetTx,
                BlkReadBytesPerSec = roundedBlkRead,
                BlkWriteBytesPerSec = roundedBlkWrite,
                MemoryLimitMb = roundedMemLimit,
            };
            // Not added to event log; stats are too frequent
            return Serialize(evt);
        }
    }

    #endregion

    #region Snapshot (for /api/state and initial WS message)

    public string ToSnapshotJson()
    {
        lock (_lock)
        {
            var snapshot = BuildSnapshot();
            return ArtifactPrettyJson.Serialize(snapshot);
        }
    }

    public object ToSnapshot()
    {
        lock (_lock)
        {
            return BuildSnapshot();
        }
    }

    /// <summary>
    /// Writes one compact JSON line per <c>(instance, stats sample)</c> to
    /// <paramref name="path"/> so the per-container CPU/memory/network/game
    /// history — collected live by <c>ContainerStatsCollector</c> and otherwise
    /// only held in memory for the UI — survives into the on-disk artifact tree
    /// for post-mortem load analysis (e.g. "was the host saturated when its SSH
    /// tunnel dropped?"). Each line carries the instance identity plus the same
    /// field set the snapshot projects (see <see cref="BuildSnapshot"/>).
    /// Instances with no samples are skipped, so the file is empty (or absent if
    /// the caller skips an empty write) when <c>SDVD_TEST_STATS=none</c>.
    /// </summary>
    public void WriteInstanceStatsJsonl(string path)
    {
        lock (_lock)
        {
            var lines = new List<string>();
            foreach (var inst in _instances.Values)
            {
                foreach (var s in inst.StatsHistory)
                {
                    lines.Add(
                        Serialize(
                            new
                            {
                                inst.InstanceId,
                                inst.HostId,
                                inst.InstanceType,
                                inst.ServerKey,
                                inst.Label,
                                s.Timestamp,
                                s.CpuPercent,
                                s.MemoryMb,
                                s.CpuCount,
                                s.TotalMemoryMb,
                                s.Fps,
                                s.Tps,
                                s.AvgTickMs,
                                s.GameMemoryMb,
                                s.TargetTps,
                                s.TargetFps,
                                s.GcRate,
                                s.PendingActions,
                                s.GameThreadWaitMs,
                                s.NetRxBytesPerSec,
                                s.NetTxBytesPerSec,
                                s.BlkReadBytesPerSec,
                                s.BlkWriteBytesPerSec,
                                s.MemoryLimitMb,
                            }
                        )
                    );
                }
            }

            File.WriteAllLines(path, lines);
        }
    }

    /// <summary>
    /// Writes the per-instance lifecycle narrative to <paramref name="path"/>:
    /// one compact JSON line per <c>History</c> entry (created/leased/returned/
    /// disposed/poisoned/connected/disconnected), each carrying the instance
    /// identity — so a post-mortem reader can reconstruct which test held which
    /// container and why it was poisoned. The <c>SetupEventBus</c> is disk-free,
    /// so this is the only on-disk home for that narrative.
    /// Instances with no history are skipped, so the file is empty when no
    /// instance lifecycle was recorded.
    /// </summary>
    public void WriteInstanceHistoryJsonl(string path)
    {
        lock (_lock)
        {
            var lines = new List<string>();
            foreach (var inst in _instances.Values)
            {
                foreach (var h in inst.History)
                {
                    lines.Add(
                        Serialize(
                            new
                            {
                                inst.InstanceId,
                                inst.HostId,
                                inst.InstanceType,
                                inst.ServerKey,
                                inst.Label,
                                h.Timestamp,
                                h.Event,
                                h.TestName,
                                h.Reason,
                                h.ServerInstanceId,
                                h.ClientInstanceId,
                            }
                        )
                    );
                }

                // Trailing final-state line so the current status fields (which the
                // history transitions don't fully carry) survive too.
                lines.Add(
                    Serialize(
                        new
                        {
                            inst.InstanceId,
                            inst.HostId,
                            inst.InstanceType,
                            inst.ServerKey,
                            inst.Label,
                            Event = "final_state",
                            inst.Status,
                            inst.Connected,
                            inst.Disposed,
                            inst.CurrentTest,
                            inst.PoisonReason,
                            inst.ConnectedServerId,
                            inst.VncUrl,
                            inst.SetupStatus,
                            inst.RecordingPath,
                        }
                    )
                );
            }

            File.WriteAllLines(path, lines);
        }
    }

    /// <summary>
    /// Writes the runner's in-memory UI event stream (the sequence the UI replays
    /// to late-connecting clients) to <paramref name="path"/>, one compact JSON
    /// line per <c>_eventLog</c> entry. Uniquely preserves the <c>diagnostic</c>
    /// and <c>error</c> events (from xUnit's <c>OnDiagnosticMessage</c>/
    /// <c>OnErrorMessage</c>), which land nowhere else on disk, plus the unified
    /// ordering of the run's event stream.
    /// <para>
    /// <c>_eventLog</c> is a bounded ring buffer (<see cref="MaxEventLogSize"/>);
    /// a normal run stays well under the cap, but if it was full at flush this
    /// prepends a <c>run_events_truncated</c> marker so a ring-buffer-evicted log
    /// is never mistaken for a complete one. The cap bounds live memory and is
    /// deliberately not raised here.
    /// </para>
    /// </summary>
    public void WriteRunEventsJsonl(string path)
    {
        lock (_lock)
        {
            var lines = new List<string>();
            if (_eventLog.Count >= MaxEventLogSize)
            {
                lines.Add(Serialize(new { Event = "run_events_truncated", Cap = MaxEventLogSize }));
            }

            foreach (var evt in _eventLog)
            {
                lines.Add(Serialize(evt));
            }

            File.WriteAllLines(path, lines);
        }
    }

    /// <summary>
    /// Writes the per-test UI-only extras to <paramref name="path"/>, one compact
    /// JSON line per test — the fields that <c>summary.json</c>/<c>ctrf</c> don't
    /// carry for non-failed tests (output, non-failed stack traces, failure
    /// context, ordering/timing, used instances). The internal-only
    /// reconciliation-provenance fields (<c>EnrichmentOutcome</c>,
    /// <c>OutcomeSource</c>) are deliberately excluded — they're not observable
    /// state.
    /// </summary>
    public void WriteTestDetailsJsonl(string path)
    {
        lock (_lock)
        {
            var lines = new List<string>();
            foreach (var col in _collections.Values)
            {
                foreach (var cls in col.Classes.Values)
                {
                    foreach (var t in cls.Tests.Values)
                    {
                        lines.Add(
                            Serialize(
                                new
                                {
                                    t.ClassName,
                                    t.DisplayName,
                                    t.Status,
                                    Output = t.Output.Count > 0 ? t.Output : null,
                                    t.StackTrace,
                                    t.FailureContext,
                                    RecordingSkipReasons = t.RecordingSkipReasons.Count > 0
                                        ? t.RecordingSkipReasons
                                        : null,
                                    UsedInstances = t.UsedInstances.Count > 0
                                        ? t.UsedInstances
                                        : null,
                                    t.DiscoveryOrder,
                                    t.ExecutionOrder,
                                    t.StartTime,
                                    t.RunningStartTime,
                                    Recordings = t.Recordings.Count > 0
                                        ? t
                                            .Recordings.Select(r => new
                                            {
                                                r.Path,
                                                r.Source,
                                                r.TimelineOffset,
                                                r.WallClockDuration,
                                            })
                                            .ToList()
                                        : null,
                                }
                            )
                        );
                    }
                }
            }

            File.WriteAllLines(path, lines);
        }
    }

    /// <summary>
    /// Writes the run-start prestart/warmup narrative to <paramref name="path"/>,
    /// one compact JSON line per <c>(phase, step)</c> from <c>_setupPhases</c> —
    /// the parent-side phases (preflight, cleanup, image/game-data distribution)
    /// and child-side per-collection prestart phases with their step-level
    /// breakdown. Scope is prestart-only: mid-run per-instance re-provisioning
    /// lives in <see cref="WriteInstanceHistoryJsonl"/>, not here.
    /// A phase with no steps still writes its own line so an in-progress or
    /// failed phase is visible.
    /// </summary>
    public void WriteSetupPhasesJsonl(string path)
    {
        lock (_lock)
        {
            var lines = new List<string>();
            foreach (var p in _setupPhases)
            {
                if (p.Steps.Count == 0)
                {
                    lines.Add(
                        Serialize(
                            new
                            {
                                p.Category,
                                Phase = p.PhaseName,
                                p.CollectionName,
                                p.Status,
                                p.StartTime,
                                p.EndTime,
                                Error = p.ErrorMessage,
                            }
                        )
                    );
                    continue;
                }

                foreach (var s in p.Steps)
                {
                    lines.Add(
                        Serialize(
                            new
                            {
                                p.Category,
                                Phase = p.PhaseName,
                                p.CollectionName,
                                p.Status,
                                p.StartTime,
                                p.EndTime,
                                Error = p.ErrorMessage,
                                Step = s.StepName,
                                StepStatus = s.Status,
                                s.Details,
                                Output = s.Output.Count > 0 ? s.Output : null,
                                s.Timestamp,
                            }
                        )
                    );
                }
            }

            File.WriteAllLines(path, lines);
        }
    }

    /// <summary>
    /// Run-level view for the report's social-media meta tags + OG card. Counts
    /// come from the same test-tree iteration BuildSnapshot uses (the _passed/
    /// _failed fields are only set at run_finished), so they match the published
    /// snapshot mid-run too. Git branch/sha are pulled from the run-metadata
    /// payload's nested "git" object when present.
    /// </summary>
    public RunSummary GetRunSummary()
    {
        lock (_lock)
        {
            var passed = 0;
            var failed = 0;
            var canceled = 0;
            var skipped = 0;
            foreach (var col in _collections.Values)
            {
                foreach (var cls in col.Classes.Values)
                {
                    foreach (var test in cls.Tests.Values)
                    {
                        switch (test.Status)
                        {
                            case "passed":
                                passed++;
                                break;
                            case "failed":
                                failed++;
                                break;
                            case "canceled":
                                canceled++;
                                break;
                            case "skipped":
                                skipped++;
                                break;
                        }
                    }
                }
            }

            string? gitBranch = null;
            string? gitSha = null;
            if (
                _runMetadataData is { } meta
                && meta.ValueKind == JsonValueKind.Object
                && meta.TryGetProperty("git", out var git)
                && git.ValueKind == JsonValueKind.Object
            )
            {
                if (git.TryGetProperty("branch", out var b) && b.ValueKind == JsonValueKind.String)
                {
                    gitBranch = b.GetString();
                }

                if (git.TryGetProperty("sha", out var s) && s.ValueKind == JsonValueKind.String)
                {
                    gitSha = s.GetString();
                }
            }

            return new RunSummary(
                Status: _status,
                TotalTests: _totalTests,
                Passed: passed,
                Failed: failed,
                Skipped: skipped,
                Canceled: canceled,
                DurationMs: _duration.HasValue ? (long)_duration.Value.TotalMilliseconds : null,
                GitBranch: gitBranch,
                GitSha: gitSha
            );
        }
    }

    /// <summary>
    /// Strongly-typed run-level view consumed by the runner-side artifact writer
    /// (summary.json, ctrf-report.json). Read under lock and copied into immutable
    /// records so the writer never reaches back into mutable state.
    /// </summary>
    public RunArtifactView GetArtifactView(
        bool aborted,
        string? abortReason,
        IReadOnlyDictionary<string, long>? droppedEventsByWorker = null,
        IReadOnlyList<string>? lostWorkers = null,
        long? rendererFailures = null,
        IReadOnlyList<string>? missingArtifacts = null,
        IReadOnlyList<System.Text.Json.JsonElement>? workerRunMetadata = null
    )
    {
        lock (_lock)
        {
            var tests = new List<TestArtifactView>();
            foreach (var col in _collections.Values)
            {
                foreach (var cls in col.Classes.Values)
                {
                    foreach (var t in cls.Tests.Values)
                    {
                        tests.Add(
                            new TestArtifactView(
                                Collection: t.Collection,
                                ClassName: t.ClassName,
                                DisplayName: t.DisplayName,
                                Status: t.Status,
                                DurationMs: t.DurationMs ?? 0,
                                QueueDurationMs: t.QueueDurationMs ?? 0,
                                FailedAt: t.FailedAt,
                                ErrorMessage: t.ErrorMessage,
                                ErrorType: t.ErrorType,
                                // Broker-level failures (e.g. AcquireServerAsync queue
                                // faults) never reach the test-side enrichment path, so
                                // classify and build the repro here from the xUnit-native
                                // fields. Enrichment-provided values win when present.
                                FailureCategory: t.FailureCategory
                                    ?? (
                                        t.Status == "failed" && t.ErrorType != null
                                            ? TestSummaryFixture.ClassifyFailureCategory(
                                                t.ErrorType
                                            )
                                            : null
                                    ),
                                ErrorPreview: t.ErrorPreview,
                                Phase: t.Phase,
                                ReproCommand: t.ReproCommand
                                    ?? (
                                        t.Status == "failed"
                                            ? TestSummaryFixture.BuildReproCommand(t.DisplayName)
                                            : null
                                    ),
                                ServerKey: t.ServerKey,
                                ServerInstanceId: t.ServerInstanceId,
                                ScreenshotPath: t.LatestScreenshotPath,
                                Lifecycle: t.Lifecycle is { } lc
                                    ? new LifecycleView(
                                        lc.TestMs,
                                        lc.CleanupMs,
                                        lc.ArtifactsMs,
                                        lc.LastKeepDisposeMs,
                                        lc.LeaseReleaseMs
                                    )
                                    : null,
                                SkipReason: t.SkipReason
                            )
                        );
                    }
                }
            }

            return new RunArtifactView(
                RunStartTime: _runStartTime ?? DateTime.UtcNow,
                RunEndTime: _runEndTime ?? DateTime.UtcNow,
                Duration: _duration ?? TimeSpan.Zero,
                ExpectedTestCount: _totalTests,
                Passed: tests.Count(t => t.Status == "passed"),
                Failed: tests.Count(t => t.Status == "failed"),
                Skipped: tests.Count(t => t.Status == "skipped"),
                Canceled: tests.Count(t => t.Status == "canceled"),
                Aborted: aborted,
                AbortReason: abortReason,
                Tests: tests,
                FlakyTests: _flakyTests,
                DroppedEventsByWorker: droppedEventsByWorker,
                LostWorkers: lostWorkers,
                RendererFailures: rendererFailures,
                MissingArtifacts: missingArtifacts,
                WorkerRunMetadata: workerRunMetadata
            );
        }
    }

    private object BuildSnapshot()
    {
        var collections = _collections
            .Values.Select(col => new
            {
                col.Name,
                Classes = col
                    .Classes.Values.Select(cls => new
                    {
                        cls.Name,
                        Tests = cls
                            .Tests.Values.Select(t => new
                            {
                                // Collection and MethodName are excluded — Collection is implicit
                                // in the tree hierarchy (collection → class → tests), and the UI
                                // derives the method name from displayName when needed.
                                t.ClassName,
                                t.DisplayName,
                                t.Status,
                                t.DurationMs,
                                t.QueueDurationMs,
                                Output = t.Output.Count > 0 ? t.Output : null,
                                ErrorMessage = t.ErrorMessage,
                                ErrorType = t.ErrorType,
                                t.StackTrace,
                                Recordings = t.Recordings.Count > 0
                                    ? t
                                        .Recordings.Select(r => new
                                        {
                                            r.Path,
                                            r.Source,
                                            r.TimelineOffset,
                                            r.WallClockDuration,
                                        })
                                        .ToList()
                                    : null,
                                // Fresh dict copy so the projection doesn't leak the live
                                // dict to a caller that serializes outside the lock.
                                RecordingSkipReasons = t.RecordingSkipReasons.Count > 0
                                    ? new Dictionary<string, string>(t.RecordingSkipReasons)
                                    : null,
                                Lifecycle = t.Lifecycle != null
                                    ? new
                                    {
                                        t.Lifecycle.TestMs,
                                        t.Lifecycle.CleanupMs,
                                        t.Lifecycle.ArtifactsMs,
                                        t.Lifecycle.LastKeepDisposeMs,
                                        t.Lifecycle.LeaseReleaseMs,
                                    }
                                    : null,
                                t.SkipReason,
                                t.DiscoveryOrder,
                                t.ExecutionOrder,
                                t.StartTime,
                                t.RunningStartTime,
                                UsedInstances = t.UsedInstances.Count > 0 ? t.UsedInstances : null,
                                // Enrichment fields (folded in by ApplyTestEnrichment).
                                t.FailureCategory,
                                t.ErrorPreview,
                                t.Phase,
                                t.ReproCommand,
                                t.ServerKey,
                                t.ServerInstanceId,
                                t.FailureContext,
                            })
                            .ToList(),
                    })
                    .ToList(),
            })
            .ToList();

        // Compute counts from the test tree so late-connecting clients get accurate
        // values even mid-run (the _passed/_failed/_skipped fields are only set at
        // run_finished time).
        var snapshotPassed = 0;
        var snapshotFailed = 0;
        var snapshotCanceled = 0;
        var snapshotSkipped = 0;
        foreach (var col in _collections.Values)
        {
            foreach (var cls in col.Classes.Values)
            {
                foreach (var test in cls.Tests.Values)
                {
                    switch (test.Status)
                    {
                        case "passed":
                            snapshotPassed++;
                            break;
                        case "failed":
                            snapshotFailed++;
                            break;
                        case "canceled":
                            snapshotCanceled++;
                            break;
                        case "skipped":
                            snapshotSkipped++;
                            break;
                    }
                }
            }
        }

        var setupPhases = _setupPhases
            .Select(p => new
            {
                p.Category,
                Phase = p.PhaseName,
                p.CollectionName,
                p.Status,
                p.StartTime,
                p.EndTime,
                Error = p.ErrorMessage,
                Steps = p
                    .Steps.Select(s => new
                    {
                        Step = s.StepName,
                        s.Status,
                        s.Details,
                        s.Output,
                        s.Timestamp,
                    })
                    .ToList(),
            })
            .ToList();

        return new
        {
            Type = "snapshot",
            Status = _status,
            RunStartTime = _runStartTime,
            RunEndTime = _runEndTime,
            TotalTests = _totalTests,
            Passed = snapshotPassed,
            Failed = snapshotFailed,
            Canceled = snapshotCanceled,
            Skipped = snapshotSkipped,
            DurationMs = _duration.HasValue
                ? (long?)((long)_duration.Value.TotalMilliseconds)
                : null,
            SetupPhases = setupPhases,
            Collections = collections,
            RunMetadata = _runMetadataData.HasValue
                ? new { RunDir = _runMetadataRunDir, Data = _runMetadataData.Value }
                : null,
            FlakyTests = _flakyTests,
            Errors = _errors
                .Select(e => new
                {
                    e.Message,
                    e.StackTrace,
                    e.Timestamp,
                })
                .ToList(),
            VncUrls = _vncUrls.Count > 0
                ? _vncUrls
                    .Select(v => new
                    {
                        v.Label,
                        v.Url,
                        v.Collection,
                    })
                    .ToList()
                : null,
            Instances = _instances.Count > 0
                ? _instances
                    .Values.Select(i => new
                    {
                        i.InstanceId,
                        i.HostId,
                        i.InstanceType,
                        i.ServerKey,
                        i.VncUrl,
                        i.Label,
                        i.Status,
                        i.Connected,
                        i.Disposed,
                        i.CurrentTest,
                        i.PoisonReason,
                        i.ConnectedServerId,
                        SetupSteps = i
                            .SetupSteps.Select(s => new
                            {
                                Step = s.StepName,
                                s.Status,
                                s.Details,
                                Output = s.Output.Count > 0 ? s.Output : null,
                                s.Timestamp,
                            })
                            .ToList(),
                        i.SetupStatus,
                        i.RecordingPath,
                        History = i
                            .History.Select(h => new
                            {
                                h.Timestamp,
                                h.Event,
                                h.TestName,
                                h.Reason,
                                h.ServerInstanceId,
                                h.ClientInstanceId,
                            })
                            .ToList(),
                        StatsHistory = i.StatsHistory.Count > 0
                            ? i
                                .StatsHistory.Select(s => new
                                {
                                    s.Timestamp,
                                    s.CpuPercent,
                                    s.MemoryMb,
                                    s.CpuCount,
                                    s.TotalMemoryMb,
                                    s.Fps,
                                    s.Tps,
                                    s.AvgTickMs,
                                    s.GameMemoryMb,
                                    s.TargetTps,
                                    s.TargetFps,
                                    s.GcRate,
                                    s.PendingActions,
                                    s.GameThreadWaitMs,
                                    s.NetRxBytesPerSec,
                                    s.NetTxBytesPerSec,
                                    s.BlkReadBytesPerSec,
                                    s.BlkWriteBytesPerSec,
                                    s.MemoryLimitMb,
                                })
                                .ToList()
                            : null,
                    })
                    .ToList()
                : null,
        };
    }

    #endregion

    #region Helpers

    private CollectionState GetOrCreateCollection(string name)
    {
        if (!_collections.TryGetValue(name, out var col))
        {
            col = new CollectionState { Name = name };
            _collections[name] = col;
        }
        return col;
    }

    private TestState GetOrCreateTest(
        string collection,
        string className,
        string methodName,
        string displayName
    )
    {
        // Resolve xUnit v3's verbose runtime collection name back to the short
        // name from reflection. e.g. "Test collection for Namespace.Class (id: hash)"
        // becomes "Test collection for Class".
        collection = ResolveCollectionName(collection, className);

        var col = GetOrCreateCollection(collection);
        var cls = col.GetOrCreateClass(className);
        if (!cls.Tests.TryGetValue(displayName, out var test))
        {
            test = new TestState
            {
                Collection = collection,
                ClassName = className,
                MethodName = methodName,
                DisplayName = displayName,
                Status = "pending",
                DiscoveryOrder = _nextDiscoveryOrder++,
            };
            cls.Tests[displayName] = test;
        }
        return test;
    }

    /// <summary>
    /// If this className was seen during PopulateTests, return the pre-populated
    /// collection name. Otherwise return the runtime name as-is.
    /// </summary>
    private string ResolveCollectionName(string runtimeName, string className)
    {
        if (_collections.ContainsKey(runtimeName))
        {
            return runtimeName;
        }

        if (_classToCollection.TryGetValue(className, out var populated))
        {
            return populated;
        }

        return runtimeName;
    }

    private static string MakePhaseKey(string category, string phaseName, string? collectionName)
    {
        return collectionName != null
            ? $"{collectionName}:{category}:{phaseName}"
            : $"{category}:{phaseName}";
    }

    private string? FindActivePhaseKey(string category, string? collectionName)
    {
        // Try exact match with collection first
        if (collectionName != null)
        {
            foreach (var key in _activePhases.Keys)
            {
                if (key.StartsWith(collectionName + ":" + category + ":"))
                {
                    return key;
                }
            }
        }

        // Try category-only match
        foreach (var key in _activePhases.Keys)
        {
            if (key.Contains(":" + category + ":") || key.StartsWith(category + ":"))
            {
                return key;
            }
        }

        // Fallback: any active phase
        return _activePhases.Keys.FirstOrDefault();
    }

    /// <summary>
    /// Parse class and method from a fully qualified display name.
    /// Same logic as RunnerCallbacks.ParseTestDisplayName.
    /// </summary>
    private static (string ClassName, string MethodName) ParseDisplayName(string displayName)
    {
        var parenIdx = displayName.IndexOf('(');
        var searchUpTo = parenIdx >= 0 ? parenIdx : displayName.Length;
        var lastDot = displayName.LastIndexOf('.', searchUpTo - 1, searchUpTo);
        if (lastDot < 0)
        {
            return ("Unknown", displayName);
        }

        var methodName = displayName[(lastDot + 1)..];
        var classPath = displayName[..lastDot];
        var secondLastDot = classPath.LastIndexOf('.');
        var className = secondLastDot >= 0 ? classPath[(secondLastDot + 1)..] : classPath;
        return (className, methodName);
    }

    private void AddEventLog(object evt)
    {
        if (_eventLog.Count >= MaxEventLogSize)
        {
            _eventLog.RemoveAt(0);
        }

        _eventLog.Add(evt);
    }

    private static string Serialize<T>(T obj) => DiagnosticEmitJson.Serialize(obj);

    /// <summary>
    /// Sets DurationMs and QueueDurationMs from xUnit's total duration and
    /// the test_running timestamp (server acquisition time).
    /// </summary>
    private static void ApplyTestDuration(TestState test, TimeSpan xunitDuration)
    {
        var totalMs = (long)xunitDuration.TotalMilliseconds;
        var queueMs =
            test.RunningStartTime != null && test.StartTime != null
                ? (long)(test.RunningStartTime.Value - test.StartTime.Value).TotalMilliseconds
                : 0L;
        if (queueMs < 0)
        {
            queueMs = 0;
        }

        test.DurationMs = totalMs - queueMs;
        test.QueueDurationMs = queueMs > 0 ? queueMs : null;
    }

    #endregion

    #region State models

    private sealed class CollectionState
    {
        public string Name { get; set; } = "";
        public Dictionary<string, ClassState> Classes { get; } = new();

        public ClassState GetOrCreateClass(string name)
        {
            if (!Classes.TryGetValue(name, out var cls))
            {
                cls = new ClassState { Name = name };
                Classes[name] = cls;
            }
            return cls;
        }
    }

    private sealed class ClassState
    {
        public string Name { get; set; } = "";
        public Dictionary<string, TestState> Tests { get; } = new();
    }

    private sealed class TestState
    {
        public string Collection { get; set; } = "";
        public string ClassName { get; set; } = "";
        public string MethodName { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string Status { get; set; } = "pending";
        public DateTime? StartTime { get; set; }
        public DateTime? RunningStartTime { get; set; }
        public long? DurationMs { get; set; }
        public long? QueueDurationMs { get; set; }
        public List<object> Output { get; set; } = new();
        public string? ErrorMessage { get; set; }
        public string? ErrorType { get; set; }
        public string? StackTrace { get; set; }

        /// <summary>
        /// Timestamp of the xUnit test_failed event. Projected into
        /// <c>summary.json.failures[].failedAt</c> so failure rows are orderable
        /// (the runbook's "identify the FIRST failure" step). Null when the test
        /// never produced a failed/canceled xUnit callback.
        /// </summary>
        public DateTime? FailedAt { get; set; }

        /// <summary>
        /// The <c>Outcome</c> string carried by the most recent <c>test_enrichment</c>
        /// event for this test (<c>"passed"</c>, <c>"failed"</c>, <c>"canceled"</c>,
        /// or null if no enrichment has arrived). The test process observes the
        /// test method's actual outcome (via <c>TestSummaryFixture.MarkCompleted</c>
        /// / <c>MarkFailed</c> / <c>MarkCanceled</c> in <c>TestBase.DisposeAsync</c>)
        /// and is authoritative for outcome reconciliation. Two disagreement cases
        /// use it:
        /// <list type="bullet">
        ///   <item>budget-timeout vs StopOnFail-cascade — both surface as OCE in
        ///   the test body, indistinguishable from xUnit's exception type alone.
        ///   Read by <see cref="ApplyTestFailed"/> via <see cref="SetOutcome"/>'s
        ///   prior-source policy.</item>
        ///   <item>passed-but-no-classification — when xUnit's typed callback
        ///   fails to fire (the bug this field defends against), the runner-side
        ///   <see cref="Status"/> stays <c>"running"</c>; the sweep in
        ///   <see cref="ApplyRunFinished"/> reads this field to fall back to the
        ///   test-process verdict.</item>
        /// </list>
        /// Set inside <see cref="ApplyTestEnrichment"/>. All Apply* sites hold
        /// <c>_lock</c>, so reads are consistent with the write.
        /// </summary>
        public string? EnrichmentOutcome { get; set; }

        /// <summary>
        /// Provenance of the most recent <see cref="Status"/> write that set a
        /// terminal value (passed/failed/skipped/canceled). One of <c>"xunit"</c>,
        /// <c>"enrichment"</c>, <c>"sweep"</c>, or null when Status is still a
        /// non-terminal lifecycle value. Read by <see cref="SetOutcome"/> to
        /// detect cross-source disagreements and emit the
        /// <c>outcome_source_disagreement</c> diagnostic. Not serialized to the
        /// wire format or artifact view — internal-only.
        /// </summary>
        public string? OutcomeSource { get; set; }
        public List<RecordingInfo> Recordings { get; set; } = new();

        /// <summary>
        /// Per-source skip reasons. Key is the source slug emitted by
        /// <see cref="ApplyRecordingSkipped"/> — <c>"server"</c>, the un-indexed
        /// <c>"client"</c> (class-level skip applying to all client cards), or
        /// an indexed <c>"client_2"</c>/<c>"client_3"</c>/… for orchestrator-late
        /// skips that mirror the corresponding success event's source. Value is
        /// the snake_case reason string from <see cref="RecordingSkipReason"/>.
        /// </summary>
        public Dictionary<string, string> RecordingSkipReasons { get; } = new();

        // Latest screenshot path appended via ApplyScreenshotCaptured. Internal —
        // not serialized into the live snapshot; the UI reads screenshots from
        // Output[]. Used by the runner-side artifact writer for CTRF attachments.
        public string? LatestScreenshotPath { get; set; }
        public LifecycleInfo? Lifecycle { get; set; }
        public string? SkipReason { get; set; }
        public int? DiscoveryOrder { get; set; }
        public int? ExecutionOrder { get; set; }
        public List<string> UsedInstances { get; set; } = new();

        // Enrichment fields populated by ApplyTestEnrichment (arrives slightly after
        // the xUnit-native test_passed/failed event). Carries fields known only to the
        // child process: failure category/preview/repro and server context.
        public string? FailureCategory { get; set; }
        public string? ErrorPreview { get; set; }
        public string? Phase { get; set; }
        public string? ReproCommand { get; set; }
        public string? ServerKey { get; set; }
        public string? ServerInstanceId { get; set; }

        // Latest FailureContext.DumpAsync result for this test (server state at the
        // failure point, plus reason/extras). Null if no dump was captured.
        public JsonElement? FailureContext { get; set; }
    }

    private sealed class SetupPhaseState
    {
        public string Category { get; set; } = "";
        public string PhaseName { get; set; } = "";
        public string? CollectionName { get; set; }
        public string Status { get; set; } = "pending";
        public DateTime? StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        public string? ErrorMessage { get; set; }
        public List<SetupStepState> Steps { get; } = new();
    }

    private sealed class SetupStepState
    {
        public string StepName { get; set; } = "";
        public string Status { get; set; } = "started";
        public string? Details { get; set; }
        public List<object> Output { get; set; } = new();
        public DateTime Timestamp { get; set; }
    }

    private sealed record RecordingInfo(
        string Path,
        string Source,
        double TimelineOffset,
        double WallClockDuration
    );

    private sealed record LifecycleInfo(
        long TestMs,
        long CleanupMs,
        long ArtifactsMs,
        long LastKeepDisposeMs = 0,
        long LeaseReleaseMs = 0
    );

    private sealed class ErrorState
    {
        public string Message { get; set; } = "";
        public string? StackTrace { get; set; }
        public DateTime Timestamp { get; set; }
    }

    private sealed class VncEndpointState
    {
        public string Label { get; set; } = "";
        public string Url { get; set; } = "";
        public string? Collection { get; set; }
    }

    private sealed class InstanceState
    {
        public string InstanceId { get; set; } = "";

        /// <summary>
        /// Docker host this instance runs on (<c>local</c>, <c>vps-1</c>, etc.).
        /// Set by the producer (broker) and threaded through unchanged for UI
        /// badge display. Empty string is a defensive fallback when the wire
        /// payload omits <c>hostId</c>; instances created via HostPool.Place
        /// always carry a populated value.
        /// </summary>
        public string HostId { get; set; } = "";
        public string InstanceType { get; set; } = "";
        public string ServerKey { get; set; } = "";
        public string? VncUrl { get; set; }
        public string Label { get; set; } = "";
        public string Status { get; set; } = "idle";
        public bool Connected { get; set; }

        /// <summary>
        /// True once the container has been torn down. Orthogonal to Status so
        /// a poisoned container still reads as "poisoned" while it drains for
        /// recording extraction. Flips the UI into retained-for-recording mode.
        /// </summary>
        public bool Disposed { get; set; }
        public string? CurrentTest { get; set; }

        /// <summary>
        /// Reason this instance was poisoned (health check failure, etc.). Set once
        /// when the instance_poisoned event arrives; null otherwise. Orthogonal to
        /// CurrentTest — a poisoned instance has no current test.
        /// </summary>
        public string? PoisonReason { get; set; }
        public string? ConnectedServerId { get; set; }
        public List<SetupStepState> SetupSteps { get; set; } = new();
        public string? SetupStatus { get; set; } // null | "running" | "completed" | "failed"
        public List<InstanceHistoryEntry> History { get; set; } = new();
        public List<InstanceStatsEntry> StatsHistory { get; set; } = new();
        public string? RecordingPath { get; set; }
    }

    private sealed class InstanceStatsEntry
    {
        public DateTime Timestamp { get; set; }
        public double CpuPercent { get; set; }
        public double MemoryMb { get; set; }
        public int CpuCount { get; set; }
        public double TotalMemoryMb { get; set; }
        public double? Fps { get; set; }
        public double? Tps { get; set; }
        public double? AvgTickMs { get; set; }
        public double? GameMemoryMb { get; set; }
        public int? TargetTps { get; set; }
        public int? TargetFps { get; set; }
        public double? GcRate { get; set; }
        public int? PendingActions { get; set; }
        public double? GameThreadWaitMs { get; set; }
        public double? NetRxBytesPerSec { get; set; }
        public double? NetTxBytesPerSec { get; set; }
        public double? BlkReadBytesPerSec { get; set; }
        public double? BlkWriteBytesPerSec { get; set; }
        public double MemoryLimitMb { get; set; }
    }

    private sealed class InstanceHistoryEntry
    {
        public DateTime Timestamp { get; set; }
        public string Event { get; set; } = "";
        public string? TestName { get; set; }
        public string? Reason { get; set; }
        public string? ServerInstanceId { get; set; }
        public string? ClientInstanceId { get; set; }
    }

    #endregion
}
