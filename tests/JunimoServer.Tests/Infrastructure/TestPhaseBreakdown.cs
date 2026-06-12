namespace JunimoServer.Tests.Infrastructure;

/// <summary>
/// Phase-level breakdown of a test's active duration, for post-mortem drill-down.
/// Flows into test_completed events, ctrf-report.json test entries, and flakiness.jsonl
/// so consumers can separate test-body time from disposal taxes.
/// </summary>
/// <param name="TestBodyMs">Time from server acquisition to DisposeAsync entry.</param>
/// <param name="ArtifactsMs">Screenshot + video extraction phase.</param>
/// <param name="CleanupMs">Outer cleanup phase (includes RunCleanupAsync, lease release, and last-keep disposal).</param>
/// <param name="LastKeepDisposeMs">
/// Stopwatch around PersistentSessionStore.RemoveAndDisposeAsync for the last test in a KeepConnected class.
/// Zero for every other test. Lets consumers detect the "last-keep tax" that otherwise hides in CleanupMs.
/// </param>
/// <param name="LeaseReleaseMs">
/// Stopwatch around Lease.DisposeAsync on non-KeepConnected tests. Includes synchronous ManagedServer.DisposeAsync
/// on evicted configs (container teardown), which dominates the cleanup of the last test on a retiring config.
/// </param>
public record TestPhaseBreakdown(
    long TestBodyMs,
    long ArtifactsMs,
    long CleanupMs,
    long LastKeepDisposeMs,
    long LeaseReleaseMs
);
