using System.Collections.Concurrent;
using DotNet.Testcontainers.Networks;
using JunimoServer.Tests.Containers;
using JunimoServer.Tests.Helpers;

namespace JunimoServer.Tests.Infrastructure;

/// <summary>
/// Per-host pool of GameClientContainer instances with Docker start throttling via
/// the host's <see cref="DockerHost.StartLimiter"/> to prevent thundering herd.
/// One pool per <see cref="DockerHost"/>; clients in this pool live on the host's
/// bridge network and survive server swaps on the same host. Concurrency is bounded
/// by <c>host.ClientCapacity.Capacity</c>. Cross-host client migration is forbidden
/// by design — a server on host X can only be used with clients in host X's pool.
/// </summary>
internal sealed class ClientPool : IAsyncDisposable
{
    private readonly ConcurrentBag<GameClientContainer> _available = new();
    private readonly List<GameClientContainer> _allClients = new();
    private readonly object _allClientsLock = new();
    private readonly DockerHost _host;
    private readonly INetwork _network;
    private readonly string _imageTag;
    private readonly string _gameDataVolume;
    private readonly string? _steamAuthUrl;
    private readonly ISteamAccountAllocator? _accountAllocator;
    // Process-wide so the artifact slug `client-{N}` and InstanceId stay unique
    // across multiple per-host pools. With per-pool counters, two pools each
    // emit `client-0`, colliding on `containers/client-0/container.log`
    // (ContainerLogFile opens with FileShare default — second writer throws
    // IOException at construction). Mirrors how ManagedServer indices come from
    // a single TestResourceBroker counter.
    private static int s_nextClientIndex;
    private int _inFlightCreations;
    // Counts in-flight CreateClientAsync calls that have already taken a Steam
    // account from the allocator but haven't yet added the container to _allClients.
    // PoolHasAnySteamBearingClient must include these so a second Steam lease
    // arriving mid-creation routes to the steam-wait branch instead of racing
    // into another cold-start that would block on the now-empty allocator.
    private int _inFlightSteamCreations;
    private volatile Task? _preWarmTask;

    /// <summary>
    /// Maximum number of client containers that can exist simultaneously on this host.
    /// Mirrors the host's <see cref="HostCapacityQueue.Capacity"/> so the pool can't
    /// out-grow the per-host scheduler's slot count.
    /// </summary>
    private readonly int _maxContainers;

    /// <summary>The Docker host this pool is bound to.</summary>
    public DockerHost Host => _host;

    /// <summary>
    /// Signaled when a client is returned to the pool or discarded, so waiters
    /// blocked at the container cap can retry.
    /// </summary>
    private readonly SemaphoreSlim _returnSignal = new(0);

    /// <summary>
    /// Signaled every time a Steam-bearing client lands in <see cref="_available"/>
    /// (prewarm completion or <see cref="ReturnClient"/>). A Steam-required lease
    /// awaits this when the bag has no Steam-bearing client; the wait wakes when
    /// a leaseholder returns one. Steam-account ownership is bound to the lease
    /// (release-on-return semantics), not to the container — so non-Steam tests
    /// running on a Steam-bearing client temporarily decrement availability and
    /// restore it on return.
    /// <para>
    /// Tickets correspond 1:1 to Steam-bearing clients currently in the bag. We
    /// never <c>Release()</c> on <see cref="DiscardClient"/> / <see cref="MarkClientDead"/>:
    /// those callers act on a currently-leased client, which by definition has
    /// no ticket outstanding (the lease consumed it on take).
    /// </para>
    /// </summary>
    private readonly SemaphoreSlim _steamAvailable = new(0);

    public ClientPool(DockerHost host, INetwork network, string imageTag, string gameDataVolume,
        string? steamAuthUrl = null, ISteamAccountAllocator? accountAllocator = null)
    {
        _host = host;
        _network = network;
        _imageTag = imageTag;
        _gameDataVolume = gameDataVolume;
        _steamAuthUrl = steamAuthUrl;
        _accountAllocator = accountAllocator;
        _maxContainers = host.ClientCapacity.Capacity;
    }

