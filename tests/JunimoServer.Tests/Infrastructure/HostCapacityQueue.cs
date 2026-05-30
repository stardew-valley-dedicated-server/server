using JunimoServer.Tests.Helpers;

namespace JunimoServer.Tests.Infrastructure;

/// <summary>
/// Per-host priority queue for slot acquisition. Each <see cref="DockerHost"/>
/// owns one instance for server slots and another for client slots.
///
/// Load-bearing semantics (see <c>test-broker-invariants.md</c>):
/// <list type="bullet">
///   <item>(Priority ASC, Sequence ASC) ordering — high-priority test classes
///   from <c>TestCollectionOrderer</c> are served first.</item>
///   <item>100ms settle window during cold start so concurrently-arriving
///   waiters all enqueue before the first drain runs.</item>
///   <item>"Release-and-reacquire": atomically enqueue a high-priority
///   reacquire waiter, then release slots, then drain — used by exclusive
///   tests that must reclaim capacity before other tests can grab the freed
///   slots.</item>
///   <item>Snapshot fields are racy-but-safe: only <c>_available</c>,
///   <c>Capacity</c>, and the count of waiters are exposed; the
///   <c>SortedSet</c> itself is not exposed externally because it is mutated
///   under <c>_lock</c>.</item>
/// </list>
/// </summary>
internal sealed class HostCapacityQueue
{
    public string Name { get; }
    public int Capacity { get; }

    /// <summary>How long to wait after the first waiter enqueues before draining.</summary>
    private static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(100);

    private readonly object _lock = new();
    private int _available;
    private long _sequence;
    private readonly SortedSet<Waiter> _waiters = new();
    private bool _drainScheduled;
    private bool _steadyState;

    public HostCapacityQueue(string name, int capacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        Name = name;
        Capacity = capacity;
        _available = capacity;
    }

    /// <summary>Diagnostic snapshot — current available slot count.</summary>
    public int Available => Volatile.Read(ref _available);

    /// <summary>Diagnostic snapshot — current waiter count (racy).</summary>
    public int WaitingCount
    {
        get { lock (_lock) { return _waiters.Count; } }
    }

    /// <summary>
    /// Acquires <paramref name="count"/> slots, blocking if capacity is exhausted.
    /// Higher-priority (lower value) waiters are served first.
    /// </summary>
    public async Task AcquireAsync(int count, string testName, int priority, CancellationToken ct)
    {
        if (count <= 0) return;
        var shortName = TestLog.Short(testName);
        Waiter waiter;
        lock (_lock)
        {
            TestLog.Test($"{shortName} requesting {count} {Name} slot(s) (priority {priority}, {_available}/{Capacity} available)");

            waiter = new Waiter(priority, _sequence++, count, testName);
            _waiters.Add(waiter);
            TestLog.Test($"{shortName} queued (priority {priority}, position {GetQueuePosition(waiter)}/{_waiters.Count})");

            if (_available > 0 && !_drainScheduled)
            {
                _drainScheduled = true;
                _ = SettleAndDrainAsync();
            }
        }

        using var ctr = ct.Register(() =>
        {
            lock (_lock)
            {
                if (_waiters.Remove(waiter))
                {
                    TestLog.Test($"{TestLog.Short(waiter.TestName)} cancelled (priority {waiter.Priority})");
                    waiter.Tcs.TrySetCanceled(ct);
                }
            }
        });

        await WaitTrace.RunAsync(
            WaitName.ClientCapacity_Acquire,
            () => waiter.Tcs.Task,
            ct,
            snapshot: () => new
            {
                test = testName,
                count,
                priority,
                available = _available,
                max = Capacity,
                queue = Name
            });
        TestLog.Test($"{shortName} got {count} {Name} slot(s) ({_available}/{Capacity} available)");
        InfrastructureEventLog.Emit("capacity_acquired", new
        {
            test = testName, count, available = _available, max = Capacity, priority, queue = Name
        });
    }

    /// <summary>Default-priority overload (50, the project-wide default).</summary>
    public Task AcquireAsync(int count, string testName, CancellationToken ct) =>
        AcquireAsync(count, testName, 50, ct);

