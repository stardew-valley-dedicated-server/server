using JunimoServer.Tests.Helpers;
using JunimoServer.Tests.Infrastructure;
using Xunit;

[assembly: AssemblyFixture(typeof(JunimoServer.Tests.Fixtures.TestSummaryFixture))]
[assembly: TestCollectionOrderer(typeof(JunimoServer.Tests.Fixtures.TestCollectionOrderer))]

namespace JunimoServer.Tests.Fixtures;

/// <summary>
/// Assembly-level fixture that tracks test execution across ALL collections
/// and prints a unified summary when all tests complete.
///
/// Uses a static singleton pattern so collection fixtures can register tests.
/// The assembly fixture lifecycle guarantees:
/// - Instance is created before any collection fixtures
/// - Instance is disposed after all collection fixtures are disposed
/// </summary>
public class TestSummaryFixture : IAsyncLifetime
{
    // Static singleton for collection fixtures to access
    private static TestSummaryFixture? _instance;
    private static readonly object _instanceLock = new();

    /// <summary>
    /// Gets the current instance. Returns null if assembly fixture not yet initialized.
    /// Collection fixtures should handle null gracefully.
    /// </summary>
    public static TestSummaryFixture? Instance
    {
        get
        {
            lock (_instanceLock)
            {
                return _instance;
            }
        }
    }

    // Test run timing
    private DateTime _testRunStartTime;

    // Expected total test count from assembly scanning (for detecting canceled tests)
    private int _expectedTestCount;

    // Test tracking: Collection -> Class -> Test records
    private readonly Dictionary<string, Dictionary<string, List<TestRecord>>> _testsByCollection =
        new();
    private readonly object _testLock = new();

    /// <summary>
    /// One-way per-test state machine. Mutual exclusion of Failed/Canceled is structural.
    /// Never-dispatched tests have NO TestRecord at all — their count is derived at finalize
    /// time as <c>_expectedTestCount - records.Count</c> and emitted as a separate field.
    /// </summary>
    internal enum TestOutcome
    {
        Running, // MarkDispatched fired; test in progress
        Passed,
        Failed,
        Canceled, // dispatched then cancelled (StopOnFail, server poison, etc.)
    }

    /// <summary>
    /// Tracks a single test's name, duration, and outcome.
    /// </summary>
    private class TestRecord
    {
        public string Name { get; }
        public string? ClassName { get; set; }
        public TimeSpan? Duration { get; set; }
        public TimeSpan? QueueDuration { get; set; }
        public TestOutcome Outcome { get; set; } = TestOutcome.Running;
        public string? Error { get; set; }
        public string? ExceptionType { get; set; }

        /// <summary>
        /// Explicit category stamp (e.g. <c>"infrastructure"</c> when the lease's
        /// host was poisoned). Wins over the exception-type classification in
        /// enrichment and flakiness accounting; null means "classify by type".
        /// </summary>
        public string? FailureCategory { get; set; }
        public string? Phase { get; set; }
        public string? ScreenshotPath { get; set; }
        public string? ServerKey { get; set; }
        public string? ServerInstanceId { get; set; }
        public Infrastructure.TestPhaseBreakdown? Breakdown { get; set; }

        public TestRecord(string name) => Name = name;
    }

    // Abort state (aggregated from all fixtures)
    private volatile bool _testRunAborted;
    private string? _abortReason;
    private readonly object _abortLock = new();

    /// <summary>
    /// Returns true if any collection was aborted.
    /// </summary>
    public bool IsTestRunAborted => _testRunAborted;

    /// <summary>
    /// Gets the abort reason if any.
    /// </summary>
    public string? AbortReason => _abortReason;

    /// <summary>
    /// Marks the test run as aborted with the given reason.
    /// Only the first abort reason is recorded.
    /// </summary>
    public void SetAborted(string reason)
    {
        lock (_abortLock)
        {
            if (_testRunAborted)
            {
                return;
            }

            _testRunAborted = true;
            _abortReason = reason;
        }
    }

