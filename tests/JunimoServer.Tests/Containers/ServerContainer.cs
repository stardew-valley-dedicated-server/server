using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Docker.DotNet.Models;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Images;
using DotNet.Testcontainers.Networks;
using JunimoServer.Tests.Clients;
using JunimoServer.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;

namespace JunimoServer.Tests.Containers;

/// <summary>
/// Manages a server container instance for E2E testing.
/// Each instance represents a complete dedicated server environment.
/// Steam auth is handled by the shared SharedSteamAuth container, not per-server.
/// </summary>
public class ServerContainer : IAsyncDisposable
{
    private readonly IContainer _serverContainer;
    private readonly INetwork _network;
    private readonly string _savesVolume;
    private readonly ServerContainerOptions _options;
    private readonly Action<string>? _logCallback;
    private readonly int _serverIndex;
    private readonly string _runId;
    private readonly string _containerName;

    // Per-host context. Set at CreateAsync time from HostPool placement.
    // Defaults to local host when no DockerHost is supplied.
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
    private ContainerLogFile? _containerLog;

    // Startup log callback. Emits log lines to UI during startup phases only.
    // Set by StartAsync, switched by ManagedServer between phases, cleared after startup.
    private volatile Action<string>? _startupLogCallback;

    // Error detection
    private readonly List<string> _serverErrors = new();
    private readonly object _serverErrorsLock = new();
    private CancellationTokenSource? _errorCancellation;

    /// <summary>
    /// Internal container ports.
    /// </summary>
    public const int ContainerApiPort = 8080;
    public const int ContainerVncPort = 5800;
    public const int ContainerGamePort = 24642;
    public const string ContainerGamePortUdp = "24642/udp";

    /// <summary>In-container path of the server settings file (injected at create, re-read by /reload).</summary>
    public const string SettingsPath = "/config/server-settings.json";

    /// <summary>
    /// Mapped host ports (available after StartAsync).
    /// </summary>
    public int ApiPort { get; private set; }
    public int VncPort { get; private set; }
    public int GamePort { get; private set; }

    /// <summary>
    /// Base URL for the server API.
    /// </summary>
    public string BaseUrl => $"http://localhost:{ApiPort}";

    /// <summary>
    /// VNC URL for debugging.
    /// </summary>
    public string VncUrl => $"http://127.0.0.1:{VncPort}";

    /// <summary>
    /// Server invite code (available after WaitForReadyAsync).
    /// </summary>
    public string? InviteCode { get; private set; }

    /// <summary>
    /// Network alias for container-to-container communication.
    /// </summary>
    public string NetworkAlias => $"server-{_runId}";

    /// <summary>
    /// The Docker network this server is attached to.
    /// </summary>
    public INetwork Network => _network;

    /// <summary>
    /// The underlying server container.
    /// </summary>
    public IContainer Container => _serverContainer;

    /// <summary>
    /// Configuration options used to create this server.
    /// </summary>
    public ServerContainerOptions Options => _options;

    /// <summary>
    /// Numeric index of this server (for multi-server scenarios).
    /// </summary>
    public int ServerIndex => _serverIndex;

    /// <summary>Whether video recording is active on this container.</summary>
    public bool IsRecording => _recorder?.IsRecording == true;

    /// <summary>
    /// UI-facing instance ID for this server. Used when emitting the
    /// <c>instance_recording</c> event after full-recording extraction.
    /// Null when the broker did not supply an instance ID; the recording-event
    /// emit is skipped in that case.
    /// </summary>
    public string? InstanceId { get; }

    /// <summary>
    /// Unique run ID for this server instance (used for Docker labels and cleanup tracking).
    /// </summary>
    public string RunId => _runId;

    /// <summary>
    /// Returns true if any server errors have been detected.
    /// </summary>
    public bool HasErrors
    {
        get
        {
            lock (_serverErrorsLock)
            {
                return _serverErrors.Count > 0;
            }
        }
    }

    /// <summary>
    /// Gets detected server errors.
    /// </summary>
    public IReadOnlyList<string> Errors
    {
        get
        {
            lock (_serverErrorsLock)
            {
                return _serverErrors.ToList();
            }
        }
    }