    /// <summary>
    /// Leases a client from the pool. Reuses an available client or creates a new one.
    /// Global concurrency is bounded by <see cref="ClientCapacity"/>.
    /// <para>
    /// Steam-account ownership is bound to the lease, not the container. The Steam
    /// account index stays pinned to its container for the container's lifetime
    /// (account swap is not possible at runtime — see <c>tests/test-client/Auth/ClientAuthService.cs</c>),
    /// but <i>availability</i> is tracked by <see cref="_steamAvailable"/>: a Steam
    /// lease consumes a ticket, return restores it. Non-Steam leases on a Steam-bearing
    /// client are allowed (LAN connections work regardless of account index) and
    /// participate in the same release-on-return discipline.
    /// </para>
    /// <para>
    /// Steam path: take a Steam-bearing client from the bag if present, otherwise
    /// wait on <see cref="_steamAvailable"/>. Cold-start (cap-and-create with a fresh
    /// Steam account) only fires when no Steam-bearing client has been created yet.
    /// </para>
    /// <para>
    /// Non-Steam path: prefer a non-Steam-bearing client to leave Steam-bearing
    /// clients available for Steam-required tests; fall back to a Steam-bearing
    /// client when that's all the bag has.
    /// </para>
    /// </summary>
    public async Task<ClientLease> LeaseClientAsync(string serverKey, CancellationToken ct, bool requireSteam = false)
    {
        TestLog.Client($"Lease requested ({_available.Count} in pool, requireSteam={requireSteam})");

        // Fast path: take from the bag if a suitable client is present.
        if (TryTakeClient(requireSteam, out var client))
        {
            if (client!.SteamAccountIndex >= 0) ConsumeSteamTicket();
            TestLog.Client($"client-{client.ClientIndex} reused (steam={client.SteamAccountIndex})");
            return new ClientLease(this, client, serverKey);
        }

        // If pre-warming is in progress, wait for it before creating a new one
        var preWarm = _preWarmTask;
        if (preWarm != null && !preWarm.IsCompleted)
        {
            TestLog.Client("Waiting for pre-warm...");
            try
            {
                await WaitTrace.RunAsync(
                    WaitName.ClientPool_PrewarmInProgress,
                    () => preWarm,
                    ct,
                    snapshot: () => new { preWarmRunning = !preWarm.IsCompleted });
            }
            catch { /* pre-warm failure is non-fatal */ }

            // Retry; pre-warm should have put a client in _available
            if (TryTakeClient(requireSteam, out client))
            {
                if (client!.SteamAccountIndex >= 0) ConsumeSteamTicket();
                TestLog.Client($"client-{client.ClientIndex} reused (pre-warmed, steam={client.SteamAccountIndex})");
                return new ClientLease(this, client, serverKey);
            }
        }

        // Steam path: lease availability is gated by _steamAvailable, not by container
        // capacity. Once any Steam-bearing client has been created on this host (prewarm
        // is the only producer today, and it runs at broker prestart before any test),
        // every subsequent Steam lease waits for a return rather than creating a new
        // container. The cold-start cap-and-create path below only fires when prewarm
        // hasn't begun yet on this host — extremely rare in practice.
        if (requireSteam && _accountAllocator != null && PoolHasAnySteamBearingClient())
        {
            int steamBearingTotal;
            lock (_allClientsLock)
            {
                steamBearingTotal = 0;
                foreach (var c in _allClients)
                    if (c.SteamAccountIndex >= 0) steamBearingTotal++;
            }
            InfrastructureEventLog.Emit("steam_pool_lease_wait_started", new
            {
                availableInBag = _available.Count,
                steamBearingClients = steamBearingTotal,
                steamSliceSize = _accountAllocator.ClientPoolSize,
            });
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (true)
            {
                await WaitTrace.RunAsync(
                    WaitName.ClientPool_LeaseSteamWait,
                    () => _steamAvailable.WaitAsync(ct),
                    ct,
                    snapshot: () => new { available = _available.Count });

                // Ticket acquired: a Steam-bearing client is in the bag (or just was).
                // Race-tolerant retry: a different waiter or a non-Steam fast-path may
                // have grabbed it. Loop until we secure one.
                if (TryTakeClient(requireSteam: true, out client))
                {
                    sw.Stop();
                    TestLog.Client($"client-{client!.ClientIndex} reused (after steam wait, steam={client.SteamAccountIndex}, awaitedMs={sw.ElapsedMilliseconds})");
                    return new ClientLease(this, client, serverKey);
                }
                // Lost the race; the ticket we just consumed corresponded to an item
                // someone else took. Don't put the ticket back — the next return will
                // emit a fresh one. Loop back to wait again.
            }
        }

        // Check container cap: if at limit, wait for a return or discard.
        // Include in-flight creations (from pre-warm or concurrent on-demand)
        // that haven't registered in _allClients yet.
        int currentCount;
        lock (_allClientsLock) { currentCount = _allClients.Count + _inFlightCreations; }

        while (currentCount >= _maxContainers)
        {
            TestLog.Client($"At container cap ({currentCount}/{_maxContainers}), waiting for return...");
            // NOTE: snapshot avoids _allClients.Count — guarded by _allClientsLock and unsafe to read outside.
            // _available is ConcurrentBag (thread-safe Count); _maxContainers is set once in ctor.
            await WaitTrace.RunAsync(
                WaitName.ClientPool_LeaseAtCap,
                () => _returnSignal.WaitAsync(ct),
                ct,
                snapshot: () => new { available = _available.Count, max = _maxContainers });

            // A client was returned or discarded; try to grab it
            if (TryTakeClient(requireSteam, out client))
            {
                if (client!.SteamAccountIndex >= 0) ConsumeSteamTicket();
                TestLog.Client($"client-{client.ClientIndex} reused (after cap wait, steam={client.SteamAccountIndex})");
                return new ClientLease(this, client, serverKey);
            }

            // Discard freed a slot; break to create a new one
            lock (_allClientsLock) { currentCount = _allClients.Count + _inFlightCreations; }
        }

        // Patience window: an outstanding client may return faster than a new
        // container can be built (~60s on typical hardware). Only wait when at
        // least one client is in flight — a cold start with no clients has
        // nothing to wait for. Triggered when two concurrent tests on the same
        // server race for clients: the second test would otherwise eagerly
        // create a new container while the first is already finishing.
        var patience = TestTimings.ClientLeasePatience;
        if (patience > TimeSpan.Zero)
        {
            int outstandingClients;
            lock (_allClientsLock) { outstandingClients = _allClients.Count - _available.Count; }
            if (outstandingClients > 0)
            {
                TestLog.Client($"No idle client, {outstandingClients} in use; waiting up to {patience.TotalSeconds:0}s for a return before creating");
                using var deadlineCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                deadlineCts.CancelAfter(patience);
                try
                {
                    await WaitTrace.RunAsync(
                        WaitName.ClientPool_LeasePatienceWait,
                        () => _returnSignal.WaitAsync(deadlineCts.Token),
                        deadlineCts.Token,
                        snapshot: () => new { available = _available.Count, outstanding = outstandingClients, patienceSec = (int)patience.TotalSeconds });

                    if (TryTakeClient(requireSteam, out client))
                    {
                        if (client!.SteamAccountIndex >= 0) ConsumeSteamTicket();
                        TestLog.Client($"client-{client.ClientIndex} reused (after patience wait, steam={client.SteamAccountIndex})");
                        return new ClientLease(this, client, serverKey);
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    TestLog.Client("Patience window elapsed, creating new client");
                }
            }
        }

        // Cold-start path: no Steam-bearing client has ever been created on this host
        // (Steam case), or no client of any kind exists yet (non-Steam case). Create
        // a new one under the global Docker creation limiter. Steam allocation happens
        // inside CreateClientAsync via _accountAllocator and binds the account to this
        // new container for its lifetime — the lease-availability gate above
        // (_steamAvailable) is the source of truth thereafter.
        client = await CreateClientGuardedAsync(requireSteam, reason: "lease_demand", StartPriority.Normal, ct);
        return new ClientLease(this, client, serverKey);
    }

    /// <summary>
    /// Takes a client from the pool. Selection differs by Steam requirement:
    /// <list type="bullet">
    /// <item><description><c>requireSteam=true</c>: drains the bag, returns the first
    /// Steam-bearing client, puts non-matches back.</description></item>
    /// <item><description><c>requireSteam=false</c>: drains the bag, prefers a
    /// non-Steam-bearing client to keep Steam-bearing clients available for Steam
    /// tests; falls back to a Steam-bearing client when no plain client is present.</description></item>
    /// </list>
    /// With <c>_maxContainers</c> typically ≤ a host's <c>ClientSlots</c>, the bag is
    /// small and draining is cheap.
    /// </summary>
    private bool TryTakeClient(bool requireSteam, out GameClientContainer? client)
    {
        // Drain the bag once, partition into preferred / fallback.
        GameClientContainer? preferred = null;
        GameClientContainer? fallback = null;
        var others = new List<GameClientContainer>();
        while (_available.TryTake(out var candidate))
        {
            var isSteam = candidate.SteamAccountIndex >= 0;

            // Steam-required leases skip clients whose Galaxy auth resolved to a
            // failure state — they bear a Steam account but cannot complete a
            // Steam-path JoinLobby. Non-Steam (LAN) leases on the same client are
            // still fine, so the client stays in the bag for that path.
            if (requireSteam && isSteam &&
                (candidate.GalaxyState == "failed" || candidate.GalaxyState == "lost"))
            {
                others.Add(candidate);
                continue;
            }

            var matchesPreference = requireSteam ? isSteam : !isSteam;
            if (preferred == null && matchesPreference)
            {
                preferred = candidate;
            }
            else if (!requireSteam && fallback == null && isSteam)
            {
                // Non-Steam request, no plain client yet — remember a Steam-bearing
                // candidate as fallback in case the bag has nothing else.
                fallback = candidate;
            }
            else
            {
                others.Add(candidate);
            }
        }

        // Restore everything we don't keep. Fallback only consumed when preferred is null.
        if (preferred == null && fallback != null)
        {
            client = fallback;
            foreach (var c in others) _available.Add(c);
            return true;
        }

        if (fallback != null) others.Add(fallback);
        foreach (var c in others) _available.Add(c);
        client = preferred;
        return preferred != null;
    }

    /// <summary>
    /// Consumes one <see cref="_steamAvailable"/> ticket synchronously after any
    /// fast-path take of a Steam-bearing client (Steam or non-Steam request, fallback
    /// or preferred). Required because such takes bypass <c>WaitAsync</c>; without
    /// the explicit drain the next Steam-required wait would see a stale ticket and
    /// short-circuit incorrectly. Tickets correspond 1:1 to "Steam-bearing client
    /// landed in bag", so every leave-the-bag path must consume one.
    /// </summary>
    private void ConsumeSteamTicket()
    {
        // Wait(0) returning false is benign: a fast-path take can briefly race ahead
        // of the corresponding Release (e.g., a returner adds the client to the bag
        // and we take it before Release fires). The accounting reconciles when the
        // late Release lands and the next waiter consumes that ticket.
        _steamAvailable.Wait(0);
    }

    /// <summary>
    /// True iff at least one Steam-bearing client exists in this pool — either
    /// already in <c>_allClients</c> (returned to the bag, currently leased, or
    /// pending teardown) or in flight in <see cref="CreateClientAsync"/> after
    /// the allocator handed out an index. When false, a Steam lease must take
    /// the cold-start cap-and-create path; when true, it must wait on
    /// <c>_steamAvailable</c> so two concurrent Steam leases never both race
    /// into cold-start and block on the now-empty allocator.
    /// </summary>
    private bool PoolHasAnySteamBearingClient()
    {
        if (Volatile.Read(ref _inFlightSteamCreations) > 0) return true;
        lock (_allClientsLock)
        {
            foreach (var c in _allClients)
                if (c.SteamAccountIndex >= 0) return true;
            return false;
        }
    }

    private async Task<GameClientContainer> CreateClientGuardedAsync(bool requireSteam, string reason, StartPriority priority, CancellationToken ct)
    {
        // Increment _inFlightSteamCreations BEFORE _inFlightCreations so a concurrent
        // PoolHasAnySteamBearingClient observer that sees _inFlightSteamCreations > 0
        // can route to the steam-wait branch and avoid racing into another cold-start.
        // Decremented AFTER _allClients.Add so the steam-bearing client is already
        // visible in the roster by the time the in-flight count drops to 0 — closing
        // the otherwise-tiny race window where neither signal would be true.
        var willAllocateSteam = requireSteam && _steamAuthUrl != null && _accountAllocator != null;
        if (willAllocateSteam) Interlocked.Increment(ref _inFlightSteamCreations);
        Interlocked.Increment(ref _inFlightCreations);
        // Tracks whether the in-flight counters have been decremented yet, so the
        // catch/outer-finally don't double-decrement. Decremented on the success path
        // right after _allClients.Add (so the client is never double-counted in the
        // capacity check `_allClients.Count + _inFlightCreations` while recording starts),
        // and on the failure path in the outer finally.
        var inFlightDecremented = false;
        try
        {
            GameClientContainer client;
            await _host.StartLimiter.WaitAsync(priority, ct);
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                client = await CreateClientAsync(requireSteam, ct);
                sw.Stop();
                lock (_allClientsLock)
                {
                    _allClients.Add(client);
                }
                // Decrement AFTER _allClients.Add so the (steam-bearing) client is visible
                // in the roster before the in-flight count drops to 0 — preserving the race
                // invariant noted at the increment site. Doing it here (rather than the outer
                // finally) means the client is counted exactly once during StartRecordingAsync.
                Interlocked.Decrement(ref _inFlightCreations);
                if (willAllocateSteam) Interlocked.Decrement(ref _inFlightSteamCreations);
                inFlightDecremented = true;
                InfrastructureEventLog.Emit("client_created", new
                {
                    clientIndex = client.ClientIndex,
                    durationMs = sw.ElapsedMilliseconds,
                    reason,
                });
            }
            catch (Exception ex)
            {
                TestLog.Client($"Client creation failed: {ex.GetType().Name}: {ex.Message}");
                InfrastructureEventLog.Emit("client_create_failed", new { error = $"{ex.GetType().Name}: {ex.Message}", ctCancelled = ct.IsCancellationRequested });
                throw;
            }
            finally
            {
                _host.StartLimiter.Release();
            }

            // Start recording AFTER releasing the start-limiter slot: recording is exec-only
            // against the already-running container, so holding a create+start slot during it
            // would serialize other containers' starts. The client is already in the roster
            // and counted once, so this is just deferred exec work on a live container.
            await client.StartRecordingAsync(ct);
            return client;
        }
        finally
        {
            if (!inFlightDecremented)
            {
                Interlocked.Decrement(ref _inFlightCreations);
                if (willAllocateSteam) Interlocked.Decrement(ref _inFlightSteamCreations);
            }
        }
    }