    /// <summary>
    /// Marks a test as dispatched (xUnit InitializeAsync reached). Creates a record
    /// in <see cref="TestOutcome.Running"/>.
    /// </summary>
    public void MarkDispatched(string collectionName, string className, string? testName = null)
    {
        lock (_testLock)
        {
            if (!_testsByCollection.TryGetValue(collectionName, out var testsByClass))
            {
                testsByClass = new Dictionary<string, List<TestRecord>>();
                _testsByCollection[collectionName] = testsByClass;
            }

            if (!testsByClass.TryGetValue(className, out var tests))
            {
                tests = new List<TestRecord>();
                testsByClass[className] = tests;
            }

            tests.Add(new TestRecord(testName ?? "(unknown)") { ClassName = className });
        }
    }

    /// <summary>
    /// Records the duration for a completed test (no queue-duration / breakdown context).
    /// Used by callers outside the TestBase pipeline (e.g. DownloadValidationFixture).
    /// </summary>
    public void MarkCompleted(
        string collectionName,
        string className,
        string? testName,
        TimeSpan duration
    ) =>
        MarkCompleted(
            collectionName,
            className,
            testName,
            duration,
            queueDuration: null,
            breakdown: null
        );

    /// <summary>
    /// Marks a test as completed: records active duration + queue duration + phase breakdown.
    /// If outcome is still <see cref="TestOutcome.Running"/>, promotes to <see cref="TestOutcome.Passed"/>.
    /// If a prior <see cref="MarkFailed"/> or <see cref="MarkCanceled"/> already terminalized
    /// the record, leaves the outcome alone and only fills in duration metadata.
    /// </summary>
    public void MarkCompleted(
        string collectionName,
        string className,
        string? testName,
        TimeSpan activeDuration,
        TimeSpan? queueDuration,
        Infrastructure.TestPhaseBreakdown? breakdown = null
    )
    {
        lock (_testLock)
        {
            var record = FindRecord(
                collectionName,
                className,
                testName,
                requireUnsetDuration: true
            );
            if (record == null)
            {
                return;
            }

            record.Duration = activeDuration;
            if (queueDuration != null)
            {
                record.QueueDuration = queueDuration;
            }

            if (breakdown != null)
            {
                record.Breakdown = breakdown;
            }

            if (record.Outcome == TestOutcome.Running)
            {
                record.Outcome = TestOutcome.Passed;
            }
        }
    }

    /// <summary>
    /// Sets server context on a test record. Called from TestBase.DisposeAsync()
    /// after server acquisition (serverKey is unknown at MarkDispatched time).
    /// </summary>
    public void SetServerContext(
        string collectionName,
        string className,
        string? testName,
        string? serverKey,
        string? serverInstanceId
    )
    {
        lock (_testLock)
        {
            var record = FindRecord(collectionName, className, testName);
            if (record == null)
            {
                return;
            }

            record.ServerKey = serverKey;
            record.ServerInstanceId = serverInstanceId;
        }
    }

    /// <summary>
    /// Marks a test as failed. <c>Running → Failed</c>. No-op-with-warning if already terminal.
    /// </summary>
    public void MarkFailed(
        string collectionName,
        string className,
        string? testName,
        string error,
        string? phase = null,
        string? screenshotPath = null,
        string? serverKey = null,
        string? serverInstanceId = null,
        string? exceptionType = null,
        string? failureCategory = null
    )
    {
        lock (_testLock)
        {
            var record = FindRecord(collectionName, className, testName);
            if (record == null)
            {
                return;
            }

            if (record.Outcome != TestOutcome.Running)
            {
                Infrastructure.TestLog.Test(
                    $"MarkFailed on {record.Name}: outcome was already {record.Outcome}, ignoring"
                );
                return;
            }

            record.Outcome = TestOutcome.Failed;
            record.Error = error;
            record.Phase = phase;
            record.ScreenshotPath = screenshotPath;
            record.ExceptionType = exceptionType;
            record.FailureCategory = failureCategory;
            if (serverKey != null)
            {
                record.ServerKey = serverKey;
            }

            if (serverInstanceId != null)
            {
                record.ServerInstanceId = serverInstanceId;
            }
        }
    }