    // Server error patterns to ignore (known non-fatal)
    private static readonly string[] IgnoredErrorPatterns = new[]
    {
        "XACT", // Audio initialization fails in headless mode
    };

    private ServerContainer(
        IContainer serverContainer,
        INetwork network,
        string savesVolume,
        int serverIndex,
        string runId,
        string containerName,
        ServerContainerOptions options,
        Action<string>? logCallback,
        string? instanceId,
        Infrastructure.DockerHost host
    )
    {
        _serverContainer = serverContainer;
        _network = network;
        _savesVolume = savesVolume;
        _serverIndex = serverIndex;
        _runId = runId;
        _containerName = containerName;
        _options = options;
        _logCallback = logCallback;
        InstanceId = instanceId;
        _host = host;
        _errorCancellation = new CancellationTokenSource();
    }

    /// <summary>
    /// Creates a new server container with the specified options.
    /// Connects to the shared steam-auth container when options.WithSteam is true.
    /// </summary>
    public static async Task<ServerContainer> CreateAsync(
        int serverIndex,
        ServerContainerOptions options,
        INetwork network,
        Infrastructure.DockerHost host,
        Dictionary<string, string>? envVars = null,
        Action<string>? logCallback = null,
        CancellationToken ct = default,
        SharedSteamAuth? sharedSteamAuth = null,
        int? steamAccountIndex = null,
        string? instanceId = null
    )
    {
        // Pre-start invariant: a steam-auth source must exist before any Steam-enabled
        // server is created. The broker creates a SharedSteamAuth container per host
        // and threads it here; if it's missing when options.WithSteam is true, that's
        // a broker bug.
        if (options.WithSteam && sharedSteamAuth == null)
            throw new InvalidOperationException(
                "Pre-start invariant violated: SharedSteamAuth is required when options.WithSteam is true. "
                    + "TestResourceBroker should initialize it before any server creation."
            );

        var runId = Guid.NewGuid().ToString("N")[..8];
        TestRunRegistry.Register(runId);
        var savesVolume = $"sdvd-test-saves-{runId}";
        var containerName = $"sdvd-server-{runId}";

        logCallback?.Invoke($"Creating Server {serverIndex} (farm {options.FarmType})...");

        // Build the server-settings.json bytes injected into the container at start
        // via WithResourceMapping (Testcontainers tar API). No host temp file involved.
        var settingsBytes = BuildSettingsFileBytes(options, logCallback);

        // Create the labeled saves volume on the chosen host's daemon.
        try
        {
            await DockerOps.CreateVolumeAsync(
                host.ApiClient,
                savesVolume,
                new Dictionary<string, string> { ["sdvd.test"] = "true", ["sdvd.run-id"] = runId },
                ct
            );
        }
        catch (Exception ex)
        {
            logCallback?.Invoke($"Volume create failed for {savesVolume}: {ex.Message}");
        }

        // Build server container with custom settings — pinned to the chosen host's daemon.
        var serverBuilder = new ContainerBuilder($"sdvd/server:{options.ImageTag}")
            .WithDockerEndpoint(host.EndpointConfig)
            .WithLogger(NullLogger.Instance)
            .WithImagePullPolicy(
                options.ImageTag == "local" ? PullPolicy.Never : PullPolicy.Missing
            )
            .WithName(containerName)
            .WithNetwork(network)
            .WithNetworkAliases($"server-{runId}")
            .WithPortBinding(ContainerApiPort, true)
            .WithPortBinding(ContainerVncPort, true)
            .WithPortBinding(ContainerGamePortUdp, true) // Lidgren uses UDP
            .WithVolumeMount(options.GameDataVolume, "/data/game")
            .WithVolumeMount(savesVolume, "/config/xdg/config/StardewValley")
            // Inject settings via the Testcontainers tar API so no host temp
            // file is involved (and so remote-host runs work without uploading
            // a path that doesn't exist on the daemon side).
            .WithResourceMapping(settingsBytes, SettingsPath)
            .WithEnvironment("SETTINGS_PATH", SettingsPath)
            .WithEnvironment("API_ENABLED", "true")
            .WithEnvironment("API_PORT", ContainerApiPort.ToString())
            // Performance/test settings
            .WithEnvironment("SERVER_TPS", TestEnvLoader.Get("SERVER_TPS") ?? "60")
            // SERVER_FPS drives both the in-container draw cap and the recorder's
            // sample rate (they're literally the same value; sampling X11 faster
            // than the framebuffer updates is wasted). 0 = rendering disabled.
            .WithEnvironment("SERVER_FPS", RecordingPolicy.ServerFps.ToString())
            // Render resolution = recording resolution. The mod resizes the window
            // to these dims on launch; the recorder's x11grab captures at the same
            // RecordingPolicy.Width/Height, so display and capture cannot drift.
            .WithEnvironment("DISPLAY_WIDTH", RecordingPolicy.Width.ToString())
            .WithEnvironment("DISPLAY_HEIGHT", RecordingPolicy.Height.ToString())
            .WithEnvironment("TEST_FAIL_FAST", options.FailFast.ToString().ToLowerInvariant())
            .WithEnvironment("SDVD_ENV", "test")
            // Opt-in: copy the image-staged TestFarmMod fixture into /data/Mods at startup
            // (adds a second Data/AdditionalFarms entry for the by-Id disambiguation test).
            .WithEnvironment(
                "SDVD_TEST_FIXTURE_FARM_MOD",
                options.FixtureFarmMod.ToString().ToLowerInvariant()
            )
            // GPU passthrough — per-host, no-op on hosts without GPU
            .WithGpuIfEnabled(host)
            // SYS_TIME capability for GOG Galaxy auth + Docker labels for ownership
            .WithCreateParameterModifier(p =>
            {
                p.HostConfig ??= new HostConfig();
                p.HostConfig.CapAdd ??= new List<string>();
                p.HostConfig.CapAdd.Add("SYS_TIME");
                p.Labels ??= new Dictionary<string, string>();
                p.Labels["sdvd.test"] = "true";
                p.Labels["sdvd.run-id"] = runId;
            })
            // Probe /health from *inside* the container via curl — transport-
            // independent, works for the local daemon and remote (SSH-tunneled)
            // daemons alike. Testcontainers' built-in UntilHttpRequestIsSucceeded
            // probes the daemon's mapped public port from the test process and
            // would resolve to coordinator-localhost when the daemon is remote.
            // WaitUntilGameReadyInContainer additionally rejects servers whose
            // HTTP listener is up but whose game loop has stalled (tickCount
            // unchanging or isFrozen=true) — the previous strategy returned
            // ready as soon as the listener answered.
            .WithWaitStrategy(
                Wait.ForUnixContainer()
                    .AddCustomWaitStrategy(
                        new WaitUntilGameReadyInContainer(
                            ContainerApiPort,
                            label: $"server-{serverIndex}",
                            // Server containers don't need Galaxy-resolution gating — that
                            // lives on the test-client mod's /health. /wait/health collapses
                            // ~25 cold-start probes into 1–3 round-trips on remote VPS hosts.
                            useLongPoll: true
                        )
                    )
            );

        // Tests run without VNC/API passwords by default
        serverBuilder = serverBuilder.WithEnvironment("ALLOW_INSECURE_SETUP", "true");

        // Only give STEAM_AUTH_URL to servers that need Steam lobby/invite codes.
        // Without it, the mod skips Galaxy auth and lobby creation entirely.
        // The mod reaches steam-auth via the host-internal Docker network alias.
        if (options.WithSteam)
        {
            serverBuilder = serverBuilder.WithEnvironment(
                "STEAM_AUTH_URL",
                sharedSteamAuth!.GetUrlForServer()
            );

            // Pass Steam account index so the mod uses the correct account via ?account=N
            if (steamAccountIndex.HasValue)
                serverBuilder = serverBuilder.WithEnvironment(
                    "SDVD_TEST_STEAM_ACCOUNT_INDEX",
                    steamAccountIndex.Value.ToString()
                );
        }

        // Pass non-Steam env vars to server (Steam credentials are handled by steam-auth service)
        if (envVars != null)
        {
            if (
                envVars.TryGetValue("VNC_PASSWORD", out var vncPass)
                && !string.IsNullOrEmpty(vncPass)
            )
                serverBuilder = serverBuilder.WithEnvironment("VNC_PASSWORD", vncPass);
            if (
                envVars.TryGetValue("SERVER_PASSWORD", out var serverPassword)
                && !string.IsNullOrEmpty(serverPassword)
            )
                serverBuilder = serverBuilder.WithEnvironment("SERVER_PASSWORD", serverPassword);
        }

        var serverContainer = serverBuilder.Build();

        return new ServerContainer(
            serverContainer,
            network,
            savesVolume,
            serverIndex,
            runId,
            containerName,
            options,
            logCallback,
            instanceId,
            host
        );
    }