    private async Task<GameClientContainer> CreateClientAsync(bool requireSteam, CancellationToken ct)
    {
        var clientIndex = Interlocked.Increment(ref s_nextClientIndex) - 1;

        var options = new GameClientOptions
        {
            ImageTag = _imageTag,
            GameDataVolume = _gameDataVolume,
            ExposeVnc = true
        };

        // Allocate a Steam account only when the caller actually needs Steam.
        // Tracked separately from the client object so a failed StartAsync still
        // returns the index to the pool (the field is only assigned after start
        // succeeds; without this guard, a startup failure leaks the slot and the
        // next allocator caller blocks forever).
        int steamAccountIndex = -1;
        var allocateSteam = requireSteam && _steamAuthUrl != null && _accountAllocator != null;
        if (allocateSteam)
        {
            steamAccountIndex = await _accountAllocator!.AllocateClientAsync(ct);
            options.SteamAuthUrl = _steamAuthUrl;
            options.SteamAccountIndex = steamAccountIndex;
            TestLog.Client($"client-{clientIndex} assigned Steam account {steamAccountIndex}");
        }

        try
        {
            TestLog.Client($"Creating client-{clientIndex}...");

            // Register instance in UI immediately (before container starts).
            // VNC URL is null until StartAsync publishes ports.
            var instanceId = $"client-{clientIndex}";
            SetupEventBus.EmitInstanceCreated(instanceId, "client", "shared", null, $"client-{clientIndex}", _host.Id);
            SetupEventBus.EmitPhaseStarted("Setup", $"client-{clientIndex}", instanceId);

            var client = await GameClientContainer.CreateAsync(
                clientIndex,
                options,
                _network,
                msg => TestLog.Client($"client-{clientIndex} {msg}"),
                ct,
                host: _host,
                requireGalaxyResolved: allocateSteam);

            await client.StartAsync(ct);

            // Track Steam account on the client container for release on discard/dispose
            if (steamAccountIndex >= 0)
            {
                client.SteamAccountIndex = steamAccountIndex;
                steamAccountIndex = -1; // ownership transferred; skip release-on-failure
            }

            TestLog.Client($"client-{clientIndex} ready ({client.BaseUrl})");

            // Update UI with VNC URL now that ports are published
            SetupEventBus.EmitInstanceCreated(instanceId, "client", "shared", client.VncUrl, $"client-{clientIndex}", _host.Id);
            SetupEventBus.EmitPhaseCompleted("Setup", $"client-{clientIndex}", true, collectionName: instanceId);

            // Register for stats tracking now that the container is running
            ContainerStatsCollector.Register(instanceId, client.Container.Id, client.Container.Name, _host, client.BaseUrl);

            return client;
        }
        finally
        {
            // Release the Steam account if ownership wasn't transferred to the client
            // (StartAsync threw, CreateAsync threw, etc.). When the assignment above runs,
            // steamAccountIndex is reset to -1 so this is a no-op on success.
            if (steamAccountIndex >= 0 && _accountAllocator != null)
                _accountAllocator.Release(steamAccountIndex);
        }
    }

