using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Images;
using DotNet.Testcontainers.Networks;
using JunimoServer.Tests.Clients;
using JunimoServer.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using System.Text.RegularExpressions;

namespace JunimoServer.Tests.Containers;

/// <summary>
/// Manages a server container instance (server + steam-auth) for E2E testing.
/// Each instance represents a complete dedicated server environment.
/// </summary>
public class ServerContainer : IAsyncDisposable
{
    private readonly IContainer _serverContainer;
    private readonly IContainer _steamAuthContainer;
    private readonly INetwork _network;
    private readonly string _savesVolume;
    private readonly string _settingsFilePath;
    private readonly ServerContainerOptions _options;
    private readonly Action<string>? _logCallback;
    private readonly int _serverIndex;

    private CancellationTokenSource? _logStreamCts;
    private Task? _logStreamTask;
    private long _logPosition;

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
    private const int ContainerSteamAuthPort = 3001;

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
    public string VncUrl => $"http://localhost:{VncPort}";

    /// <summary>
    /// Server invite code (available after WaitForReadyAsync).
    /// </summary>
    public string? InviteCode { get; private set; }

    /// <summary>
    /// Network alias for container-to-container communication.
    /// </summary>
    public string NetworkAlias => $"server-{_serverIndex}";

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
        IContainer steamAuthContainer,
        INetwork network,
        string savesVolume,
        string settingsFilePath,
        int serverIndex,
        ServerContainerOptions options,
        Action<string>? logCallback)
    {
        _serverContainer = serverContainer;
        _steamAuthContainer = steamAuthContainer;
        _network = network;
        _savesVolume = savesVolume;
        _settingsFilePath = settingsFilePath;
        _serverIndex = serverIndex;
        _options = options;
        _logCallback = logCallback;
        _errorCancellation = new CancellationTokenSource();
    }

    /// <summary>
    /// Creates a new server container with the specified options.
    /// </summary>
    public static async Task<ServerContainer> CreateAsync(
        int serverIndex,
        ServerContainerOptions options,
        Dictionary<string, string>? envVars = null,
        Action<string>? logCallback = null,
        CancellationToken ct = default)
    {
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var networkName = $"sdvd-test-{uniqueId}";
        var savesVolume = $"sdvd-test-saves-{uniqueId}";

        logCallback?.Invoke($"Creating server {serverIndex} ({options.FarmTypeName} farm)...");

        // Create server-settings.json file for this container
        var settingsFilePath = CreateSettingsFile(options, uniqueId, logCallback);

        // Create network
        var network = new NetworkBuilder()
            .WithName(networkName)
            .Build();
        await network.CreateAsync(ct);

        // Build steam-auth container
        var steamAuthBuilder = new ContainerBuilder()
            .WithLogger(NullLogger.Instance)
            .WithImage($"sdvd/steam-service:{options.ImageTag}")
            .WithImagePullPolicy(options.ImageTag == "local" ? PullPolicy.Never : PullPolicy.Missing)
            .WithName($"sdvd-steam-auth-{uniqueId}")
            .WithNetwork(network)
            .WithNetworkAliases("steam-auth")
            .WithPortBinding(ContainerSteamAuthPort, true)
            .WithVolumeMount(options.SteamSessionVolume, "/data/steam-session")
            .WithVolumeMount(options.GameDataVolume, "/data/game")
            .WithEnvironment("PORT", ContainerSteamAuthPort.ToString())
            .WithEnvironment("GAME_DIR", "/data/game")
            .WithEnvironment("SESSION_DIR", "/data/steam-session")
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilHttpRequestIsSucceeded(r => r
                    .ForPort(ContainerSteamAuthPort)
                    .ForPath("/health")
                    .ForStatusCode(System.Net.HttpStatusCode.OK)));

        // Add Steam credentials from env vars
        if (envVars != null)
        {
            if (envVars.TryGetValue("STEAM_USERNAME", out var steamUser) && !string.IsNullOrEmpty(steamUser))
                steamAuthBuilder = steamAuthBuilder.WithEnvironment("STEAM_USERNAME", steamUser);
            if (envVars.TryGetValue("STEAM_PASSWORD", out var steamPass) && !string.IsNullOrEmpty(steamPass))
                steamAuthBuilder = steamAuthBuilder.WithEnvironment("STEAM_PASSWORD", steamPass);
            if (envVars.TryGetValue("STEAM_REFRESH_TOKEN", out var steamToken) && !string.IsNullOrEmpty(steamToken))
                steamAuthBuilder = steamAuthBuilder.WithEnvironment("STEAM_REFRESH_TOKEN", steamToken);
        }

        var steamAuthContainer = steamAuthBuilder.Build();

        // Build server container with custom settings
        var serverBuilder = new ContainerBuilder()
            .WithLogger(NullLogger.Instance)
            .WithImage($"sdvd/server:{options.ImageTag}")
            .WithImagePullPolicy(options.ImageTag == "local" ? PullPolicy.Never : PullPolicy.Missing)
            .WithName($"sdvd-server-{uniqueId}")
            .WithNetwork(network)
            .WithNetworkAliases($"server-{serverIndex}")
            .WithPortBinding(ContainerApiPort, true)
            .WithPortBinding(ContainerVncPort, true)
            .WithPortBinding(ContainerGamePort, true)
            .WithVolumeMount(options.GameDataVolume, "/data/game")
            .WithVolumeMount(savesVolume, "/config/xdg/config/StardewValley")
            // Mount custom settings file
            .WithBindMount(settingsFilePath, "/config/server-settings.json", DotNet.Testcontainers.Configurations.AccessMode.ReadOnly)
            .WithEnvironment("SETTINGS_PATH", "/config/server-settings.json")
            .WithEnvironment("STEAM_AUTH_URL", $"http://steam-auth:{ContainerSteamAuthPort}")
            .WithEnvironment("API_ENABLED", "true")
            .WithEnvironment("API_PORT", ContainerApiPort.ToString())
            // Performance/test settings
            .WithEnvironment("DISABLE_RENDERING", options.DisableRendering.ToString().ToLowerInvariant())
            .WithEnvironment("TEST_FAIL_FAST", options.FailFast.ToString().ToLowerInvariant())
            // SYS_TIME capability for GOG Galaxy auth
            .WithCreateParameterModifier(p =>
            {
                p.HostConfig.CapAdd ??= new List<string>();
                p.HostConfig.CapAdd.Add("SYS_TIME");
            })
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilHttpRequestIsSucceeded(r => r
                    .ForPort(ContainerApiPort)
                    .ForPath("/health")
                    .ForStatusCode(System.Net.HttpStatusCode.OK)));

        // Pass Steam credentials to server for SteamKit2 lobby creation
        if (envVars != null)
        {
            if (envVars.TryGetValue("STEAM_USERNAME", out var serverSteamUser) && !string.IsNullOrEmpty(serverSteamUser))
                serverBuilder = serverBuilder.WithEnvironment("STEAM_USERNAME", serverSteamUser);
            if (envVars.TryGetValue("STEAM_PASSWORD", out var serverSteamPass) && !string.IsNullOrEmpty(serverSteamPass))
                serverBuilder = serverBuilder.WithEnvironment("STEAM_PASSWORD", serverSteamPass);
            if (envVars.TryGetValue("VNC_PASSWORD", out var vncPass) && !string.IsNullOrEmpty(vncPass))
                serverBuilder = serverBuilder.WithEnvironment("VNC_PASSWORD", vncPass);
        }

        var serverContainer = serverBuilder.Build();

        return new ServerContainer(
            serverContainer,
            steamAuthContainer,
            network,
            savesVolume,
            settingsFilePath,
            serverIndex,
            options,
            logCallback);
    }

    /// <summary>
    /// Creates a server-settings.json file with the specified options.
    /// </summary>
    private static string CreateSettingsFile(ServerContainerOptions options, string uniqueId, Action<string>? logCallback)
    {
        var settings = new
        {
            game = new
            {
                farmName = options.FarmName,
                farmType = options.FarmType,
                profitMargin = options.ProfitMargin,
                startingCabins = options.StartingCabins,
                spawnMonstersAtNight = options.SpawnMonstersAtNight
            },
            server = new
            {
                maxPlayers = options.MaxPlayers,
                cabinStrategy = options.CabinStrategy,
                separateWallets = options.SeparateWallets,
                existingCabinBehavior = options.ExistingCabinBehavior,
                verboseLogging = false,
                allowIpConnections = options.AllowIpConnections
            }
        };

        var json = JsonConvert.SerializeObject(settings, Formatting.Indented);

        // Create temp file
        var tempDir = Path.Combine(Path.GetTempPath(), "sdvd-test-settings");
        Directory.CreateDirectory(tempDir);
        var settingsPath = Path.Combine(tempDir, $"server-settings-{uniqueId}.json");
        File.WriteAllText(settingsPath, json);

        logCallback?.Invoke($"Created settings file: {settingsPath}");
        logCallback?.Invoke($"  FarmType={options.FarmType} ({options.FarmTypeName}), StartingCabins={options.StartingCabins}");

        return settingsPath;
    }

    /// <summary>
    /// Starts the server containers and waits for them to be healthy.
    /// </summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_options.StartupTimeout);

        try
        {
            _logCallback?.Invoke($"Starting steam-auth container...");
            await _steamAuthContainer.StartAsync(timeoutCts.Token);
            _logCallback?.Invoke($"Steam-auth container started");

            _logCallback?.Invoke($"Starting server container...");
            await _serverContainer.StartAsync(timeoutCts.Token);
            _logCallback?.Invoke($"Server container started");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            var logs = await _serverContainer.GetLogsAsync();
            throw new TimeoutException(
                $"Server {_serverIndex} failed to start within {_options.StartupTimeout.TotalSeconds}s.\n" +
                $"Logs:\n{logs.Stdout}\n\nErrors:\n{logs.Stderr}");
        }

        // Get mapped ports
        ApiPort = _serverContainer.GetMappedPublicPort(ContainerApiPort);
        VncPort = _serverContainer.GetMappedPublicPort(ContainerVncPort);
        GamePort = _serverContainer.GetMappedPublicPort(ContainerGamePort);

        _logCallback?.Invoke($"Server {_serverIndex} ports: API={ApiPort}, VNC={VncPort}, Game={GamePort}");

        // Start log streaming
        _logStreamCts = new CancellationTokenSource();
        _logStreamTask = Task.Run(() => StreamLogsAsync(_logStreamCts.Token));
    }

    /// <summary>
    /// Waits for the server to be fully ready with a valid invite code.
    /// </summary>
    public async Task<bool> WaitForReadyAsync(CancellationToken ct = default)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_options.ReadyTimeout);

        var client = new ServerApiClient(BaseUrl);
        try
        {
            var status = await client.WaitForServerOnline(
                _options.ReadyTimeout,
                TimeSpan.FromSeconds(2),
                timeoutCts.Token);

            if (status == null)
            {
                _logCallback?.Invoke($"Server {_serverIndex} did not become ready in time");
                return false;
            }

            InviteCode = status.InviteCode;
            _logCallback?.Invoke($"Server {_serverIndex} ready: InviteCode={InviteCode}, Farm={status.FarmName}");
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
    /// Creates an API client for this server.
    /// </summary>
    public ServerApiClient CreateApiClient()
    {
        return new ServerApiClient(BaseUrl);
    }

    private async Task StreamLogsAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var logs = await _serverContainer.GetLogsAsync(timestampsEnabled: false, ct: ct);
                var combinedOutput = (logs.Stdout ?? "") + (logs.Stderr ?? "");
                var allLines = combinedOutput.Split('\n');

                for (var i = (int)_logPosition; i < allLines.Length; i++)
                {
                    var line = allLines[i];
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    // Check for server errors
                    if (Regex.IsMatch(line, @"\b(ERROR|FATAL)\b"))
                    {
                        var isIgnored = IgnoredErrorPatterns.Any(pattern =>
                            line.Contains(pattern, StringComparison.OrdinalIgnoreCase));

                        if (!isIgnored)
                        {
                            lock (_serverErrorsLock)
                            {
                                _serverErrors.Add(line);
                            }
                            _logCallback?.Invoke($"[Server {_serverIndex}] ERROR: {line}");
                            try { _errorCancellation?.Cancel(); } catch { }
                        }
                    }
                }

                _logPosition = allLines.Length;
                await Task.Delay(500, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                if (!ct.IsCancellationRequested)
                {
                    await Task.Delay(1000, ct);
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Stop log streaming
        if (_logStreamCts != null)
        {
            _logStreamCts.Cancel();
            if (_logStreamTask != null)
            {
                try { await _logStreamTask.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
            }
            _logStreamCts.Dispose();
        }

        _errorCancellation?.Dispose();

        // Dispose containers
        _logCallback?.Invoke($"Disposing server {_serverIndex}...");

        await DisposeContainerSafely(_serverContainer, "server");
        await DisposeContainerSafely(_steamAuthContainer, "steam-auth");

        // Dispose network
        try { await _network.DisposeAsync(); } catch { }

        // Remove test saves volume
        await RemoveDockerVolume(_savesVolume);

        // Remove settings file
        try { if (File.Exists(_settingsFilePath)) File.Delete(_settingsFilePath); } catch { }

        _logCallback?.Invoke($"Server {_serverIndex} disposed");
    }

    private static async Task DisposeContainerSafely(IContainer container, string label)
    {
        try
        {
            await container.DisposeAsync();
        }
        catch
        {
            // Fallback: force remove via Docker CLI
            try
            {
                var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "docker",
                    Arguments = $"rm -f {container.Name}",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                });
                if (proc != null) await proc.WaitForExitAsync();
            }
            catch { }
        }
    }

    private static async Task RemoveDockerVolume(string volumeName)
    {
        try
        {
            var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "docker",
                Arguments = $"volume rm {volumeName}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            });
            if (proc != null) await proc.WaitForExitAsync();
        }
        catch { }
    }
}
