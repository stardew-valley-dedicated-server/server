using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using JunimoServer.Tests.Containers;
using JunimoServer.Tests.Fixtures;
using JunimoServer.Tests.Helpers;
using JunimoServer.Tests.Schema.Events;
using JunimoServer.Tests.Schema.Json;

namespace JunimoServer.Tests.Infrastructure;

/// <summary>
/// Assembly-scoped broker that manages server and client lifecycle.
/// Starts servers in the background before tests request them (pre-start).
/// Tests queue behind <see cref="ServerQueue"/> instances until servers are ready.
/// </summary>
public sealed class TestResourceBroker : IAsyncDisposable
{
    public static TestResourceBroker Instance { get; private set; } = new();

    private readonly ServerPool _servers = new();

    // Per-host queues / locks / counters: keyed by $"{key}@{hostId}". Two waiters
    // for the same config on different hosts must drive independent deferred
    // creations — without per-host keying, one would block on a queue resolved
    // with a wrong-host instance and either deadlock or fault each other's queues.
    // Helper: BrokerKeyFor(key, host).
    private readonly ConcurrentDictionary<string, ServerQueue> _queues = new();
    private readonly ConcurrentDictionary<string, int> _creationsInFlight = new();
    private readonly ConcurrentDictionary<string, object> _creationLocks = new();

    // Pending and remaining demand stay keyed by config (test-level concept; not host-scoped).
    private readonly ConcurrentDictionary<string, int> _pendingDemand = new();
    private readonly ConcurrentDictionary<string, int> _remainingDemand = new();

    /// <summary>
    /// Composes the per-host broker key used for <see cref="_queues"/>,
    /// <see cref="_creationsInFlight"/>, and <see cref="_creationLocks"/>.
    /// Cluster-aware: each <c>(config-key, host-id)</c> tuple is its own
    /// broker bucket so reuse never crosses hosts.
    /// </summary>
    private static string BrokerKeyFor(string key, DockerHost host) => $"{key}@{host.Id}";

    /// <summary>
    /// Picks the exception a faulted server-acquisition hands to its waiters,
    /// so the runner's exception-type classifier
    /// (<c>TestRunState.ApplyTestFailed</c>) buckets stopOnFail cascade victims
    /// as "canceled" while genuine outages stay "failed"/infrastructure.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="_stopOnFailNotified"/> is the precise signal (set only by
    /// <see cref="NotifyStopOnFail"/>); gating on a bare <c>_runCts</c> would
    /// also catch <c>DisposeAsync</c> and the pre-start abort fan-out, which
    /// must remain real failures.
    /// </para>
    /// <para>
    /// The OCE must be returned BARE (no inner exception, no wrapping):
    /// <c>RunnerCallbacks.UnwrapException</c> walks to the deepest inner, so a
    /// nested OCE whose deepest inner is something else silently reverts to
    /// "failed".
    /// </para>
    /// </remarks>
    private Exception BuildAcquisitionFault(string displayLabel, DockerHost host, Exception? cause)
    {
        if (_stopOnFailNotified && !host.IsPoisoned)
        {
            return new OperationCanceledException(
                $"Server acquisition for {displayLabel} aborted by stopOnFail"
            );
        }

        return cause
            ?? new ServerUnavailableException(
                $"All server creation attempts failed for {displayLabel} on {host.Id}"
            );
    }

    // Server.DisposeAsync tasks kicked off in the background by
    // TryEvictIdleServerForAsync so the waiting test can proceed without
    // blocking on Docker stop-grace + recording extraction. Also receives
    // deferred per-test recording extraction (TestBase.DisposeAsync, "all"
    // mode passing tests) so ffmpeg work doesn't sit on the test critical
    // path. Drained in DisposeAsync via Task.WhenAll so the broker doesn't
    // exit with orphan containers or pending extractions.
    private readonly ConcurrentQueue<Task> _backgroundDisposeTasks = new();

    /// <summary>
    /// Enqueues a background task whose completion is awaited during broker
    /// disposal. Used by <see cref="TestBase"/> to defer non-critical work
    /// (per-test recording clip extraction in "all" mode passing tests) off
    /// the test's <c>DisposeAsync</c> critical path. Wrapped in
    /// <see cref="ExecutionContext.SuppressFlow"/> so the deferred work
    /// doesn't carry the deferring test's <c>TestContext.Current</c> across
    /// later events emitted from inside the work itself.
    /// </summary>
    public void EnqueueBackgroundTask(Func<Task> work)
    {
        using (ExecutionContext.SuppressFlow())
        {
            _backgroundDisposeTasks.Enqueue(
                Task.Run(async () =>
                {
                    try
                    {
                        await work();
                    }
                    catch (Exception ex)
                    {
                        InfrastructureEventLog.Emit(
                            "background_task_failed",
                            new { source = "broker_background", error = ex.Message }
                        );
                    }
                })
            );
        }
    }

    /// <summary>
    /// Forwards a <c>mod_phase</c> event from a server container to whichever
    /// tests are currently executing on that server. Called by
    /// <see cref="Containers.SimpleContainerLogStreamer.TryForwardSdvdEvent"/>
    /// after parsing the SDVD_EVENT line. <paramref name="forwardedVia"/> is the
    /// container label (<c>server-N</c>); only server-prefixed labels resolve to a
    /// tracked server. Empty <see cref="ManagedServer.RegisterRunningTest"/> set
    /// (no current lessee, prestart, post-teardown) silently drops the
    /// annotation — the typed event still lands in <c>infrastructure.jsonl</c>
    /// via <c>ForwardRaw</c>.
    /// </summary>
    internal void EmitModPhaseAnnotation(string forwardedVia, string phase)
    {
        if (!forwardedVia.StartsWith("server-", StringComparison.Ordinal))
        {
            return;
        }

        if (!int.TryParse(forwardedVia.AsSpan("server-".Length), out var serverIndex))
        {
            return;
        }

        foreach (var (_, managed) in _servers.GetAll())
        {
            if (managed.Server.ServerIndex != serverIndex)
            {
                continue;
            }

            managed.EmitAnnotationToRunningTests(AnnotationLevel.Info, $"mod_phase: {phase}");
            return;
        }
    }

    private IReadOnlyList<ServerDemand> _discoveredDemands = Array.Empty<ServerDemand>();
    private readonly Lazy<Task> _imagesBuildTask;

    private int _serverIndexCounter;
    private CancellationTokenSource? _prestartCts;

    /// <summary>
    /// Per-host client pools keyed by <see cref="DockerHost.Id"/>. Eagerly populated
    /// for hosts that have at least one pre-start placement (so pre-warm can run
    /// concurrently with server creation); lazily populated for hosts that first
    /// receive on-demand creation. Clients on a given host's pool only join that
    /// host's bridge network — cross-host client migration is forbidden.
    /// </summary>
    private readonly ConcurrentDictionary<string, ClientPool> _clientPools = new();

    // Per-host steam-auth containers, one per Steam-capable host (each on its
    // own bridge network with the alias "steam-auth"). Disjoint by construction:
    // each host serves a slice of STEAM_ACCOUNTS so SteamKit's single-session
    // rule isn't violated. Hosts that aren't Steam-capable have no entry.
    private readonly ConcurrentDictionary<string, SharedSteamAuth> _steamAuthByHost = new();

    // Per-host Steam-account allocators, parallel to _steamAuthByHost. Each
    // allocator's index space is slice-local (0..k-1). The broker routes
    // allocate/release calls via host.Id so a server bound to host H only
    // ever touches H's allocator.
    private readonly ConcurrentDictionary<string, ISteamAccountAllocator> _accountAllocatorByHost =
        new();

    // Slicer output cached for the run. Populated once in StartPrestart so the
    // placement pass and PrestartAsync's bring-up see the same slicing decision.
    // ServerConfigDiscovery.ValidateRequirements re-runs the (pure) slicer for
    // its capability check; both call sites give identical answers by construction.
    private IReadOnlyList<SteamAccountSlice>? _slices;

    /// <summary>
    /// Run-level cancellation: cancelled in DisposeAsync to abort any in-flight
    /// background server creation (deferred, on-demand, or poison replacement)
    /// that would otherwise continue after the test run has ended.
    /// </summary>
    private readonly CancellationTokenSource _runCts = new();

    // Upper bound on how long a poisoned server waits for active leases to
    // release before it force-disposes. Must exceed TestBase's artifact phase
    // budget (screenshot ~15s + log save ~5s + video extract ~10s) plus the
    // WaitForPlayer polling window (~15s) that might still be running.
    private static readonly TimeSpan PoisonDrainTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Background pre-start task. Starts eagerly in the constructor so Docker image
    /// builds and server creation begin immediately. The test UI shows setup progress
    /// right away instead of waiting for the first test to call AcquireAsync.
    /// All callers await the same Task instance.
    /// </summary>
    private readonly Task _prestartTask;
    private Exception? _prestartException;

    public TestResourceBroker()
    {
        Instance = this;
        _imagesBuildTask = new Lazy<Task>(() =>
            DockerImageBuilder.EnsureImagesExistAsync(
                includeTestClient: true,
                new SetupEventBusBuildProgressSink("Setup", collectionName: null)
            )
        );

        // SuppressFlow before Task.Run so prestart and its descendants don't inherit
        // the constructing test's TestContext.Current. Without this, every
        // background event emitted from prestart (server creates, client prewarm,
        // image transfers, container_started, health checks) gets attributed to
        // whichever test happened to first touch TestResourceBroker.Instance.
        // See .claude/rules/asynclocal-pitfalls.md.
        using (ExecutionContext.SuppressFlow())
        {
            _prestartTask = Task.Run(StartPrestart);
        }

        // Start container stats collection (streams via Docker Engine API)
        ContainerStatsCollector.Start();
    }

