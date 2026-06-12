using System.Diagnostics;
using JunimoServer.Tests.Helpers;

namespace JunimoServer.Tests.Infrastructure;

/// <summary>
/// Thread-safe allocator for Steam account indices with separate server/client pools.
/// Account 0 is reserved for the server pool; accounts 1+ are the client pool. Ensures
/// no two containers use the same Steam account concurrently.
///
/// <para>
/// Allocation is async + queueing: a <see cref="SemaphoreSlim"/> per pool gates dequeue,
/// and the dequeue itself runs inside a lock so the dequeue + count snapshot used in
/// <c>steam_account_allocated</c> telemetry is atomic.
/// </para>
///
/// <para>
/// <b>Scope.</b> The allocator owns "this container has a Steam account index" — it
/// hands out indices to brand-new containers at create-time and reclaims them when the
/// container is torn down (discard / mark-dead / dispose). It is <i>not</i> the
/// lease-availability gate for the test path: that responsibility belongs to
/// <c>ClientPool</c>'s internal Steam-availability semaphore, which tracks "a Steam-bearing
/// client is available for the next Steam-required lease." A leased Steam-bearing client
/// keeps its account index across the lease boundary; only the lease-availability ticket
/// changes hands. Pool exhaustion of <i>this</i> allocator only blocks the cold-start
/// container-creation path, which fires at most once per Steam-bearing pool slot.
/// </para>
/// </summary>
public sealed class SteamAccountAllocator : ISteamAccountAllocator
{
    private readonly object _lock = new();
    private readonly Queue<int> _serverAccounts = new();
    private readonly Queue<int> _clientAccounts = new();
    private readonly SemaphoreSlim _serverSem;
    private readonly SemaphoreSlim _clientSem;
    private readonly int _totalAccounts;
    private readonly int _clientPoolSize;
    private readonly int _serverIndex;
    private readonly HashSet<int> _knownIndices;
    private readonly Func<int, CancellationToken, Task<bool>>? _readinessProbe;
    private static readonly TimeSpan AllocationReadinessBudget = TimeSpan.FromSeconds(90);

    public SteamAccountAllocator(
        int accountCount,
        Func<int, CancellationToken, Task<bool>>? readinessProbe = null
    )
    {
        _totalAccounts = accountCount;
        _clientPoolSize = accountCount >= 1 ? accountCount - 1 : 0;
        _serverIndex = 0;
        _knownIndices = new HashSet<int>();
        _readinessProbe = readinessProbe;

        // Account 0 → server pool, accounts 1+ → client pool
        if (accountCount >= 1)
        {
            _serverAccounts.Enqueue(0);
            _knownIndices.Add(0);
        }

        for (var i = 1; i < accountCount; i++)
        {
            _clientAccounts.Enqueue(i);
            _knownIndices.Add(i);
        }

        var serverPoolSize = accountCount >= 1 ? 1 : 0;
        _serverSem = new SemaphoreSlim(serverPoolSize, serverPoolSize);
        _clientSem = new SemaphoreSlim(_clientPoolSize, _clientPoolSize);
    }

    public bool HasClientAccounts => _clientPoolSize > 0;

    public int ClientPoolSize => _clientPoolSize;

    public Task<int> AllocateServerAsync(CancellationToken ct) =>
        AllocateAsync(
            _serverSem,
            _serverAccounts,
            kind: "server",
            poolSize: _totalAccounts >= 1 ? 1 : 0,
            ct
        );

    public Task<int> AllocateClientAsync(CancellationToken ct) =>
        AllocateAsync(_clientSem, _clientAccounts, kind: "client", poolSize: _clientPoolSize, ct);

    private async Task<int> AllocateAsync(
        SemaphoreSlim sem,
        Queue<int> queue,
        string kind,
        int poolSize,
        CancellationToken ct
    )
    {
        // Fast path: try to acquire immediately. If the pool is exhausted, emit a
        // wait-start telemetry event before blocking so operators can see queue depth.
        long awaitedMs = 0;
        if (!sem.Wait(0, ct))
        {
            InfrastructureEventLog.Emit(
                "steam_account_pool_insufficient",
                new { kind, totalSize = poolSize }
            );
            var sw = Stopwatch.StartNew();
            await sem.WaitAsync(ct);
            sw.Stop();
            awaitedMs = sw.ElapsedMilliseconds;
        }

        if (_readinessProbe != null)
        {
            var head = PeekHeadUnderLock(queue);
            if (head.HasValue)
                await WaitForHeadHealthyAsync(head.Value, kind, ct);
        }

        return Dequeue(queue, kind, awaitedMs);
    }

