namespace JunimoServer.Tests.Helpers;

/// <summary>
/// In-method instrumentation pattern for blocking-wait primitives.
///
/// <para>
/// Wraps a wait body so the owning method emits a <c>wait</c> event with
/// <c>data.phase</c> set to <c>started</c> / <c>completed</c> / <c>cancelled</c>
/// / <c>failed</c> on <c>infrastructure.jsonl</c>. Tracing lives with the
/// resource, not the call site — callers see no API change.
/// </para>
///
/// <para>
/// <b>Snapshot delegate rules</b>: the optional <paramref name="snapshot"/>
/// runs at start AND completion. It MUST follow these rules:
/// <list type="bullet">
///   <item>No locks — never acquire a lock the wait path itself holds.</item>
///   <item>No reads of non-thread-safe collections without the appropriate lock
///   (<c>SortedSet&lt;T&gt;.Count</c>, <c>List&lt;T&gt;.Count</c> guarded by an
///   external lock, etc.). Use thread-safe sources (<c>ConcurrentBag&lt;T&gt;.Count</c>,
///   <c>Volatile.Read</c>, <c>volatile</c> fields) or plain value-type reads.</item>
///   <item>Racy-but-pure value-type reads (e.g. <c>_refCount</c>, <c>_available</c>)
///   are acceptable — worst case is a stale value in the log, never a crash.</item>
///   <item>No side effects — reading a property that increments a counter or
///   invalidates a cache is forbidden.</item>
/// </list>
/// Exceptions thrown by the snapshot delegate are swallowed and recorded as a
/// <c>snapshotError</c> field; they never break the wait.
/// </para>
///
/// <para>
/// <b>Pre-init waits</b>: events emitted before <see cref="InfrastructureEventLog.Initialize"/>
/// land in <c>infrastructure.jsonl</c> via the pre-init buffer.
/// </para>
/// </summary>
internal static class WaitTrace
{
    // wait_started events are Full-only — they're paired with wait_completed
    // and add ~10k events to a passing run for no diagnostic value beyond the
    // duration that wait_completed already carries. Failure phases
    // (cancelled, failed) emit at every level so the failure runbook works
    // unchanged at None/Basic.
    private static bool EmitStarted => TestTracing.Level == TestTracingLevel.Full;

    public static async Task RunAsync(
        WaitName name,
        Func<Task> body,
        CancellationToken ct,
        Func<object>? snapshot = null
    )
    {
        var startMs = RunMetadata.RunClock.ElapsedMilliseconds;
        var (snap, snapErr) = SafeSnapshot(snapshot);
        if (EmitStarted)
        {
            InfrastructureEventLog.EmitWait(name, WaitPhase.Started, null, snap, snapErr);
        }

        try
        {
            await body();
            var duration = RunMetadata.RunClock.ElapsedMilliseconds - startMs;
            (snap, snapErr) = SafeSnapshot(snapshot);
            InfrastructureEventLog.EmitWait(name, WaitPhase.Completed, duration, snap, snapErr);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Caller's token was canceled; classify as a clean cancellation.
            // We check `ct.IsCancellationRequested` rather than `oce.CancellationToken == ct`
            // because linked CTS sources can produce an OCE carrying the linked-source token,
            // not the original ct. The semantically correct check is "did the caller's token fire?"
            var duration = RunMetadata.RunClock.ElapsedMilliseconds - startMs;
            (snap, snapErr) = SafeSnapshot(snapshot);
            InfrastructureEventLog.EmitWait(name, WaitPhase.Cancelled, duration, snap, snapErr);
            throw;
        }
        catch (Exception ex)
        {
            var duration = RunMetadata.RunClock.ElapsedMilliseconds - startMs;
            (snap, snapErr) = SafeSnapshot(snapshot);
            InfrastructureEventLog.EmitWait(
                name,
                WaitPhase.Failed,
                duration,
                snap,
                snapErr,
                errorType: ex.GetType().FullName,
                errorMessage: ex.Message
            );
            throw;
        }
    }

    public static async Task<T> RunAsync<T>(
        WaitName name,
        Func<Task<T>> body,
        CancellationToken ct,
        Func<object>? snapshot = null
    )
    {
        var startMs = RunMetadata.RunClock.ElapsedMilliseconds;
        var (snap, snapErr) = SafeSnapshot(snapshot);
        if (EmitStarted)
        {
            InfrastructureEventLog.EmitWait(name, WaitPhase.Started, null, snap, snapErr);
        }

        try
        {
            var result = await body();
            var duration = RunMetadata.RunClock.ElapsedMilliseconds - startMs;
            (snap, snapErr) = SafeSnapshot(snapshot);
            InfrastructureEventLog.EmitWait(name, WaitPhase.Completed, duration, snap, snapErr);
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            var duration = RunMetadata.RunClock.ElapsedMilliseconds - startMs;
            (snap, snapErr) = SafeSnapshot(snapshot);
            InfrastructureEventLog.EmitWait(name, WaitPhase.Cancelled, duration, snap, snapErr);
            throw;
        }
        catch (Exception ex)
        {
            var duration = RunMetadata.RunClock.ElapsedMilliseconds - startMs;
            (snap, snapErr) = SafeSnapshot(snapshot);
            InfrastructureEventLog.EmitWait(
                name,
                WaitPhase.Failed,
                duration,
                snap,
                snapErr,
                errorType: ex.GetType().FullName,
                errorMessage: ex.Message
            );
            throw;
        }
    }

    private static (object? value, string? error) SafeSnapshot(Func<object>? f)
    {
        if (f == null)
        {
            return (null, null);
        }

        try
        {
            return (f(), null);
        }
        catch (Exception ex)
        {
            return (null, $"{ex.GetType().Name}: {ex.Message}");
        }
    }
}