    /// <summary>
    /// Pre-creates client containers and adds them to the pool for immediate reuse.
    /// Respects the global creation limiter to avoid Docker overload.
    /// </summary>
    public Task PreWarmAsync(int count, CancellationToken ct)
    {
        TestLog.Client($"Pre-warming {count} client(s)...");
        var task = PreWarmCoreAsync(count, ct);
        _preWarmTask = task;
        return task;
    }

    private async Task PreWarmCoreAsync(int count, CancellationToken ct)
    {
        // Prewarm fills the host's Steam slice (count = ClientPoolSize). Each prewarmed
        // client receives a Steam-account index pinned to the container for its lifetime.
        // Lease-availability is not pinned: both Steam and non-Steam tests reuse these
        // via TryTakeClient (the non-Steam path prefers non-Steam-bearing clients but
        // falls back to Steam-bearing when the bag has nothing else). Each successful
        // prewarm signals _steamAvailable so the first Steam-required lease can wake.
        var tasks = Enumerable.Range(0, count)
            .Select(async i =>
            {
                try
                {
                    var client = await CreateClientGuardedAsync(requireSteam: true, reason: "prewarm", StartPriority.Low, ct);
                    _available.Add(client);
                    if (client.SteamAccountIndex >= 0)
                        _steamAvailable.Release();
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
                catch (Exception ex)
                {
                    TestLog.Client($"Pre-warm client {i + 1}/{count} failed: {ex.Message}");
                }
            });
        await Task.WhenAll(tasks);
        _preWarmTask = null;
        TestLog.Client($"Pre-warm complete ({_available.Count} client(s) ready)");
    }

    /// <summary>
    /// Returns a client to the pool for reuse by subsequent tests. A Steam-bearing
    /// client also signals <see cref="_steamAvailable"/>: the Steam account stays
    /// pinned to this container, but lease-availability is restored so the next
    /// Steam-required test can take it.
    /// </summary>
    internal void ReturnClient(GameClientContainer client, string serverKey)
    {
        _available.Add(client);
        TestLog.Client($"client-{client.ClientIndex} returned to pool");
        InfrastructureEventLog.Emit("client_returned", new
        {
            clientIndex = client.ClientIndex,
            serverKey,
            poolAvailable = _available.Count,
        });
        _returnSignal.Release();
        if (client.SteamAccountIndex >= 0)
            _steamAvailable.Release();
    }

    /// <summary>
    /// Removes a dead client from the pool's tracking list and signals cap waiters
    /// so they can create a replacement. Called when disconnect fails and the
    /// container is disposed without returning to the pool. The Steam account index
    /// (if any) is released back to <see cref="_accountAllocator"/> for reuse on a
    /// fresh container; <see cref="_steamAvailable"/> is not signaled because a
    /// dead container holds no outstanding ticket — its lease consumed the ticket
    /// at take-time and the client was never returned.
    /// </summary>
    internal void DiscardClient(GameClientContainer client)
    {
        try
        {
            ReleaseClientSteamAccount(client);
        }
        catch (Exception ex)
        {
            TestLog.Client($"Failed to release Steam account for client-{client.ClientIndex}: {ex.Message}");
            InfrastructureEventLog.Emit("steam_account_release_error", new { clientIndex = client.ClientIndex, error = ex.Message });
        }
        int remaining;
        lock (_allClientsLock)
        {
            _allClients.Remove(client);
            remaining = _allClients.Count;
        }
        TestLog.Client($"client-{client.ClientIndex} discarded (dead)");
        InfrastructureEventLog.Emit("client_discarded", new { clientIndex = client.ClientIndex, totalContainers = remaining });
        _returnSignal.Release();
    }

    /// <summary>
    /// Marks a client as dead (won't be reused) but keeps it in <c>_allClients</c>
    /// so <see cref="DisposeAsync"/> can still retrieve the full recording from it.
    /// Releases the Steam account index back to <see cref="_accountAllocator"/>;
    /// <see cref="_steamAvailable"/> is not signaled (see <see cref="DiscardClient"/>).
    /// </summary>
    internal void MarkClientDead(GameClientContainer client)
    {
        try
        {
            ReleaseClientSteamAccount(client);
        }
        catch (Exception ex)
        {
            TestLog.Client($"Failed to release Steam account for client-{client.ClientIndex}: {ex.Message}");
            InfrastructureEventLog.Emit("steam_account_release_error", new { clientIndex = client.ClientIndex, error = ex.Message });
        }
        // Keep in _allClients; DisposeAsync will handle cleanup + recording extraction
        TestLog.Client($"client-{client.ClientIndex} marked dead (kept for recording extraction)");
        InfrastructureEventLog.Emit("client_marked_dead", new { clientIndex = client.ClientIndex });
        _returnSignal.Release();
    }

    public async ValueTask DisposeAsync()
    {
        List<GameClientContainer> clients;
        lock (_allClientsLock)
        {
            clients = new List<GameClientContainer>(_allClients);
            _allClients.Clear();
        }

        // Parallelize per-client teardown. Heavy extraction work inside
        // GameClientContainer.DisposeAsync is gated by host.ExtractLimiter, so
        // unbounded concurrency here is safe. SteamAccountAllocator.Release,
        // ContainerStatsCollector.Unregister (ConcurrentDictionary), and the
        // event emitters are already concurrency-safe.
        var disposeTasks = clients.Select(async client =>
        {
            ReleaseClientSteamAccount(client);
            var instanceId = $"client-{client.ClientIndex}";
            ContainerStatsCollector.Unregister(instanceId);
            SetupEventBus.EmitInstanceDisposed(instanceId);
            // Recording emit lives inside GameClientContainer.DisposeAsync (scoped to the
            // point where the full recording file is written).
            try { await client.DisposeAsync(); }
            catch (Exception ex)
            {
                TestLog.Client($"Failed to dispose client-{client.ClientIndex}: {ex.Message}");
            }
        }).ToArray();
        await Task.WhenAll(disposeTasks);
    }

    private void ReleaseClientSteamAccount(GameClientContainer client)
    {
        if (client.SteamAccountIndex >= 0 && _accountAllocator != null)
        {
            _accountAllocator.Release(client.SteamAccountIndex);
            client.SteamAccountIndex = -1;
        }
    }
}
