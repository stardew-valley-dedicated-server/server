using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Docker.DotNet;
using DotNet.Testcontainers.Containers;
using JunimoServer.Tests.Helpers;

namespace JunimoServer.Tests.Infrastructure;

/// <summary>
/// One Docker host the coordinator can place containers on. A "host" is a
/// daemon, addressed by an endpoint URL — for the local host this is the OS
/// named pipe / Unix socket; for a remote host it's a coordinator-side
/// <c>tcp://localhost:N</c> endpoint that <see cref="TunnelManager"/> opened
/// via <c>ssh -L N:/var/run/docker.sock</c>. Docker.DotNet doesn't speak
/// <c>ssh://</c> natively, so we forward the daemon socket over SSH and dial
/// it as plain TCP.
///
/// Each host owns:
/// <list type="bullet">
///   <item>Its <see cref="DockerEndpointConfig"/> (used by every Testcontainers
///   builder via <c>.WithDockerEndpoint(host.EndpointConfig)</c>).</item>
///   <item>A long-lived <see cref="DockerClient"/> for direct API consumers
///   (<see cref="JunimoServer.Tests.Helpers.ContainerStatsCollector"/>,
///   <see cref="JunimoServer.Tests.Helpers.EmergencyCleanup"/>, etc.).</item>
///   <item>Two capacity gates: <see cref="ServerCapacity"/> and
///   <see cref="ClientCapacity"/>, each enforcing per-host server- and
///   client-slot limits with priority + settle-window semantics.</item>
/// </list>
///
/// Construction is two-phase:
///   1. The constructor takes the user-facing <c>ssh://</c> URI
///      (or null for local) and stores it for diagnostics. The api client
///      and endpoint config are unset.
///   2. <see cref="InitializeAsync"/> — for remote hosts, opens the daemon
///      socket forward via <see cref="TunnelManager"/> and builds the
///      <see cref="DockerClient"/> against <c>tcp://localhost:N</c>. For
///      local hosts, builds the client against the OS default endpoint.
///
/// The two-phase split exists because Docker.DotNet rejects <c>ssh://</c> at
/// client-construction time; we need the SSH master and the forward to be up
/// before the client materializes.
/// </summary>
public sealed class DockerHost : IAsyncDisposable
{
    public string Id { get; }
    public string? SshDestination { get; }

    /// <summary>
    /// Optional SSH private-key path (resolved, absolute or `~`-expanded).
    /// Threaded into every `ssh -M` / `ssh -O` invocation as `-i {path}`. Null
    /// means "use ~/.ssh/config + ssh-agent" — the default OpenSSH behavior.
    /// </summary>
    public string? SshKeyPath { get; }

    /// <summary>
    /// Remote Unix socket path for the Docker daemon, used as the endpoint of
    /// the <c>ssh -L</c> daemon-socket forward. Defaults to <c>/var/run/docker.sock</c>
    /// (the standard Docker location). Operators override this for hosts where
    /// the daemon listens elsewhere — e.g. macOS Docker Desktop's per-user
    /// <c>~/.docker/run/docker.sock</c>. Null/empty for local hosts (which use
    /// the OS default endpoint, not an explicit socket path).
    /// </summary>
    public string RemoteSocketPath { get; }
    public int ServerSlots { get; }
    public int ClientSlots { get; }

    /// <summary>
    /// Whether this host has an NVIDIA GPU + Container Toolkit available.
    /// Containers placed on this host call <c>WithGpuIfEnabled(host)</c> to
    /// request GPU access; hosts without GPU produce CPU-only containers
    /// (libx264 fallback for video recording, no NVENC).
    /// </summary>
    public bool HasGpu { get; }
    internal HostCapacityQueue ServerCapacity { get; }
    internal HostCapacityQueue ClientCapacity { get; }

    /// <summary>
    /// Per-host bound on concurrent <c>docker create+start</c> calls against this
    /// daemon. Independent across hosts because separate daemons share no
    /// resources. <see cref="Poison"/> releases pending waiters so the
    /// <c>host_disconnected</c> cascade fails fast instead of hanging.
    /// </summary>
    internal DockerStartLimiter StartLimiter { get; }

    /// <summary>
    /// Per-host bound on concurrent video-extraction operations (in-container
    /// ffmpeg TS→MP4 concat + Docker tar pull of the full recording) against
    /// this daemon at run-end. Independent of <see cref="StartLimiter"/> —
    /// extraction and container start gate different daemon code paths.
    /// <see cref="Poison"/> releases pending waiters.
    /// </summary>
    internal DockerExtractLimiter ExtractLimiter { get; }