    /// <summary>
    /// Serializes server-settings.json into the byte payload injected at container
    /// creation via Testcontainers' WithResourceMapping (tar API). Avoids any host
    /// temp file, which keeps remote-host runs honest: remote daemons cannot read
    /// coordinator-side paths.
    /// </summary>
    private static byte[] BuildSettingsFileBytes(
        ServerContainerOptions options,
        Action<string>? logCallback
    )
    {
        var settings = new
        {
            game = new
            {
                farmName = options.FarmName,
                farmType = options.FarmType.ToJsonValue(),
                profitMargin = options.ProfitMargin,
                startingCabins = options.StartingCabins,
                spawnMonstersAtNight = options.SpawnMonstersAtNight,
            },
            server = new
            {
                maxPlayers = options.MaxPlayers,
                cabinStrategy = options.CabinStrategy,
                separateWallets = options.SeparateWallets,
                existingCabinBehavior = options.ExistingCabinBehavior,
                verboseLogging = false,
                allowIpConnections = options.AllowIpConnections,
            },
        };

        var json = JsonConvert.SerializeObject(settings, Formatting.Indented);

        logCallback?.Invoke(
            $"Settings: FarmType={options.FarmType}, Cabins={options.StartingCabins}"
        );

        return Encoding.UTF8.GetBytes(json);
    }

