using JunimoServer.Tests.Fixtures;
using JunimoServer.Tests.Helpers;
using JunimoServer.Tests.Schema.Events;

namespace JunimoServer.Tests.Infrastructure.Fixture;

/// <summary>
/// Owns per-test outcome reporting: records dispatch / failure / cancellation
/// against <see cref="TestSummaryFixture"/>, emits the <c>test_error</c> event
/// for failures, and emits the per-test <c>test_enrichment</c> IPC event at
/// the end of every test (passed, failed, or canceled). Sole caller of
/// <see cref="TestSummaryFixture.MarkDispatched"/> /
/// <see cref="TestSummaryFixture.MarkFailed"/> /
/// <see cref="TestSummaryFixture.MarkCanceled"/> within the TestBase pipeline.
/// </summary>
internal sealed class TestFailureReporter
{
    private readonly TestBase _testBase;
    private readonly string _displayName;

    public TestFailureReporter(TestBase testBase, string displayName)
    {
        _testBase = testBase;
        _displayName = displayName;
    }

    internal void RecordDispatched(string collectionName, string className)
    {
        TestSummaryFixture.Instance?.MarkDispatched(collectionName, className, _displayName);
    }

    internal void RecordFailure(string collectionName, string className,
        string error, string? phase, string? screenshotPath,
        string? serverKey, string? serverInstanceId, string? exceptionType,
        string? failureCategory = null)
    {
        TestSummaryFixture.Instance?.MarkFailed(
            collectionName, className, _displayName, error, phase, screenshotPath,
            serverKey, serverInstanceId, exceptionType, failureCategory);
        InfrastructureEventLog.Emit("test_error", new
        {
            phase,
            error,
            screenshotPath,
        });
    }

    internal void RecordCancellation(string collectionName, string className)
    {
        TestSummaryFixture.Instance?.MarkCanceled(collectionName, className, _displayName);
    }

    /// <summary>
    /// Builds and emits the <c>test_enrichment</c> IPC event for the current
    /// test. The runner correlates by displayName and patches the existing
    /// TestSnapshot. The xUnit-native test_passed/failed event remains the
    /// primary outcome carrier; this enrichment carries failure
    /// category/phase/repro, server context, and the lifecycle phase
    /// breakdown. Reads <see cref="FailureContext.LatestForCurrentTest"/>
    /// from the test-execution AsyncLocal (safe inside the test's
    /// DisposeAsync — see asynclocal-pitfalls.md).
    /// </summary>
    internal void EmitEnrichment(
        string collectionName,
        string className,
        string? serverKey,
        string? serverInstanceId,
        TestPhaseBreakdown? breakdown,
        TimeSpan testBodyDuration,
        TimeSpan artifactsDuration,
        TimeSpan cleanupDuration,
        long lastKeepDisposeMs,
        long leaseReleaseMs)
    {
        var snap = TestSummaryFixture.Instance?.GetEnrichmentSnapshot(
            collectionName, className, _displayName);
        var outcome = snap?.Outcome switch
        {
            TestSummaryFixture.TestOutcome.Failed => "failed",
            TestSummaryFixture.TestOutcome.Canceled => "canceled",
            _ => "passed", // Running here means the dispatcher promoted to Passed via MarkCompleted
        };
        // The record's stamped category (host-poison stamp) wins over the
        // exception-type classification.
        var failureCategory = snap?.Outcome == TestSummaryFixture.TestOutcome.Failed
            ? snap.FailureCategory ?? TestSummaryFixture.ClassifyFailureCategory(snap.ExceptionType)
            : null;
        SetupEventBus.EmitTestEnrichment(_displayName, new TestEnrichmentData(
            Outcome: outcome,
            FailureCategory: failureCategory,
            ErrorPreview: TestSummaryFixture.BuildErrorPreview(snap?.Error),
            Phase: snap?.Phase,
            ReproCommand: TestSummaryFixture.BuildReproCommand(_displayName),
            ServerKey: snap?.ServerKey ?? serverKey,
            ServerInstanceId: snap?.ServerInstanceId ?? serverInstanceId,
            ScreenshotPath: snap?.ScreenshotPath,
            // Breakdown is null if the inner try threw before the breakdown is
            // populated. Use zeros for the timing fields so failure metadata
            // still surfaces.
            TestBodyMs: breakdown?.TestBodyMs ?? (long)testBodyDuration.TotalMilliseconds,
            ArtifactsMs: breakdown?.ArtifactsMs ?? (long)artifactsDuration.TotalMilliseconds,
            CleanupMs: (long)cleanupDuration.TotalMilliseconds,
            LastKeepDisposeMs: breakdown?.LastKeepDisposeMs ?? lastKeepDisposeMs,
            LeaseReleaseMs: breakdown?.LeaseReleaseMs ?? leaseReleaseMs,
            FailureContext: FailureContext.LatestForCurrentTest));
    }
}
