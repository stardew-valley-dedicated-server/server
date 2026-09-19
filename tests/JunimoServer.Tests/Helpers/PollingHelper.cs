using System.Diagnostics;
using JunimoServer.Tests.Clients;

namespace JunimoServer.Tests.Helpers;

/// <summary>
/// Wait primitives for the harness: client-throttled snapshot polls and server-blocking
/// long-polls, one loop behind both, traced through <see cref="WaitTrace"/>.
/// </summary>
public static class PollingHelper
{
    /// <summary>
    /// Polls until <paramref name="condition"/> returns true or <paramref name="timeout"/>
    /// expires. Returns whether the condition was met.
    /// </summary>
    /// <param name="name">Wire-stable wait identifier. The wait emits the <see cref="WaitTrace"/>
    /// envelope plus one <c>poll_completed</c> event in <c>finally</c> with cumulative
    /// <c>iterations</c> and <c>durationMs</c>.</param>
    /// <param name="onTimeoutAsync">Diagnostic collector run once on a deadline timeout, never
    /// on an exception or cancellation. Its result lands on <c>poll_completed</c> as
    /// <c>diagnostics</c>; a throw or a 2s overrun is reported as <c>onTimeoutError</c>
    /// instead.</param>
    /// <param name="isRetryable">Decides what a throwing <paramref name="condition"/> means.
    /// <c>null</c>: every exception is retried to the deadline and the last one surfaces
    /// wrapped in a <see cref="TimeoutException"/>. A filter: accepted faults (expected
    /// transport blips) count as a non-match; anything rejected propagates at once.</param>
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
    /// One long-poll round trip. <see cref="Sequence"/> is the cursor the next round trip
    /// passes as <c>since=</c>: the response's sequence on a non-match, or the prior
    /// <c>since</c> on a 408 or transport fault. The loop only advances its cursor when
    /// this value is greater than the current one.
    /// </summary>
    public readonly record struct LongPollResult(bool Matched, long Sequence);

    /// <summary>
    /// Long-poll variant of <see cref="WaitUntilAsync"/>. Each iteration calls a server
    /// <c>/wait/*</c> endpoint, which blocks until a matching snapshot exists or its 10s
    /// cap elapses; the loop re-issues until <paramref name="timeout"/>. No client-side
    /// poll interval: the server block is the throttle.
    ///
    /// <para>
    /// <paramref name="condition"/> receives the <c>since</c> cursor and the remaining
    /// outer budget. Pass the budget through as the request's <c>?timeout=</c>, or a
    /// first iteration that hits the server cap overshoots a sub-10s outer budget.
    /// </para>
    ///
    /// <para>
    /// Emits <c>long_poll_completed</c>, not <c>poll_completed</c>, once per wait with
    /// cumulative <c>iterations</c> and <c>durationMs</c>. Snapshot-cursor endpoints go
    /// through the <c>ServerApiClient</c> <c>WaitFor*Async</c> helpers, which add the
    /// request bound and transport filter; the stateless <c>/wait/health</c> site calls
    /// this directly.
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
    /// The one loop behind both public primitives. Returns
    /// <see cref="PollOutcome{T}.Value"/> on the first match, <c>default</c> on a clean
    /// timeout.
    ///
    /// <para>
    /// <paramref name="isRetryable"/> decides what a throwing <paramref name="step"/> means.
    /// With no filter, every exception except <see cref="OperationCanceledException"/> is
    /// stored, the loop retries to the deadline, and the last one surfaces wrapped in a
    /// <see cref="TimeoutException"/>. With a filter, an accepted fault counts as a non-match
    /// (clean <c>default</c> at the deadline, the last fault on the event's <c>error</c>
    /// field) and a rejected one, such as a deserialization failure, propagates at once
    /// instead of burning the budget behind a masking <see cref="TimeoutException"/>. A
    /// filter may accept <see cref="OperationCanceledException"/> to absorb a per-request
    /// timeout. Cancellation always wins: once <paramref name="cancellationToken"/> is
    /// cancelled, any exception surfaces as cancellation, even when the deadline expired on
    /// the same iteration.
    /// </para>
    /// </summary>
    /// <param name="longPoll">Snapshot mode (<c>false</c>) throttles each iteration with a
    /// client-side delay and emits <c>poll_completed</c>; long-poll mode relies on the server
    /// blocking the request, keeps a <c>since</c> cursor, and emits
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
                    // Checked before the deadline so a cancelled caller surfaces as a
                    // cancellation, not a clean timeout with a diagnostic dump attached.
                    cancellationToken.ThrowIfCancellationRequested();

                    var remaining = timeout - sw.Elapsed;
                    if (remaining <= TimeSpan.Zero)
                    {
                        deadlineExpired = true;
                        break;
                    }

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

                        lastException = null; // ran, no match
                        // Move past the snapshot just seen so the server doesn't return it
                        // again. A 408 reports Sequence == since, so this is a no-op there.
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
                        // Cancellation wins over the incidental fault (a reset from a torn-down
                        // server). Surface it here: the next iteration may break on an expired
                        // budget before its own check, and the deadline path would re-wrap the
                        // stored fault as a masking TimeoutException.
                        cancellationToken.ThrowIfCancellationRequested();
                        lastException = ex;
                        if (longPoll)
                        {
                            // A fault returns at once instead of blocking server-side; throttle
                            // so an unreachable server isn't hot-looped.
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
            // Only a deadline timeout runs the collector. A fail-fast exception or a
            // cancellation also passes here unsucceeded; dumping then would delay the real
            // error and emit timeout-shaped diagnostics.
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
    /// Emits <c>wait_matched</c> with envelope <c>ts</c>/<c>runMs</c> at the
    /// predicate-transition instant on the server's clock, taken from the
    /// <c>X-Predicate-Changed-At-Ms-Ago</c> header each <c>/wait/*</c> match carries.
    /// Sharper than the snapshot capture time, which is gated to the 1Hz publish cadence
    /// and can lag the field change by up to 1s.
    ///
    /// <para>
    /// Skipped when the matched response carried no header (a sequence-only
    /// <c>/wait/players</c> with no <c>playerId</c> filter, or test-client-mod endpoints):
    /// the producer instant is unknown, and observer time would mix two clock regimes
    /// under one event name.
    /// </para>
    ///
    /// <para>
    /// Call at success before returning, so the producer event lands before any
    /// consequence the caller emits. Requires an active
    /// <see cref="HttpResponseDiagnostics.BeginScope"/>.
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
    /// Runs the collector under a 2s deadline so a broken or slow collector cannot extend
    /// the failure path. Exactly one of the two results is non-null.
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