    /// <summary>
    /// Starts the server containers and waits for them to be healthy.
    /// </summary>
    public async Task StartAsync(CancellationToken ct = default, Action<string>? onProgress = null)
    {
        var sw = Stopwatch.StartNew();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(
            ct,
            _errorCancellation!.Token
        );
        timeoutCts.CancelAfter(_options.StartupTimeout);

        try
        {
            _logStreamCts = new CancellationTokenSource();

            onProgress?.Invoke("Starting server container");
            _logCallback?.Invoke($"Starting game server...");

            // Start log streaming before the health-check wait so we can detect
            // fatal errors (e.g. "Mod crashed on entry") and abort immediately
            // instead of waiting for the full startup timeout.
            // Also emits each log line to the UI via _startupLogCallback.
            _startupLogCallback = onProgress;
            _containerLog = new ContainerLogFile($"server-{_serverIndex}");
            // SuppressFlow: log-streaming outlives the test that started this container
            // and emits events for many subsequent tests. Without this it would carry
            // the starting test's TestContext.Current to every later log line.
            // See .claude/rules/asynclocal-pitfalls.md.
            using (ExecutionContext.SuppressFlow())
            {
                _logStreamTask = Task.Run(() => StreamLogsAsync(_logStreamCts.Token));
            }

            // Boundary marker: caller has already passed host.StartLimiter, so
            // any further wall time goes to Testcontainers (docker create + start
            // on the daemon, then the wait strategies). Pairs with container_started
            // to localize slow phases.
            JunimoServer.Tests.Helpers.InfrastructureEventLog.Emit(
                "container_start_invoked",
                new
                {
                    role = "server",
                    name = _containerName,
                    index = _serverIndex,
                    host_id = HostId,
                }
            );

            // WaitAsync(ct) makes cancellation prompt: when the CT fires we throw
            // OperationCanceledException within milliseconds, even if Testcontainers'
            // internal wait strategies are mid-ExecAsync (which doesn't poll the CT
            // and can hold us for tens of seconds on a busy daemon). The container
            // start task continues in the background; cleanup is best-effort.
            await _serverContainer.StartAsync(timeoutCts.Token).WaitAsync(timeoutCts.Token);
            _logCallback?.Invoke($"Game server started");

            JunimoServer.Tests.Helpers.InfrastructureEventLog.Emit(
                "container_started",
                new
                {
                    role = "server",
                    name = _containerName,
                    image = $"sdvd/server:{_options.ImageTag}",
                    index = _serverIndex,
                    startupMs = sw.ElapsedMilliseconds,
                    host_id = HostId,
                }
            );
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // Caller cancelled; propagate without wrapping
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            // Caller cancelled. Propagate as OperationCanceledException regardless of
            // how Testcontainers wrapped it. Testcontainers' WaitStrategy throws
            // TimeoutException when the CT is cancelled (not OperationCanceledException),
            // which disguises cancellation as a timeout.
            throw new OperationCanceledException(
                $"server-{_serverIndex} startup cancelled (caller CT fired after {sw.ElapsedMilliseconds}ms)",
                ct
            );
        }
        catch (Exception ex)
        {
            // Check if the abort was triggered by a detected fatal error in the logs.
            // This gives a clear "mod crashed" message instead of a generic timeout.
            // Container logs are NOT embedded in the exception message: they are
            // streamed continuously to containers/server-N/container.log. Embedding
            // them here would duplicate the same multi-KB payload into ex.Message,
            // infrastructure.jsonl, and failure.json.
            var detectedErrors = Errors;
            var logHint = $"See containers/server-{_serverIndex}/container.log.";

            JunimoServer.Tests.Helpers.InfrastructureEventLog.Emit(
                "container_start_failed",
                new
                {
                    role = "server",
                    name = _containerName,
                    image = $"sdvd/server:{_options.ImageTag}",
                    index = _serverIndex,
                    elapsedMs = sw.ElapsedMilliseconds,
                    exceptionType = ex.GetType().Name,
                    message = ex.Message,
                    detectedErrorCount = detectedErrors.Count,
                    host_id = HostId,
                }
            );

            if (detectedErrors.Count > 0)
            {
                var errorSummary = string.Join("\n", detectedErrors);
                throw new InvalidOperationException(
                    $"server-{_serverIndex} crashed during startup: {errorSummary}. {logHint}",
                    ex
                );
            }

            if (ex is TimeoutException or OperationCanceledException)
            {
                var elapsedSec = sw.Elapsed.TotalSeconds;
                var trigger = detectedErrors.Count > 0 ? "log-detected error" : "timeout";
                throw new TimeoutException(
                    FormattableString.Invariant(
                        $"server-{_serverIndex} startup cancelled after {elapsedSec:F1}s "
                    )
                        + FormattableString.Invariant(
                            $"(configured timeout: {_options.StartupTimeout.TotalSeconds}s). "
                        )
                        + $"Trigger: {trigger}. {logHint}",
                    ex
                );
            }

            throw new InvalidOperationException(
                $"server-{_serverIndex} failed to start: {ex.Message}. {logHint}",
                ex
            );
        }

        // Resolve coordinator-visible ports through TunnelManager so callers
        // never see a remote daemon's 127.0.0.1 directly. For local hosts the
        // forward is a no-op and the coordinator-side port equals the mapped
        // port; for remote hosts it is opened over a per-forward `ssh -N -L`.
        var apiMapped = _serverContainer.GetMappedPublicPort(ContainerApiPort);
        var vncMapped = _serverContainer.GetMappedPublicPort(ContainerVncPort);
        _apiForward = await _tunnels.OpenAsync(HostId, SshDestination, SshKeyPath, apiMapped, ct);
        _vncForward = await _tunnels.OpenAsync(HostId, SshDestination, SshKeyPath, vncMapped, ct);
        ApiPort = _apiForward.CoordinatorPort;
        VncPort = _vncForward.CoordinatorPort;
        // Game/UDP port stays daemon-internal: the coordinator never connects to it.
        GamePort = _serverContainer.GetMappedPublicPort(ContainerGamePortUdp);

        onProgress?.Invoke($"Ports mapped: API={ApiPort}, VNC={VncPort}, Game={GamePort}");
        _logCallback?.Invoke($"Ports: API={ApiPort}, VNC={VncPort}, Game={GamePort}");

        // Register emergency cleanup so containers/network/volume are force-removed even if DisposeAsync never runs
        // Order: containers first (they hold references), then network, then volume
        var savesVolume = _savesVolume;
        var runId = _runId;
        var containerName = _containerName;
        var host = _host;
        EmergencyCleanup.Register(
            $"ServerContainer-{runId}",
            () =>
            {
                try
                {
                    DockerOps.ForceRemoveContainerSync(host.ApiClient, containerName);
                    DockerOps.RemoveVolumeSync(host.ApiClient, savesVolume);
                }
                catch
                { /* best effort */
                }
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
        if (!RecordingPolicy.RecordServerEnabled)
            return;

        SetupEventBus.EmitStep(
            "Setup",
            "Starting video recording",
            SetupStepStatus.Started,
            collectionName: $"server-{_serverIndex}"
        );
        _recorder = new ContainerRecorder(
            _serverContainer,
            _host,
            $"server-{_serverIndex}",
            RecordingPolicy.ServerFps,
            msg => _logCallback?.Invoke(msg),
            useGpu: _host.HasGpu,
            extractLimiter: _host.ExtractLimiter
        );
        if (await _recorder.StartAsync(ct))
        {
            // Keys the recorder by Testcontainers' daemon-prefixed name ("/sdvd-…")
            // to match TestBase.MarkContainerUsedAsync, which passes
            // Lease.Server.Container.Name. Reachable only after a successful
            // StartAsync, so the read does not throw ResourceNotFound.
            RecordingOrchestrator.RegisterRecorder(_serverContainer.Name, _recorder);
            SetupEventBus.EmitStep(
                "Setup",
                "Starting video recording",
                SetupStepStatus.Completed,
                $"recording segments to /recordings/seg_*.ts",
                collectionName: $"server-{_serverIndex}"
            );
        }
        else
        {
            SetupEventBus.EmitStep(
                "Setup",
                "Starting video recording",
                SetupStepStatus.Failed,
                "ffmpeg failed to start",
                collectionName: $"server-{_serverIndex}"
            );
            _recorder = null;
        }
    }

    /// <summary>
    /// Waits for the server to be fully ready with a valid invite code.
    /// </summary>
    public async Task<bool> WaitForReadyAsync(
        CancellationToken ct = default,
        Action<string>? onProgress = null
    )
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_options.ReadyTimeout);

        // Only wait for invite code on Steam servers (Galaxy SDK must initialize first)
        var requireInviteCode = _options.WithSteam;

        var client = new ServerApiClient(BaseUrl);
        try
        {
            var status = await client.WaitForServerOnline(
                _options.ReadyTimeout,
                TimeSpan.FromSeconds(2),
                timeoutCts.Token,
                msg =>
                {
                    _logCallback?.Invoke(msg);
                    onProgress?.Invoke(msg);
                },
                requireInviteCode
            );

            if (status == null)
            {
                var reason = $"Did not become ready within {_options.ReadyTimeout.TotalSeconds}s";
                _logCallback?.Invoke(reason);
                onProgress?.Invoke(reason);
                return false;
            }

            InviteCode = status.InviteCode;
            _logCallback?.Invoke($"Ready: InviteCode={InviteCode}, Farm={status.FarmName}");
            return true;
        }
        finally
        {
            client.Dispose();
        }
    }