    /// <summary>
    /// Marks a test as canceled (e.g., StopOnFail cascade, server poisoned).
    /// <c>Running → Canceled</c>. No-op-with-warning if already terminal.
    /// </summary>
    public void MarkCanceled(string collectionName, string className, string? testName)
    {
        lock (_testLock)
        {
            var record = FindRecord(collectionName, className, testName);
            if (record == null)
            {
                return;
            }

            if (record.Outcome != TestOutcome.Running)
            {
                Infrastructure.TestLog.Test(
                    $"MarkCanceled on {record.Name}: outcome was already {record.Outcome}, ignoring"
                );
                return;
            }

            record.Outcome = TestOutcome.Canceled;
        }
    }

    private TestRecord? FindRecord(
        string collectionName,
        string className,
        string? testName,
        bool requireUnsetDuration = false
    )
    {
        if (!_testsByCollection.TryGetValue(collectionName, out var testsByClass))
        {
            return null;
        }

        if (!testsByClass.TryGetValue(className, out var tests))
        {
            return null;
        }

        var name = testName ?? "(unknown)";
        for (var i = tests.Count - 1; i >= 0; i--)
        {
            if (tests[i].Name != name)
            {
                continue;
            }

            if (requireUnsetDuration && tests[i].Duration != null)
            {
                continue;
            }

            return tests[i];
        }
        return null;
    }

    /// <summary>
    /// Snapshot of the failure-specific fields needed by the runner-side
    /// <c>test_enrichment</c> IPC event. Returned by value; safe to read after
    /// MarkFailed/MarkCompleted have populated the record. Returns null if no
    /// matching record exists (e.g. test never dispatched through TestBase).
    /// </summary>
    internal TestEnrichmentSnapshot? GetEnrichmentSnapshot(
        string collectionName,
        string className,
        string? testName
    )
    {
        lock (_testLock)
        {
            var r = FindRecord(collectionName, className, testName);
            if (r == null)
            {
                return null;
            }

            return new TestEnrichmentSnapshot(
                Outcome: r.Outcome,
                Error: r.Error,
                ExceptionType: r.ExceptionType,
                FailureCategory: r.FailureCategory,
                Phase: r.Phase,
                ScreenshotPath: r.ScreenshotPath,
                ServerKey: r.ServerKey,
                ServerInstanceId: r.ServerInstanceId
            );
        }
    }

    /// <summary>
    /// Failure-specific record fields exposed for the test_enrichment IPC event.
    /// </summary>
    internal record TestEnrichmentSnapshot(
        TestOutcome Outcome,
        string? Error,
        string? ExceptionType,
        string? FailureCategory,
        string? Phase,
        string? ScreenshotPath,
        string? ServerKey,
        string? ServerInstanceId
    );

    /// <summary>
    /// Classification of an exception type into a coarse failure category.
    /// Public so producers (TestBase enrichment emit) can label the event consistently
    /// with what summary.json would record.
    /// </summary>
    public static string ClassifyFailureCategory(string? exceptionType) =>
        ClassifyFailure(exceptionType);

    /// <summary>
    /// First-line preview of a failure message, capped at 120 chars.
    /// Public so the enrichment emit can attach the same preview the summary uses.
    /// </summary>
    public static string? BuildErrorPreview(string? error) => ErrorPreview(error);

    /// <summary>
    /// Builds the conventional repro command for a test. Public for use at the enrichment emit site.
    /// </summary>
    public static string BuildReproCommand(string testName)
    {
        var methodName = ExtractMethodFromTestName(testName);
        return $"make test-llm FILTER=\"{methodName}\"";
    }

    /// <summary>
    /// Gets the total test count across all collections.
    /// </summary>
    public int TotalTestCount
    {
        get
        {
            lock (_testLock)
            {
                return _testsByCollection
                    .Values.SelectMany(c => c.Values)
                    .Sum(tests => tests.Count);
            }
        }
    }

    // Mutual exclusion between graceful DisposeAsync and EmergencyFlush.
    // FlakinessTracker.RecordRun is append-only; double-invocation would duplicate lines.
    private volatile bool _finalized;

