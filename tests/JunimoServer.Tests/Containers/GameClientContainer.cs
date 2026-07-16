using System.Diagnostics;
using Docker.DotNet.Models;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Images;
using DotNet.Testcontainers.Networks;
using JunimoServer.Tests.Clients;
using JunimoServer.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace JunimoServer.Tests.Containers;

/// <summary>
/// Manages a containerized game client instance for E2E testing.
/// Each container runs Stardew Valley with the JunimoTestClient mod.
/// </summary>
public class GameClientContainer : IAsyncDisposable
{
    private readonly IContainer _container;
    private readonly GameTestClient _apiClient;
    private readonly int _clientIndex;
    private readonly GameClientOptions _options;
    private readonly Action<string>? _logCallback;
    private readonly string _runId;
    private readonly string _containerName;

    // Per-host context. Set at CreateAsync time. Defaults to local host when
    // no DockerHost is supplied.
    private readonly Infrastructure.DockerHost _host;
    private readonly Infrastructure.TunnelManager _tunnels = Infrastructure.TunnelManager.Default;
    private string HostId => _host.Id;
    private string? SshDestination => _host.SshDestination;
    private string? SshKeyPath => _host.SshKeyPath;
    private Infrastructure.ForwardLease? _apiForward;
    private Infrastructure.ForwardLease? _vncForward;

    private CancellationTokenSource? _logStreamCts;
    private Task? _logStreamTask;
    private ContainerLogStreamReader? _logStreamReader;
    private ContainerRecorder? _recorder;
    private bool _containerExitDetected;
    private ContainerLogFile? _containerLog;

    // Startup log callback. Emits log lines to UI during startup phases only.
    private volatile Action<string>? _startupLogCallback;

    /// <summary>
    /// Numeric index of this client (0, 1, 2...).
    /// </summary>
    public int ClientIndex => _clientIndex;

    /// <summary>
    /// Instance ID matching the UI event bus (e.g., "client-0").
    /// </summary>
    private string InstanceId => $"client-{_clientIndex}";

    /// <summary>
    /// The internal container port for the test client API.
    /// </summary>
    public const int ContainerApiPort = 5123;

    /// <summary>
    /// The internal container port for VNC (noVNC web viewer).
    /// </summary>
    public const int ContainerVncPort = 5800;

    /// <summary>
    /// The mapped host port for the test client API.
    /// </summary>
    public int ApiPort { get; private set; }

    /// <summary>
    /// The mapped host port for the VNC web viewer.
    /// Only available when <see cref="GameClientOptions.ExposeVnc"/> is true.
    /// </summary>
    public int VncPort { get; private set; }

    /// <summary>
    /// URL for the noVNC web viewer (http://127.0.0.1:{VncPort}).
    /// Null if VNC is not exposed.
    /// </summary>
    public string? VncUrl => VncPort > 0 ? $"http://127.0.0.1:{VncPort}" : null;

    /// <summary>
    /// Base URL for the test client API (http://localhost:{ApiPort}).
    /// </summary>
    public string BaseUrl => $"http://localhost:{ApiPort}";

    /// <summary>
    /// The Steam account index allocated to this client, or -1 if none.
    /// Set by <see cref="Infrastructure.ClientPool"/> after creation.
    /// </summary>
    public int SteamAccountIndex { get; internal set; } = -1;

    /// <summary>
    /// Final Galaxy auth state captured from <c>/health</c> when readiness was reached.
    /// Only populated when the container was created with <c>requireGalaxyResolved=true</c>
    /// (i.e. it bears a Steam account). Values mirror the test-client side:
    /// "signed_in" | "failed" | "lost" | "disabled" | "pending" | null.
    /// </summary>
    public string? GalaxyState { get; private set; }

    /// <summary>
    /// HTTP client for controlling the game client.
    /// </summary>
    public GameTestClient Client => _apiClient;

    /// <summary>
    /// The underlying Testcontainers container.
    /// </summary>
    public IContainer Container => _container;

    /// <summary>
    /// Sets or clears the startup log callback. When set, each log line from
    /// StreamLogsAsync is forwarded to this callback (for UI progress events).
    /// </summary>
    public void SetStartupLogCallback(Action<string>? callback)
    {
        _startupLogCallback = callback;
    }