    private DockerEndpointConfig? _endpointConfig;
    private DockerClient? _apiClient;
    private ForwardLease? _daemonForward;
    private readonly object _lazyInitLock = new();

    /// <summary>
    /// Host-shared host↔container clock offset (seconds). All recorders on
    /// this host translate their per-test mark timestamps via the same
    /// constant — Linux Docker containers share the host's
    /// <c>CLOCK_REALTIME</c>, so there is exactly one true offset per host
    /// and measuring it per-recorder introduces inter-recorder differential
    /// noise that makes cross-clip alignment depend on calibration luck.
    /// Materialized on first call to <see cref="GetHostClockOffsetAsync"/>
    /// and immutable thereafter.
    /// </summary>
    private double? _hostClockOffsetSec;
    private double _hostClockCalibrationRttMs;
    private int _hostClockCalibrationSamples;
    private readonly SemaphoreSlim _hostClockCalibrationLock = new(1, 1);

    /// <summary>
    /// Endpoint config wired into every Testcontainers builder for this host.
    /// Materialized eagerly by <see cref="InitializeAsync"/> in the parent
    /// (which owns the SSH forward), or lazily on first read in the child
    /// (which dials the parent's loopback listener).
    /// </summary>
    internal DockerEndpointConfig EndpointConfig
    {
        get
        {
            EnsureInitialized();
            return _endpointConfig!;
        }
    }

    /// <summary>
    /// Direct Docker.DotNet client for this host. Materialized eagerly by
    /// <see cref="InitializeAsync"/> in the parent or lazily on first read
    /// in the child.
    /// </summary>
    public DockerClient ApiClient
    {
        get
        {
            EnsureInitialized();
            return _apiClient!;
        }
    }

    /// <summary>
    /// Lazy fallback for processes that didn't call <see cref="InitializeAsync"/>
    /// (the xUnit child). Local hosts get a fresh <see cref="DockerEndpointConfig.CreateLocal"/>;
    /// remote hosts read the parent's per-host coordinator port from
    /// <see cref="RunArtifactNames.HostTunnelsEnv"/> and dial that loopback
    /// listener. The eager <see cref="InitializeAsync"/> path takes the same
    /// lock and short-circuits this on subsequent reads.
    /// </summary>
    private void EnsureInitialized()
    {
        if (_endpointConfig != null)
        {
            return;
        }

        lock (_lazyInitLock)
        {
            if (_endpointConfig != null)
            {
                return;
            }

            if (string.IsNullOrEmpty(SshDestination))
            {
                _endpointConfig = DockerEndpointConfig.CreateLocal();
                _apiClient = _endpointConfig.CreateDockerClient();
                return;
            }

            var port =
                ReadTunnelPortFromEnv(Id)
                ?? throw new InvalidOperationException(
                    $"DockerHost '{Id}' (remote: {SshDestination}) has no tunnel port. "
                        + $"In the parent process, call HostPool.PreflightAsync first. "
                        + $"In the child, ensure {RunArtifactNames.HostTunnelsEnv} is inherited from the parent."
                );
            var localEndpoint = new Uri($"tcp://localhost:{port}");
            _endpointConfig = DockerEndpointConfig.CreateRemote(localEndpoint);
            _apiClient = _endpointConfig.CreateDockerClient();
        }
    }

