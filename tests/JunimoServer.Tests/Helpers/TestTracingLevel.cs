namespace JunimoServer.Tests.Helpers;

/// <summary>
/// Tiered tracing levels for the E2E test harness, gated by the
/// <c>SDVD_TEST_TRACING</c> environment variable.
///
/// <para>
/// Default (when <c>SDVD_TEST_TRACING</c> is unset): <see cref="Full"/> — an
/// ad-hoc run is fully reconstructable from artifacts without a re-run.
/// Per-test attribution (the <c>X-Test-Id</c> header) is emitted at every
/// level, so throughput-sensitive contexts opt down explicitly without losing
/// it: CI pins <c>none</c> (see <c>.github/workflows/e2e-tests.yml</c>) for
/// parallel-suite throughput while every server event stays test-attributable.
/// </para>
///
/// <para>
/// <see cref="None"/> is the cheapest path: no <c>X-Request-Id</c>, no body
/// buffering. Cheap snapshot endpoints (<c>/players</c>, <c>/health</c>,
/// <c>/status</c>) skip body buffering and JSON re-parsing.
/// </para>
///
/// <para>
/// <see cref="Basic"/> opts into <c>X-Request-Id</c> for mutating verbs
/// (POST/PUT/PATCH/DELETE) so a debug session can correlate a write request
/// with the mod-side event timeline without paying the body-buffer cost on
/// every read poll.
/// </para>
///
/// <para>
/// <see cref="Full"/> adds X-Request-Id on every verb, (capped, scrubbed)
/// response-body capture, and <c>wait_started</c> emits. Used by <c>test-llm</c>
/// for AI-debug context capture and by flake-repro sessions where every
/// cross-process correlation matters.
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
    /// <summary>Cheapest. No body buffer, no request-id, no wait_started.</summary>
    None = 0,

    /// <summary>Adds <c>X-Request-Id</c> for mutating verbs (POST/PUT/PATCH/DELETE) only.</summary>
    Basic = 1,

    /// <summary>Richest: <c>respBody</c> capture, request-id on every verb, wait_started. The unset default.</summary>
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
            // Unset → Full: every server event gets a requestId and (capped,
            // scrubbed) response bodies are captured, so a failing ad-hoc run is
            // fully reconstructable from artifacts without a re-run. Per-test
            // attribution (X-Test-Id) is level-independent, so throughput-sensitive
            // contexts opt down explicitly (CI pins "none").
            null or "" => TestTracingLevel.Full,
            // A typo must not silently select the expensive tier.
            _ => throw new InvalidOperationException(
                $"SDVD_TEST_TRACING='{raw}' is not recognized; use none, basic, or full (unset = full)."
            ),
        };
    }
}