    private GameClientContainer(
        IContainer container,
        int clientIndex,
        string runId,
        string containerName,
        GameClientOptions options,
        Action<string>? logCallback,
        Infrastructure.DockerHost host
    )
    {
        _container = container;
        _clientIndex = clientIndex;
        _runId = runId;
        _containerName = containerName;
        _options = options;
        _logCallback = logCallback;
        _host = host;
        _apiClient = new GameTestClient(); // Will be reconfigured after start
    }

    /// <summary>
    /// Creates a new game client container.
    /// </summary>
    /// <param name="clientIndex">Numeric index for this client (0, 1, 2...).</param>
    /// <param name="options">Configuration options.</param>
    /// <param name="network">Optional Docker network to join.</param>
    /// <param name="logCallback">Optional callback for log output.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task<GameClientContainer> CreateAsync(
        int clientIndex,
        GameClientOptions options,
        INetwork? network,
        Action<string>? logCallback,
        CancellationToken ct,
        Infrastructure.DockerHost host,
        bool requireGalaxyResolved = false
    )
    {
        var collectionName = $"client-{clientIndex}";
        SetupEventBus.EmitStep(
            "Setup",
            "Creating client container",
            SetupStepStatus.Started,
            $"image: sdvd/test-client:{options.ImageTag}",
            collectionName: collectionName
        );

        var containerName = $"sdvd-test-client-{clientIndex}-{Guid.NewGuid():N}";

        var runId = Guid.NewGuid().ToString("N")[..8];
        TestRunRegistry.Register(runId);

        // Forward declaration so the wait strategy's callback can write back to the
        // container instance once it's constructed at the bottom of this method.
        GameClientContainer? selfRef = null;

        var builder = new ContainerBuilder($"sdvd/test-client:{options.ImageTag}")
            .WithDockerEndpoint(host.EndpointConfig)
            .WithLogger(NullLogger.Instance)
            .WithImagePullPolicy(
                options.ImageTag == "local" ? PullPolicy.Never : PullPolicy.Missing
            )
            .WithName(containerName)
            .WithPortBinding(ContainerApiPort, true) // Dynamic host port
            .WithVolumeMount(options.GameDataVolume, "/data/game")
            .WithEnvironment("JUNIMO_TEST_PORT", ContainerApiPort.ToString())
            .WithEnvironment("STEAM_AUTH_URL", options.SteamAuthUrl ?? "")
            .WithEnvironment("SDVD_TEST_STEAM_ACCOUNT_INDEX", options.SteamAccountIndex.ToString())
            .WithEnvironment("CLIENT_TPS", TestEnvLoader.Get("CLIENT_TPS") ?? "60")
            // Kill-switch for the TPS-agnostic pacing patches, same as the server container: pass
            // .env.test's value through (default-on) so a run can set it =false to A/B the client's
            // movement pacing. The test client registers the same patches as the server mod.
            .WithEnvironment(
                "SDVD_TPS_AGNOSTIC_PACING",
                TestEnvLoader.Get("SDVD_TPS_AGNOSTIC_PACING") ?? "true"
            )
            // CLIENT_FPS drives both the in-container draw cap and the recorder's
            // sample rate (they're literally the same value; sampling X11 faster
            // than the framebuffer updates is wasted). 0 = rendering disabled.
            .WithEnvironment("CLIENT_FPS", RecordingPolicy.ClientFps.ToString())
            // Render resolution = recording resolution. The mod resizes the window
            // to these dims on launch; the recorder's x11grab captures at the same
            // RecordingPolicy.Width/Height, so display and capture cannot drift.
            .WithEnvironment("DISPLAY_WIDTH", RecordingPolicy.Width.ToString())
            .WithEnvironment("DISPLAY_HEIGHT", RecordingPolicy.Height.ToString())
            .WithEnvironment("KEEP_APP_RUNNING", "1") // Keep container alive if game crashes so ffmpeg recording can still be extracted
            // Silence the three Xvnc stats loggers that emit a 14-line summary
            // on every VNC disconnect. Connections stays at default verbosity so
            // accept/error events still surface. Read by the base image's
            // /etc/services.d/xvnc/params script.
            .WithEnvironment(
                "XVNC_SERVER_CUSTOM_PARAMS",
                "-Log EncodeManager:stderr:0,VNCSConnST:stderr:0,ComparingUpdateTracker:stderr:0"
            )
            .WithTmpfsMount("/config") // Override base image's VOLUME /config to prevent orphaned anonymous volumes
            // GPU passthrough — per-host, no-op on hosts without GPU
            .WithGpuIfEnabled(host)
            .WithCreateParameterModifier(p =>
            {
                p.HostConfig ??= new HostConfig();
                p.HostConfig.CapAdd ??= new List<string>();
                p.HostConfig.CapAdd.Add("SYS_TIME");
                p.Labels ??= new Dictionary<string, string>();
                p.Labels["sdvd.test"] = "true";
                p.Labels["sdvd.run-id"] = runId;
            })
            // WaitUntilGameReadyInContainer probes /health, which is a strict
            // superset of /ping (it also returns tickCount + isFrozen). The
            // previous /ping pre-check halved the docker-exec rate but added
            // latency to every starting client; WaitUntilGameReadyInContainer
            // alone is correct.
            .WithWaitStrategy(
                Wait.ForUnixContainer()
                    .AddCustomWaitStrategy(
                        new WaitUntilGameReadyInContainer(
                            ContainerApiPort,
                            label: $"client-{clientIndex}",
                            requireGalaxyResolved: requireGalaxyResolved,
                            onGalaxyStateResolved: state =>
                            {
                                if (selfRef != null)
                                {
                                    selfRef.GalaxyState = state;
                                }
                            }
                        )
                    )
            );

        // Conditionally expose VNC port for visual observation
        if (options.ExposeVnc)
        {
            builder = builder.WithPortBinding(ContainerVncPort, true);
        }

        if (network != null)
        {
            builder = builder.WithNetwork(network).WithNetworkAliases($"test-client-{clientIndex}");
        }

        var container = builder.Build();

        SetupEventBus.EmitStep(
            "Setup",
            "Creating client container",
            SetupStepStatus.Completed,
            $"container: {containerName}",
            collectionName: collectionName
        );

        var instance = new GameClientContainer(
            container,
            clientIndex,
            runId,
            containerName,
            options,
            logCallback,
            host
        );
        selfRef = instance;
        return instance;
    }

    /// <summary>
    /// Starts the container and waits for the API to be ready.
    /// </summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        SetupEventBus.EmitStep(
            "Setup",
            "Starting client container",
            SetupStepStatus.Started,
            collectionName: InstanceId
        );

        var _startSw = Stopwatch.StartNew();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_options.StartupTimeout);

        // Remote hosts only: tighter daemon-responsiveness deadline on docker create+start —
        // same rationale as ServerContainer.StartAsync. A wedged shared daemon-socket forward
        // otherwise hangs this call the full StartupTimeout (120s). 75s flags it decisively
        // (healthy client start is well under that); the trip surfaces as TimeoutException, which
        // ClientPool's create seam routes through PoisonIfTransportFaultAsync (corroborate via
        // ssh -O check → heal+retry if the master is alive, poison if dead).
        using var remoteDaemonCts = CancellationTokenSource.CreateLinkedTokenSource(
            timeoutCts.Token
        );
        if (SshDestination is not null)
        {
            var remoteStartTimeoutS =
                int.TryParse(
                    Environment.GetEnvironmentVariable("SDVD_REMOTE_DAEMON_START_TIMEOUT_S"),
                    out var s
                )
                && s > 0
                    ? s
                    : 75;
            remoteDaemonCts.CancelAfter(TimeSpan.FromSeconds(remoteStartTimeoutS));
        }

        // Start log streaming before container start (same pattern as ServerContainer).
        // Emits each log line to UI via _startupLogCallback during startup phases.
        _containerExitDetected = false;
        _startupLogCallback = detail =>
            SetupEventBus.EmitStep(
                "Setup",
                "Starting client container",
                SetupStepStatus.InProgress,
                detail,
                collectionName: InstanceId
            );
        _containerLog = new ContainerLogFile($"client-{_clientIndex}");
        _logStreamCts = new CancellationTokenSource();
        // SuppressFlow: log-streaming outlives the test that started this container
        // and emits events for many subsequent tests. Without this it would carry
        // the starting test's TestContext.Current to every later log line.
        // See .claude/rules/asynclocal-pitfalls.md.
        using (ExecutionContext.SuppressFlow())
        {
            _logStreamTask = Task.Run(() => StreamLogsAsync(_logStreamCts.Token));
        }

        try
        {
            // Boundary marker: caller has already passed host.StartLimiter, so
            // any further wall time goes to Testcontainers (docker create + start
            // on the daemon, then the wait strategies). Pairs with container_started
            // to localize slow phases — a long elapsedMs here with a fast wait-
            // strategy log means daemon contention; the inverse means cont-init.d
            // or game launch is the bottleneck.
            JunimoServer.Tests.Helpers.InfrastructureEventLog.Emit(
                "container_start_invoked",
                new
                {
                    role = "client",
                    name = _containerName,
                    index = _clientIndex,
                    host_id = HostId,
                }
            );

            // WaitAsync(ct) makes cancellation prompt: when the CT fires we throw
            // OperationCanceledException within milliseconds, even if Testcontainers'
            // internal wait strategies are mid-ExecAsync (which doesn't poll the CT
            // and can hold us for tens of seconds on a busy daemon). The container
            // start task continues in the background; cleanup is best-effort.
            // Without this, a 300s per-test timeout can take 360s+ to actually
            // unblock the test body, starving the per-host client-capacity gate.
            // remoteDaemonCts == timeoutCts for local hosts; tighter deadline for remote.
            await _container.StartAsync(remoteDaemonCts.Token).WaitAsync(remoteDaemonCts.Token);

            JunimoServer.Tests.Helpers.InfrastructureEventLog.Emit(
                "container_started",
                new
                {
                    role = "client",
                    name = _containerName,
                    image = $"sdvd/test-client:{_options.ImageTag}",
                    index = _clientIndex,
                    startupMs = _startSw.ElapsedMilliseconds,
                    host_id = HostId,
                }
            );
        }
        catch (Exception)
            when (remoteDaemonCts.IsCancellationRequested
                && !ct.IsCancellationRequested
                && _startSw.Elapsed < _options.StartupTimeout
            )
        {
            // Remote daemon-responsiveness deadline tripped (not the caller, not the full
            // StartupTimeout). Surface as TimeoutException so ClientPool's create seam
            // corroborates via ssh -O check and poisons-fast-or-retries, instead of hanging
            // to StartupTimeout. Mirrors ServerContainer.StartAsync.
            throw new TimeoutException(
                $"client-{_clientIndex} docker start exceeded the remote daemon-responsiveness "
                    + $"deadline after {_startSw.ElapsedMilliseconds}ms (daemon-socket forward wedged?)."
            );
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            // Caller cancelled. Propagate as OperationCanceledException regardless of
            // how Testcontainers wrapped it. Testcontainers' WaitStrategy throws
            // TimeoutException when the CT is cancelled (not OperationCanceledException),
            // which disguises cancellation as a timeout and produces misleading error
            // messages like "failed to start within 120s" after only 2 seconds.
            throw new OperationCanceledException(
                $"client-{_clientIndex} startup cancelled (caller CT fired)",
                ct
            );
        }
        catch (Exception ex)
        {
            // Attach container logs to any startup failure for diagnostics.
            // Skip the attempt entirely on a poisoned host — the retrieval goes
            // through the same dead daemon/tunnel and would only append its own
            // multi-line failure to the start-failure message.
            string logs;
            if (_host.IsPoisoned)
            {
                logs =
                    $"(container log retrieval skipped: host '{_host.Id}' is poisoned: {_host.PoisonReason})";
            }
            else
            {
                try
                {
                    var containerLogs = await _container.GetLogsAsync();
                    logs = $"Logs:\n{containerLogs.Stdout}\n\nErrors:\n{containerLogs.Stderr}";
                }
                catch (Exception logEx)
                {
                    logs =
                        $"(unable to retrieve container logs: {logEx.GetType().Name}: {logEx.Message})";
                }
            }

            JunimoServer.Tests.Helpers.InfrastructureEventLog.Emit(
                "container_start_failed",
                new
                {
                    role = "client",
                    name = _containerName,
                    image = $"sdvd/test-client:{_options.ImageTag}",
                    index = _clientIndex,
                    elapsedMs = _startSw.ElapsedMilliseconds,
                    exceptionType = ex.GetType().Name,
                    message = ex.Message,
                    containerExitDetected = _containerExitDetected,
                    host_id = HostId,
                }
            );

            if (_containerExitDetected)
            {
                throw new InvalidOperationException(
                    $"Game client {_clientIndex} container exited unexpectedly during startup.\n"
                        + $"The container process crashed before becoming healthy.\n\n"
                        + logs,
                    ex
                );
            }

            if (ex is TimeoutException or OperationCanceledException)
            {
                throw new TimeoutException(
                    $"Game client {_clientIndex} failed to start within {_options.StartupTimeout.TotalSeconds}s.\n"
                        + $"Common causes:\n"
                        + $"  - game-data volume not populated (run 'make up' first to download game files)\n"
                        + $"  - SMAPI initialization stuck (check logs below for errors)\n"
                        + $"  - test-client mod failed to load\n\n"
                        + logs,
                    ex
                );
            }

            throw new InvalidOperationException(
                $"Game client {_clientIndex} failed to start: {ex.Message}\n\n{logs}",
                ex
            );
        }

        // Stop emitting log lines to UI (log streaming continues for collection)
        _startupLogCallback = null;

        SetupEventBus.EmitStep(
            "Setup",
            "Starting client container",
            SetupStepStatus.Completed,
            collectionName: InstanceId
        );

        // Resolve coordinator-visible ports through TunnelManager so callers
        // never see a remote daemon's 127.0.0.1 directly. Local hosts: no-op.
        var apiMapped = _container.GetMappedPublicPort(ContainerApiPort);
        _apiForward = await _tunnels.OpenAsync(HostId, SshDestination, SshKeyPath, apiMapped, ct);
        ApiPort = _apiForward.CoordinatorPort;

        if (_options.ExposeVnc)
        {
            var vncMapped = _container.GetMappedPublicPort(ContainerVncPort);
            _vncForward = await _tunnels.OpenAsync(
                HostId,
                SshDestination,
                SshKeyPath,
                vncMapped,
                ct
            );
            VncPort = _vncForward.CoordinatorPort;
        }

        // Reconfigure the API client with the correct URL. On remote hosts it transparently
        // heals a dropped SSH forward (re-open + retry on the new port) so an in-flight join /
        // navigate survives a master keepalive blip instead of failing the test.
        _apiClient.Dispose();
        var newClient = new GameTestClient(
            BaseUrl,
            defaultWaitTimeout: null,
            liveBaseUrl: () => BaseUrl,
            healAsync: HealApiForwardAsync
        );
        // Copy the client reference (GameTestClient is a wrapper, we need to update the internal HttpClient)
        typeof(GameTestClient)
            .GetField(
                "_httpClient",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance
            )
            ?.SetValue(
                _apiClient,
                typeof(GameTestClient)
                    .GetField(
                        "_httpClient",
                        System.Reflection.BindingFlags.NonPublic
                            | System.Reflection.BindingFlags.Instance
                    )
                    ?.GetValue(newClient)
            );

        // Register emergency cleanup so container is force-removed even if DisposeAsync never runs
        var host = _host;
        var name = _containerName;
        EmergencyCleanup.Register(
            $"GameClient-{_runId}",
            () =>
            {
                try
                {
                    DockerOps.ForceRemoveContainerSync(host.ApiClient, name);
                }
                catch { }
            }
        );
    }

    /// <summary>
    /// Starts video recording if enabled. Separate from <see cref="StartAsync"/> so the
    /// caller can invoke it AFTER releasing the host start-limiter slot: recording is
    /// exec-only against the already-running container (no docker create/start), so it
    /// must not hold a create+start slot and stall other containers' starts. No-op when
    /// recording is disabled.
    /// </summary>
    public async Task StartRecordingAsync(CancellationToken ct = default)
    {
        if (!RecordingPolicy.RecordClientEnabled)
        {
            return;
        }

        SetupEventBus.EmitStep(
            "Setup",
            "Starting video recording",
            SetupStepStatus.Started,
            collectionName: InstanceId
        );
        _recorder = new ContainerRecorder(
            _container,
            _host,
            $"client-{_clientIndex}",
            RecordingPolicy.ClientFps,
            msg => _logCallback?.Invoke(msg),
            useGpu: _host.HasGpu,
            extractLimiter: _host.ExtractLimiter
        );
        if (await _recorder.StartAsync(ct))
        {
            // Keys the recorder by Testcontainers' daemon-prefixed name ("/sdvd-…")
            // to match TestBase.MarkContainerUsedAsync, which passes
            // lease.Container.Container.Name. Reachable only after a successful
            // StartAsync, so the read does not throw ResourceNotFound.
            RecordingOrchestrator.RegisterRecorder(_container.Name, _recorder);
            SetupEventBus.EmitStep(
                "Setup",
                "Starting video recording",
                SetupStepStatus.Completed,
                $"recording segments to /recordings/seg_*.ts",
                collectionName: InstanceId
            );
        }
        else
        {
            SetupEventBus.EmitStep(
                "Setup",
                "Starting video recording",
                SetupStepStatus.Failed,
                "ffmpeg failed to start",
                collectionName: InstanceId
            );
            _recorder = null;
        }
    }

    /// <summary>
    /// Connect to the server via invite code (Steam/Galaxy).
    /// </summary>
    public async Task<ConnectionResult> ConnectViaInviteCodeAsync(
        string inviteCode,
        CancellationToken ct = default
    )
    {
        var helper = new ConnectionHelper(
            _apiClient,
            new ConnectionOptions
            {
                MaxAttempts = 3,
                FarmhandMenuTimeout = _options.ConnectionTimeout,
            }
        );

        return await helper.ConnectToServerAsync(inviteCode, ct);
    }

    /// <summary>
    /// Connect to the server via LAN/IP address.
    /// </summary>
    /// <param name="address">Server address (hostname or IP).</param>
    /// <param name="port">Server game port (default: 24642).</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<ConnectionResult> ConnectViaLanAsync(
        string address,
        int port = 24642,
        CancellationToken ct = default
    )
    {
        var helper = new ConnectionHelper(
            _apiClient,
            new ConnectionOptions
            {
                MaxAttempts = 3,
                FarmhandMenuTimeout = _options.ConnectionTimeout,
            }
        );

        return await helper.ConnectViaLanAsync(address, port, ct);
    }

    /// <summary>
    /// Disconnect from the server and wait for the network connection to fully close.
    /// Without ForDisconnected, the client reaches the title screen but the server may
    /// not have processed the disconnect yet. If the container is returned to the pool
    /// and reused, the server sees a stale peer reconnecting, causing "farmhand
    /// availability failed" rejection loops that block the game thread.
    /// </summary>
    public async Task DisconnectAsync()
    {
        await _apiClient.Exit();
        await _apiClient.Wait.ForTitle(TimeSpan.FromSeconds(10));
        await _apiClient.Wait.ForDisconnected(TimeSpan.FromSeconds(10));
    }

    private async Task StreamLogsAsync(CancellationToken ct)
    {
        _logStreamReader = new ContainerLogStreamReader(
            _host.ApiClient,
            _container,
            $"client-{_clientIndex}",
            HandleLine,
            msg => _logCallback?.Invoke($"client-{_clientIndex} {msg}")
        );

        await _logStreamReader.RunAsync(ct);
    }

    private void HandleLine(string line)
    {
        _containerLog?.WriteLine(line);

        // Forward any SDVD_EVENT structured event lines (stdout fallback
        // transport from the test-client mod) to infrastructure.jsonl. Once
        // forwarded, the line is a spent machine envelope — keep it off the
        // human-facing sinks (UI ticker, operator terminal/CI log) below.
        var isEvent = SimpleContainerLogStreamer.TryForwardSdvdEvent(
            line,
            $"client-{_clientIndex}"
        );

        // Emit to UI during startup phases (callback is null post-startup)
        if (!isEvent)
        {
            _startupLogCallback?.Invoke(line);
        }

        if (!isEvent)
        {
            _logCallback?.Invoke(line);
        }
    }

    private readonly SemaphoreSlim _forwardHealLock = new(1, 1);
    private long _lastHealedPort;

    /// <summary>
    /// Re-opens the client's API <c>ssh -L</c> forward on a fresh loopback port and updates
    /// <see cref="ApiPort"/> / <see cref="BaseUrl"/>. No-op (false) on local hosts or before
    /// the port is mapped. Mirrors <c>ServerContainer.ReopenApiForwardAsync</c>.
    /// </summary>
    public async Task<bool> ReopenApiForwardAsync(CancellationToken ct = default)
    {
        if (SshDestination is null || _container is null || ApiPort == 0)
        {
            return false;
        }

        var apiMapped = _container.GetMappedPublicPort(ContainerApiPort);
        var staleLease = _apiForward;
        var stalePort = ApiPort;

        var fresh = await _tunnels.OpenAsync(HostId, SshDestination, SshKeyPath, apiMapped, ct);
        _apiForward = fresh;
        ApiPort = fresh.CoordinatorPort;

        if (staleLease != null)
        {
            try
            {
                await staleLease.DisposeAsync();
            }
            catch { }
        }

        InfrastructureEventLog.Emit(
            "client_api_forward_reopened",
            new
            {
                host_id = HostId,
                stalePort,
                freshPort = ApiPort,
                mappedPort = apiMapped,
            }
        );
        return true;
    }

    /// <summary>
    /// Corroborates the master is usable then re-opens the client forward — the heal callback
    /// wired into <see cref="GameTestClient"/> so an in-flight join/navigate retries against the
    /// fresh port. Deduped against concurrent callers. Mirrors
    /// <c>ServerContainer.HealApiForwardAsync</c>.
    /// </summary>
    public async Task<bool> HealApiForwardAsync(CancellationToken ct = default)
    {
        if (SshDestination is null)
        {
            return false;
        }

        var portBeforeWait = (long)ApiPort;
        await _forwardHealLock.WaitAsync(ct);
        try
        {
            if (Interlocked.Read(ref _lastHealedPort) != 0 && ApiPort != portBeforeWait)
            {
                return true;
            }

            if (!await _tunnels.EnsureMasterUsableAsync(HostId, ct))
            {
                return false;
            }

            var reopened = await ReopenApiForwardAsync(ct);
            if (reopened)
            {
                Interlocked.Exchange(ref _lastHealedPort, ApiPort);
            }
            return reopened;
        }
        finally
        {
            _forwardHealLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Close any open forwards immediately. Local hosts treat this as a no-op.
        if (_apiForward != null)
        {
            try
            {
                await _apiForward.DisposeAsync();
            }
            catch { }
            _apiForward = null;
        }
        if (_vncForward != null)
        {
            try
            {
                await _vncForward.DisposeAsync();
            }
            catch { }
            _vncForward = null;
        }

        // Drain the log-stream reader before closing the file sink so any
        // fully-formed lines already split out of the in-flight chunk land in
        // container.log / infrastructure.jsonl. Per drain-before-consume-
        // disposal.md — DrainAsync awaits the read loop's exit; DisposeAsync
        // only releases the CTS.
        if (_logStreamReader != null)
        {
            try
            {
                await _logStreamReader.DrainAsync(TimeSpan.FromSeconds(2));
            }
            catch { }
            try
            {
                await _logStreamReader.DisposeAsync();
            }
            catch { }
        }
        if (_logStreamCts != null)
        {
            try
            {
                _logStreamCts.Cancel();
            }
            catch { }
            if (_logStreamTask != null)
            {
                try
                {
                    await _logStreamTask.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch { }
            }
            _logStreamCts.Dispose();
        }

        if (_containerLog != null)
        {
            await _containerLog.DisposeAsync();
        }

        // Pass ShutdownCoordinator.Token so recording extraction aborts immediately on Ctrl+C
        // instead of hanging the shutdown chain on slow Docker I/O.
        var ct = ShutdownCoordinator.Token;
        _logCallback?.Invoke(
            $"[Recording] client-{_clientIndex} disposal: recorder={(_recorder != null ? _recorder.State : "null")}, container={_containerName}"
        );
        if (_recorder != null)
        {
            try
            {
                _logCallback?.Invoke(
                    $"[Recording] client-{_clientIndex}: stopping ffmpeg (IsRecording={_recorder.IsRecording})"
                );
                if (_recorder.IsRecording)
                {
                    await _recorder.StopAsync(ct);
                }

                var destPath = Path.Combine(
                    TestArtifacts.GetContainerDir($"client-{_clientIndex}"),
                    "full_recording.mp4"
                );

                // Heavy I/O (in-container ffmpeg concat + tar pull) is gated by the
                // host's ExtractLimiter so N clients tearing down in parallel don't
                // saturate the daemon. StopAsync stays outside so every ffmpeg starts
                // finalizing at the same instant rather than the trailing N−cap
                // containers recording past the test boundary while waiting their turn.
                await _host.ExtractLimiter.WaitAsync(ct);
                try
                {
                    _logCallback?.Invoke($"[Recording] client-{_clientIndex}: converting TS->MP4");
                    await _recorder.ConvertToMp4Async(ct);
                    _logCallback?.Invoke(
                        $"[Recording] client-{_clientIndex}: retrieving to {destPath}"
                    );
                    await _recorder.RetrieveFullRecordingAsync(destPath, ct);
                }
                finally
                {
                    _host.ExtractLimiter.Release();
                }
                if (File.Exists(destPath))
                {
                    _logCallback?.Invoke(
                        $"[Recording] client-{_clientIndex}: full recording saved ({new FileInfo(destPath).Length / 1024}KB)"
                    );
                    // Announce to the UI immediately — right where the file becomes available.
                    // Wrapped so any event-bus failure can't skip the container-teardown steps below.
                    try
                    {
                        SetupEventBus.EmitInstanceRecording(InstanceId, destPath);
                    }
                    catch (Exception ex)
                    {
                        _logCallback?.Invoke(
                            $"[Recording] client-{_clientIndex} emit failed: {ex.Message}"
                        );
                    }
                }
                else
                {
                    _logCallback?.Invoke(
                        $"[Recording] client-{_clientIndex}: full recording NOT found at {destPath}"
                    );
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                _logCallback?.Invoke(
                    $"[Recording] client-{_clientIndex} recording extraction cancelled (shutdown)"
                );
            }
            catch (Exception ex)
            {
                _logCallback?.Invoke(
                    $"[Recording] WARNING: client-{_clientIndex} recording retrieval error: {ex.Message}"
                );
            }
            RecordingOrchestrator.UnregisterRecorder(_container.Name);
            await _recorder.DisposeAsync();
            _recorder = null;
        }
        else
        {
            _logCallback?.Invoke(
                $"[Recording] client-{_clientIndex}: no recorder (recording was never started or already disposed)"
            );
        }

        _apiClient.Dispose();

        string? preDisposeState = null;
        try
        {
            preDisposeState = _container.State.ToString();
        }
        catch { }

        long? exitCode = null;
        try
        {
            using var ecCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            exitCode = await _container.GetExitCodeAsync(ecCts.Token);
        }
        catch
        { /* exit code is advisory */
        }

        var stopSw = Stopwatch.StartNew();
        try
        {
            await _container.DisposeAsync();
        }
        catch (Exception ex)
        {
            // Fallback: force remove via Docker.DotNet against the host's daemon
            Infrastructure.TestLog.Client(
                $"Container dispose failed, falling back to Docker.DotNet RemoveContainer: {ex.Message}"
            );
            try
            {
                await DockerOps.ForceRemoveContainerAsync(_host.ApiClient, _containerName);
            }
            catch { }
        }

        stopSw.Stop();
        JunimoServer.Tests.Helpers.InfrastructureEventLog.Emit(
            "container_stopped",
            new
            {
                role = "client",
                name = _containerName,
                index = _clientIndex,
                preDisposeState,
                exitCode,
                disposeDurationMs = stopSw.ElapsedMilliseconds,
                host_id = HostId,
            }
        );

        if (exitCode == DockerExitCodes.SigKill)
        {
            JunimoServer.Tests.Helpers.InfrastructureEventLog.Emit(
                "container_oom_killed",
                new
                {
                    role = "client",
                    name = _containerName,
                    index = _clientIndex,
                    host_id = HostId,
                }
            );
        }

        // Normal cleanup succeeded; remove emergency fallback
        EmergencyCleanup.Unregister($"GameClient-{_runId}");

        // Unregister run ID after all resources are cleaned up
        TestRunRegistry.Unregister(_runId);
    }
}