    /// <summary>
    /// Gets a cancellation token that triggers when a server error is detected.
    /// </summary>
    public CancellationToken GetErrorCancellationToken()
    {
        return _errorCancellation?.Token ?? CancellationToken.None;
    }

    /// <summary>
    /// Clears detected errors (for test isolation).
    /// </summary>
    public void ClearErrors()
    {
        lock (_serverErrorsLock)
        {
            _serverErrors.Clear();
        }
        _errorCancellation?.Dispose();
        _errorCancellation = new CancellationTokenSource();
    }

    /// <summary>
    /// Sets or clears the startup log callback. When set, each log line from
    /// StreamLogsAsync is forwarded to this callback (for UI progress events).
    /// </summary>
    public void SetStartupLogCallback(Action<string>? callback)
    {
        _startupLogCallback = callback;
    }

    /// <summary>
    /// Creates an API client for this server.
    /// </summary>
    public ServerApiClient CreateApiClient()
    {
        return new ServerApiClient(BaseUrl);
    }

    // Matches SMAPI log line prefix: [HH:MM:SS LEVEL Source]
    // Lines WITHOUT this prefix are continuation lines (exception details, stack traces).
    private static readonly Regex SmapiLogLinePrefix = new(
        @"^\[[\d:]+\s+\w+\s+",
        RegexOptions.Compiled
    );

