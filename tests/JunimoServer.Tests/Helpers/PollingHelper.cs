using System.Diagnostics;
using JunimoServer.Tests.Clients;

namespace JunimoServer.Tests.Helpers;

/// <summary>
/// Provides tight polling loops that replace fixed delays.
/// Polls a condition at short intervals, returning as soon as it's met.
/// Falls back to the timeout if the condition is never satisfied.
/// </summary>
public static class PollingHelper
{
    /// <summary>
    /// Polls until the condition returns true, or the timeout expires.
    /// Returns true if the condition was met, false if timed out.
    /// If the condition throws, the last exception is stored and re-examined on timeout.
    /// </summary>
    /// <param name="name">Wire-stable wait identifier for tracing. The poll lifetime emits
    /// <c>wait_started</c>/<c>wait_completed</c>/<c>wait_cancelled</c>/<c>wait_failed</c>
    /// via <see cref="WaitTrace"/> in addition to a single <c>poll_completed</c>
    /// event emitted in <c>finally</c> with cumulative <c>iterations</c> and
    /// <c>durationMs</c> across the whole outer wait.</param>
    /// <param name="onTimeoutAsync">Optional diagnostic collector invoked exactly once when
    /// the poll times out (no exception path). The returned object is attached to the emitted
    /// <c>poll_completed</c> event under <c>diagnostics</c>. Exceptions from the collector
    /// are swallowed and replaced with an <c>onTimeoutError</c> field. Short-circuited by a
    /// 2-second internal deadline so a broken collector cannot hang the test.</param>
    /// <param name="isRetryable">See <see cref="PollCoreAsync{T}"/>. Leave <c>null</c> for a
    /// <paramref name="condition"/> whose faults are all unexpected: every exception is retried
    /// to the deadline and the last one surfaces wrapped. Pass a filter when the condition makes
    /// network calls whose transport blips are expected: those count as a non-match, and any
    /// other exception is a genuine bug that surfaces at once.</param>
    public static Task<bool> WaitUntilAsync(
        WaitName name,
        Func<Task<bool>> condition,
        TimeSpan timeout,
        TimeSpan? pollInterval = null,
        CancellationToken cancellationToken = default,
        Func<Task<object?>>? onTimeoutAsync = null,
        Func<Exception, bool>? isRetryable = null
    )
    {
        return WaitTrace.RunAsync<bool>(
            name,
            () =>
                PollCoreAsync<bool>(
                    name,
                    async (_, _) =>
                    {
                        var matched = await condition();
                        return new PollOutcome<bool>(matched, matched, 0);
                    },
                    timeout,
                    longPoll: false,
                    pollInterval,
                    cancellationToken,
                    onTimeoutAsync,
                    isRetryable
                ),
            cancellationToken
        );
    }

    /// <summary>
    /// Polls until the async function returns a non-null result, or the timeout expires.
    /// Returns the result, or default if timed out.
    /// If the producer throws, the last exception is stored and re-examined on timeout.
    /// </summary>
    /// <param name="name">Wire-stable wait identifier for tracing.</param>
    /// <param name="onTimeoutAsync">See <see cref="WaitUntilAsync"/>.</param>
    /// <param name="isRetryable">See <see cref="WaitUntilAsync"/>.</param>
    public static Task<T?> WaitForResultAsync<T>(
        WaitName name,
        Func<Task<T?>> producer,
        TimeSpan timeout,
        TimeSpan? pollInterval = null,
        CancellationToken cancellationToken = default,
        Func<Task<object?>>? onTimeoutAsync = null,
        Func<Exception, bool>? isRetryable = null
    )
        where T : class
    {
        return WaitTrace.RunAsync<T?>(
            name,
            () =>
                PollCoreAsync<T>(
                    name,
                    async (_, _) =>
                    {
                        var result = await producer();
                        return new PollOutcome<T>(result != null, result, 0);
                    },
                    timeout,
                    longPoll: false,
                    pollInterval,
                    cancellationToken,
                    onTimeoutAsync,
                    isRetryable
                ),
            cancellationToken
        );
    }

