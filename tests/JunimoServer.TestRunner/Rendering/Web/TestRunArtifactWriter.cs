using System.Text.Json;
using JunimoServer.Tests.Helpers;
using JunimoServer.Tests.Schema.Json;

namespace JunimoServer.TestRunner.Rendering.Web;

/// <summary>
/// Runner-side writer for the durable run artifacts: <c>summary.json</c> and
/// <c>ctrf-report.json</c>. Both are projections of <see cref="RunArtifactView"/>,
/// so the same in-memory state model that drives the live UI also produces the
/// disk files. No parallel per-test list.
///
/// Owns <c>latest.txt</c> too: written after <c>summary.json</c> succeeds so a
/// crashed run does not poison the pointer that <c>make test-summary</c> reads.
/// </summary>
public sealed class TestRunArtifactWriter
{
    private readonly object _lock = new();
    private string? _runDir;
    private string? _runId;
    private string? _outputDir;
    private bool _written;

    /// <summary>
    /// Records the run dir / id. Required before <see cref="WriteIfNotWritten"/>
    /// can do anything. Called from two sites: the parent process via
    /// <c>SeedRunIdentity</c> right after <c>RunMetadata.BeginRun</c>, and the
    /// renderer's <c>OnRunMetadata</c> handler when the child's <c>run_metadata</c>
    /// IPC event arrives. Idempotent — last-writer-wins under the lock.
    /// </summary>
    public void OnRunMetadata(string runDir, string runId)
    {
        lock (_lock)
        {
            _runDir = runDir;
            _runId = runId;
            // outputDir is the parent of runs/{id}/. summary.json + ctrf go in runDir;
            // latest.txt goes in outputDir.
            var runsDir = Path.GetDirectoryName(runDir);
            _outputDir = runsDir != null ? Path.GetDirectoryName(runsDir) : null;
        }
    }