    private Task StartPrestart()
    {
        try
        {
            // SDVD_TEST_FILTER is set by the parent runner when --filter is given.
            // Passed as methodFilter so demand counts (TestCount, NonExclusiveTestCount)
            // reflect only the methods xUnit will actually dispatch, not the whole
            // shared-config bucket. Without this, a 1-test filter that hits a shared
            // default config bucket (e.g. CropSaverTests sharing a key with ~13 other
            // classes) inflates TestCount to the bucket total and the slot clamp
            // below allocates servers for tests that won't run.
            var methodFilter = Environment.GetEnvironmentVariable("SDVD_TEST_FILTER");
            var demands = ServerConfigDiscovery.DiscoverRequiredConfigs(
                skipValidation: false,
                methodFilter: methodFilter
            );
            _discoveredDemands = demands;
            if (demands.Count == 0)
            {
                TestLog.Server("No server configs discovered for pre-start");
                return Task.CompletedTask;
            }

            // Pre-start sizes by per-instance client throughput, not by raw test count.
            // A single shared instance co-tenants up to (clientCap / clientsPerTest)
            // concurrent tests via RefCount, so prestarting more instances than that
            // floor strands server slots that on-demand expansion can't reclaim until
            // a disposal fires (TryReuseFreedSlotAsync, line 1358). Worst-case host
            // cap keeps the math host-agnostic at allocation time — placement
            // distributes across hosts after.
            var totalSlots = HostPool.Instance.Hosts.Sum(h => h.ServerSlots);
            var minHostClientCap = HostPool.Instance.Hosts.Min(h => h.ClientCapacity.Capacity);
            var totalDemandNeed = demands.Sum(d =>
            {
                var clientsPerInstance = Math.Max(
                    1,
                    minHostClientCap / Math.Max(1, d.Requirements.Clients)
                );
                var nonExclusive = Math.Max(d.NonExclusiveTestCount, 1);
                return Math.Max(1, (int)Math.Ceiling((double)nonExclusive / clientsPerInstance));
            });
            var instancePlan = AllocateInstances(
                demands,
                Math.Min(totalSlots, totalDemandNeed),
                minHostClientCap
            );

            RunMetadata.WriteRunMetadata(demands, instancePlan);
            InfrastructureEventLog.Initialize();

            // Ensure the child's infrastructure event log is drained on abnormal
            // exit (ProcessExit / SIGHUP / second Ctrl+C). The parent registers
            // its own in TestRunner/Program.cs against its parent log; this is
            // the symmetric registration for the test child's canonical log.
            EmergencyCleanup.EnsureRegistered();
            EmergencyCleanup.RegisterDrainable(
                "infrastructure-event-log",
                () => new ValueTask(InfrastructureEventLog.DrainAsync(TimeSpan.FromSeconds(2)))
            );

            TestLog.Server($"Discovered {demands.Count} unique server config(s) for pre-start:");
            foreach (var d in demands)
            {
                TestLog.Server(
                    $"  {d.Requirements.GetDisplayLabel()}: {d.ClassCount} class(es), {d.TestCount} test(s):"
                );
                foreach (var name in d.ClassNames)
                {
                    TestLog.Server($"    · {name}");
                }
            }

            // Initialize remaining demand counters from discovered test counts.
            // This prevents premature eviction of servers that still have tests to run.
            foreach (var d in demands)
            {
                _remainingDemand[d.Key] = d.TestCount;
            }

            // Compute slices once. The slicer is pure and deterministic; ServerConfigDiscovery
            // calls it again with the same inputs and gets the same answer (single source of
            // truth for capability prediction). Cached on _slices for PrestartAsync to reuse
            // without re-parsing STEAM_ACCOUNTS.
            _slices = SteamAccountSlicer.Slice(
                TestEnvLoader.Get("STEAM_ACCOUNTS"),
                HostPool.Instance.Hosts
            );
            var capableIds = new HashSet<string>(
                _slices.Where(s => s.IsSteamCapable).Select(s => s.HostId),
                StringComparer.Ordinal
            );

            // Distribute pre-start instances in two passes:
            //  (1) Steam tokens go to Steam-capable hosts only — predicted by slicing,
            //      so the result matches what PrestartAsync will materialize. No
            //      coordinator pin: any capable host serves Steam tests equally.
            //  (2) Non-Steam tokens distribute over all hosts, with each host's budget
            //      reduced by the Steam tokens already placed on it.
            // Both passes use the same Hamilton/largest-remainder math, parameterized on
            // (host subset, per-host budget, token count).
            var steamTokens = instancePlan
                .Where(p => p.Demand.Requirements.WithSteam)
                .SelectMany(p => Enumerable.Repeat(p.Demand, p.Count))
                .ToList();
            var nonSteamTokens = instancePlan
                .Where(p => !p.Demand.Requirements.WithSteam)
                .SelectMany(p => Enumerable.Repeat(p.Demand, p.Count))
                .ToList();

            var placements = new List<(ServerDemand Demand, DockerHost Host)>();

            // Initial budget = each host's full ServerSlots. Subtracted as tokens
            // place onto a host so pass (2) sees the correct remaining capacity.
            var budgets = HostPool.Instance.Hosts.ToDictionary(h => h.Id, h => h.ServerSlots);

            if (steamTokens.Count > 0)
            {
                var capableHosts = HostPool
                    .Instance.Hosts.Where(h => capableIds.Contains(h.Id))
                    .ToList();
                if (capableHosts.Count == 0)
                {
                    throw new InvalidOperationException(
                        "Pre-start: configs require Steam but no host's slice is Steam-capable. "
                            + "ServerConfigDiscovery should have caught this — check STEAM_ACCOUNTS sizing."
                    );
                }

                DistributeAndAppend(capableHosts, budgets, steamTokens, placements);
            }

            if (nonSteamTokens.Count > 0)
            {
                DistributeAndAppend(HostPool.Instance.Hosts, budgets, nonSteamTokens, placements);
            }

            // Create queues for each (config, host) placement upfront so deferred-create
            // and on-demand paths see them.
            foreach (var (demand, host) in placements)
            {
                _queues.GetOrAdd(BrokerKeyFor(demand.Key, host), _ => new ServerQueue());
            }

            _prestartCts = new CancellationTokenSource();
            // Defense in depth: StartPrestart() already suppressed flow before its
            // Task.Run, so any EC capture here would already be empty. Suppress again
            // in case future refactors inline this method into a test-attributed path.
            // See .claude/rules/asynclocal-pitfalls.md.
            using (ExecutionContext.SuppressFlow())
            {
                return Task.Run(() =>
                    PrestartAsync(demands, instancePlan, placements, _prestartCts.Token)
                );
            }
        }
        catch (Exception ex)
        {
            // Synchronous failures here (config discovery, run-metadata write,
            // event-log init, slicer, Hamilton allocator, or the no-Steam-capable-host
            // throw) bypass PrestartAsync's catch entirely. Mirror its cascade so
            // AcquireAsync's _prestartException re-throw fires for every test.
            // Returning Task.CompletedTask matches the async path's outcome
            // (PrestartAsync's catch swallows-then-falls-through).
            TestLog.Server($"Pre-start failed: {ex.GetType().Name}: {ex.Message}");
            TestLog.Server($"  {ex.StackTrace?.Split('\n').FirstOrDefault()?.Trim()}");

            TestSummaryFixture.Instance?.SetAborted(ex.Message);
            _prestartException = ex;

            TestLog.Server("Pre-start failed, aborting run");
            _runCts.Cancel();

            foreach (var (_, queue) in _queues)
            {
                if (!queue.IsReady && !queue.IsFaulted)
                {
                    queue.ServerFailed(ex);
                }
            }

            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Background pre-start: builds images then starts servers up to the cluster's
    /// total server-slot capacity. Demands beyond the slot limit are deferred;
    /// they'll be created on-demand by evicting an idle server when a test needs them.
    ///
    /// <paramref name="instancePlan"/> is computed once by the caller
    /// (<see cref="StartPrestart"/>) and threaded here so metadata and pre-start
    /// see the same allocation. <paramref name="placements"/> expands the plan into
    /// per-instance (demand, host) pairs based on per-host ServerSlots.
    /// </summary>
    private async Task PrestartAsync(
        List<ServerDemand> demands,
        List<(ServerDemand Demand, int Count)> instancePlan,
        List<(ServerDemand Demand, DockerHost Host)> placements,
        CancellationToken ct
    )
    {
        try
        {
            var sw = Stopwatch.StartNew();
            TestLog.Server("Building Docker images...");
            await _imagesBuildTask.Value;
            TestLog.Server(
                FormattableString.Invariant($"Docker images ready ({sw.Elapsed.TotalSeconds:F1}s)")
            );

            // One steam-auth container per Steam-capable host. Each host's bridge
            // resolves the alias "steam-auth" to its own container, so the
            // STEAM_AUTH_URL is identical everywhere — only the resolution target
            // and the slice-local index space differ. Disjoint slicing avoids
            // SteamKit's single-session ping-pong (Steam kicks the prior login
            // with LogonSessionReplaced when the same account logs in twice).
            var needsSteam = demands.Any(d => d.Requirements.WithSteam);
            if (needsSteam)
            {
                // _slices was populated in StartPrestart (same pure inputs, same
                // outputs); reuse it here to avoid double-emitting
                // steam_account_slicing events. Defensive fallback if a future
                // refactor inlines this method into a path that skips StartPrestart.
                var slices =
                    _slices
                    ?? SteamAccountSlicer.Slice(
                        TestEnvLoader.Get("STEAM_ACCOUNTS"),
                        HostPool.Instance.Hosts
                    );
                _slices = slices;
                var capablePairs = slices
                    .Where(s => s.IsSteamCapable)
                    .Select(s =>
                        (Slice: s, Host: HostPool.Instance.Hosts.First(h => h.Id == s.HostId))
                    )
                    .ToList();

                TestLog.Server(
                    $"Initializing per-host steam-auth: "
                        + $"{capablePairs.Count} capable host(s) "
                        + $"[{string.Join(", ", capablePairs.Select(p => $"{p.Host.Id}={p.Slice.SliceSize}"))}]"
                );

                var steamAuthOptions = new ServerContainerOptions();
                await Task.WhenAll(
                    capablePairs.Select(async pair =>
                    {
                        var (slice, host) = pair;
                        SharedSteamAuth? created = null;
                        try
                        {
                            var network = await TestNetworkManager.GetOrCreateNetworkAsync(
                                host,
                                ct
                            );
                            created = await SharedSteamAuth.CreateAndStartAsync(
                                network,
                                steamAuthOptions.ImageTag,
                                steamAuthOptions.GameDataVolume,
                                steamAuthOptions.SteamSessionVolume,
                                ct,
                                host,
                                slice.SliceJson
                            );
                            TestLog.Server($"steam-auth ready on {host.Id}");

                            await created.WaitForAccountsLoggedInAsync(slice.SliceSize, ct);
                            TestLog.Server(
                                $"All {slice.SliceSize} Steam account(s) ready on {host.Id}"
                            );

                            _steamAuthByHost[host.Id] = created;
                            _accountAllocatorByHost[host.Id] = new SteamAccountAllocator(
                                slice.SliceSize,
                                readinessProbe: (idx, c) => created.IsAccountHealthyAsync(idx, c)
                            );
                            host.MarkSteamCapable();
                        }
                        catch (Exception ex)
                        {
                            TestLog.Server(
                                $"steam-auth init failed on {host.Id}: {ex.GetType().Name}: {ex.Message}"
                            );
                            // Preserve the full failure message — a downstream aggregate throw
                            // surfaces this verbatim to the test UI / per-test error.
                            host.Poison(ex.Message);
                            // Best-effort cleanup of any partially-created container so
                            // we don't leak across the run.
                            if (created != null)
                            {
                                try
                                {
                                    await created.DisposeAsync();
                                }
                                catch (Exception dispEx)
                                {
                                    TestLog.Server(
                                        $"steam-auth cleanup failed on {host.Id}: {dispEx.Message}"
                                    );
                                }
                            }
                        }
                    })
                );

                if (_steamAuthByHost.IsEmpty && demands.Any(d => d.Requirements.WithSteam))
                {
                    var perHost = capablePairs
                        .Select(p => (host: p.Host, reason: p.Host.PoisonReason))
                        .Where(t => !string.IsNullOrWhiteSpace(t.reason))
                        .Select(t => $"  [{t.host.Id}] {t.reason}")
                        .ToList();

                    var detail =
                        perHost.Count > 0
                            ? "Per-host failure:\n" + string.Join("\n", perHost)
                            : "No per-host failure was recorded.";

                    throw new InvalidOperationException(
                        "Steam-capable hosts failed steam-auth bring-up; aborting run before any tests dispatch.\n"
                            + detail
                    );
                }
            }

            // Instance allocation was computed by StartPrestart and passed in
            // (so run-metadata.json and pre-start see the same plan).
            var prestartedKeys = new HashSet<string>(instancePlan.Select(p => p.Demand.Key));
            var deferred = demands.Where(d => !prestartedKeys.Contains(d.Key)).ToList();
            var totalInstances = placements.Count;

            if (deferred.Count > 0 || instancePlan.Any(p => p.Count > 1))
            {
                var labels = instancePlan.Select(p =>
                    p.Count > 1
                        ? $"{p.Demand.Requirements.GetDisplayLabel()} x{p.Count}"
                        : p.Demand.Requirements.GetDisplayLabel()
                );
                var hostSummary = string.Join(
                    ", ",
                    placements.GroupBy(p => p.Host.Id).Select(g => $"{g.Key}={g.Count()}")
                );
                TestLog.Server(
                    $"Pre-starting {totalInstances} server instance(s) across {instancePlan.Count} config(s) on hosts [{hostSummary}]: {string.Join(", ", labels)}"
                );
                foreach (var d in deferred)
                {
                    TestLog.Server($"  deferred: {d.Requirements.GetDisplayLabel()}");
                }
            }

            // Eagerly construct per-host client pools for every host that has at
            // least one pre-start placement. Hosts with zero pre-start placements
            // get their pool lazily in EnsureClientPoolAsync the first time an
            // on-demand server lands there.
            //
            // Server starts and client pre-warm run concurrently: each host's
            // StartLimiter gates server starts at High priority and pre-warm at
            // Low, so a backlog of slow client starts cannot starve a server
            // start past its 120s StartupTimeout. Pre-warm is best-effort —
            // if a test arrives before its pre-warm completes, AcquireSharedCoreAsync
            // creates a client on-demand at Normal priority; PreWarmAsync swallows
            // its own failures.
            var defaults = new ServerContainerOptions();
            var hostsWithPrestart = placements.Select(p => p.Host).DistinctBy(h => h.Id).ToList();

            foreach (var host in hostsWithPrestart)
            {
                var hostNetwork = await TestNetworkManager.GetOrCreateNetworkAsync(host, ct);
                // Per-host wiring: each pool gets its host's steam-auth URL (or
                // null on non-Steam-capable hosts — Steam tests can't place
                // there, so a null URL is correct) and its host's allocator.
                var hostAllocator = _accountAllocatorByHost.GetValueOrDefault(host.Id);
                var hostSteamAuthUrl = _steamAuthByHost.TryGetValue(host.Id, out var sa)
                    ? sa.GetUrlForServer()
                    : null;
                var pool = new ClientPool(
                    host,
                    hostNetwork,
                    defaults.ImageTag,
                    defaults.GameDataVolume,
                    hostSteamAuthUrl,
                    hostAllocator
                );
                _clientPools[host.Id] = pool;
            }

            var allTasks = new List<Task>(placements.Count + hostsWithPrestart.Count);
            allTasks.AddRange(placements.Select(p => PrestartServerAsync(p.Demand, p.Host, ct)));

            foreach (var host in hostsWithPrestart)
            {
                var pool = _clientPools[host.Id];
                var hostAllocator = _accountAllocatorByHost.GetValueOrDefault(host.Id);

                // Pre-warm bound by three caps: host's client capacity, slice's
                // client-pool size, AND the max clients any discovered demand
                // actually requests. CreateClientAsync allocates a Steam account
                // while holding host.StartLimiter — pre-warming beyond the per-host
                // slice deadlocks the limiter. Each host's slice is independent, so
                // no global accumulator is needed. The demand cap matters on
                // non-Steam fleets: when no demand needs Steam, slice cap is
                // int.MaxValue, so prewarm would otherwise climb to full host
                // ClientCapacity even when every filtered test only needs 1 client.
                var localCap = hostAllocator?.ClientPoolSize ?? int.MaxValue;
                var maxDemandClients = demands.Max(d => Math.Max(1, d.Requirements.Clients));
                var prewarmCount = Math.Min(
                    Math.Min(host.ClientCapacity.Capacity, localCap),
                    maxDemandClients
                );
                if (prewarmCount > 0)
                {
                    var capDescription = localCap == int.MaxValue ? "n/a" : localCap.ToString();
                    TestLog.Server(
                        $"Pre-warming {prewarmCount} client(s) on {host.Id} (host cap {host.ClientCapacity.Capacity}, slice cap {capDescription}, demand cap {maxDemandClients})"
                    );
                    allTasks.Add(pool.PreWarmAsync(prewarmCount, ct));
                }
            }

            await Task.WhenAll(allTasks);

            TestLog.Server(
                FormattableString.Invariant(
                    $"Pre-start complete: {totalInstances} server instance(s) ready ({sw.Elapsed.TotalSeconds:F1}s)"
                )
            );
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            TestLog.Server($"Pre-start failed: {ex.GetType().Name}: {ex.Message}");
            TestLog.Server($"  {ex.StackTrace?.Split('\n').FirstOrDefault()?.Trim()}");

            // Surface the abort to summary.json. SetAborted is idempotent, so it's
            // safe alongside any future emergency abort paths. Null-conditional
            // handles the edge case where pre-start fails before InitializeAsync.
            TestSummaryFixture.Instance?.SetAborted(ex.Message);

            // Capture so AcquireAsync can fault tests on deferred configs whose
            // queues are created AFTER this catch runs. The foreach-fault below
            // only reaches queues that exist now; the placement-time GetOrAdd in
            // StartPrestart covers pre-start configs, but on-demand and deferred
            // creation paths build their own queues lazily.
            _prestartException = ex;

            // Abort the entire run. Image build failures are unrecoverable.
            // Without this, every test fails individually with the same build error.
            TestLog.Server("Pre-start failed, aborting run");
            _runCts.Cancel();

            // Signal failure to any queues that haven't been resolved yet
            foreach (var (key, queue) in _queues)
            {
                if (!queue.IsReady && !queue.IsFaulted)
                {
                    queue.ServerFailed(ex);
                }
            }
        }
    }

    private async Task PrestartServerAsync(
        ServerDemand demand,
        DockerHost host,
        CancellationToken ct
    )
    {
        var brokerKey = BrokerKeyFor(demand.Key, host);
        var queue = _queues.GetOrAdd(brokerKey, _ => new ServerQueue());

        // Increment in-flight counter so the on-demand path in AcquireSharedCoreAsync
        // sees that creation is already in flight and waits on the queue instead of
        // triggering a duplicate creation.
        _creationsInFlight.AddOrUpdate(brokerKey, 1, (_, v) => v + 1);
        try
        {
            await CreateAndResolveAsync(demand.Key, demand.Requirements, host, queue, ct);
        }
        catch
        {
            // CreateAndResolveAsync handles queue.ServerFailed in its finally block.
        }
    }

    /// <summary>
    /// Returns the per-host client pool for <paramref name="host"/>, constructing
    /// it lazily if this host saw no pre-start placements. The pool is bound to
    /// the host's bridge network; clients in this pool only join that network.
    /// </summary>
    private async Task<ClientPool> EnsureClientPoolAsync(DockerHost host, CancellationToken ct)
    {
        if (_clientPools.TryGetValue(host.Id, out var existing))
        {
            return existing;
        }

        var network = await TestNetworkManager.GetOrCreateNetworkAsync(host, ct);
        var defaults = new ServerContainerOptions();
        // Per-host steam-auth URL + allocator. Non-Steam-capable hosts get
        // nulls — correct because Steam tests are filtered out by
        // Place(requireSteam:true) before they reach here.
        var hostSteamAuthUrl = _steamAuthByHost.TryGetValue(host.Id, out var sa)
            ? sa.GetUrlForServer()
            : null;
        var hostAllocator = _accountAllocatorByHost.GetValueOrDefault(host.Id);
        var pool = new ClientPool(
            host,
            network,
            defaults.ImageTag,
            defaults.GameDataVolume,
            hostSteamAuthUrl,
            hostAllocator
        );
        // Race-safe: if another thread won, return the winner and drop ours.
        return _clientPools.GetOrAdd(host.Id, pool);
    }

    /// <summary>
    /// Acquires a server matching the given requirements. For shared servers,
    /// reuses existing or waits for server readiness. For PerTest, creates fresh.
    /// </summary>
    public async Task<ResourceLease> AcquireAsync(
        ResourceRequirements requirements,
        string testName,
        CancellationToken ct = default,
        int priority = 50
    )
    {
        // Await pre-start completion: ensures pre-start servers acquire their server
        // slots before any on-demand/deferred creation can compete. Without this, a test
        // for a deferred config can race at host.ServerCapacity.AcquireAsync and steal the
        // slot from a higher-priority pre-start server (especially when ServerSlots=1).
        // The readonly field guarantees all callers see the same Task instance.
        try
        {
            await _prestartTask;
        }
        catch
        {
            // Swallow here; the captured exception is rethrown below as a clean
            // "Pre-start failed" so each test's failure record names the real cause
            // instead of a downstream OperationCanceledException from _runCts.
        }

        // Fault every test (including those on deferred configs whose queues
        // didn't exist yet during PrestartAsync's foreach-fault) with the actual
        // pre-start cause. Without this, deferred-config tests would only see
        // OperationCanceledException from the _runCts cascade and operators would
        // have to chase the root cause through TestRunner output.
        //
        // Rethrow the captured exception directly so the test-UI / per-test error
        // shows the real cause without an outer wrapper layer.
        if (_prestartException != null)
        {
            ExceptionDispatchInfo.Capture(_prestartException).Throw();
        }

        var key = requirements.GetServerKey();
        var displayLabel = requirements.GetDisplayLabel();
        var shortTest = TestLog.Short(testName);

        TestLog.Test($"{shortTest} requesting server {displayLabel}");

        // PLACEMENT.
        //   1. Reuse-first lookup across all hosts: if any host already has a healthy
        //      instance for this key, route the test to that host and skip Place. This
        //      keeps warm-cache reuse intact even when other hosts have free capacity.
        //   2. No reuse: ask HostPool.Place to pick a host based on per-host server +
        //      client capacity. Place is racy by design (snapshot reads); the actual
        //      gating happens at host.ClientCapacity.AcquireAsync below — fairness is
        //      the queue's job.
        DockerHost host;
        var existingAnyHost = _servers.TryGetBest(key);
        if (existingAnyHost != null && !existingAnyHost.IsPoisoned && existingAnyHost.IsInitialized)
        {
            host = existingAnyHost.Host;
        }
        else
        {
            // Steam-capable filtering happens inside Place when requireSteam=true.
            // Each host runs its own steam-auth on its own bridge with the alias
            // "steam-auth", so any Steam-capable host can serve a Steam test —
            // no coordinator pin needed.
            host = HostPool.Instance.Place(
                1,
                requirements.Clients,
                requireSteam: requirements.WithSteam
            );
        }

        // For shared servers: if no instance exists on the placed host AND that host's
        // server slots are full, wait for the queue (release-and-reacquire happens
        // inside AcquireSharedCoreAsync, scoped to *this* host's capacity).
        if (requirements.Isolation != IsolationMode.PerTest)
        {
            await WaitForServerAvailableAsync(key, requirements, host, ct);
        }

        // Acquire per-host client capacity. The server is either already running on
        // `host` or we can create it without blocking on this host's server slots.
        await host.ClientCapacity.AcquireAsync(requirements.Clients, testName, priority, ct);

        if (requirements.Isolation == IsolationMode.PerTest)
        {
            try
            {
                return await AcquirePerTestAsync(key, requirements, host, testName, ct);
            }
            catch
            {
                // PerTest path doesn't temporarily release capacity, so safe to release here.
                host.ClientCapacity.Release(requirements.Clients);
                throw;
            }
        }

        // Shared path manages its own capacity lifecycle because the
        // release-while-waiting path temporarily gives up and re-acquires capacity.
        return await AcquireSharedAsync(key, requirements, host, testName, priority, ct);
    }

    /// <summary>
    /// Ensures the server for this key is available on <paramref name="host"/>
    /// BEFORE the caller acquires client capacity. If the placed host's server slots
    /// are full and no instance exists, waits for the queue to resolve. Per-host
    /// scoped: a host0-placed test doesn't wait for host1's slots to free.
    /// </summary>
    private async Task WaitForServerAvailableAsync(
        string key,
        ResourceRequirements requirements,
        DockerHost host,
        CancellationToken ct
    )
    {
        var displayLabel = requirements.GetDisplayLabel();

        // Fast path: a healthy server instance already exists *on this host*, no waiting needed.
        if (_servers.TryGetBest(key, host.Id) != null)
        {
            return;
        }

        // No healthy instance exists on this host. If this host's server slots are
        // full, we can't create one now — wait for the host's queue to resolve
        // (eviction on this host frees a slot).
        if (host.ServerCapacity.Available <= 0)
        {
            TestLog.Test(
                $"Waiting: server {displayLabel} not running on {host.Id}, "
                    + $"all {host.ServerCapacity.Capacity} slot(s) busy on host"
            );

            var brokerKey = BrokerKeyFor(key, host);
            var queue = _queues.GetOrAdd(brokerKey, _ => new ServerQueue());

            // Ensure creation is triggered (will block at host.ServerCapacity.AcquireAsync
            // until another config's server on this host is evicted and frees the slot).
            // Lock prevents concurrent threads from both seeing inFlight==0 and
            // spawning duplicate CreateAndResolveAsync tasks for the same (key, host).
            var deferredLock = _creationLocks.GetOrAdd(brokerKey, _ => new object());
            lock (deferredLock)
            {
                if (
                    !queue.IsReady
                    && !queue.IsFaulted
                    && _creationsInFlight.GetOrAdd(brokerKey, 0) == 0
                )
                {
                    TestLog.Server($"Starting deferred server {displayLabel} on {host.Id}...");
                    _creationsInFlight[brokerKey] = 1;
                    _ = CreateAndResolveAsync(key, requirements, host, queue, _runCts.Token);
                }
            }

            await queue.WaitUntilReadyAsync(ct);
            TestLog.Test($"Server {displayLabel} now available on {host.Id}");
        }

        // If this host's server slots are available, the server can be created
        // on-demand in AcquireSharedCoreAsync after we acquire client capacity.
    }

    private async Task<ResourceLease> AcquireSharedAsync(
        string key,
        ResourceRequirements requirements,
        DockerHost host,
        string testName,
        int priority,
        CancellationToken ct
    )
    {
        // Track that this test is waiting for a server with this key.
        // ReleaseAsync checks this counter to avoid evicting servers that still
        // have tests queued behind client capacity.
        _pendingDemand.AddOrUpdate(key, 1, (_, v) => v + 1);
        try
        {
            return await AcquireSharedCoreAsync(key, requirements, host, testName, priority, ct);
        }
        finally
        {
            _pendingDemand.AddOrUpdate(key, 0, (_, v) => v - 1);
        }
    }

    private async Task<ResourceLease> AcquireSharedCoreAsync(
        string key,
        ResourceRequirements requirements,
        DockerHost host,
        string testName,
        int priority,
        CancellationToken ct
    )
    {
        var displayLabel = requirements.GetDisplayLabel();
        var shortTest = TestLog.Short(testName);
        var brokerKey = BrokerKeyFor(key, host);

        // Per-host client capacity is already held (acquired in AcquireAsync before entering here).
        // This method owns the capacity lifecycle: on success, capacity transfers to the
        // caller via the returned lease. On failure, this method releases capacity itself.
        // The release-while-waiting path temporarily gives up capacity; if an exception
        // occurs in that window, capacity is already released, so nothing to clean up.
        var ownsCapacity = true;
        // Reservation held on the current loop iteration's picked instance. Set
        // when TryReserveBest returns non-null; cleared once AddRef consumes it
        // (consumeReservation: true) or an early-exit path releases it explicitly.
        // The catch block at the bottom uses this to release on throw.
        ManagedServer? reservedServer = null;
        try
        {
            // Retry loop: the server may have been evicted or poisoned. If so, retry.
            while (true)
            {
                // Pick the best available instance ON THIS HOST. For exclusive tests,
                // route to the instance already holding the gate for this test's class
                // (if any). TryReserve* atomically stakes a reservation on the chosen
                // instance under ServerPool._lock so concurrent acquirers see climbing
                // load and fan out across siblings.
                var server = requirements.Exclusive
                    ? _servers.TryReserveBestForExclusive(
                        key,
                        ManagedServer.ExtractClassName(testName),
                        host.Id
                    )
                    : _servers.TryReserveBest(key, host.Id);
                reservedServer = server;

                if (server != null)
                {
                    // Fast path: a healthy instance exists on this host
                    await server.EnsureInitializedAsync(ct);
                }
                else
                {
                    // No healthy instance on this host; ensure creation is in progress.
                    // Poisoned instances are handled by OnServerPoisoned callback.
                    var queue = _queues.GetOrAdd(brokerKey, _ => new ServerQueue());

                    // Trigger on-demand creation if no creation is in flight.
                    // Lock prevents concurrent threads from both seeing inFlight==0
                    // and spawning duplicate CreateAndResolveAsync tasks for the same (key, host).
                    var onDemandLock = _creationLocks.GetOrAdd(brokerKey, _ => new object());
                    lock (onDemandLock)
                    {
                        if (
                            !queue.IsReady
                            && !queue.IsFaulted
                            && _creationsInFlight.GetOrAdd(brokerKey, 0) == 0
                        )
                        {
                            TestLog.Server($"Creating server {displayLabel} on {host.Id}...");
                            _creationsInFlight[brokerKey] = 1;
                            _ = CreateAndResolveAsync(
                                key,
                                requirements,
                                host,
                                queue,
                                _runCts.Token
                            );
                        }
                    }

                    // The server is being created but isn't ready yet. We hold per-host
                    // client capacity, but the creation might block on the host's server
                    // slot (which requires OTHER servers' tests on this host to finish,
                    // and those tests need client capacity on this host). Always release
                    // client capacity while waiting to prevent deadlock — scoped per host
                    // so other hosts' tests aren't affected.
                    if (!queue.IsReady)
                    {
                        TestLog.Test(
                            $"{shortTest} waiting, server {displayLabel} starting on {host.Id}, releasing client meanwhile"
                        );
                        host.ClientCapacity.Release(requirements.Clients);
                        ownsCapacity = false;
                        _pendingDemand.AddOrUpdate(key, 0, (_, v) => v - 1);

                        await queue.WaitUntilReadyAsync(ct);

                        _pendingDemand.AddOrUpdate(key, 1, (_, v) => v + 1);
                        await host.ClientCapacity.AcquireAsync(
                            requirements.Clients,
                            testName,
                            priority,
                            ct
                        );
                        ownsCapacity = true;
                    }

                    // Queue is ready; restart loop to pick an instance via TryGetBest
                    continue;
                }

                // Verify the server is still valid, then claim it.
                if (_servers.Contains(key, server) && !server.IsPoisoned)
                {
                    if (requirements.Exclusive)
                    {
                        // Exclusive: acquire gate, add ref, hold gate until test finishes.
                        // Waits for existing refs to drain before returning.
                        // Uses ReleaseAndReacquireAsync to atomically enqueue a reacquire
                        // waiter (int.MinValue priority) THEN release slots, so the drain
                        // serves us before other waiters that would block on our gate.
                        await server.AddRefAndAcquireExclusiveAsync(
                            testName,
                            ct,
                            releaseAndReacquireCapacity: async () =>
                            {
                                ownsCapacity = false;
                                await host.ClientCapacity.ReleaseAndReacquireAsync(
                                    requirements.Clients,
                                    testName,
                                    int.MinValue,
                                    ct
                                );
                                ownsCapacity = true;
                            },
                            consumeReservation: true
                        );
                    }
                    else
                    {
                        // If this instance is gated, poll briefly for an ungated instance
                        // ON THE SAME HOST before committing. Without this, the test blocks
                        // on this instance's exclusive TCS even if another instance on the
                        // same host frees up seconds later.
                        if (server.HasExclusiveGate)
                        {
                            var foundUngated = false;
                            var sw = Stopwatch.StartNew();
                            while (sw.Elapsed.TotalSeconds < 30 && !ct.IsCancellationRequested)
                            {
                                await Task.Delay(TestTimings.FastPollInterval, ct);
                                var better = _servers.TryGetBest(key, host.Id);
                                if (
                                    better != null
                                    && better != server
                                    && !better.HasExclusiveGate
                                    && !better.IsPoisoned
                                    && _servers.Contains(key, better)
                                )
                                {
                                    TestLog.Test(
                                        $"{shortTest} found ungated instance on {host.Id}, re-evaluating"
                                    );
                                    foundUngated = true;
                                    break;
                                }
                            }
                            if (foundUngated)
                            {
                                // Drop the reservation we staked on the gated instance
                                // before restarting; the next iteration will reserve the
                                // ungated one.
                                server.ReleaseReservation();
                                reservedServer = null;
                                continue; // restart outer while(true) -- TryReserveBest will pick the ungated instance
                            }
                        }

                        // Non-exclusive: blocks if an exclusive test holds the gate.
                        // Release/reacquire per-host capacity while waiting so the exclusive
                        // test's ReleaseAndReacquireAsync can complete (it needs freed
                        // capacity on the same host to atomically reclaim its slot).
                        await server.AddRefExclusiveAwareAsync(
                            testName,
                            ct,
                            releaseCapacity: () =>
                            {
                                host.ClientCapacity.Release(requirements.Clients);
                                ownsCapacity = false;
                                return Task.CompletedTask;
                            },
                            reacquireCapacity: async () =>
                            {
                                await host.ClientCapacity.AcquireAsync(
                                    requirements.Clients,
                                    testName,
                                    priority,
                                    ct
                                );
                                ownsCapacity = true;
                            },
                            consumeReservation: true
                        );
                    }

                    // AddRef consumed the reservation on its success path; clear the
                    // local tracker so the catch block doesn't double-release.
                    reservedServer = null;

                    // Re-verify after AddRef: TryEvictIdleServerForAsync may have evicted
                    // this server between the _servers check above and AddRef (it checks
                    // RefCount==0, which was true until AddRef ran). If the server was
                    // evicted, undo the ref and retry to get the replacement.
                    if (!_servers.Contains(key, server) || server.IsPoisoned)
                    {
                        server.Release();
                        if (requirements.Exclusive)
                        {
                            server.ReleaseExclusive();
                        }

                        TestLog.Test(
                            $"{shortTest} server {displayLabel} was evicted after AddRef, retrying"
                        );
                        continue;
                    }

                    var clientPool = await EnsureClientPoolAsync(host, ct);
                    TestLog.Test(
                        $"{shortTest} got server {displayLabel} on {host.Id} ({server.RefCount} active tests)"
                    );
                    return new ResourceLease(server, requirements, testName, clientPool);
                }

                // Server was evicted or poisoned BEFORE AddRef ran; release the
                // reservation and retry server lookup/creation. Client capacity
                // is still held; no need to release/re-acquire.
                server.ReleaseReservation();
                reservedServer = null;
                TestLog.Test($"{shortTest} server {displayLabel} was replaced, retrying");
            }
        }
        catch
        {
            // Release any reservation we still hold (AddRef didn't consume it).
            reservedServer?.ReleaseReservation();
            if (ownsCapacity)
            {
                host.ClientCapacity.Release(requirements.Clients);
            }

            throw;
        }
    }

    /// <summary>
    /// Creates a server on <paramref name="host"/>, resolves its queue, and handles
    /// cleanup if the server arrived too late (no remaining demand). Used by both
    /// the pre-start path and the on-demand path via per-(key, host)
    /// <see cref="_creationsInFlight"/> deduplication.
    /// </summary>
    private async Task<ManagedServer> CreateAndResolveAsync(
        string key,
        ResourceRequirements requirements,
        DockerHost host,
        ServerQueue queue,
        CancellationToken ct
    )
    {
        var displayLabel = requirements.GetDisplayLabel();
        var brokerKey = BrokerKeyFor(key, host);
        var succeeded = false;
        Exception? capturedCreateException = null;
        try
        {
            var managed = await CreateServerAsync(key, requirements, host, ct);
            managed.SetPoisonCallback(OnServerPoisoned);
            queue.ServerReady();
            succeeded = true;

            // Safety net: if no tests are waiting for this key and other servers need
            // the slot ON THIS HOST, this server arrived too late. All its consumers
            // were already served by a different instance. Dispose immediately to free
            // the host's slot. Also check remainingDemand: tests may not have entered
            // AcquireSharedAsync yet (so pendingDemand=0) but are queued at the host's
            // ClientCapacity and will arrive shortly. Without this check, we'd
            // create-then-immediately-destroy the server.
            var remaining = _remainingDemand.TryGetValue(key, out var rd) ? rd : 0;
            if (
                managed.RefCount <= 0
                && GetPendingDemand(key) <= 0
                && remaining <= 0
                && host.ServerCapacity.WaitingCount > 0
            )
            {
                TestLog.Server(
                    $"{displayLabel} created on {host.Id} but no remaining demand with "
                        + $"{host.ServerCapacity.WaitingCount} waiter(s) on host, disposing to free slot"
                );
                _servers.TryRemove(key, managed);
                ReleaseSteamAccount(managed);
                if (_servers.TryGetBest(key, host.Id) == null)
                {
                    queue.Reset();
                }

                InfrastructureEventLog.Emit(
                    "server_disposed",
                    new
                    {
                        server = key,
                        instanceId = managed.InstanceId,
                        reason = "no_demand_on_ready",
                        host_id = host.Id,
                    }
                );
                await managed.DisposeAsync();
            }
            else
            {
                TestLog.Server($"{displayLabel} ready on {host.Id}");
            }

            return managed;
        }
        catch (Exception ex)
        {
            // Stash the real cause so the finally's BuildAcquisitionFault can
            // propagate it when this isn't a stopOnFail abort.
            capturedCreateException = ex;
            TestLog.Server(
                $"{displayLabel} creation FAILED on {host.Id}: {ex.GetType().Name}: {ex.Message}"
            );
            TestLog.Server($"  {ex.StackTrace?.Split('\n').FirstOrDefault()?.Trim()}");
            var serverIndex = ex.Data["serverIndex"] as int?;
            InfrastructureEventLog.Emit(
                "server_creation_failed",
                new
                {
                    server = key,
                    displayLabel,
                    serverIndex,
                    error = ex.Message,
                    host_id = host.Id,
                }
            );
            // Poison the host on a transport fault. This catch runs under the
            // triggering test's EC (fire-and-forget, no SuppressFlow), so
            // host_disconnected is attributed to the test that hit the dead host.
            await host.PoisonIfTransportFaultAsync(ex);
            throw;
        }
        finally
        {
            // Lock so the decrement and the fault decision are atomic w.r.t.
            // TryReuseFreedSlotAsync's check+bump on the same brokerKey.
            // Without the lock, reuse can observe inFlight==0 and start a
            // replacement creation between this decrement and the fault below,
            // faulting waiters even though a successor is already underway.
            lock (_creationLocks.GetOrAdd(brokerKey, _ => new object()))
            {
                var inFlightRemaining = _creationsInFlight.AddOrUpdate(
                    brokerKey,
                    0,
                    (_, v) => Math.Max(0, v - 1)
                );
                // Only fault the queue if ALL in-flight creations for this (key, host) have
                // completed and none succeeded. If a sibling instance succeeded, queue.IsReady
                // is true.
                if (!succeeded && inFlightRemaining == 0 && !queue.IsReady)
                {
                    queue.ServerFailed(
                        BuildAcquisitionFault(displayLabel, host, capturedCreateException)
                    );
                }
            }
        }
    }

    private async Task<ResourceLease> AcquirePerTestAsync(
        string key,
        ResourceRequirements requirements,
        DockerHost host,
        string testName,
        CancellationToken ct
    )
    {
        // Per-host client capacity is already held (acquired in AcquireAsync before entering here).
        var managed = await CreateServerAsync(key, requirements, host, ct);
        managed.AddRef(testName);
        var clientPool = await EnsureClientPoolAsync(host, ct);
        return new ResourceLease(managed, requirements, testName, clientPool);
    }

    private async Task<ManagedServer> CreateServerAsync(
        string key,
        ResourceRequirements requirements,
        DockerHost host,
        CancellationToken ct
    )
    {
        var sw = Stopwatch.StartNew();
        var serverIndex = Interlocked.Increment(ref _serverIndexCounter) - 1;
        var displayLabel = requirements.GetDisplayLabel();

        // Fail fast on a poisoned host: a container start against a dead daemon
        // or tunnel burns ~12s per doomed attempt. Covers every creation seam
        // (pre-start, on-demand, replacement, per-test) at the single chokepoint.
        if (host.IsPoisoned)
        {
            throw new ServerUnavailableException(
                $"Host {host.Id} is poisoned ({host.PoisonReason}); refusing to create {displayLabel}"
            );
        }

        var isShared = requirements.Isolation != IsolationMode.PerTest;
        TestLog.Server($"{displayLabel} starting up on {host.Id} (server-{serverIndex})...");

        // If THIS host's server slots are busy, try to evict an idle server on this
        // host with a different key. Eviction is host-local: if no eviction candidate
        // exists on this host, we just queue on host.ServerCapacity below — Place's
        // tiebreak prefers admitting hosts so this case is rare.
        if (host.ServerCapacity.Available <= 0)
        {
            await TryEvictIdleServerForAsync(key, host);
        }

        var slotName = $"{key}#{serverIndex}@{host.Id}";
        await host.ServerCapacity.AcquireAsync(1, slotName, ct);
        var slotWait = sw.Elapsed;

        ManagedServer? managed = null;
        var slotHeldOutsideManaged = true;
        try
        {
            var options = requirements.ToServerOptions();

            // Container env: VNC password from .env.test (optional) + per-test server password.
            var envVars = new Dictionary<string, string>();
            var vncPassword = TestEnvLoader.Get("VNC_PASSWORD");
            if (!string.IsNullOrEmpty(vncPassword))
            {
                envVars["VNC_PASSWORD"] = vncPassword;
            }

            if (requirements.Password != null)
            {
                envVars["SERVER_PASSWORD"] = requirements.Password;
            }

            var network = await TestNetworkManager.GetOrCreateNetworkAsync(host, ct);

            // Allocate a Steam account from the host's slice if this server needs
            // Steam. The slice's index space is local (0..k-1) — that's what the
            // wire format carries (?account=N, SDVD_TEST_STEAM_ACCOUNT_INDEX). The
            // host placement (Place(requireSteam:true)) guarantees this host is
            // Steam-capable; the per-host allocator was set in PrestartAsync.
            int? steamAccountIndex = null;
            if (
                requirements.WithSteam
                && _accountAllocatorByHost.TryGetValue(host.Id, out var hostAllocator)
            )
            {
                steamAccountIndex = await hostAllocator.AllocateServerAsync(ct);
            }

            var hostSteamAuth = _steamAuthByHost.TryGetValue(host.Id, out var sa) ? sa : null;
            var server = await ServerContainer.CreateAsync(
                serverIndex: serverIndex,
                options: options,
                network: network,
                host: host,
                envVars: envVars,
                logCallback: msg => TestLog.Server($"{displayLabel} {msg}"),
                ct: ct,
                sharedSteamAuth: hostSteamAuth,
                steamAccountIndex: steamAccountIndex,
                instanceId: $"server-{key}-{serverIndex}"
            );

            managed = new ManagedServer(key, server, host, requirements);
            // The slot is now owned by the ManagedServer; its DisposeAsync releases it.
            slotHeldOutsideManaged = false;
            if (steamAccountIndex.HasValue)
            {
                managed.SteamAccountIndex = steamAccountIndex.Value;
            }

            if (isShared)
            {
                _servers.Add(key, managed);
            }

            await managed.EnsureInitializedAsync(ct);

            // Ensure this host's client pool exists (lazily for hosts with no pre-start).
            await EnsureClientPoolAsync(host, ct);

            TestLog.Server(
                FormattableString.Invariant(
                    $"{displayLabel} ready on {host.Id} ({sw.Elapsed.TotalSeconds:F1}s total, waited {slotWait.TotalSeconds:F1}s for server slot)"
                )
            );
            InfrastructureEventLog.Emit(
                "server_created",
                new
                {
                    server = key,
                    instanceId = managed.InstanceId,
                    totalMs = sw.ElapsedMilliseconds,
                    slotWaitMs = (long)slotWait.TotalMilliseconds,
                    serverIndex,
                    host_id = host.Id,
                }
            );

            return managed;
        }
        catch (Exception ex)
        {
            // Stash serverIndex so the outer server_creation_failed emitter can
            // include it for correlation with the on-disk server-N/ dir.
            ex.Data["serverIndex"] = serverIndex;

            // Best-effort artifact persistence BEFORE dispose. Must run before
            // managed.DisposeAsync() tears down the container; after dispose
            // GetLogsAsync returns nothing.
            try
            {
                var failureDir = TestArtifacts.GetContainerDir($"server-{serverIndex}");

                var failure = new
                {
                    key,
                    displayLabel,
                    serverIndex,
                    host_id = host.Id,
                    exceptionType = ex.GetType().FullName,
                    message = ex.Message,
                    stackTrace = ex.StackTrace,
                    timestamp = DateTime.UtcNow.ToString("o"),
                };
                await ArtifactPrettyJson.WriteAsync(
                    Path.Combine(failureDir, "failure.json"),
                    failure
                );
            }
            catch (Exception artifactEx)
            {
                TestLog.Server(
                    $"{displayLabel} artifact save failed (original error preserved): {artifactEx.GetType().Name}: {artifactEx.Message}"
                );
            }

            // Clean up on failure to prevent server-slot leaks.
            // If a ManagedServer was constructed, it owns the slot — its DisposeAsync
            // releases it. Otherwise we still hold the raw acquire from above.
            if (managed != null)
            {
                _servers.TryRemove(key, managed);
                ReleaseSteamAccount(managed);
                try
                {
                    await managed.DisposeAsync();
                }
                catch (Exception cleanupEx)
                {
                    TestLog.Server(
                        $"{displayLabel} cleanup after failed init: {cleanupEx.Message}"
                    );
                }
            }
            else if (slotHeldOutsideManaged)
            {
                try
                {
                    host.ServerCapacity.Release(1);
                }
                catch (Exception cleanupEx)
                {
                    TestLog.Server(
                        $"{displayLabel} slot release after failed create: {cleanupEx.Message}"
                    );
                }
            }
            throw;
        }
    }

    /// <summary>
    /// Callback invoked when a managed server is poisoned.
    /// Removes from pool, resets the per-host queue, starts replacement in background.
    /// </summary>
    private void OnServerPoisoned(ManagedServer managed)
    {
        var key = managed.Key;
        var host = managed.Host;
        var displayLabel = managed.Requirements.GetDisplayLabel();
        var brokerKey = BrokerKeyFor(key, host);

        // Remove this specific poisoned instance from the pool
        _servers.TryRemove(key, managed);

        // During shutdown, don't attempt replacement; just clean up
        if (ShutdownCoordinator.IsShuttingDown || _runCts.IsCancellationRequested)
        {
            TestLog.Server(
                $"{displayLabel} poisoned during shutdown, disposing without replacement"
            );
            _ = DisposeWithoutReplacementAsync(displayLabel, managed);
            return;
        }

        var pending = GetPendingDemand(key);

        // Only replace if tests are actively using or waiting for this server.
        if (managed.RefCount > 0 || pending > 0)
        {
            TestLog.Server(
                $"{displayLabel} on {host.Id} poisoned, scheduling replacement (refs={managed.RefCount}, pending={pending})"
            );

            // Only reset the per-host queue if no other healthy instances remain on
            // this host for this key.
            if (
                _servers.TryGetBest(key, host.Id) == null
                && _queues.TryGetValue(brokerKey, out var queue)
            )
            {
                queue.Reset();
            }

            _ = ReplaceServerInBackgroundAsync(key, managed.Requirements, host, managed);
        }
        else
        {
            TestLog.Server(
                $"{displayLabel} on {host.Id} poisoned, no active or pending demand, disposing without replacement"
            );

            // Clear remaining demand and reset queue only if no other instances exist anywhere
            if (!_servers.HasHealthy(key))
            {
                _remainingDemand.TryRemove(key, out _);
            }

            if (
                _servers.TryGetBest(key, host.Id) == null
                && _queues.TryGetValue(brokerKey, out var queue)
            )
            {
                queue.Reset();
            }

            _ = DisposeWithoutReplacementAsync(displayLabel, managed);
        }
    }

    private async Task DisposeWithoutReplacementAsync(string displayLabel, ManagedServer poisoned)
    {
        try
        {
            ReleaseSteamAccount(poisoned);
            TestLog.Server($"{displayLabel} disposing (no replacement)...");
            InfrastructureEventLog.Emit(
                "server_disposed",
                new
                {
                    server = poisoned.Key,
                    instanceId = poisoned.InstanceId,
                    reason = "poisoned_no_replacement",
                }
            );
            await poisoned.DisposeAfterDrainAsync(PoisonDrainTimeout);
            TestLog.Server($"{displayLabel} disposed, server limit freed");
        }
        catch (Exception ex)
        {
            TestLog.Server($"{displayLabel} disposal failed: {ex.Message}");
        }
    }

    private async Task ReplaceServerInBackgroundAsync(
        string key,
        ResourceRequirements requirements,
        DockerHost host,
        ManagedServer? poisoned = null
    )
    {
        var displayLabel = requirements.GetDisplayLabel();
        var brokerKey = BrokerKeyFor(key, host);

        // Dispose the poisoned server to free its host server-slot.
        // DisposeAfterDrainAsync releases the slot immediately (unblocking
        // replacement creation below), then waits for active leases to drain
        // so tests can finish artifact collection against the live container.
        if (poisoned != null)
        {
            ReleaseSteamAccount(poisoned);
            try
            {
                TestLog.Server(
                    $"{displayLabel} disposing poisoned server on {host.Id} to free slot..."
                );
                InfrastructureEventLog.Emit(
                    "server_disposed",
                    new
                    {
                        server = poisoned.Key,
                        instanceId = poisoned.InstanceId,
                        reason = "poisoned_replacing",
                        host_id = host.Id,
                    }
                );
                await poisoned.DisposeAfterDrainAsync(PoisonDrainTimeout);
                TestLog.Server($"{displayLabel} poisoned server disposed");
            }
            catch (Exception ex)
            {
                TestLog.Server($"{displayLabel} poisoned server disposal failed: {ex.Message}");
            }
        }

        var queue = _queues.GetOrAdd(brokerKey, _ => new ServerQueue());
        // Ensure queue is in a fresh state for the replacement only if no other
        // healthy instance exists on this host for this key.
        if (_servers.TryGetBest(key, host.Id) == null && (queue.IsReady || queue.IsFaulted))
        {
            queue.Reset();
        }

        _creationsInFlight.AddOrUpdate(brokerKey, 1, (_, v) => v + 1);
        var replacementSucceeded = false;
        Exception? replacementError = null;
        try
        {
            TestLog.Server($"{displayLabel} replacing on {host.Id}...");
            var managed = await CreateServerAsync(key, requirements, host, _runCts.Token);
            managed.SetPoisonCallback(OnServerPoisoned);
            queue.ServerReady();
            replacementSucceeded = true;
            TestLog.Server($"{displayLabel} replacement ready on {host.Id}");
            InfrastructureEventLog.Emit(
                "server_replaced",
                new
                {
                    server = key,
                    instanceId = managed.InstanceId,
                    host_id = host.Id,
                }
            );
        }
        catch (Exception ex)
        {
            replacementError = ex;
            TestLog.Server($"{displayLabel} replacement FAILED on {host.Id}: {ex.Message}");
            InfrastructureEventLog.Emit(
                "server_replacement_failed",
                new
                {
                    server = key,
                    error = ex.Message,
                    host_id = host.Id,
                }
            );
        }
        finally
        {
            // Mirror CreateAndResolveAsync's finally: decrement and fault decision
            // are atomic w.r.t. TryReuseFreedSlotAsync's check+bump on this brokerKey.
            // Faulting unconditionally on failure (the previous behavior) would wake
            // waiters with an exception even when a sibling creation is about to
            // succeed — same race shape as the on-demand path.
            lock (_creationLocks.GetOrAdd(brokerKey, _ => new object()))
            {
                var inFlightRemaining = _creationsInFlight.AddOrUpdate(
                    brokerKey,
                    0,
                    (_, v) => Math.Max(0, v - 1)
                );
                if (!replacementSucceeded && inFlightRemaining == 0 && !queue.IsReady)
                {
                    queue.ServerFailed(BuildAcquisitionFault(displayLabel, host, replacementError));
                }
            }
        }
    }

    /// <summary>
    /// Proactively evicts an idle server with a DIFFERENT key on
    /// <paramref name="requestingHost"/> to free a server slot on that host.
    /// Called from CreateServerAsync when the host's slots are all busy.
    ///
    /// A server is eligible for eviction when refs=0 and pendingDemand=0 (no test
    /// is actively using or waiting for it). Its remainingTests may be > 0; those
    /// tests will re-create the server on demand when they arrive later. Eviction
    /// is host-local: only servers on <paramref name="requestingHost"/> are
    /// considered; cross-host eviction would defeat the per-host invariant.
    /// </summary>
    private async Task TryEvictIdleServerForAsync(string requestingKey, DockerHost requestingHost)
    {
        foreach (var (key, server) in _servers.GetAll())
        {
            if (key == requestingKey)
            {
                continue;
            }

            if (server.Host.Id != requestingHost.Id)
            {
                continue;
            }

            if (server.IsPoisoned)
            {
                continue;
            }

            if (!server.IsInitialized)
            {
                continue;
            }

            if (server.RefCount > 0)
            {
                continue;
            }

            if (GetPendingDemand(key) > 0)
            {
                continue;
            }

            // Don't evict servers that still have remaining tests. They were
            // pre-started for a reason and evicting them causes wasteful create/destroy
            // cycles. Tests for the requesting key will wait at WaitForServerAvailableAsync
            // until a server finishes naturally.
            var remaining = _remainingDemand.TryGetValue(key, out var r) ? r : 0;
            if (remaining > 0)
            {
                continue;
            }

            var evictLabel = server.Requirements.GetDisplayLabel();
            var evictBrokerKey = BrokerKeyFor(key, requestingHost);
            TestLog.Server($"{evictLabel} idle on {requestingHost.Id}, swapping out");
            InfrastructureEventLog.Emit(
                "server_evicted",
                new
                {
                    server = key,
                    requestingKey,
                    host_id = requestingHost.Id,
                }
            );

            _servers.TryRemove(key, server);
            ReleaseSteamAccount(server);

            if (
                _servers.TryGetBest(key, requestingHost.Id) == null
                && _queues.TryGetValue(evictBrokerKey, out var queue)
            )
            {
                queue.Reset();
            }

            // Broker-shutdown short-circuit. _runCts is cancelled only in
            // DisposeAsync, so IsCancellationRequested means "we're tearing
            // down". Without this, a late eviction could enqueue a task
            // after Task.WhenAll already drained the queue and leak a
            // Docker container.
            if (_runCts.IsCancellationRequested)
            {
                await server.DisposeAsync();
                return;
            }

            // Free the host server-slot synchronously so the waiting test's
            // host.ServerCapacity.AcquireAsync unblocks immediately. The later
            // ManagedServer.DisposeAsync will see _slotReleased=1 and skip
            // the second release.
            try
            {
                server.ReleaseSlotEarly();
            }
            catch (Exception ex)
            {
                TestLog.Server($"{evictLabel} early slot release failed: {ex.Message}");
            }

            // Container teardown (Docker stop-grace + recording extraction)
            // runs in the background. Eviction guards above guarantee
            // RefCount == 0 and no pending/remaining demand, so nothing
            // else can reach this instance.
            var capturedKey = key;
            var capturedServer = server;
            // SuppressFlow: this dispose runs in the background after the evicting
            // test has finished. Inheriting that test's TestContext.Current
            // misattributes recording_extracted / server_dispose_* events.
            // See .claude/rules/asynclocal-pitfalls.md.
            using (ExecutionContext.SuppressFlow())
            {
                _backgroundDisposeTasks.Enqueue(
                    Task.Run(async () =>
                    {
                        try
                        {
                            await capturedServer.DisposeAsync();
                        }
                        catch (Exception ex)
                        {
                            InfrastructureEventLog.Emit(
                                "server_dispose_background_failed",
                                new
                                {
                                    server = capturedKey,
                                    instanceId = capturedServer.InstanceId,
                                    error = ex.Message,
                                }
                            );
                        }
                    })
                );
            }
            return; // Only need to free one slot on this host
        }
    }

    /// <summary>
    /// Called after a server is disposed and its host server-slot freed. Checks if
    /// any config has enough remaining non-exclusive demand to justify an additional
    /// instance on <paramref name="freedHost"/>, and creates one to consume the freed slot.
    /// Per-host scoped: a slot freed on host0 only triggers expansion on host0.
    /// </summary>
    private async Task TryReuseFreedSlotAsync(DockerHost freedHost)
    {
        if (_runCts.IsCancellationRequested || ShutdownCoordinator.IsShuttingDown)
        {
            return;
        }

        // Slot may not be visible yet (DisposeAsync is async)
        if (freedHost.ServerCapacity.Available <= 0)
        {
            return;
        }

        string? bestKey = null;
        ServerDemand? bestDemand = null;
        var bestScore = 0;

        foreach (var demand in _discoveredDemands)
        {
            var remaining = _remainingDemand.TryGetValue(demand.Key, out var r) ? r : 0;
            if (remaining <= 0)
            {
                continue;
            }

            if (_creationsInFlight.GetOrAdd(BrokerKeyFor(demand.Key, freedHost), 0) > 0)
            {
                continue;
            }

            // Only expand if remaining tests exceed current cluster-wide instance throughput.
            // Sum each existing instance's per-host client cap so a config served by
            // hosts with different caps gets correctly-sized expansion decisions.
            var instances = _servers.GetAll(demand.Key);
            var clusterClientThroughput = instances.Sum(i => i.Host.ClientCapacity.Capacity);
            if (remaining <= clusterClientThroughput)
            {
                continue;
            }

            // Score by non-exclusive demand (exclusive tests serialize, extra instances don't help)
            var score = Math.Max(remaining - demand.ExclusiveTestCount, 0);
            if (score <= 0)
            {
                continue;
            }

            if (score > bestScore)
            {
                bestScore = score;
                bestKey = demand.Key;
                bestDemand = demand;
            }
        }

        if (bestKey == null || bestDemand == null)
        {
            return;
        }

        var brokerKey = BrokerKeyFor(bestKey, freedHost);
        var queue = _queues.GetOrAdd(brokerKey, _ => new ServerQueue());

        // Atomic re-check + bump under the per-brokerKey lock. The candidate-selection
        // loop above read _creationsInFlight without the lock; between that read and
        // here, a sibling creation could have started, OR a sibling's finally could
        // be deciding whether to fault the queue. Holding the lock guarantees we
        // either (a) bump first and the finally observes inFlight>0 → no fault, or
        // (b) lose the race and skip — but never start a redundant creation while
        // a sibling is in flight.
        lock (_creationLocks.GetOrAdd(brokerKey, _ => new object()))
        {
            if (_creationsInFlight.GetOrAdd(brokerKey, 0) > 0)
            {
                return;
            }

            _creationsInFlight.AddOrUpdate(brokerKey, 1, (_, v) => v + 1);
        }

        TestLog.Server(
            $"Reusing freed slot on {freedHost.Id}: creating {bestDemand.Requirements.GetDisplayLabel()} "
                + $"instance ({bestScore} non-exclusive tests remaining)"
        );
        InfrastructureEventLog.Emit(
            "pool_expansion",
            new
            {
                server = bestKey,
                score = bestScore,
                host_id = freedHost.Id,
            }
        );
        try
        {
            await CreateAndResolveAsync(
                bestKey,
                bestDemand.Requirements,
                freedHost,
                queue,
                _runCts.Token
            );
        }
        catch (Exception ex)
        {
            TestLog.Server($"Slot reuse failed on {freedHost.Id}: {ex.Message}");
        }
    }

    internal async Task ReleaseAsync(ManagedServer managed, bool wasExclusive = false)
    {
        var remaining = managed.Release();
        var remainingTests = DecrementRemainingDemand(managed.Key);
        var displayLabel = managed.Requirements.GetDisplayLabel();
        TestLog.Test(
            $"Done: {remaining} active tests, {remainingTests} remaining on {displayLabel}"
        );
        InfrastructureEventLog.Emit(
            "server_released",
            new
            {
                server = managed.Key,
                instanceId = managed.InstanceId,
                host_id = managed.Host.Id,
                refCount = remaining,
                remainingTests,
                exclusive = wasExclusive,
            }
        );

        // During shutdown, do NOT self-dispose servers here. Server disposal kills the
        // container, which crashes client game processes connected to it. Their containers
        // exit before ClientPool can extract recordings via docker exec.
        // TestResourceBroker.DisposeAsync() handles the correct ordering: clients first,
        // then servers. Servers left in _servers are cleaned up there.
        if (ShutdownCoordinator.IsShuttingDown)
        {
            if (remaining <= 0)
            {
                TestLog.Server(
                    $"{displayLabel} idle during shutdown, deferring disposal to broker"
                );
            }

            return;
        }

        if (remaining <= 0 && managed.Requirements.Isolation == IsolationMode.PerTest)
        {
            _servers.TryRemove(managed.Key, managed);
            ReleaseSteamAccount(managed);
            InfrastructureEventLog.Emit(
                "server_disposed",
                new
                {
                    server = managed.Key,
                    instanceId = managed.InstanceId,
                    reason = "per_test_release",
                    host_id = managed.Host.Id,
                }
            );
            managed.EmitAnnotationToRunningTests(
                AnnotationLevel.Info,
                "Server disposed (per-test release)"
            );
            await managed.DisposeAsync();
            _ = TryReuseFreedSlotAsync(managed.Host);
        }
        else if (remaining <= 0 && GetPendingDemand(managed.Key) <= 0)
        {
            // Server is idle (refs=0) and no tests are queued behind client capacity for it.
            // Evict only when this config is done permanently (no remaining tests).
            // Do NOT evict servers that still have tests just because another config
            // is waiting for a slot. That causes ping-pong restarts at host.ServerSlots=1
            // (~30s per swap). The waiting config's tests will block at
            // WaitForServerAvailableAsync until this config finishes naturally.
            // Demand-side eviction (TryEvictIdleServerForAsync in CreateServerAsync)
            // handles the initial swap when a new config first needs a server.
            if (remainingTests <= 0)
            {
                var host = managed.Host;
                TestLog.Server(
                    $"{displayLabel} idle on {host.Id}, no remaining demand, shutting down"
                );
                _servers.TryRemove(managed.Key, managed);
                ReleaseSteamAccount(managed);
                InfrastructureEventLog.Emit(
                    "server_disposed",
                    new
                    {
                        server = managed.Key,
                        instanceId = managed.InstanceId,
                        reason = "demand_exhausted",
                        host_id = host.Id,
                    }
                );
                await managed.DisposeAsync();

                // Sweep other idle instances of the same key (any host) that became
                // orphaned when remainingTests hit 0 but their last ReleaseAsync saw
                // remainingTests > 0. Cluster-wide because demand exhaustion is a
                // config-level concept, not a per-host one.
                //
                // Each sibling's container teardown (Docker stop-grace + recording
                // extraction) runs in the background so it doesn't sit on the last
                // test's leaseReleaseMs critical path. The synchronous test-thread
                // work — pool removal, Steam-account release, slot release, and the
                // server_disposed event emit — happens here so observers see the
                // sibling leave the pool immediately. Drained in DisposeAsync via
                // Task.WhenAll(_backgroundDisposeTasks). Mirrors the eviction path
                // (TryEvictIdleServerForAsync).
                foreach (var sibling in _servers.GetAll(managed.Key))
                {
                    if (sibling.RefCount <= 0)
                    {
                        _servers.TryRemove(managed.Key, sibling);
                        ReleaseSteamAccount(sibling);
                        InfrastructureEventLog.Emit(
                            "server_disposed",
                            new
                            {
                                server = managed.Key,
                                instanceId = sibling.InstanceId,
                                reason = "sibling_sweep",
                                host_id = sibling.Host.Id,
                            }
                        );

                        try
                        {
                            sibling.ReleaseSlotEarly();
                        }
                        catch (Exception ex)
                        {
                            TestLog.Server($"sibling early slot release failed: {ex.Message}");
                        }

                        var capturedKey = managed.Key;
                        var capturedSibling = sibling;
                        // SuppressFlow: this dispose runs in the background after the
                        // last test has finished. Inheriting that test's
                        // TestContext.Current misattributes recording_extracted and
                        // server_dispose_* events. See .claude/rules/asynclocal-pitfalls.md.
                        using (ExecutionContext.SuppressFlow())
                        {
                            _backgroundDisposeTasks.Enqueue(
                                Task.Run(async () =>
                                {
                                    try
                                    {
                                        await capturedSibling.DisposeAsync();
                                    }
                                    catch (Exception ex)
                                    {
                                        InfrastructureEventLog.Emit(
                                            "server_dispose_background_failed",
                                            new
                                            {
                                                server = capturedKey,
                                                instanceId = capturedSibling.InstanceId,
                                                error = ex.Message,
                                            }
                                        );
                                    }
                                })
                            );
                        }
                    }
                }

                // Reset the per-host queue if no other healthy instance exists on
                // this host for this key.
                var brokerKey = BrokerKeyFor(managed.Key, host);
                if (
                    _servers.TryGetBest(managed.Key, host.Id) == null
                    && _queues.TryGetValue(brokerKey, out var queue)
                )
                {
                    queue.Reset();
                }

                _ = TryReuseFreedSlotAsync(host);
            }
        }
    }

    /// <summary>
    /// Returns the Steam account index held by a managed server back to its
    /// host's allocator. <see cref="ManagedServer.Host"/> identifies which slice
    /// the index belongs to — global indices are never crossed across hosts.
    /// </summary>
    private void ReleaseSteamAccount(ManagedServer managed)
    {
        if (managed.SteamAccountIndex < 0)
        {
            return;
        }

        if (_accountAllocatorByHost.TryGetValue(managed.Host.Id, out var allocator))
        {
            allocator.Release(managed.SteamAccountIndex);
            managed.SteamAccountIndex = -1;
        }
    }

    private int GetPendingDemand(string key)
    {
        return _pendingDemand.TryGetValue(key, out var count) ? count : 0;
    }

    /// <summary>
    /// Decrements the remaining discovered-test demand for a server key and returns the new value.
    /// Returns 0 if the key was never discovered (e.g., dynamically-created servers).
    /// </summary>
    private int DecrementRemainingDemand(string key)
    {
        return _remainingDemand.AddOrUpdate(key, 0, (_, v) => Math.Max(0, v - 1));
    }

    /// <summary>
    /// Notifies the broker that a test for the given server key has completed, without
    /// releasing the server ref or lease. Used by persistent sessions where intermediate
    /// tests don't own the lease. The session keeps the server alive, but the broker
    /// needs to know the test is done so <see cref="_remainingDemand"/> stays accurate.
    /// Without this, servers are never evicted because remainingTests never reaches 0.
    /// </summary>
    internal void NotifyTestCompleted(string serverKey)
    {
        var remaining = DecrementRemainingDemand(serverKey);
        var first = _servers.GetAll(serverKey).FirstOrDefault();
        var displayLabel = first != null ? first.Requirements.GetDisplayLabel() : serverKey;
        TestLog.Test($"Test completed: {remaining} remaining on {displayLabel}");
    }

    /// <summary>
    /// Called when stopOnFail triggers. Zeros all remaining demand so that
    /// servers with no active refs can be evicted, freeing environment slots
    /// for deferred configs whose tests were skipped by xUnit (never dispatched,
    /// so their DisposeAsync never runs to decrement demand).
    /// Idempotent; safe to call from multiple concurrent DisposeAsync invocations.
    /// </summary>
    private volatile bool _stopOnFailNotified;

    internal void NotifyStopOnFail()
    {
        if (_stopOnFailNotified)
        {
            return;
        }

        _stopOnFailNotified = true;

        TestLog.Server("StopOnFail: zeroing all remaining demand and cancelling run");

        // Cancel run-level token first. Prevents deferred server creation from
        // starting new containers after stopOnFail. Without this, evicting idle
        // servers frees slots that trigger CreateAndResolveAsync for deferred
        // configs, wastefully spinning up servers for cancelled tests.
        _runCts.Cancel();

        foreach (var key in _remainingDemand.Keys.ToArray())
        {
            _remainingDemand[key] = 0;
        }

        // Kick off eviction of idle servers in the background to free
        // environment slots and unblock any tests stuck at WaitForServerAvailableAsync
        // (they'll see the cancelled token and bail out).
        _ = EvictAllIdleServersAsync();
    }

    /// <summary>
    /// Evicts all servers that have refs=0 and pendingDemand=0.
    /// Used by NotifyStopOnFail to free environment slots for deferred configs.
    /// </summary>
    private async Task EvictAllIdleServersAsync()
    {
        // During shutdown, don't evict; let DisposeAsync handle ordering (clients first)
        if (ShutdownCoordinator.IsShuttingDown)
        {
            return;
        }

        foreach (var (key, server) in _servers.GetAll())
        {
            if (server.RefCount > 0)
            {
                continue;
            }

            if (GetPendingDemand(key) > 0)
            {
                continue;
            }

            var displayLabel = server.Requirements.GetDisplayLabel();
            var host = server.Host;
            TestLog.Server($"{displayLabel} idle on {host.Id}, shutting down (stopOnFail)");
            if (_servers.TryRemove(key, server))
            {
                ReleaseSteamAccount(server);

                var brokerKey = BrokerKeyFor(key, host);
                if (
                    _servers.TryGetBest(key, host.Id) == null
                    && _queues.TryGetValue(brokerKey, out var queue)
                )
                {
                    queue.Reset();
                }

                try
                {
                    await server.DisposeAsync();
                }
                catch (Exception ex)
                {
                    TestLog.Server($"{displayLabel} shutdown failed: {ex.Message}");
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        TestLog.Server("Disposing all servers...");

        // Stop container stats collection before tearing down containers
        ContainerStatsCollector.Stop();

        // Cancel run-level token. Aborts any in-flight background server creation
        // (deferred, on-demand, or poison replacement) that would otherwise continue
        // spinning up containers after the test run has ended.
        _runCts.Cancel();

        // Cancel pre-start if still running. Use shutdown token so we don't block
        // indefinitely if pre-start is stuck (e.g. Docker pull hanging).
        _prestartCts?.Cancel();
        try
        {
            await _prestartTask.WaitAsync(ShutdownCoordinator.Token);
        }
        catch (OperationCanceledException)
        { /* expected during shutdown */
        }
        catch (Exception ex)
        {
            TestLog.Server(
                $"Pre-start task faulted during shutdown: {ex.GetType().Name}: {ex.Message}"
            );
        }

        // Dispose every per-host client pool BEFORE servers. Client containers run
        // game processes connected to the servers. Once servers are stopped, client
        // processes crash and their containers may exit, making docker exec (needed
        // for ffmpeg stop and video extraction) impossible. Disposing clients first
        // lets us stop ffmpeg and extract recordings while containers are still alive.
        // Each pool is bound to its host's network, so cross-pool ordering is
        // independent — dispose them in parallel.
        var poolDisposeTasks = _clientPools
            .Values.Select(async pool =>
            {
                try
                {
                    await pool.DisposeAsync();
                }
                catch (Exception ex)
                {
                    TestLog.Client(
                        $"Failed to dispose client pool on {pool.Host.Id}: {ex.Message}"
                    );
                }
            })
            .ToArray();
        await Task.WhenAll(poolDisposeTasks);
        _clientPools.Clear();

        // Await any background server disposals enqueued by
        // TryEvictIdleServerForAsync. Placed after client-pool dispose
        // (preserves the clients-before-servers ordering invariant
        // documented above) and before the _servers loop so the shutdown
        // log and cleanup finish in one pass. _runCts is already cancelled
        // above, so any in-flight eviction now takes the synchronous path.
        try
        {
            await Task.WhenAll(_backgroundDisposeTasks);
        }
        catch (Exception ex)
        {
            TestLog.Server($"Background server dispose(s) faulted during shutdown: {ex.Message}");
        }

        // Parallelize server teardown. Heavy extraction work inside
        // ServerContainer.DisposeAsync is gated by host.ExtractLimiter, so
        // unbounded concurrency here is safe. ManagedServer.ReleaseSlotEarly is
        // Interlocked-guarded; SteamAccountAllocator.Release is lock-protected;
        // the event emitters are already concurrency-safe.
        var serverDisposeTasks = _servers
            .GetAll()
            .Select(async pair =>
            {
                var (key, server) = pair;
                var displayLabel = server.Requirements.GetDisplayLabel();
                ReleaseSteamAccount(server);
                InfrastructureEventLog.Emit(
                    "server_disposed",
                    new
                    {
                        server = key,
                        instanceId = server.InstanceId,
                        reason = "broker_shutdown",
                    }
                );
                try
                {
                    await server.DisposeAsync();
                }
                catch (Exception ex)
                {
                    TestLog.Server($"Failed to dispose {displayLabel}: {ex.Message}");
                }
            })
            .ToArray();
        await Task.WhenAll(serverDisposeTasks);
        _servers.Clear();
        _queues.Clear();

        // Flush any in-flight Steam-release POSTs in distributed-worker mode before
        // we drop the allocators. Local impl is a no-op. Bounded internally so a
        // hung coordinator can't block worker shutdown — reclaim picks up the slack.
        // Drain in parallel: per-host allocators are independent.
        if (!_accountAllocatorByHost.IsEmpty)
        {
            try
            {
                await Task.WhenAll(
                    _accountAllocatorByHost.Values.Select(a => a.DrainPendingReleasesAsync())
                );
            }
            catch (Exception ex)
            {
                TestLog.Server($"Steam allocator drain failed: {ex.Message}");
            }
        }

        // Dispose every per-host steam-auth in parallel, after all servers and
        // clients on those hosts are already gone (they were torn down above).
        // Each instance's DisposeAsync drains its own log stream + SSH tunnel
        // before tearing down the container.
        if (!_steamAuthByHost.IsEmpty)
        {
            TestLog.Server(
                $"Disposing {_steamAuthByHost.Count} per-host steam-auth container(s)..."
            );
            await Task.WhenAll(
                _steamAuthByHost.Values.Select(async sa =>
                {
                    try
                    {
                        await sa.DisposeAsync();
                    }
                    catch (Exception ex)
                    {
                        TestLog.Server($"Failed to dispose steam-auth: {ex.Message}");
                    }
                })
            );
            _steamAuthByHost.Clear();
            _accountAllocatorByHost.Clear();
        }

        // Dispose shared network (must be after all containers are gone)
        await TestNetworkManager.DisposeAsync();

        // Shutdown infrastructure event log. Async variant so we don't block a
        // thread-pool thread on the channel drain while other broker subsystems
        // are still settling.
        await InfrastructureEventLog.ShutdownAsync();

        TestLog.Server("All servers disposed");
    }

    /// <summary>
    /// Distributes <paramref name="tokens"/> across <paramref name="eligibleHosts"/>
    /// proportional to each host's *remaining* budget (Hamilton/largest-remainder).
    /// Mutates <paramref name="budgets"/> in place — subtracting placed tokens — so
    /// callers can run multiple passes (e.g., Steam first, then non-Steam) without
    /// over-allocating any host. Tokens that can't fit (cluster cap exceeded) are
    /// dropped from pre-start and arrive on-demand later via <see cref="HostPool.Place"/>.
    /// </summary>
    private static void DistributeAndAppend(
        IReadOnlyList<DockerHost> eligibleHosts,
        Dictionary<string, int> budgets,
        List<ServerDemand> tokens,
        List<(ServerDemand Demand, DockerHost Host)> placements
    )
    {
        var available = eligibleHosts
            .Where(h => budgets[h.Id] > 0)
            .Select(h => (Host: h, Budget: budgets[h.Id]))
            .ToList();
        var totalBudget = available.Sum(x => x.Budget);
        if (totalBudget <= 0)
        {
            return;
        }

        var tokenCount = Math.Min(tokens.Count, totalBudget);
        var quotas = available
            .Select(b => (b.Host, b.Budget, Exact: (double)tokenCount * b.Budget / totalBudget))
            .ToList();
        var counts = quotas.ToDictionary(
            q => q.Host.Id,
            q => Math.Min((int)Math.Floor(q.Exact), q.Budget)
        );
        var leftover = tokenCount - counts.Values.Sum();
        foreach (
            var q in quotas
                .OrderByDescending(q => q.Exact - Math.Floor(q.Exact))
                .ThenBy(q => q.Host.Id, StringComparer.Ordinal)
        )
        {
            if (leftover <= 0)
            {
                break;
            }

            if (counts[q.Host.Id] < q.Budget)
            {
                counts[q.Host.Id]++;
                leftover--;
            }
        }

        // Materialize placements in declared host order across the eligible set so
        // re-runs produce the same flat list (reuse-cache stability).
        var hostOrder = new List<DockerHost>(tokenCount);
        foreach (var host in eligibleHosts)
        {
            if (!counts.TryGetValue(host.Id, out var n))
            {
                continue;
            }

            for (var i = 0; i < n; i++)
            {
                hostOrder.Add(host);
            }
        }
        for (var i = 0; i < tokens.Count && i < hostOrder.Count; i++)
        {
            var host = hostOrder[i];
            placements.Add((tokens[i], host));
            budgets[host.Id]--;
        }
    }

    /// <summary>
    /// Allocates server instances proportionally to per-instance client throughput
    /// using Hamilton's method (largest-remainder). Each config gets at least 1
    /// slot; leftover slots go to configs with the largest fractional remainder.
    ///
    /// Weight is <c>ceil(NonExclusiveTestCount / clientsPerInstance)</c> where
    /// <c>clientsPerInstance = minHostClientCap / requirements.Clients</c> — i.e.
    /// how many instances the demand actually needs to avoid queueing on
    /// <see cref="DockerHost.ClientCapacity"/>. Weighting by raw test count
    /// inflates configs that one instance could already absorb. Exclusive tests
    /// serialize on one instance regardless of count, so they're excluded.
    /// </summary>
    private static List<(ServerDemand Demand, int Count)> AllocateInstances(
        List<ServerDemand> demands,
        int slots,
        int minHostClientCap
    )
    {
        if (demands.Count == 0 || slots <= 0)
        {
            return new List<(ServerDemand, int)>();
        }

        int InstancesNeeded(ServerDemand d)
        {
            var clientsPerInstance = Math.Max(
                1,
                minHostClientCap / Math.Max(1, d.Requirements.Clients)
            );
            var nonExclusive = Math.Max(d.NonExclusiveTestCount, 1);
            return Math.Max(1, (int)Math.Ceiling((double)nonExclusive / clientsPerInstance));
        }

        var weights = demands.Select(InstancesNeeded).ToList();
        var totalWeight = weights.Sum();

        // If more configs than slots, only top N configs get 1 slot each
        if (demands.Count > slots)
        {
            return demands.Take(slots).Select(d => (d, 1)).ToList();
        }

        // Hamilton's method: proportional allocation with largest-remainder distribution
        var entries = demands
            .Select(
                (d, i) =>
                {
                    var exact = (double)weights[i] / totalWeight * slots;
                    return new
                    {
                        Demand = d,
                        Base = Math.Max(1, (int)Math.Floor(exact)),
                        Remainder = exact - Math.Floor(exact),
                    };
                }
            )
            .ToList();

        var allocated = entries.Sum(e => e.Base);
        var remaining = slots - allocated;

        // Distribute leftover slots by largest remainder
        var indices = entries
            .Select((e, i) => (Index: i, e.Remainder))
            .OrderByDescending(x => x.Remainder)
            .Take(Math.Max(0, remaining))
            .Select(x => x.Index)
            .ToHashSet();

        // Cap each demand at its computed need: if a demand only needs 1 instance,
        // Hamilton's largest-remainder pass cannot push it above 1 even when
        // slots > demand-count. Without this, the leftover-distribution step would
        // re-introduce the over-allocation this allocator exists to prevent.
        return entries
            .Select(
                (e, i) =>
                {
                    var raw = e.Base + (indices.Contains(i) ? 1 : 0);
                    return (e.Demand, Math.Min(raw, weights[i]));
                }
            )
            .ToList();
    }
}
