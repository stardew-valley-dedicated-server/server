namespace JunimoServer.Tests.Helpers;

/// <summary>
/// Per-host limiter for concurrent Docker container starts on a single daemon.
/// Three priority bands gate the queue so a flood of best-effort client starts
/// can never starve a server start past its 120s <c>StartupTimeout</c>:
///
/// <list type="bullet">
/// <item><description><see cref="StartPriority.High"/> — server starts.</description></item>
/// <item><description><see cref="StartPriority.Normal"/> — on-demand client leases (a live test holds capacity and races the per-test timeout).</description></item>
/// <item><description><see cref="StartPriority.Low"/> — client pre-warm (best-effort; <c>PreWarmAsync</c> swallows failures).</description></item>
/// </list>
///
/// <para>
/// When a slot frees, the highest-priority waiter wins; equal-priority waiters
/// are FIFO by enqueue order. There is no preemption — an in-flight
/// <c>docker create+start</c> always finishes. A High waiter cuts the queue
/// but cannot evict a slot already held by a Low waiter.
/// </para>
///
/// <para>
/// One instance per <see cref="Infrastructure.DockerHost"/>; bounds are
/// independent across hosts because separate daemons share no resources.
/// Sized from each host's <c>concurrentStarts</c> JSON field. When omitted,
/// <c>SDVD_MAX_CONCURRENT_STARTS</c> wins if set; otherwise the default is
/// the host's own <c>serverSlots + clientSlots</c> — so a host that's been
/// sized for N concurrent containers can also start that many concurrently.
/// </para>
///
/// <para>
/// <b>Poison semantics</b>: a host-scoped <see cref="CancellationTokenSource"/>
/// is linked into every <see cref="WaitAsync"/>. When <see cref="DockerHost.Poison"/>
/// fires, all queued waiters return promptly with <see cref="OperationCanceledException"/>
/// instead of hanging until outer cancellation. This preserves the
/// <c>host_disconnected</c> cascade documented in
/// <c>.claude/rules/test-broker-invariants.md</c>.
/// </para>
/// </summary>
internal sealed class DockerStartLimiter : IDisposable
{
    private readonly object _lock = new();
    private readonly SortedSet<Waiter> _queue = new(WaiterComparer.Instance);
    private readonly CancellationTokenSource _poisonCts = new();
    private readonly string _hostId;
    private readonly int _maxConcurrent;

    // Queue depth as a racy-but-pure read for the diagnostic snapshot.
    // Updated under _lock; snapshot uses Volatile.Read so the wait-trace
    // emit can run outside the lock without throwing on a SortedSet
    // concurrent-mutation. Per .claude/rules/test-broker-invariants.md
    // "Diagnostic Snapshots".
    private int _queueDepth;
    private int _availableSlots;
    private long _nextSeq;

    public DockerStartLimiter(string hostId, int maxConcurrent)
    {
        _hostId = hostId;
        _maxConcurrent = maxConcurrent;
        _availableSlots = maxConcurrent;
    }

    public int MaxConcurrent => _maxConcurrent;

    public Task WaitAsync(StartPriority priority, CancellationToken ct)
    {
        return WaitTrace.RunAsync(
            WaitName.DockerStartLimiter_StartSlot,
            () => WaitCoreAsync(priority, ct),
            ct,
            snapshot: () =>
                new
                {
                    host_id = _hostId,
                    maxConcurrent = _maxConcurrent,
                    priority = priority.ToString(),
                    queueDepth = Volatile.Read(ref _queueDepth),
                }
        );
    }

    private async Task WaitCoreAsync(StartPriority priority, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Waiter waiter;
        lock (_lock)
        {
            if (_availableSlots > 0 && _queue.Count == 0)
            {
                _availableSlots--;
                return;
            }

            waiter = new Waiter(priority, Interlocked.Increment(ref _nextSeq));
            _queue.Add(waiter);
            Volatile.Write(ref _queueDepth, _queue.Count);
        }

        // Link caller ct + poison outside the lock; the cancellation callback
        // re-enters the lock to remove the waiter if still queued.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _poisonCts.Token);
        await using var _ = linked
            .Token.Register(() =>
            {
                bool removed;
                lock (_lock)
                {
                    removed = _queue.Remove(waiter);
                    if (removed)
                        Volatile.Write(ref _queueDepth, _queue.Count);
                }
                // If we removed the waiter, no Release will reach this Tcs — we
                // own the cancellation. If not removed, a Release granted the
                // slot first; let the granted path proceed (TrySetCanceled would
                // race a TrySetResult and the latter wins, which is correct).
                if (removed)
                    waiter.Tcs.TrySetCanceled(linked.Token);
            })
            .ConfigureAwait(false);

        await waiter.Tcs.Task.ConfigureAwait(false);
    }

    public void Release()
    {
        Waiter? next = null;
        lock (_lock)
        {
            if (_queue.Count > 0)
            {
                next = _queue.Min;
                _queue.Remove(next!);
                Volatile.Write(ref _queueDepth, _queue.Count);
            }
            else
            {
                _availableSlots++;
                if (_availableSlots > _maxConcurrent)
                    throw new InvalidOperationException(
                        $"DockerStartLimiter[{_hostId}] released more slots than acquired."
                    );
            }
        }
        // Hand the slot directly to the next waiter without bumping
        // _availableSlots — preserves the invariant that available + held = max.
        next?.Tcs.TrySetResult();
    }

    /// <summary>
    /// Cancels every queued waiter, unblocking them with
    /// <see cref="OperationCanceledException"/>. Idempotent. Called from
    /// <see cref="DockerHost.Poison"/>.
    /// </summary>
    public void CancelPending()
    {
        try
        {
            _poisonCts.Cancel();
        }
        catch (ObjectDisposedException)
        { /* already disposed */
        }
    }

    public void Dispose()
    {
        try
        {
            _poisonCts.Cancel();
        }
        catch (ObjectDisposedException)
        { /* already disposed */
        }
        _poisonCts.Dispose();
    }

    private sealed class Waiter
    {
        public Waiter(StartPriority priority, long sequence)
        {
            Priority = priority;
            Sequence = sequence;
            Tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public StartPriority Priority { get; }
        public long Sequence { get; }
        public TaskCompletionSource Tcs { get; }
    }

    private sealed class WaiterComparer : IComparer<Waiter>
    {
        public static readonly WaiterComparer Instance = new();

        public int Compare(Waiter? x, Waiter? y)
        {
            if (ReferenceEquals(x, y))
                return 0;
            if (x is null)
                return -1;
            if (y is null)
                return 1;
            // Lower priority value wins (High=0, Normal=1, Low=2).
            var p = ((int)x.Priority).CompareTo((int)y.Priority);
            if (p != 0)
                return p;
            return x.Sequence.CompareTo(y.Sequence);
        }
    }
}

/// <summary>
/// Priority bands for <see cref="DockerStartLimiter.WaitAsync"/>. Lower numeric
/// value cuts higher in the queue.
/// </summary>
internal enum StartPriority
{
    High = 0,
    Normal = 1,
    Low = 2,
}