    /// <summary>
    /// Result of a single long-poll round-trip. <see cref="Matched"/> is the
    /// success bit; <see cref="Sequence"/> is the cursor the next round-trip
    /// should pass as <c>since=</c> — typically <c>response.Sequence</c> on a
    /// non-match (so the server doesn't return the same stale snapshot again),
    /// or the prior <c>since</c> value on a 408 / connection error (no newer
    /// sequence was observed). <see cref="LongPollAsync"/> only advances the
    /// internal cursor when this <see cref="Sequence"/> is greater than the
    /// current one, so passing <c>0</c> here is also safe and equivalent for
    /// any prior cursor &gt; 0.
    /// </summary>
    public readonly record struct LongPollResult(bool Matched, long Sequence);

    /// <summary>
    /// Long-poll variant of <see cref="WaitUntilAsync"/>. Each iteration calls
    /// the server's <c>/wait/*</c> endpoint, which blocks until either a
    /// matching condition holds or the server's hard cap (10 s) elapses; the
    /// outer loop here just re-issues until <paramref name="timeout"/> is
    /// reached. There is no <c>pollInterval</c> — server-side blocking
    /// replaces client-side throttling.
    ///
    /// <para>
    /// Emits <c>long_poll_completed</c> (NOT <c>poll_completed</c>) once per
    /// outer wait in <c>finally</c>, with cumulative <c>iterations</c> and
    /// <c>durationMs</c> across all round-trips. Wraps in
    /// <see cref="WaitTrace.RunAsync{T}"/> so the standard
    /// <c>wait_started</c>/<c>wait_completed</c>/etc. envelope still fires.
    /// </para>
    ///
    /// <para>
    /// The <paramref name="condition"/> receives the current <c>since</c>
    /// cursor and the outer-loop's remaining budget, and returns a
    /// <see cref="LongPollResult"/>. Pass the remaining budget through to the
    /// underlying <c>/wait/*</c> request as <c>?timeout=</c> so a server-side
    /// 10 s blocking call can't overshoot a smaller outer budget — without
    /// this, callers with <c>timeout &lt; 10 s</c> (e.g. <c>TimePausedVerification</c>
    /// = 2 s) bear up to a 5× overshoot when the first iteration hits the
    /// server's hard cap.
    /// </para>
    ///
    /// <para>
    /// Scope: snapshot-cursor endpoints go through <c>ServerApiClient</c>'s
    /// <c>WaitFor*Async</c> helpers (<c>WaitForStatusMatchAsync</c>,
    /// <c>WaitForPlayerByIdAsync</c>, <c>WaitForFarmhandByNameAsync</c>), which
    /// add the request bound and transport filter; the stateless
    /// <c>/wait/health</c> site calls this directly. Loops with their own
    /// failure semantics (<c>WaitForServerOnlineCoreAsync</c>, the
    /// <c>DayChangeWaiter</c> day loop) emit <c>long_poll_completed</c> from a
    /// bespoke loop.
    /// </para>
    /// </summary>
    /// <param name="isRetryable">See <see cref="WaitUntilAsync"/>.</param>
    public static Task<bool> LongPollAsync(
        WaitName name,
        Func<long, TimeSpan, Task<LongPollResult>> condition,
        TimeSpan timeout,
        CancellationToken cancellationToken = default,
        Func<Task<object?>>? onTimeoutAsync = null,
        Func<Exception, bool>? isRetryable = null
    )
    {
        return WaitTrace.RunAsync<bool>(
            name,
            () =>
                PollCoreAsync<bool>(
                    name,
                    async (since, remaining) =>
                    {
                        var result = await condition(since, remaining);
                        return new PollOutcome<bool>(
                            result.Matched,
                            result.Matched,
                            result.Sequence
                        );
                    },
                    timeout,
                    longPoll: true,
                    pollInterval: null,
                    cancellationToken,
                    onTimeoutAsync,
                    isRetryable
                ),
            cancellationToken
        );
    }

    /// <summary>
    /// One iteration's outcome. <see cref="Value"/> is what the outer wait
    /// returns on a match; <see cref="Sequence"/> is the snapshot cursor the
    /// next iteration receives as <c>since</c> (always 0 in snapshot mode).
    /// </summary>
    private readonly record struct PollOutcome<T>(bool Matched, T? Value, long Sequence);

