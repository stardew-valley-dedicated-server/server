using JunimoServer.Tests.Helpers;

namespace JunimoServer.Tests.Infrastructure;

/// <summary>
/// TCS-based readiness gate for server availability. Multiple tests can safely
/// await the same Task (it's multi-awaitable). The creation path resolves the TCS
/// when at least one server instance is ready; tests then pick an instance via
/// <see cref="ServerPool.TryGetBest"/>. No server reference is carried by the queue.
/// </summary>
internal sealed class ServerQueue
{
    private volatile TaskCompletionSource _tcs = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );

    /// <summary>
    /// Whether at least one server instance is ready (TCS resolved successfully).
    /// </summary>
    public bool IsReady => _tcs.Task.IsCompletedSuccessfully;

    /// <summary>
    /// Whether all server creation attempts failed.
    /// </summary>
    public bool IsFaulted => _tcs.Task.IsFaulted;

    /// <summary>
    /// Awaits until at least one server instance is ready.
    /// Cancellation only affects this waiter; other waiters on the same
    /// queue are not impacted.
    /// </summary>
    public Task WaitUntilReadyAsync(CancellationToken ct) =>
        WaitTrace.RunAsync(
            WaitName.ServerQueue_WaitUntilReady,
            () => WaitUntilReadyCoreAsync(ct),
            ct,
            snapshot: () => new { isReady = IsReady, isFaulted = IsFaulted }
        );

    private async Task WaitUntilReadyCoreAsync(CancellationToken ct)
    {
        var tcs = _tcs;
        if (tcs.Task.IsCompletedSuccessfully)
        {
            return;
        }

        // Per-waiter TCS so cancellation doesn't nuke the shared TCS.
        var waiterTcs = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        using var reg = ct.Register(() => waiterTcs.TrySetCanceled(ct));

        _ = tcs.Task.ContinueWith(
            t =>
            {
                if (t.IsCompletedSuccessfully)
                {
                    waiterTcs.TrySetResult();
                }
                else if (t.IsFaulted)
                {
                    waiterTcs.TrySetException(t.Exception!.InnerExceptions);
                }
                else
                {
                    waiterTcs.TrySetCanceled();
                }
            },
            TaskScheduler.Default
        );

        await waiterTcs.Task;
    }

    /// <summary>
    /// Signals that at least one server instance is ready. Wakes all waiters.
    /// Idempotent: calling after a prior success is a no-op.
    /// Handles the case where a sibling instance faulted the queue first by
    /// resetting and re-signaling.
    /// </summary>
    public void ServerReady()
    {
        if (!_tcs.TrySetResult())
        {
            // Queue was faulted by a sibling instance that failed first; reset and signal
            if (_tcs.Task.IsFaulted)
            {
                _tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _tcs.TrySetResult();
            }
            // If already succeeded (another sibling), no-op
        }
    }

    /// <summary>
    /// Propagates a creation failure to all waiters.
    /// </summary>
    public void ServerFailed(Exception ex)
    {
        _tcs.TrySetException(ex);
    }

    /// <summary>
    /// Resets the queue for server replacement (e.g., after poisoning).
    /// Creates a fresh TCS so new waiters block until the replacement is ready.
    /// Old waiters attached to the previous TCS via ContinueWith will NOT be
    /// explicitly woken; they rely on their per-waiter cancellation token for
    /// cleanup. This is safe because Reset is only called during poison replacement
    /// or eviction, and those paths call ServerReady/ServerFailed on the new TCS.
    /// </summary>
    public void Reset()
    {
        _tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