    private static int? ReadTunnelPortFromEnv(string hostId)
    {
        var raw = Environment.GetEnvironmentVariable(RunArtifactNames.HostTunnelsEnv);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        try
        {
            var map = JsonSerializer.Deserialize<Dictionary<string, int>>(raw);
            return map != null && map.TryGetValue(hostId, out var p) && p > 0 ? p : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Set when a host call throws a transport-class exception. <see cref="HostPool"/>
    /// filters poisoned hosts out of <c>Place</c> and emits <c>host_disconnected</c>.
    /// </summary>
    private int _poisoned;
    public bool IsPoisoned => Volatile.Read(ref _poisoned) != 0;
    public string? PoisonReason { get; private set; }

    /// <summary>
    /// Set after this host's per-host steam-auth container has come up healthy
    /// and its account slice is logged in. <see cref="HostPool.Place"/> filters
    /// to Steam-capable hosts when <c>requireSteam</c> is true. Orthogonal to
    /// <see cref="IsPoisoned"/> — a host can become poisoned at any time even
    /// after being marked Steam-capable, and the placement filter checks both
    /// (per <c>orthogonal-fields.md</c>: split lifecycle and capability into
    /// independent flags rather than overloading a single enum).
    /// </summary>
    private int _steamCapable;
    public bool IsSteamCapable => Volatile.Read(ref _steamCapable) != 0;

    internal DockerHost(
        string id,
        string? sshDestination,
        string? sshKeyPath,
        int serverSlots,
        int clientSlots,
        int concurrentStarts,
        int concurrentExtractions,
        bool hasGpu = false,
        string? remoteSocketPath = null
    )
    {
        Id = id;
        SshDestination = sshDestination;
        SshKeyPath = sshKeyPath;
        // Default to the standard Docker location when caller passes null/empty.
        // Local hosts ignore this field; remote hosts use it for the ssh -L
        // socket forward target.
        RemoteSocketPath = string.IsNullOrWhiteSpace(remoteSocketPath)
            ? "/var/run/docker.sock"
            : remoteSocketPath;
        ServerSlots = serverSlots;
        ClientSlots = clientSlots;
        HasGpu = hasGpu;
        ServerCapacity = new HostCapacityQueue($"server[{id}]", serverSlots);
        ClientCapacity = new HostCapacityQueue($"client[{id}]", clientSlots);
        StartLimiter = new DockerStartLimiter(id, concurrentStarts);
        ExtractLimiter = new DockerExtractLimiter(id, concurrentExtractions);
    }

    /// <summary>
    /// Materializes <see cref="EndpointConfig"/> and <see cref="ApiClient"/>.
    /// For local hosts, uses the OS default daemon. For remote hosts, opens
    /// a daemon-socket forward via <paramref name="tunnels"/> against
    /// <see cref="RemoteSocketPath"/> and points the client at
    /// <c>tcp://localhost:&lt;coordinatorPort&gt;</c>.
    /// </summary>
    internal async Task InitializeAsync(TunnelManager tunnels, CancellationToken ct = default)
    {
        if (_endpointConfig != null)
        {
            return;
        }

        if (string.IsNullOrEmpty(SshDestination))
        {
            // Local hosts: pure setup, identical to the lazy path.
            EnsureInitialized();
            return;
        }

        // Remote hosts: open the SSH forward outside the lock (it does I/O),
        // then commit the resulting endpoint under the lock so a concurrent
        // lazy reader can't race in with the env-var-driven path.
        var forward = await tunnels.OpenSocketForwardAsync(
            Id,
            SshDestination!,
            SshKeyPath,
            RemoteSocketPath,
            ct
        );
        lock (_lazyInitLock)
        {
            if (_endpointConfig != null)
            {
                // A lazy reader beat us here. Drop the just-opened forward —
                // the lazy path dialed the parent's existing listener (in the
                // parent, we ARE the parent, so this branch is theoretical
                // unless the child somehow shared this DockerHost instance,
                // which it doesn't). Be defensive anyway.
                _ = forward.DisposeAsync();
                return;
            }
            _daemonForward = forward;
            var localEndpoint = new Uri($"tcp://localhost:{forward.CoordinatorPort}");
            _endpointConfig = DockerEndpointConfig.CreateRemote(localEndpoint);
            _apiClient = _endpointConfig.CreateDockerClient();
        }
    }

    /// <summary>
    /// Re-opens the daemon-socket forward and rebuilds <see cref="ApiClient"/> /
    /// <see cref="EndpointConfig"/> after a transient master keepalive blip tore the
    /// forward down (the host stayed alive — <c>ssh -O check</c> passed). Without this the
    /// host's <c>ApiClient</c> stays pinned to a dead coordinator port and every later
    /// container op fails with ConnectionRefused even though the daemon is reachable.
    /// No-op (returns false) for local hosts or before <see cref="InitializeAsync"/> ran.
    /// Best-effort: opens the replacement before dropping the stale forward so a failure
    /// leaves the old endpoint in place rather than a null one.
    /// </summary>
    internal async Task<bool> HealDaemonForwardAsync(
        TunnelManager tunnels,
        CancellationToken ct = default
    )
    {
        if (string.IsNullOrEmpty(SshDestination) || _endpointConfig == null)
        {
            return false;
        }

        var fresh = await tunnels.OpenSocketForwardAsync(
            Id,
            SshDestination!,
            SshKeyPath,
            RemoteSocketPath,
            ct
        );

        ForwardLease? stale;
        DockerClient? staleClient;
        lock (_lazyInitLock)
        {
            stale = _daemonForward;
            staleClient = _apiClient;
            _daemonForward = fresh;
            var localEndpoint = new Uri($"tcp://localhost:{fresh.CoordinatorPort}");
            _endpointConfig = DockerEndpointConfig.CreateRemote(localEndpoint);
            _apiClient = _endpointConfig.CreateDockerClient();
        }

        try
        {
            staleClient?.Dispose();
        }
        catch { }
        if (stale != null)
        {
            try
            {
                await stale.DisposeAsync();
            }
            catch { }
        }

        InfrastructureEventLog.Emit("host_daemon_forward_healed", new { host_id = Id });
        return true;
    }

    /// <summary>
    /// Calibration result for the host's host↔container clock offset.
    /// <c>FromCache</c> is true when this caller observed the value cached
    /// from a prior calibration (no new exec round-trip happened).
    /// <c>CalibrationRttMs</c> is the single exec round-trip that produced the
    /// offset; it bounds the absolute calibration error at ±RttMs/2.
    /// <c>CalibrationSamples</c> is 1 (one `date` read per host).
    /// </summary>
    public readonly record struct HostClockOffset(
        double OffsetSec,
        double CalibrationRttMs,
        int CalibrationSamples,
        bool FromCache
    );

    /// <summary>
    /// Returns the constant offset that converts host-monotonic
    /// <see cref="Stopwatch"/> ticks (the coordinator's clock) to this host's
    /// container <c>CLOCK_REALTIME</c> (in seconds since the Unix epoch).
    ///
    /// <para>
    /// Calibrates on first call using <paramref name="calibrationContainer"/>
    /// as the exec target (any running container on this host works — all
    /// containers on a Linux Docker host share the host kernel's
    /// <c>CLOCK_REALTIME</c>). Subsequent calls return the cached value;
    /// the offset is constant for the host's lifetime.
    /// </para>
    ///
    /// <para>
    /// The single-host-shared offset is load-bearing for inter-recorder
    /// alignment. Independent per-recorder calibrations produced offsets
    /// that differed by tens of ms even when the underlying containers'
    /// clocks agreed to within ~1 ms — measured on the VPS via
    /// <c>tools/.playground/recording-validator/inter-container-clock-probe.sh</c>.
    /// The differential made cross-clip alignment depend on calibration
    /// luck. Sharing eliminates the differential entirely (all recorders on
    /// this host use the same number), at the cost of any absolute
    /// calibration error being uniform — which is exactly what
    /// cross-clip comparison needs.
    /// </para>
    /// </summary>
    public async Task<HostClockOffset> GetHostClockOffsetAsync(
        IContainer calibrationContainer,
        CancellationToken ct = default
    )
    {
        if (_hostClockOffsetSec is double cached)
        {
            return new HostClockOffset(
                cached,
                _hostClockCalibrationRttMs,
                _hostClockCalibrationSamples,
                FromCache: true
            );
        }

        await _hostClockCalibrationLock.WaitAsync(ct);
        try
        {
            if (_hostClockOffsetSec is double cached2)
            {
                return new HostClockOffset(
                    cached2,
                    _hostClockCalibrationRttMs,
                    _hostClockCalibrationSamples,
                    FromCache: true
                );
            }

            // Single `date +%s.%N` exec bracketed by one Stopwatch pair. The offset is
            // host-shared (this cached value is read by every recorder on the host), so
            // any residual error is common-mode and cancels in cross-clip alignment —
            // the alignment base is each clip's segments.csv-derived first-frame PTS, not
            // this offset (which only picks the `ffmpeg -ss` seek target, gated by the
            // SelectCoveringSegments slack). A prior multi-sample / smallest-RTT burst
            // bought no alignment accuracy the slack didn't already absorb, and cost one
            // exec round-trip per sample — ~6s each under Windows parallel-startup load,
            // serializing every other recorder on the lock below.
            var before = Stopwatch.GetTimestamp();
            ExecResult result;
            try
            {
                result = await calibrationContainer.ExecAsync(
                    new[] { "sh", "-c", "date +%s.%N" },
                    ct
                );
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"DockerHost '{Id}' clock calibration failed: exec threw on container "
                        + $"'{calibrationContainer.Id}'.",
                    ex
                );
            }
            var after = Stopwatch.GetTimestamp();

            if (
                !double.TryParse(
                    result.Stdout.Trim(),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var epoch
                )
                || epoch <= 1e9
            )
            {
                throw new InvalidOperationException(
                    $"DockerHost '{Id}' clock calibration failed: unparseable or "
                        + $"out-of-range `date` output '{result.Stdout.Trim()}' from container "
                        + $"'{calibrationContainer.Id}'."
                );
            }

            var hostMidpointSec = (before + after) / 2.0 / Stopwatch.Frequency;
            _hostClockOffsetSec = epoch - hostMidpointSec;
            _hostClockCalibrationRttMs = (after - before) * 1000.0 / Stopwatch.Frequency;
            _hostClockCalibrationSamples = 1;

            return new HostClockOffset(
                _hostClockOffsetSec.Value,
                _hostClockCalibrationRttMs,
                _hostClockCalibrationSamples,
                FromCache: false
            );
        }
        finally
        {
            _hostClockCalibrationLock.Release();
        }
    }

    /// <summary>
    /// Coordinator-side loopback port for this host's daemon-socket forward,
    /// or 0 if no forward is open (local host, or not yet initialized). Used
    /// by <see cref="HostPool.PreflightAsync"/> to serialize the parent's
    /// per-host listener ports into <see cref="RunArtifactNames.HostTunnelsEnv"/>
    /// for the xUnit child process to inherit.
    /// </summary>
    internal int GetCoordinatorPortOrZero() => _daemonForward?.CoordinatorPort ?? 0;

    /// <summary>
    /// Marks the host as poisoned. Idempotent — the first caller wins, emits one
    /// <c>host_disconnected</c>; later calls no-op. When <paramref name="transport"/>
    /// (a tunnel/daemon fault, vs an app-level steam-auth poison), the event
    /// carries the master's death line and a console breadcrumb points at the log.
    /// When false, NO tail is attached — a stale log from an earlier blip must not
    /// be misread as the cause of an app failure.
    /// </summary>
    public void Poison(string reason, bool transport = false)
    {
        if (Interlocked.Exchange(ref _poisoned, 1) == 0)
        {
            PoisonReason = reason;
            // Release any pending StartLimiter waiters so the host_disconnected
            // cascade fails fast instead of hanging until outer cancellation.
            // With the global limiter, waiters were unblocked by other hosts'
            // Release calls; per-host they'd hang without this.
            StartLimiter.CancelPending();
            ExtractLimiter.CancelPending();

            // Tail only for transport poisons; null (omitted) when absent — an
            // RST drop or an app poison leaves no tail.
            var tail = transport ? TunnelManager.ReadMasterLogTailForHost(Id) : "";
            InfrastructureEventLog.Emit(
                "host_disconnected",
                new
                {
                    host_id = Id,
                    reason,
                    sshMasterLogTail = string.IsNullOrEmpty(tail) ? null : tail,
                }
            );

            // Console breadcrumb so the CI job log isn't blind to a tunnel fault.
            // Names the host + log file only — the VPS IP is kept out of CI logs.
            if (transport)
            {
                TestLog.Test(
                    $"[ssh] host '{Id}' poisoned (transport): {reason}. "
                        + $"See diagnostics/ssh-master-{Id}.log"
                );
            }
        }
    }

    /// <summary>Outcome of <see cref="PoisonIfTransportFaultAsync"/>.</summary>
    internal enum TransportFaultOutcome
    {
        /// <summary>Not a transport fault (application error) — caller rethrows.</summary>
        NotTransport,

        /// <summary>A recoverable forward-scoped blip on a still-alive (or respawned) host —
        /// the host was NOT poisoned and the forwards are healing. The caller should RETRY the
        /// operation rather than fail; a fresh attempt lands on the healed forward.</summary>
        RecoveredRetry,

        /// <summary>The host was genuinely gone and got poisoned — caller rethrows; the
        /// poisoned-host cascade takes over.</summary>
        Poisoned,
    }

    /// <summary>
    /// Shared mid-run failure-seam wiring: classify <paramref name="ex"/> and either poison the
    /// host (genuine outage), report a recoverable forward blip (host kept, forwards healing —
    /// caller should retry), or report not-a-transport-fault. Called from both on-demand
    /// container-creation seams (<c>TestResourceBroker.CreateAndResolveAsync</c>,
    /// <c>ClientPool.CreateClientGuardedAsync</c>) so the decision is identical. Never throws.
    /// </summary>
    internal async Task<TransportFaultOutcome> PoisonIfTransportFaultAsync(Exception ex)
    {
        var (transportReason, forwardScoped) = TransportFaultClassifier.Classify(ex);

        // Two paths need corroboration against the master before poisoning the
        // whole host (remote only — local hosts have no master):
        //   1. A bare TimeoutException is ambiguous (slow-but-live server vs dead
        //      tunnel).
        //   2. A forward-scoped fault (loopback ConnectionRefused / ConnectionReset /
        //      ConnectionAborted / broken-pipe / EOF). On a loopback -L forward NONE of
        //      these proves the host died — the connection only ever talks to the local
        //      forward listener. A transient master keepalive blip drops ALL forwards at
        //      once while the master + host stay alive (reproduced 2026-06-26: Docker
        //      stats never stopped). Poisoning the host on this cascades every test on it.
        // In both cases, a live `ssh -O check` means the host is fine — don't poison; heal
        // the forwards instead so in-flight/next operations retry against the live daemon.
        var needsCorroboration =
            (transportReason is null && ex is TimeoutException) || forwardScoped;
        if (needsCorroboration && SshDestination is not null)
        {
            var masterAlive = await TunnelManager.Default.IsMasterAliveAsync(Id);

            // Master dead ⇒ try resurrecting it once before condemning the host. One
            // shared master carries every forward, so a transient master death (fd /
            // mux-accept-backlog exhaustion — `accept: Resource temporarily unavailable`)
            // loses the whole host even though the VPS is fine. A successful respawn means
            // the host is recoverable: don't poison. See host-poison-deadlocks-run.md.
            if (!masterAlive)
            {
                masterAlive = await TunnelManager.Default.TryRespawnMasterAsync(Id);
            }

            if (masterAlive)
            {
                // Host reachable ⇒ a forward/timeout blip or a recovered master, not a host
                // outage. Re-open the daemon-socket forward NOW so the host's ApiClient stops
                // pointing at the dead port (it is opened once in InitializeAsync and never
                // re-opened otherwise — without this every later container op fails
                // ConnectionRefused even though the daemon is reachable). Per-server -L API
                // forwards heal separately via the ManagedServer health watchdog. Best-effort.
                try
                {
                    await HealDaemonForwardAsync(TunnelManager.Default);
                }
                catch (Exception healEx)
                {
                    TestLog.Test($"[ssh] daemon forward heal failed on '{Id}': {healEx.Message}");
                }

                InfrastructureEventLog.Emit(
                    "host_forward_fault_not_poisoned",
                    new
                    {
                        host_id = Id,
                        reason = transportReason,
                        forwardScoped,
                        detail = "ssh master alive/respawned; forwards healed, host kept",
                    }
                );
                // Recoverable blip: tell the caller to retry rather than fail — a fresh attempt
                // lands on the healed forward. Only when the fault was actually transport
                // (forwardScoped, or a corroborated bare timeout); a non-transport bare timeout
                // would leave transportReason null AND forwardScoped false → NotTransport.
                return forwardScoped || transportReason is not null
                    ? TransportFaultOutcome.RecoveredRetry
                    : TransportFaultOutcome.NotTransport;
            }

            // Master dead AND respawn failed ⇒ the host really is gone. Stamp a reason for
            // the bare-timeout path (forward-scoped already has one).
            transportReason ??=
                $"ssh master for {Id} not responding to -O check after {ex.GetType().Name}";
        }

        if (transportReason is not null)
        {
            // Poison emits an event + reads the master-log tail, either of which
            // can throw. The caller invokes us from a catch before re-throwing
            // the real fault, so a throw here would mask it — best-effort only.
            try
            {
                Poison(transportReason, transport: true);
            }
            catch (Exception poisonEx)
            {
                TestLog.Test(
                    $"[ssh] best-effort transport poison failed on '{Id}': {poisonEx.Message}"
                );
            }
            return TransportFaultOutcome.Poisoned;
        }

        return TransportFaultOutcome.NotTransport;
    }

    /// <summary>
    /// Marks the host as Steam-capable. Called once by the broker after a
    /// successful steam-auth bring-up + slice login on this host. Idempotent;
    /// never reset — capability degradation is signalled via
    /// <see cref="Poison"/> (which is checked alongside this flag in
    /// <see cref="HostPool.Place"/>).
    /// </summary>
    internal void MarkSteamCapable()
    {
        Interlocked.Exchange(ref _steamCapable, 1);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            StartLimiter.Dispose();
        }
        catch { }
        try
        {
            ExtractLimiter.Dispose();
        }
        catch { }
        try
        {
            _apiClient?.Dispose();
        }
        catch { }
        if (_daemonForward != null)
        {
            try
            {
                await _daemonForward.DisposeAsync();
            }
            catch { }
        }
    }
}