    private int? PeekHeadUnderLock(Queue<int> queue)
    {
        lock (_lock)
        {
            return queue.TryPeek(out var i) ? i : (int?)null;
        }
    }

    private async Task WaitForHeadHealthyAsync(int idx, string kind, CancellationToken ct)
    {
        // With one server-pool index, re-enqueue rotation is pointless — the
        // same index pops back. Retry the same index with backoff and let the
        // sidecar's auto-reconnect catch up.
        var deadline = DateTime.UtcNow + AllocationReadinessBudget;
        var startedSw = Stopwatch.StartNew();
        var emittedWait = false;
        while (DateTime.UtcNow < deadline)
        {
            bool ok;
            try
            {
                ok = await _readinessProbe!(idx, ct);
            }
            catch
            {
                ok = false;
            }
            if (ok)
            {
                if (emittedWait)
                    InfrastructureEventLog.Emit(
                        "steam_account_allocation_recovered",
                        new
                        {
                            kind,
                            index = idx,
                            waitedMs = startedSw.ElapsedMilliseconds,
                        }
                    );
                return;
            }
            if (!emittedWait)
            {
                InfrastructureEventLog.Emit(
                    "steam_account_allocation_waiting",
                    new
                    {
                        kind,
                        index = idx,
                        budgetSec = AllocationReadinessBudget.TotalSeconds,
                    }
                );
                emittedWait = true;
            }
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(3), ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
        InfrastructureEventLog.Emit(
            "steam_account_allocation_unhealthy",
            new
            {
                kind,
                index = idx,
                waitedMs = startedSw.ElapsedMilliseconds,
            }
        );
        // Fall through and dequeue anyway — the mod's VerifyServiceReady will throw
        // with a per-account diagnostic that pinpoints the disconnected index.
    }

    private int Dequeue(Queue<int> queue, string kind, long awaitedMs)
    {
        int index;
        int remaining;
        lock (_lock)
        {
            // Semaphore guarantees the dequeue succeeds.
            index = queue.Dequeue();
            remaining = queue.Count;
        }

        if (kind == "server")
            TestLog.Server($"Steam server account {index} allocated");
        else
            TestLog.Client($"Steam client account {index} allocated ({remaining} remaining)");

        if (awaitedMs > 0)
        {
            InfrastructureEventLog.Emit(
                "steam_account_allocated",
                new
                {
                    kind,
                    index,
                    remaining,
                    awaitedMs,
                }
            );
        }
        else
        {
            InfrastructureEventLog.Emit(
                "steam_account_allocated",
                new
                {
                    kind,
                    index,
                    remaining,
                }
            );
        }
        return index;
    }

    /// <summary>
    /// Returns an account index to the appropriate pool for reuse. Indices outside
    /// the allocator's known slice are ignored.
    /// </summary>
    public void Release(int index)
    {
        if (!_knownIndices.Contains(index))
            return;

        int available;
        bool isServer = index == _serverIndex;
        lock (_lock)
        {
            if (isServer)
            {
                _serverAccounts.Enqueue(index);
                available = _serverAccounts.Count;
            }
            else
            {
                _clientAccounts.Enqueue(index);
                available = _clientAccounts.Count;
            }
        }

        if (isServer)
        {
            _serverSem.Release();
            TestLog.Server($"Steam server account {index} released");
            InfrastructureEventLog.Emit(
                "steam_account_released",
                new
                {
                    kind = "server",
                    index,
                    available,
                }
            );
        }
        else
        {
            _clientSem.Release();
            TestLog.Client($"Steam client account {index} released ({available} available)");
            InfrastructureEventLog.Emit(
                "steam_account_released",
                new
                {
                    kind = "client",
                    index,
                    available,
                }
            );
        }
    }

    public Task DrainPendingReleasesAsync() => Task.CompletedTask;
}
