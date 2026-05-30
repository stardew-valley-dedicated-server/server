namespace JunimoServer.Tests.Infrastructure;

/// <summary>
/// Allocates Steam-account indices to server / client containers from a shared pool.
/// Account 0 is reserved for the server pool; accounts 1+ are the client pool.
///
/// <para>
/// Single-process model: one in-process <see cref="SteamAccountAllocator"/> serves
/// every host. Allocation is async + queueing — when the pool is exhausted, the call
/// awaits a release rather than returning a sentinel.
/// </para>
///
/// <para>
/// Allocation is bound to a container's lifetime, not a lease's: a leased container
/// keeps its account index across return-to-pool. The test path's "Steam-bearing
/// client available" gate lives on <see cref="ClientPool"/>, not here.
/// </para>
/// </summary>
public interface ISteamAccountAllocator
{
    /// <summary>
    /// True when at least one client account (index 1+) is configured. Reflects pool size,
    /// not instantaneous availability — callers needing "is one free right now" must call
    /// <see cref="AllocateClientAsync"/>.
    /// </summary>
    bool HasClientAccounts { get; }

    /// <summary>
    /// Total number of Steam accounts configured for the *client* pool (i.e., the
    /// allocator's full capacity for client allocations). Pre-warm sizing reads this
    /// to avoid pre-creating more clients than the Steam allocator can serve, which
    /// would deadlock the global Docker-start limiter holding a limiter slot while
    /// awaiting an account that will never come.
    /// </summary>
    int ClientPoolSize { get; }

    /// <summary>
    /// Leases the server Steam-account index. Awaits a release when the pool is exhausted.
    /// </summary>
    Task<int> AllocateServerAsync(CancellationToken ct);

    /// <summary>
    /// Leases the next client Steam-account index. Awaits a release when the pool is exhausted.
    /// </summary>
    Task<int> AllocateClientAsync(CancellationToken ct);

    /// <summary>
    /// Returns an account to the pool. Synchronous because dispose call sites are synchronous;
    /// the remote implementation enqueues releases into a background pump for retry-with-backoff.
    /// </summary>
    void Release(int index);

    /// <summary>
    /// Worker-disposal hook. Awaits in-flight release POSTs (remote impl); no-op for the
    /// local impl. Bound the wait so a hung coordinator doesn't block worker shutdown —
    /// reclaims fall back to the coordinator's <c>worker_marked_lost</c> hook.
    /// </summary>
    Task DrainPendingReleasesAsync();
}