    // Error-block accumulation across log lines. A SMAPI ERROR/FATAL header
    // arrives on its own log line; continuation lines (stack traces, exception
    // details) follow without the SMAPI prefix. The streaming reader emits
    // each line synchronously on a single task, so these fields don't need
    // locking — only the FlushError path is shared between the read loop and
    // RunAsync's finally block, which runs after the loop exits.
    private string? _currentErrorHeader;
    private readonly List<string> _currentErrorDetails = new();

    private async Task StreamLogsAsync(CancellationToken ct)
    {
        _logStreamReader = new ContainerLogStreamReader(
            _host.ApiClient,
            _serverContainer,
            $"server-{_serverIndex}",
            HandleLine,
            msg => _logCallback?.Invoke(msg)
        );

        try
        {
            await _logStreamReader.RunAsync(ct);
        }
        finally
        {
            // Flush any remaining error on shutdown
            FlushError();
        }
    }

    private void HandleLine(string line)
    {
        _containerLog?.WriteLine(line);

        // Forward any SDVD_EVENT structured event lines (stdout fallback
        // transport from the mod) to infrastructure.jsonl. Once forwarded, the
        // line is a spent machine envelope — keep it off the UI ticker below.
        // Error detection still runs unconditionally: a SDVD_EVENT line is JSON,
        // so SmapiLogLinePrefix never matches it, and leaving the block
        // unguarded preserves error-continuation accounting exactly.
        var isEvent = SimpleContainerLogStreamer.TryForwardSdvdEvent(
            line,
            $"server-{_serverIndex}"
        );

        // Emit to UI during startup phases (callback is null post-startup)
        if (!isEvent)
            _startupLogCallback?.Invoke(line);

        var isNewLogLine = SmapiLogLinePrefix.IsMatch(line);

        if (isNewLogLine)
        {
            // Flush any accumulated error before starting a new log line
            FlushError();

            // Check if this new line is an error
            if (Regex.IsMatch(line, @"\b(ERROR|FATAL)\b"))
            {
                var isIgnored = IgnoredErrorPatterns.Any(pattern =>
                    line.Contains(pattern, StringComparison.OrdinalIgnoreCase)
                );

                if (!isIgnored)
                {
                    _currentErrorHeader = line;
                }
            }
        }
        else if (_currentErrorHeader != null)
        {
            // Continuation line belonging to the current error
            _currentErrorDetails.Add(line.TrimEnd());
        }
    }

