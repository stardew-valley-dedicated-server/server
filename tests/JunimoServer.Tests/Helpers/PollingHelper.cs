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
    public static Task<bool> WaitUntilAsync(
        WaitName name,
        Func<Task<bool>> condition,
        TimeSpan timeout,
        TimeSpan? pollInterval = null,
        CancellationToken cancellationToken = default,
        Func<Task<object?>>? onTimeoutAsync = null)
    {
        return WaitTrace.RunAsync<bool>(
            name,
            () => WaitUntilCoreAsync(name, condition, timeout, pollInterval, cancellationToken, onTimeoutAsync),
            cancellationToken);
    }

    private static async Task<bool> WaitUntilCoreAsync(
        WaitName name,
        Func<Task<bool>> condition,
        TimeSpan timeout,
        TimeSpan? pollInterval,
        CancellationToken cancellationToken,
        Func<Task<object?>>? onTimeoutAsync)
    {
        var interval = pollInterval ?? TestTimings.FastPollInterval;
        var sw = Stopwatch.StartNew();
        Exception? lastException = null;
        var iterations = 0;
        var succeeded = false;
        var label = name.ToString();

        // Bracket the slot to the helper's lifetime so the wait_matched emit
        // attributes only to HTTP calls made by this helper's condition.
        using var _diagScope = HttpResponseDiagnostics.BeginScope();

        try
        {
            while (sw.Elapsed < timeout)
            {
                cancellationToken.ThrowIfCancellationRequested();
                iterations++;

                try
                {
                    if (await condition())
                    {
                        succeeded = true;
                        EmitWaitMatched(label);
                        return true;
                    }

                    lastException = null; // Condition ran successfully, just returned false
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    lastException = ex;
                }

                await Task.Delay(interval, cancellationToken);
            }

            // Surface the last exception if polling timed out due to repeated failures
            if (lastException != null)
                throw new TimeoutException(
                    FormattableString.Invariant($"Polling [{label}] timed out after {timeout.TotalSeconds:F1}s ({iterations} iterations). ") +
                    $"Last error: {lastException.Message}", lastException);

            return false;
        }
        finally
        {
            object? diagnostics = null;
            string? onTimeoutError = null;
            if (!succeeded && onTimeoutAsync != null)
            {
                (diagnostics, onTimeoutError) = await CollectTimeoutDiagnosticsAsync(onTimeoutAsync);
            }

            InfrastructureEventLog.Emit("poll_completed", new
            {
                label,
                succeeded,
                iterations,
                durationMs = sw.ElapsedMilliseconds,
                timeoutMs = (long)timeout.TotalMilliseconds,
                error = lastException?.Message,
                ctCancelled = cancellationToken.IsCancellationRequested,
                diagnostics,
                onTimeoutError
            });
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
        if (msAgo is not long ago) return;
        var producerTime = new InfrastructureEventLog.EventTime(
            DateTime.UtcNow - TimeSpan.FromMilliseconds(ago),
            RunMetadata.GetRunMs() - ago);
        InfrastructureEventLog.Emit("wait_matched",
            new { label, predicateChangedMsAgo = ago },
            eventTime: producerTime);
    }

    /// <summary>
    /// Polls until the async function returns a non-null result, or the timeout expires.
    /// Returns the result, or default if timed out.
    /// If the producer throws, the last exception is stored and re-examined on timeout.
    /// </summary>
    /// <param name="name">Wire-stable wait identifier for tracing.</param>
    /// <param name="onTimeoutAsync">See <see cref="WaitUntilAsync"/>.</param>
    public static Task<T?> WaitForResultAsync<T>(
        WaitName name,
        Func<Task<T?>> producer,
        TimeSpan timeout,
        TimeSpan? pollInterval = null,
        CancellationToken cancellationToken = default,
        Func<Task<object?>>? onTimeoutAsync = null) where T : class
    {
        return WaitTrace.RunAsync<T?>(
            name,
            () => WaitForResultCoreAsync<T>(name, producer, timeout, pollInterval, cancellationToken, onTimeoutAsync),
            cancellationToken);
    }

    private static async Task<T?> WaitForResultCoreAsync<T>(
        WaitName name,
        Func<Task<T?>> producer,
        TimeSpan timeout,
        TimeSpan? pollInterval,
        CancellationToken cancellationToken,
        Func<Task<object?>>? onTimeoutAsync) where T : class
    {
        var interval = pollInterval ?? TestTimings.FastPollInterval;
        var sw = Stopwatch.StartNew();
        Exception? lastException = null;
        var iterations = 0;
        var succeeded = false;
        var label = name.ToString();

        using var _diagScope = HttpResponseDiagnostics.BeginScope();

        try
        {
            while (sw.Elapsed < timeout)
            {
                cancellationToken.ThrowIfCancellationRequested();
                iterations++;

                try
                {
                    var result = await producer();
                    if (result != null)
                    {
                        succeeded = true;
                        EmitWaitMatched(label);
                        return result;
                    }

                    lastException = null;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    lastException = ex;
                }

                await Task.Delay(interval, cancellationToken);
            }

            if (lastException != null)
                throw new TimeoutException(
                    FormattableString.Invariant($"Polling [{label}] timed out after {timeout.TotalSeconds:F1}s ({iterations} iterations). ") +
                    $"Last error: {lastException.Message}", lastException);

            return default;
        }
        finally
        {
            object? diagnostics = null;
            string? onTimeoutError = null;
            if (!succeeded && onTimeoutAsync != null)
            {
                (diagnostics, onTimeoutError) = await CollectTimeoutDiagnosticsAsync(onTimeoutAsync);
            }

            InfrastructureEventLog.Emit("poll_completed", new
            {
                label,
                succeeded,
                iterations,
                durationMs = sw.ElapsedMilliseconds,
                timeoutMs = (long)timeout.TotalMilliseconds,
                error = lastException?.Message,
                ctCancelled = cancellationToken.IsCancellationRequested,
                diagnostics,
                onTimeoutError
            });
        }
    }

    /// <summary>
    /// Result of a single long-poll round-trip. <see cref="Matched"/> is the
    /// success bit; <see cref="Version"/> is the cursor the next round-trip
    /// should pass as <c>since=</c> — typically <c>response.Version</c> on a
    /// non-match (so the server doesn't return the same stale snapshot again),
    /// or the prior <c>since</c> value on a 408 / connection error (no newer
    /// version was observed). <see cref="LongPollAsync"/> only advances the
    /// internal cursor when this <see cref="Version"/> is greater than the
    /// current one, so passing <c>0</c> here is also safe and equivalent for
    /// any prior cursor &gt; 0.
    /// </summary>
    public readonly record struct LongPollResult(bool Matched, long Version);

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
    /// Scope: snapshot-cursor call sites only (<c>WaitForPlayerByIdAsync</c>,
    /// <c>/wait/farmhands</c> presence-only callers). Stateless-predicate
    /// sites (<c>/wait/health</c> cold-start retry) and partial-match sites
    /// that need out-of-snapshot data (<c>WaitForServerOnlineCoreAsync</c>,
    /// file-derived invite codes) emit <c>long_poll_completed</c> directly
    /// from a bespoke loop.
    /// </para>
    /// </summary>
    public static Task<bool> LongPollAsync(
        WaitName name,
        Func<long, TimeSpan, Task<LongPollResult>> condition,
        TimeSpan timeout,
        CancellationToken cancellationToken = default,
        Func<Task<object?>>? onTimeoutAsync = null)
    {
        return WaitTrace.RunAsync<bool>(
            name,
            () => LongPollCoreAsync(name, condition, timeout, cancellationToken, onTimeoutAsync),
            cancellationToken);
    }

    private static async Task<bool> LongPollCoreAsync(
        WaitName name,
        Func<long, TimeSpan, Task<LongPollResult>> condition,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        Func<Task<object?>>? onTimeoutAsync)
    {
        var sw = Stopwatch.StartNew();
        Exception? lastException = null;
        var iterations = 0;
        var succeeded = false;
        long since = 0;
        long? snapshotVersionAtMatch = null;
        var label = name.ToString();

        using var _diagScope = HttpResponseDiagnostics.BeginScope();

        try
        {
            while (true)
            {
                var remaining = timeout - sw.Elapsed;
                if (remaining <= TimeSpan.Zero) break;

                cancellationToken.ThrowIfCancellationRequested();
                iterations++;

                try
                {
                    var result = await condition(since, remaining);
                    if (result.Matched)
                    {
                        succeeded = true;
                        snapshotVersionAtMatch = result.Version;
                        EmitWaitMatched(label);
                        return true;
                    }

                    lastException = null;
                    // Advance the cursor on a non-match so the server doesn't
                    // return the same stale snapshot on the next round-trip.
                    // 408 responses with no observed version leave Version
                    // unchanged from `since`, so this guard is a no-op there.
                    if (result.Version > since) since = result.Version;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    lastException = ex;
                }
            }

            if (lastException != null)
                throw new TimeoutException(
                    FormattableString.Invariant($"Long-poll [{label}] timed out after {timeout.TotalSeconds:F1}s ({iterations} iterations). ") +
                    $"Last error: {lastException.Message}", lastException);

            return false;
        }
        finally
        {
            object? diagnostics = null;
            string? onTimeoutError = null;
            if (!succeeded && onTimeoutAsync != null)
            {
                (diagnostics, onTimeoutError) = await CollectTimeoutDiagnosticsAsync(onTimeoutAsync);
            }

            InfrastructureEventLog.Emit("long_poll_completed", new
            {
                label,
                succeeded,
                iterations,
                durationMs = sw.ElapsedMilliseconds,
                timeoutMs = (long)timeout.TotalMilliseconds,
                snapshotVersionAtMatch,
                error = lastException?.Message,
                ctCancelled = cancellationToken.IsCancellationRequested,
                diagnostics,
                onTimeoutError
            });
        }
    }

    /// <summary>
    /// Runs an optional on-timeout diagnostic collector under a hard deadline
    /// so a broken or slow collector cannot extend the test's failure path.
    /// Returns <c>(diagnostics, errorMessage)</c> with exactly one non-null.
    /// </summary>
    private static async Task<(object? diagnostics, string? onTimeoutError)> CollectTimeoutDiagnosticsAsync(
        Func<Task<object?>> onTimeoutAsync)
    {
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var task = onTimeoutAsync();
            var done = await Task.WhenAny(task, Task.Delay(Timeout.InfiniteTimeSpan, deadline.Token));
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