    public ValueTask InitializeAsync()
    {
        // Single owner of the run directory. Runs before any test's InitializeAsync
        // (xUnit assembly-fixture guarantee), so InfrastructureEventLog and
        // container log streamers always see a set TestArtifacts.RunDir
        // instead of the default TestResults root.
        RunMetadata.BeginRun();

        lock (_instanceLock)
        {
            _instance = this;
        }
        _testRunStartTime = DateTime.UtcNow;

        // Compute expected test count for canceled-detection. In distributed
        // worker mode, SDVD_WORKER_TEST_COUNT is the authoritative answer (the
        // worker only runs its slice of the suite, not the full assembly), so
        // skip DiscoverRequiredConfigs entirely -- using the global count would
        // make canceled = (full_suite - worker_share) every run.
        var workerTestCountEnv = Environment.GetEnvironmentVariable("SDVD_WORKER_TEST_COUNT");
        if (
            !string.IsNullOrEmpty(workerTestCountEnv)
            && int.TryParse(workerTestCountEnv, out var workerCount)
        )
        {
            _expectedTestCount = workerCount;
        }
        else
        {
            try
            {
                // Mirror the broker's filter scoping so a `--filter`-narrowed
                // run reports the right expectedCount. Without this the full
                // suite's count wins and every non-matched test is recorded
                // as canceled in summary.json (per not-dispatched-derivation).
                var methodFilter = Environment.GetEnvironmentVariable("SDVD_TEST_FILTER");
                _expectedTestCount = ServerConfigDiscovery
                    .DiscoverRequiredConfigs(methodFilter: methodFilter)
                    .Sum(d => d.TestCount);
            }
            catch (Exception ex)
            {
                Infrastructure.TestLog.Server(
                    $"TestSummaryFixture: discovery failed for expected-count seed: {ex.GetType().Name}: {ex.Message}"
                );
                // If discovery fails, fall back to registered count (no canceled detection)
            }
        }

        // Register emergency-flush so summary.json is written even on hard kill
        // (Ctrl+C timeout, SIGHUP, unhandled exception before DisposeAsync fires).
        Helpers.EmergencyCleanup.Register("TestSummaryFixture", EmergencyFlush);

        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        // Dispose the broker to trigger video recording extraction from all containers.
        // Client pool is disposed first (inside DisposeAsync), then remaining servers.
        // Servers that self-disposed via ReleaseAsync are safely skipped (Interlocked guard).
        // This catches servers that weren't naturally released (e.g., demand counting mismatch).
        try
        {
            await TestResourceBroker.Instance.DisposeAsync();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[TestSummaryFixture] Broker dispose crashed: {ex}");
        }

        FinalizeRun();

        Helpers.EmergencyCleanup.Unregister("TestSummaryFixture");
        lock (_instanceLock)
        {
            _instance = null;
        }
    }

    /// <summary>
    /// Writes all end-of-run artifacts (summary.json, ctrf-report.json,
    /// latest.txt, flakiness.jsonl). Idempotent via <see cref="_finalized"/>;
    /// shared by the graceful <see cref="DisposeAsync"/> path and the
    /// <see cref="EmergencyFlush"/> path.
    /// </summary>
    private void FinalizeRun()
    {
        if (_finalized)
        {
            return;
        }

        _finalized = true;

        // Sweep records still in Running. Only happens on hard process kill where
        // EmergencyFlush runs before DisposeAsync — the test was dispatched but
        // never reached MarkCompleted / MarkFailed / MarkCanceled.
        lock (_testLock)
        {
            foreach (var testsByClass in _testsByCollection.Values)
            {
                foreach (var classTests in testsByClass.Values)
                {
                    foreach (var test in classTests)
                    {
                        if (test.Outcome == TestOutcome.Running)
                        {
                            test.Outcome = TestOutcome.Canceled;
                        }
                    }
                }
            }
        }

        // summary.json + ctrf-report.json + latest.txt are written by the runner-side
        // TestRunArtifactWriter. Both graceful (run_finished) and abnormal-exit paths
        // are covered there.

        // ORDERING INVARIANT: RecordRun (file append) must complete before
        // ComputeFlakiness (file read) so the current run is included in its own
        // flakiness window. Then the IPC emission ferries the result to the UI.
        try
        {
            FlakinessTracker.RecordRun(this);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[TestSummaryFixture] FlakinessTracker crashed: {ex}");
        }

        try
        {
            var flaky = FlakinessTracker.ComputeFlakiness();
            SetupEventBus.EmitFlakyTests(flaky);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[TestSummaryFixture] Flaky emit crashed: {ex}");
        }
    }

