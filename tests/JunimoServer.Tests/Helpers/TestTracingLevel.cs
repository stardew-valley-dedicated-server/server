namespace JunimoServer.Tests.Helpers;

/// <summary>
/// Tiered tracing levels for the E2E test harness, gated by the
/// <c>SDVD_TEST_TRACING</c> environment variable.
///
/// <para>
/// Unset defaults to <see cref="Full"/> so an ad-hoc run is reconstructable
/// from its artifacts. Per-test attribution (the <c>X-Test-Id</c> header) is
/// sent at every level, so CI pins <c>none</c> for throughput without losing it.
/// </para>
///
/// <para>
/// <see cref="None"/>: no <c>X-Request-Id</c>, no response-body capture, no
/// <c>wait_started</c> events.
/// </para>
///
/// <para>
/// <see cref="Basic"/>: <c>X-Request-Id</c> on mutating verbs
/// (POST/PUT/PATCH/DELETE) so writes correlate with the mod-side event
/// timeline, without the body-capture cost on every read poll.
/// </para>
///
/// <para>
/// <see cref="Full"/>: <c>X-Request-Id</c> on every verb, capped and scrubbed
/// response-body capture, and <c>wait_started</c> events. Used by
/// <c>test-llm</c> and flake-repro sessions.
/// </para>
///
/// <para>
/// Failure-path events (<c>wait_failed</c>, <c>wait_cancelled</c>,
/// <c>poll_completed</c> carrying error / diagnostics, <c>failure_context</c>,
/// <c>recording_*</c>) emit at every level, so the failure runbook works at
/// <see cref="None"/> as it does at <see cref="Full"/>.
/// </para>
/// </summary>
public enum TestTracingLevel
{
    /// <summary>No request-id, no response-body capture, no wait_started.</summary>
    None = 0,

    /// <summary>Adds <c>X-Request-Id</c> for mutating verbs (POST/PUT/PATCH/DELETE) only.</summary>
    Basic = 1,

    /// <summary><c>respBody</c> capture, request-id on every verb, wait_started. The unset default.</summary>
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
        var raw = Environment
            .GetEnvironmentVariable("SDVD_TEST_TRACING")
            ?.Trim()
            .ToLowerInvariant();
        return raw switch
        {
            "none" => TestTracingLevel.None,
            "basic" => TestTracingLevel.Basic,
            "full" => TestTracingLevel.Full,
            null or "" => TestTracingLevel.Full,
            // A typo must not silently select a tier.
            _ => throw new InvalidOperationException(
                $"SDVD_TEST_TRACING='{raw}' is not recognized; use none, basic, or full (unset = full)."
            ),
        };
    }
}
