namespace JunimoServer.Tests.Helpers;

/// <summary>
/// Tiered tracing levels for the E2E test harness, gated by the
/// <c>SDVD_TEST_TRACING</c> environment variable.
///
/// <para>
/// Default: <see cref="None"/>. Cheap snapshot endpoints (<c>/players</c>,
/// <c>/health</c>, <c>/status</c>) skip body buffering and JSON re-parsing.
/// Used by <c>test</c> / <c>test-ci</c> targets where throughput dominates
/// over diagnostic richness.
/// </para>
///
/// <para>
/// <see cref="Basic"/> opts back into <c>X-Request-Id</c> for mutating verbs
/// (POST/PUT/PATCH/DELETE) so a debug session can correlate a write request
/// with the mod-side event timeline without paying the body-buffer cost on
/// every read poll.
/// </para>
///
/// <para>
/// <see cref="Full"/> is today's behavior — body buffering, <c>respSummary</c>,
/// X-Request-Id on every verb, <c>wait_started</c> emits. Used by
/// <c>test-llm</c> for AI-debug context capture and by flake-repro sessions
/// where every cross-process correlation matters.
/// </para>
///
/// <para>
/// Failure-path events (<c>wait_failed</c>, <c>wait_cancelled</c>,
/// <c>poll_completed</c> carrying error / diagnostics, <c>failure_context</c>,
/// <c>recording_*</c>) emit at every level — the failure runbook works at
/// <see cref="None"/> as it does at <see cref="Full"/>.
/// </para>
/// </summary>
public enum TestTracingLevel
{
    /// <summary>Cheapest. No body buffer, no respSummary, no request-id, no wait_started.</summary>
    None = 0,

    /// <summary>Adds <c>X-Request-Id</c> for mutating verbs (POST/PUT/PATCH/DELETE) only.</summary>
    Basic = 1,

    /// <summary>All of today's tracing: body buffer, respSummary, request-id on every verb, wait_started.</summary>
    Full = 2,
}

/// <summary>
/// Static accessor for the process-wide tracing level. Reads
/// <c>SDVD_TEST_TRACING</c> once at first access; the runner / runsettings /
/// .env.test pass it through.
/// </summary>
public static class TestTracing
{
    private static readonly Lazy<TestTracingLevel> _level = new(Resolve);

    /// <summary>Resolved tracing level; immutable for the process lifetime.</summary>
    public static TestTracingLevel Level => _level.Value;

    private static TestTracingLevel Resolve()
    {
        var raw = Environment.GetEnvironmentVariable("SDVD_TEST_TRACING")?.Trim().ToLowerInvariant();
        return raw switch
        {
            null or "" or "none" => TestTracingLevel.None,
            "basic" => TestTracingLevel.Basic,
            "full" => TestTracingLevel.Full,
            _ => TestTracingLevel.None, // unknown values fall back to the cheapest path
        };
    }
}