    /// <summary>
    /// Synchronous emergency-flush invoked from <see cref="Helpers.EmergencyCleanup"/>
    /// when the process is terminating without graceful dispose. Writes summary.json
    /// with an aborted status so readers can always trust the file's presence.
    /// </summary>
    private void EmergencyFlush()
    {
        if (_finalized)
        {
            return;
        }

        if (!_testRunAborted)
        {
            SetAborted("emergency-shutdown");
        }

        FinalizeRun();
    }

    #region summary.json

    private static string ClassifyFailure(string? exceptionType)
    {
        if (string.IsNullOrEmpty(exceptionType))
        {
            return "crash";
        }

        if (exceptionType.StartsWith("Xunit.Sdk.") || exceptionType.Contains("AssertException"))
        {
            return "assertion";
        }

        if (
            exceptionType.Contains("TimeoutException")
            || exceptionType.Contains("OperationCanceledException")
            || exceptionType.Contains("TaskCanceledException")
        )
        {
            return "timeout";
        }

        if (
            exceptionType.Contains("Docker")
            || exceptionType.Contains("Testcontainers")
            || exceptionType.Contains("ServerUnavailableException")
            || exceptionType.Contains("TestRunAbortedException")
            // Raw transport faults: the E2E harness reaches the server only over the
            // ssh-forwarded transport, so a Socket/Http/stream fault that surfaces to a test
            // is infrastructure (a forward/host blip), not a product bug — including a live
            // server-start wrap like "server-N failed to start: …SocketException", which
            // carries the SocketException type. (Most such faults are now skipped at acquire
            // time per AcquireWithInfrastructureSkipAsync; this keeps any that still surface as
            // a failure correctly bucketed rather than as a "crash".)
            || exceptionType.Contains("SocketException")
            || exceptionType.Contains("HttpRequestException")
            || exceptionType.Contains("EndOfStreamException")
        )
        {
            return "infrastructure";
        }

        return "crash";
    }

    private static string? ErrorPreview(string? error)
    {
        if (string.IsNullOrEmpty(error))
        {
            return null;
        }

        var firstLine = error.Split('\n', 2)[0].Trim();
        return firstLine.Length > 120 ? firstLine[..117] + "..." : firstLine;
    }

    private static string ExtractMethodFromTestName(string testName)
    {
        // "ClassName.MethodName" or "ClassName.MethodName(params)"
        var paren = testName.IndexOf('(');
        var name = paren >= 0 ? testName[..paren] : testName;
        var dot = name.LastIndexOf('.');
        return dot >= 0 ? name[(dot + 1)..] : name;
    }

    // The failure-classification helpers above are used by TestBase when building
    // the per-test test_enrichment IPC event.

    /// <summary>
    /// Returns all test records carrying their terminal <see cref="TestOutcome"/> for
    /// use by <see cref="FlakinessTracker"/>. Never-dispatched tests have no record
    /// and therefore no entry here (by design — they cannot evidence flakiness).
    /// </summary>
    internal List<FlakinessTestResult> GetAllTestResults()
    {
        var results = new List<FlakinessTestResult>();
        lock (_testLock)
        {
            foreach (var (_, testsByClass) in _testsByCollection)
            {
                foreach (var (className, classTests) in testsByClass)
                {
                    foreach (var test in classTests)
                    {
                        results.Add(
                            new FlakinessTestResult(
                                ClassName: className,
                                TestName: test.Name,
                                Outcome: test.Outcome,
                                DurationMs: (long)(test.Duration?.TotalMilliseconds ?? 0),
                                // Same category the enrichment event carries: explicit
                                // stamp first, exception-type classification otherwise.
                                FailureCategory: test.Outcome == TestOutcome.Failed
                                    ? test.FailureCategory ?? ClassifyFailure(test.ExceptionType)
                                    : null,
                                Breakdown: test.Breakdown
                            )
                        );
                    }
                }
            }
        }
        return results;
    }

    internal record FlakinessTestResult(
        string ClassName,
        string TestName,
        TestOutcome Outcome,
        long DurationMs,
        string? FailureCategory,
        Infrastructure.TestPhaseBreakdown? Breakdown
    );

    #endregion
}