    /// <summary>Releases <paramref name="count"/> slots, draining waiters in priority order.</summary>
    public void Release(int count)
    {
        if (count <= 0) return;
        lock (_lock)
        {
            _available += count;
            if (_available > Capacity)
            {
                TestLog.Test($"WARNING: available ({_available}) exceeds {Name} capacity ({Capacity}), clamping (double-release bug?)");
                _available = Capacity;
            }
            TestLog.Test($"Released {count} {Name} slot(s) ({_available}/{Capacity} available)");
            InfrastructureEventLog.Emit("capacity_released", new
            {
                count, available = _available, max = Capacity, waiters = _waiters.Count, queue = Name
            });
            DrainQueue();
        }
    }

    /// <summary>
    /// Atomically enqueues a high-priority reacquire waiter THEN releases slots,
    /// so the reacquire is serviced ahead of any waiter that would block on the
    /// caller's gate.
    /// </summary>
    public async Task ReleaseAndReacquireAsync(int count, string testName, int priority, CancellationToken ct)
    {
        if (count <= 0) return;
        var shortName = TestLog.Short(testName);
        Waiter waiter;
        lock (_lock)
        {
            waiter = new Waiter(priority, _sequence++, count, testName);
            _waiters.Add(waiter);
            TestLog.Test($"{shortName} release-and-reacquire on {Name}: enqueued (priority {priority}), releasing {count} slot(s)");

            _available += count;
            if (_available > Capacity) _available = Capacity;
            DrainQueue();
        }

        using var ctr = ct.Register(() =>
        {
            lock (_lock)
            {
                if (_waiters.Remove(waiter))
                {
                    TestLog.Test($"{TestLog.Short(waiter.TestName)} reacquire cancelled (priority {waiter.Priority})");
                    waiter.Tcs.TrySetCanceled(ct);
                }
            }
        });

        await WaitTrace.RunAsync(
            WaitName.ClientCapacity_ReleaseAndReacquire,
            () => waiter.Tcs.Task,
            ct,
            snapshot: () => new
            {
                test = testName,
                count,
                priority,
                available = _available,
                max = Capacity,
                queue = Name
            });
        TestLog.Test($"{shortName} reacquired {count} {Name} slot(s) ({_available}/{Capacity} available)");
    }

    /// <summary>Fail-fast: throws if a test requires more slots than the cap allows.</summary>
    public void ValidateRequirements(int needed, string testName)
    {
        if (needed > Capacity)
        {
            throw new InvalidOperationException(
                $"Test '{testName}' requires {needed} {Name} slot(s) but capacity={Capacity} on this host.");
        }
    }

    private async Task SettleAndDrainAsync()
    {
        if (!_steadyState)
            await Task.Delay(SettleDelay);
        lock (_lock)
        {
            _drainScheduled = false;
            TestLog.Test($"Scheduling {_waiters.Count} queued test(s) ({_available}/{Capacity} {Name} slots available)");
            DrainQueue();
        }
    }

    private void DrainQueue()
    {
        while (_waiters.Count > 0)
        {
            var head = _waiters.Min!;
            if (_available < head.Count)
                break;
            _waiters.Remove(head);
            _available -= head.Count;
            TestLog.Test($"{TestLog.Short(head.TestName)} granted {head.Count} {Name} slot(s) (priority {head.Priority})");
            head.Tcs.TrySetResult();
        }

        if (!_steadyState && _available <= 0)
            _steadyState = true;
    }

    private int GetQueuePosition(Waiter waiter)
    {
        var pos = 0;
        foreach (var w in _waiters)
        {
            pos++;
            if (w == waiter) return pos;
        }
        return pos;
    }

    private sealed class Waiter : IComparable<Waiter>
    {
        public int Priority { get; }
        public long Sequence { get; }
        public int Count { get; }
        public string TestName { get; }
        public TaskCompletionSource Tcs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Waiter(int priority, long sequence, int count, string testName)
        {
            Priority = priority; Sequence = sequence; Count = count; TestName = testName;
        }

        public int CompareTo(Waiter? other)
        {
            if (other is null) return -1;
            var cmp = Priority.CompareTo(other.Priority);
            if (cmp != 0) return cmp;
            return Sequence.CompareTo(other.Sequence);
        }
    }
}