    private void FlushError()
    {
        if (_currentErrorHeader == null)
            return;

        var fullError =
            _currentErrorDetails.Count > 0
                ? $"{_currentErrorHeader}\n{string.Join("\n", _currentErrorDetails)}"
                : _currentErrorHeader;

        lock (_serverErrorsLock)
        {
            _serverErrors.Add(fullError);
        }
        _logCallback?.Invoke($"ERROR: {fullError}");
        try
        {
            _errorCancellation?.Cancel();
        }
        catch { }

        _currentErrorHeader = null;
        _currentErrorDetails.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        // Close any open forwards immediately so lingering OS handles don't
        // outlive the test. Local hosts treat this as a no-op.
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
            await _containerLog.DisposeAsync();

        _errorCancellation?.Dispose();

        // Stop video recording, extract all queued per-test clips, then retrieve full recording.
        // Pass ShutdownCoordinator.Token so these abort immediately on Ctrl+C instead of
        // hanging the shutdown chain on slow Docker I/O.
        if (_recorder != null)
        {
            var ct = ShutdownCoordinator.Token;
            try
            {
                if (_recorder.IsRecording)
                    await _recorder.StopAsync(ct);

                var destPath = Path.Combine(
                    TestArtifacts.GetContainerDir($"server-{_serverIndex}"),
                    "full_recording.mp4"
                );

                // Heavy I/O (in-container ffmpeg concat + tar pull) is gated by the
                // host's ExtractLimiter so N containers tearing down in parallel don't
                // saturate the daemon. StopAsync stays outside so every ffmpeg starts
                // finalizing at the same instant rather than recording past the test
                // boundary while waiting their turn.
                await _host.ExtractLimiter.WaitAsync(ct);
                try
                {
                    // Convert TS->MP4 (stream copy, instant) and retrieve to host
                    await _recorder.ConvertToMp4Async(ct);
                    await _recorder.RetrieveFullRecordingAsync(destPath, ct);
                }
                finally
                {
                    _host.ExtractLimiter.Release();
                }
                if (File.Exists(destPath))
                {
                    // Announce to the UI immediately — right where the file becomes available.
                    // Wrapped so any event-bus failure can't skip the container-teardown steps below.
                    try
                    {
                        if (InstanceId != null)
                            SetupEventBus.EmitInstanceRecording(InstanceId, destPath);
                    }
                    catch (Exception ex)
                    {
                        _logCallback?.Invoke(
                            $"[Recording] server-{_serverIndex} emit failed: {ex.Message}"
                        );
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                _logCallback?.Invoke(
                    $"[Recording] server-{_serverIndex} recording extraction cancelled (shutdown)"
                );
            }
            catch (Exception ex)
            {
                _logCallback?.Invoke(
                    $"[Recording] WARNING: server-{_serverIndex} recording retrieval error: {ex.Message}"
                );
            }
            RecordingOrchestrator.UnregisterRecorder(_serverContainer.Name);
            await _recorder.DisposeAsync();
            _recorder = null;
        }

        // Dispose containers
        _logCallback?.Invoke($"Shutting down server-{_serverIndex}...");

        // Capture state + exit code BEFORE dispose since DisposeAsync tears the
        // container down and subsequent reads fail. Exit code 137 indicates an
        // OOMKill — surface it as its own event so capacity-related flakiness
        // is diagnosable without a separate docker inspect.
        string? preDisposeState = null;
        try
        {
            preDisposeState = _serverContainer.State.ToString();
        }
        catch
        { /* defensive — property is cheap */
        }

        long? exitCode = null;
        try
        {
            using var ecCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            exitCode = await _serverContainer.GetExitCodeAsync(ecCts.Token);
        }
        catch
        { /* exit code is advisory */
        }

        var stopStopwatch = Stopwatch.StartNew();
        await DisposeContainerSafely(_serverContainer, _containerName, "server");
        stopStopwatch.Stop();

        JunimoServer.Tests.Helpers.InfrastructureEventLog.Emit(
            "container_stopped",
            new
            {
                role = "server",
                name = _containerName,
                index = _serverIndex,
                preDisposeState,
                exitCode,
                disposeDurationMs = stopStopwatch.ElapsedMilliseconds,
                host_id = HostId,
            }
        );

        if (exitCode == DockerExitCodes.SigKill)
        {
            JunimoServer.Tests.Helpers.InfrastructureEventLog.Emit(
                "container_oom_killed",
                new
                {
                    role = "server",
                    name = _containerName,
                    index = _serverIndex,
                    host_id = HostId,
                }
            );
        }

        // Remove test saves volume
        await RemoveDockerVolume(_savesVolume);

        // Normal cleanup succeeded; remove emergency fallback
        EmergencyCleanup.Unregister($"ServerContainer-{_runId}");

        // Unregister run ID after all resources are cleaned up
        TestRunRegistry.Unregister(_runId);

        _logCallback?.Invoke($"server-{_serverIndex} shut down");
    }

    private async Task DisposeContainerSafely(
        IContainer container,
        string containerName,
        string label
    )
    {
        try
        {
            await container.DisposeAsync();
        }
        catch (Exception ex)
        {
            _logCallback?.Invoke(
                $"server-{_serverIndex} {label} dispose failed ({ex.Message}), force-removing"
            );
            try
            {
                await DockerOps.ForceRemoveContainerAsync(_host.ApiClient, containerName);
            }
            catch (Exception rmEx)
            {
                _logCallback?.Invoke(
                    $"server-{_serverIndex} force-remove {label} also failed: {rmEx.Message}"
                );
            }
        }
    }

    private async Task RemoveDockerVolume(string volumeName)
    {
        try
        {
            await DockerOps.RemoveVolumeAsync(_host.ApiClient, volumeName, force: true);
        }
        catch (Exception ex)
        {
            _logCallback?.Invoke($"server-{_serverIndex} volume rm exception: {ex.Message}");
        }
    }
}