    /// <summary>
    /// Idempotent. Called from the renderer's <c>DisposeAsync</c>, after the setup
    /// pipe has drained — covers both the graceful path and abnormal child exit.
    /// </summary>
    public void WriteIfNotWritten(RunArtifactView view)
    {
        lock (_lock)
        {
            if (_written)
            {
                return;
            }

            if (_runDir == null || _runId == null)
            {
                // Run identity never set. Either the parent process aborted before
                // calling SeedRunIdentity (possible if BeginRun failed), or — rarer
                // — neither the parent seed nor the child's run_metadata IPC ever
                // landed. summary.json depends on the run dir, so we cannot write
                // it. Log and fall through.
                Console.Error.WriteLine(
                    "[ArtifactWriter] run identity not set; cannot write summary.json/ctrf-report.json/latest.txt"
                );
                return;
            }
            _written = true;

            try
            {
                WriteSummaryJson(view);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[ArtifactWriter] summary.json failed: {ex.Message}");
            }

            try
            {
                WriteCtrfReport(view);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[ArtifactWriter] ctrf-report.json failed: {ex.Message}");
            }

            try
            {
                WriteRunOutput(view);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[ArtifactWriter] run-output.json failed: {ex.Message}");
            }

            try
            {
                WriteRunMetadataMerged(view);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"[ArtifactWriter] run-metadata.json (merged) failed: {ex.Message}"
                );
            }

            try
            {
                WriteLatestPointer();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[ArtifactWriter] latest.txt failed: {ex.Message}");
            }
        }
    }

    private void WriteSummaryJson(RunArtifactView v)
    {
        var failures = new List<Dictionary<string, object?>>();
        long activeDurationTotalMs = 0;
        long queueDurationTotalMs = 0;

        foreach (var t in v.Tests)
        {
            activeDurationTotalMs += t.DurationMs;
            queueDurationTotalMs += t.QueueDurationMs;
            if (t.Status != "failed")
            {
                continue;
            }

            var methodName = ExtractMethodFromTestName(t.DisplayName);
            var testDirName = $"{t.ClassName}.{methodName}";

            failures.Add(
                new Dictionary<string, object?>
                {
                    ["test"] = t.DisplayName,
                    ["className"] = t.ClassName,
                    ["methodName"] = methodName,
                    // failedAt orders the rows (runbook step 1: "identify the FIRST
                    // failure"). The active/queue split mirrors the run-level totals;
                    // queueDurationMs is 0 when the queue→active transition was never
                    // observed (e.g. the test failed inside server acquisition), in
                    // which case activeDurationMs still contains the wait.
                    ["failedAt"] = t.FailedAt?.ToString("o"),
                    ["activeDurationMs"] = t.DurationMs,
                    ["queueDurationMs"] = t.QueueDurationMs,
                    ["failureCategory"] = t.FailureCategory,
                    ["errorPreview"] = t.ErrorPreview,
                    ["error"] = t.ErrorMessage,
                    ["exceptionType"] = t.ErrorType,
                    ["phase"] = t.Phase,
                    ["screenshotDir"] = $"tests/{testDirName}/screenshots/",
                    ["serverKey"] = t.ServerKey,
                    ["serverInstanceId"] = t.ServerInstanceId,
                    ["reproCommand"] = t.ReproCommand,
                }
            );
        }

        // Never-dispatched count: expected total minus every test that has any record
        // (passed/failed/canceled/skipped). Skipped tests (xUnit's [Fact(Skip=...)])
        // are part of the discovered total but didn't run — they're not "never dispatched".
        var notDispatched = Math.Max(
            0,
            v.ExpectedTestCount - (v.Passed + v.Failed + v.Canceled + v.Skipped)
        );

        var infraErrors = InfrastructureErrorAggregator.Read(_runDir!);

        var summary = new Dictionary<string, object?>
        {
            ["schemaVersion"] = 1,
            ["timestamp"] = v.RunEndTime.ToString("o"),
            ["runId"] = _runId,
            ["durationMs"] = (long)v.Duration.TotalMilliseconds,
            ["activeDurationTotalMs"] = activeDurationTotalMs,
            ["queueDurationTotalMs"] = queueDurationTotalMs,
            ["result"] = ResultStatus(v),
            ["passed"] = v.Passed,
            ["failed"] = v.Failed,
            ["skipped"] = v.Skipped,
            ["canceled"] = v.Canceled,
            ["notDispatched"] = notDispatched,
            ["aborted"] = v.Aborted,
            ["abortReason"] = v.AbortReason,
            ["failures"] = failures,
            ["infrastructureErrors"] = infraErrors.Entries,
            ["infrastructureErrorsTruncated"] = infraErrors.Truncated ? (object)true : null,
            ["flakyTests"] = v.FlakyTests,
            // Coordinator-only signals; null in local-mode runs (omitted via JsonIgnoreCondition).
            ["degradation"] = BuildDegradation(v),
        };

        Directory.CreateDirectory(_runDir!);
        var json = ArtifactPrettyJson.Serialize(summary);
        File.WriteAllText(Path.Combine(_runDir!, RunArtifactNames.SummaryJson), json);
    }

    /// <summary>
    /// Top-level run status. Precedence: failed &gt; canceled &gt; passed.
    /// Skipped tests do not affect the run status — a run with only skipped
    /// tests (or skipped + passed) is reported as <c>"passed"</c>.
    /// <c>aborted</c> is orthogonal: it surfaces alongside <c>result</c> in
    /// summary.json (and CTRF's <c>extra</c> block) but never overrides it,
    /// so an aborted-but-no-failure run reports <c>"passed"</c> with
    /// <c>aborted=true</c>.
    /// </summary>
    private static string ResultStatus(RunArtifactView v) =>
        v.Failed > 0 ? "failed"
        : v.Canceled > 0 ? "canceled"
        : "passed";

    /// <summary>
    /// Build the coordinator-only degradation block. Returns null when no
    /// degradation signals are populated (local-mode case) so JsonIgnoreCondition
    /// drops the key from the output.
    /// </summary>
    private static Dictionary<string, object?>? BuildDegradation(RunArtifactView v)
    {
        if (
            v.DroppedEventsByWorker is null
            && v.LostWorkers is null
            && v.RendererFailures is null
            && v.MissingArtifacts is null
        )
        {
            return null;
        }

        var anyDataLoss =
            (v.DroppedEventsByWorker?.Values.Any(n => n > 0) ?? false)
            || (v.LostWorkers?.Count ?? 0) > 0
            || (v.RendererFailures ?? 0) > 0
            || (v.MissingArtifacts?.Count ?? 0) > 0;

        return new Dictionary<string, object?>
        {
            ["status"] = anyDataLoss ? "degraded" : "clean",
            ["droppedEvents"] = v.DroppedEventsByWorker,
            ["lostWorkers"] = v.LostWorkers,
            ["rendererFailures"] = v.RendererFailures,
            ["missingArtifacts"] = v.MissingArtifacts,
        };
    }

    private void WriteCtrfReport(RunArtifactView v)
    {
        var tests = new List<Dictionary<string, object?>>();

        foreach (var t in v.Tests)
        {
            var status = t.Status switch
            {
                "passed" => "passed",
                "failed" => "failed",
                _ => "other",
            };

            var testObj = new Dictionary<string, object?>
            {
                ["name"] = t.DisplayName,
                ["status"] = status,
                ["duration"] = t.DurationMs,
                ["suite"] = t.ClassName,
            };

            if (t.Status == "failed" && t.ErrorMessage != null)
            {
                testObj["message"] = t.ErrorMessage;
            }

            // CTRF `extra` carve-out for our additions: failure metadata, server
            // context, and the lifecycle phase breakdown.
            var extra = new Dictionary<string, object>();
            if (t.Phase != null)
            {
                extra["phase"] = t.Phase;
            }

            if (t.FailureCategory != null)
            {
                extra["failureCategory"] = t.FailureCategory;
            }

            if (t.ErrorPreview != null)
            {
                extra["errorPreview"] = t.ErrorPreview;
            }

            if (t.ReproCommand != null)
            {
                extra["reproCommand"] = t.ReproCommand;
            }

            if (t.ServerKey != null)
            {
                extra["serverKey"] = t.ServerKey;
            }

            if (t.ServerInstanceId != null)
            {
                extra["serverInstanceId"] = t.ServerInstanceId;
            }

            if (t.Lifecycle is { } lc)
            {
                extra["testBodyMs"] = lc.TestMs;
                extra["artifactsMs"] = lc.ArtifactsMs;
                extra["cleanupMs"] = lc.CleanupMs;
                extra["lastKeepDisposeMs"] = lc.LastKeepDisposeMs;
                extra["leaseReleaseMs"] = lc.LeaseReleaseMs;
            }
            if (t.SkipReason != null)
            {
                extra["skipReason"] = t.SkipReason;
            }

            if (extra.Count > 0)
            {
                testObj["extra"] = extra;
            }

            if (t.ScreenshotPath != null)
            {
                testObj["attachments"] = new[]
                {
                    new
                    {
                        name = "screenshot",
                        path = t.ScreenshotPath,
                        contentType = "image/png",
                    },
                };
            }

            tests.Add(testObj);
        }

        // Never-dispatched tests fold into CTRF's `other` bucket (CTRF spec has no
        // dedicated never-dispatched status). The tests[] array deliberately omits
        // them — summary.tests (expected total) and tests.length (actual dispatched
        // list) intentionally diverge on aborted runs.
        // Skipped is excluded from the notDispatched derivation: skipped tests have a
        // record (xUnit emits them) so they're already part of the discovered total.
        var notDispatched = Math.Max(
            0,
            v.ExpectedTestCount - (v.Passed + v.Failed + v.Canceled + v.Skipped)
        );
        var other = v.Canceled + notDispatched;
        var totalTests = v.Passed + v.Failed + v.Skipped + other;

        var report = new Dictionary<string, object?>
        {
            ["specVersion"] = "0.0.0",
            ["reportFormat"] = "CTRF",
            ["timestamp"] = v.RunStartTime.ToString("o"),
            ["results"] = new Dictionary<string, object?>
            {
                ["tool"] = new { name = "xunit" },
                ["summary"] = new
                {
                    tests = totalTests,
                    passed = v.Passed,
                    failed = v.Failed,
                    pending = 0,
                    skipped = v.Skipped,
                    other,
                    start = new DateTimeOffset(v.RunStartTime).ToUnixTimeMilliseconds(),
                    stop = new DateTimeOffset(v.RunEndTime).ToUnixTimeMilliseconds(),
                },
                ["tests"] = tests,
                ["extra"] = v.Aborted ? new { aborted = true, abortReason = v.AbortReason } : null,
            },
        };

        Directory.CreateDirectory(_runDir!);
        var json = ArtifactPrettyJson.Serialize(report);
        File.WriteAllText(Path.Combine(_runDir!, RunArtifactNames.CtrfReport), json);
    }

    /// <summary>
    /// Machine-readable CI summary. Mirrors the run-level facts a CI consumer
    /// needs without parsing summary.json: status, counters, paths, degradation
    /// flag. Always written (in both local and coordinator modes). The same
    /// shape is also echoed to stdout as a single JSON line for log scrapers.
    /// </summary>
    private void WriteRunOutput(RunArtifactView v)
    {
        var status = ResultStatus(v);
        var degradation = BuildDegradation(v);
        var degradationStatus = degradation is null
            ? "clean"
            : (string?)degradation["status"] ?? "clean";

        var output = new Dictionary<string, object?>
        {
            ["runId"] = _runId,
            ["runDir"] = _runDir,
            ["status"] = status,
            ["passed"] = v.Passed,
            ["failed"] = v.Failed,
            ["skipped"] = v.Skipped,
            ["canceled"] = v.Canceled,
            ["duration_s"] = (int)(v.Duration.TotalMilliseconds / 1000),
            ["ctrfReport"] = Path.Combine(_runDir!, RunArtifactNames.CtrfReport),
            ["summaryJson"] = Path.Combine(_runDir!, RunArtifactNames.SummaryJson),
            ["degradation"] = degradationStatus,
        };

        Directory.CreateDirectory(_runDir!);
        var json = ArtifactPrettyJson.Serialize(output);
        File.WriteAllText(Path.Combine(_runDir!, RunArtifactNames.RunOutputJson), json);

        // One-line JSON to stdout so CI scrapers can parse the run summary directly.
        Console.WriteLine(JsonSerializer.Serialize(output));
    }

    /// <summary>
    /// In coordinator mode, take a representative worker's run-metadata.json as
    /// the canonical envelope and attach a <c>workers</c> array carrying every
    /// worker's payload. In local mode <see cref="RunArtifactView.WorkerRunMetadata"/>
    /// is null and this writer is a no-op (the local test-child already wrote
    /// its own run-metadata.json directly).
    /// </summary>
    private void WriteRunMetadataMerged(RunArtifactView v)
    {
        if (v.WorkerRunMetadata is null || v.WorkerRunMetadata.Count == 0)
        {
            return;
        }

        // Use worker 0 as the canonical envelope (schema version, git, env are
        // all coordinator-side properties; any worker's view of them is equivalent).
        var canonical = v.WorkerRunMetadata[0];
        if (canonical.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var merged = new Dictionary<string, object?>();
        foreach (var prop in canonical.EnumerateObject())
        {
            merged[prop.Name] = prop.Value.Clone();
        }

        // Override the runDir to the merged dir (each worker's payload had its
        // own per-worker runDir).
        merged["runDir"] = _runDir;
        merged["workers"] = v.WorkerRunMetadata.Select(w => (object)w.Clone()).ToList();

        Directory.CreateDirectory(_runDir!);
        var json = ArtifactPrettyJson.Serialize(merged);
        File.WriteAllText(Path.Combine(_runDir!, RunArtifactNames.RunMetadataJson), json);
    }

    private void WriteLatestPointer()
    {
        if (_outputDir == null || _runDir == null)
        {
            return;
        }

        var latestPath = Path.Combine(_outputDir, RunArtifactNames.LatestPointer);
        File.WriteAllText(latestPath, _runDir);
    }

    private static string ExtractMethodFromTestName(string testName)
    {
        var paren = testName.IndexOf('(');
        var name = paren >= 0 ? testName[..paren] : testName;
        var dot = name.LastIndexOf('.');
        return dot >= 0 ? name[(dot + 1)..] : name;
    }
}