    /// <summary>
    /// The one poll loop behind the three public primitives. Returns
    /// <see cref="PollOutcome{T}.Value"/> on the first match and
    /// <c>default</c> on a clean timeout.
    ///
    /// <para>
    /// <paramref name="isRetryable"/> decides what a throwing <paramref name="step"/> means.
    /// With no filter, every exception except <see cref="OperationCanceledException"/> is
    /// stored and the loop retries until the timeout, then surfaces the last one wrapped in a
    /// <see cref="TimeoutException"/> — right for a step whose faults are all unexpected. With
    /// a filter, a fault it accepts is an expected transport blip and counts as a non-match
    /// (the wait ends with a clean <c>default</c> at the deadline, the last blip on the event's
    /// <c>error</c> field), while anything it rejects — e.g. a deserialization failure — is a
    /// genuine bug that propagates at once instead of burning the whole budget behind a
    /// masking <see cref="TimeoutException"/>. A filter may accept
    /// <see cref="OperationCanceledException"/> to absorb a per-request timeout.
    /// Cancellation always wins: while <paramref name="cancellationToken"/> is cancelled, an
    /// incidental exception surfaces as a clean cancellation instead of transport noise from a
    /// torn-down server — even when the deadline expired on the same iteration.
    /// </para>
    /// </summary>
    /// <param name="longPoll">Snapshot mode (<c>false</c>) throttles each iteration with a
    /// client-side <c>Task.Delay</c> and emits <c>poll_completed</c>; long-poll mode relies on
    /// the server blocking the request, keeps a <c>since</c> cursor, and emits
    /// <c>long_poll_completed</c> with <c>snapshotSequenceAtMatch</c>.</param>
    private static async Task<T?> PollCoreAsync<T>(
        WaitName name,
        Func<long, TimeSpan, Task<PollOutcome<T>>> step,
        TimeSpan timeout,
        bool longPoll,
        TimeSpan? pollInterval,
        CancellationToken cancellationToken,
        Func<Task<object?>>? onTimeoutAsync,
        Func<Exception, bool>? isRetryable
    )
    {
        var interval = pollInterval ?? TestTimings.FastPollInterval;
        var sw = Stopwatch.StartNew();
        Exception? lastException = null;
        var iterations = 0;
        var succeeded = false;
        var deadlineExpired = false;
        long since = 0;
        long? snapshotSequenceAtMatch = null;
        var label = name.ToString();

        // Bracket the slot to the helper's lifetime so the wait_matched emit
        // attributes only to HTTP calls made by this helper's condition.
        using var _diagScope = HttpResponseDiagnostics.BeginScope();

        try
        {
            try
            {
                while (true)
                {
                    var remaining = timeout - sw.Elapsed;
                    if (remaining <= TimeSpan.Zero)
                    {
                        deadlineExpired = true;
                        break;
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    iterations++;

                    try
                    {
                        var outcome = await step(since, remaining);
                        if (outcome.Matched)
                        {
                            succeeded = true;
                            snapshotSequenceAtMatch = outcome.Sequence;
                            EmitWaitMatched(label);
                            return outcome.Value;
                        }

                        lastException = null; // Step ran successfully, just didn't match
                        // Advance the cursor on a non-match so the server doesn't
                        // return the same stale snapshot on the next round-trip.
                        // 408 responses with no observed sequence leave Sequence
                        // unchanged from `since`, so this guard is a no-op there.
                        if (outcome.Sequence > since)
                        {
                            since = outcome.Sequence;
                        }
                    }
                    catch (Exception ex)
                        when (cancellationToken.IsCancellationRequested
                            || (isRetryable?.Invoke(ex) ?? ex is not OperationCanceledException)
                        )
                    {
                        // Cancellation wins over the incidental fault: a reset from an in-flight
                        // request while the server is torn down must surface as a clean cancellation,
                        // not a stored exception the deadline break could later re-wrap as a masking
                        // TimeoutException. Surface it here rather than relying on the next loop, which
                        // may break on an expired budget before reaching ThrowIfCancellationRequested.
                        cancellationToken.ThrowIfCancellationRequested();
                        lastException = ex;
                        if (longPoll)
                        {
                            // The server block normally paces long-poll; a fault returns at once, so
                            // throttle here to keep an unreachable server from a hot loop.
                            await Task.Delay(interval, cancellationToken);
                        }
                    }

                    if (!longPoll)
                    {
                        await Task.Delay(interval, cancellationToken);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastException = ex; // the completion event names the fault that ended the wait
                throw;
            }

            // Without a filter, repeated faults are unexpected: surface the last one.
            if (lastException != null && isRetryable == null)
            {
                var kind = longPoll ? "Long-poll" : "Polling";
                throw new TimeoutException(
                    FormattableString.Invariant(
                        $"{kind} [{label}] timed out after {timeout.TotalSeconds:F1}s ({iterations} iterations). "
                    ) + $"Last error: {lastException.Message}",
                    lastException
                );
            }

            return default;
        }
        finally
        {
            object? diagnostics = null;
            string? onTimeoutError = null;
            // Only a genuine deadline timeout warrants the diagnostic collector. A fail-fast
            // exception (rejected by the filter) or a cancellation propagates through here with
            // succeeded == false but deadlineExpired == false — running onTimeoutAsync there
            // would delay the real error behind a 2s dump and emit timeout-shaped diagnostics.
            if (!succeeded && deadlineExpired && onTimeoutAsync != null)
            {
                (diagnostics, onTimeoutError) = await CollectTimeoutDiagnosticsAsync(
                    onTimeoutAsync
                );
            }

            var durationMs = sw.ElapsedMilliseconds;
            var timeoutMs = (long)timeout.TotalMilliseconds;
            var error = lastException?.Message;
            var ctCancelled = cancellationToken.IsCancellationRequested;
            if (longPoll)
            {
                InfrastructureEventLog.Emit(
                    "long_poll_completed",
                    new
                    {
                        label,
                        succeeded,
                        iterations,
                        durationMs,
                        timeoutMs,
                        snapshotSequenceAtMatch,
                        error,
                        ctCancelled,
                        diagnostics,
                        onTimeoutError,
                    }
                );
            }
            else
            {
                InfrastructureEventLog.Emit(
                    "poll_completed",
                    new
                    {
                        label,
                        succeeded,
                        iterations,
                        durationMs,
                        timeoutMs,
                        error,
                        ctCancelled,
                        diagnostics,
                        onTimeoutError,
                    }
                );
            }
        }
    }

    /// <summary>
    /// On a successful poll, emit <c>wait_matched</c> with envelope <c>ts</c>
    /// and <c>runMs</c> attributed to the predicate-transition instant on the
    /// server's clock (producer-time), not the harness observation instant.
    /// The server reports this via the
    /// <c>X-Predicate-Changed-At-Ms-Ago</c> response header on each
    /// <c>/wait/*</c> match — sharper than the snapshot's capture time, which
    /// is gated to the 1Hz snapshot publish cadence and can lag the actual
    /// tick of the field change by up to 1s.
    ///
    /// <para>
    /// Skipped when the matched response carried no
    /// <c>X-Predicate-Changed-At-Ms-Ago</c> header — happens for endpoints
    /// whose predicate has no associated field-change time (e.g. version-only
    /// `/wait/players` with no playerId filter, or test-client-mod endpoints
    /// that don't emit the header). The producer instant is unknown in those
    /// cases and observer-time would conflate the two clock regimes on one
    /// event name.
    /// </para>
    ///
    /// <para>
    /// Call immediately at success, before returning, so the producer event
    /// lands on disk before any consequence the caller emits. Caller must
    /// have an active <see cref="HttpResponseDiagnostics.BeginScope"/>.
    /// </para>
    /// </summary>
    internal static void EmitWaitMatched(string label)
    {
        var msAgo = HttpResponseDiagnostics.LastPredicateChangedMsAgo;
        if (msAgo is not long ago)
        {
            return;
        }

        var producerTime = new InfrastructureEventLog.EventTime(
            DateTime.UtcNow - TimeSpan.FromMilliseconds(ago),
            RunMetadata.GetRunMs() - ago
        );
        InfrastructureEventLog.Emit(
            "wait_matched",
            new { label, predicateChangedMsAgo = ago },
            eventTime: producerTime
        );
    }

    /// <summary>
    /// Runs an optional on-timeout diagnostic collector under a hard deadline
    /// so a broken or slow collector cannot extend the test's failure path.
    /// Returns <c>(diagnostics, errorMessage)</c> with exactly one non-null.
    /// </summary>
    private static async Task<(
        object? diagnostics,
        string? onTimeoutError
    )> CollectTimeoutDiagnosticsAsync(Func<Task<object?>> onTimeoutAsync)
    {
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var task = onTimeoutAsync();
            var done = await Task.WhenAny(
                task,
                Task.Delay(Timeout.InfiniteTimeSpan, deadline.Token)
            );
            if (done != task)
            {
                return (null, "onTimeoutAsync exceeded 2s deadline");
            }
            return (await task, null);
        }
        catch (Exception ex)
        {
            return (null, $"{ex.GetType().Name}: {ex.Message}");
        }
    }
}
