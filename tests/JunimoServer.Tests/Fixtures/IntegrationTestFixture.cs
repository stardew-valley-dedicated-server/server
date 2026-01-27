using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Images;
using DotNet.Testcontainers.Networks;
using JunimoServer.Tests.Clients;
using JunimoServer.Tests.Helpers;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace JunimoServer.Tests.Fixtures;

/// <summary>
/// Integration test fixture that manages:
/// - Server container (via Testcontainers)
/// - Steam auth container (via Testcontainers)
/// - Game test client (local Stardew Valley with JunimoTestClient mod)
///
/// Both are shared across all tests in the collection for performance.
/// Containers are created fresh for each test run (clean state).
///
/// If server/game are already running locally, they will be reused (for development).
/// </summary>
public class IntegrationTestFixture : IAsyncLifetime
{
    private IContainer? _serverContainer;
    private IContainer? _steamAuthContainer;
    private INetwork? _network;
    private string? _testSavesVolume;
    private Process? _gameProcess;
    private readonly StringBuilder _outputLog = new();
    private readonly StringBuilder _errorLog = new();
    private bool _usingExistingServer;
    private bool _usingExistingGameClient;

    // File-based logging to avoid pipe buffer deadlocks when debugging
    private string? _gameLogFile;
    private CancellationTokenSource? _logTailCts;
    private Task? _logTailTask;

    // Paths
    private static readonly string ServerRepoDir = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    private static readonly string TestClientDir = Path.GetFullPath(
        Path.Combine(ServerRepoDir, "tools", "test-client"));

    // Image configuration
    // Use "local" for locally built images, or a specific version/latest for CI
    private static readonly string ImageTag = Environment.GetEnvironmentVariable("SDVD_IMAGE_TAG") ?? "local";
    private static readonly bool UseLocalImages = ImageTag == "local";
    private static readonly bool SkipBuild = string.Equals(
        Environment.GetEnvironmentVariable("SDVD_SKIP_BUILD"), "true", StringComparison.OrdinalIgnoreCase);

    // Ports
    private const int ServerApiPort = 8080;
    private const int VncPort = 5800;
    private const int TestClientApiPort = 5123;
    private const int SteamAuthPort = 3001;

    // Timeouts
    private const int ServerReadyTimeoutSeconds = 180;
    private const int GameReadyTimeoutSeconds = 120;

    public bool IsReady { get; private set; }
    public int ServerPort { get; private set; } = ServerApiPort;
    public int ServerVncPort { get; private set; } = VncPort;
    public string ServerBaseUrl => $"http://localhost:{ServerPort}";
    public string ServerVncUrl => $"http://localhost:{ServerVncPort}";
    public string GameClientBaseUrl => $"http://localhost:{TestClientApiPort}";
    public IContainer? ServerContainer => _serverContainer;
    public string? InviteCode { get; private set; }
    public string OutputLog => _outputLog.ToString();
    public string ErrorLog => _errorLog.ToString();

    // ---------------------------------------------------------------------------
    //  Logging helpers
    // ---------------------------------------------------------------------------

    private enum LogLevel { Header, Info, Success, Warn, Error, Detail }

    private static readonly bool UseColor = ResolveColorSupport();

    /// <summary>
    /// Detect whether the output terminal supports ANSI color codes.
    /// VS Test Explorer and piped output get plain text; Windows Terminal / CI get color.
    /// Override with SDVD_COLOR=true|false or the standard NO_COLOR variable.
    /// </summary>
    private static bool ResolveColorSupport()
    {
        // Explicit override
        var colorEnv = Environment.GetEnvironmentVariable("SDVD_COLOR");
        if (string.Equals(colorEnv, "true", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(colorEnv, "false", StringComparison.OrdinalIgnoreCase)) return false;

        // NO_COLOR standard (https://no-color.org/)
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR"))) return false;

        // Positive indicators of color-capable environments
        // (checked before IsOutputRedirected which is unreliable under dotnet test)
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WT_SESSION"))) return true;
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI"))) return true;
        var term = Environment.GetEnvironmentVariable("TERM");
        if (!string.IsNullOrEmpty(term) && term != "dumb") return true;

        // No positive signal — fall back to redirect check (catches VS Test Explorer, piped output)
        return !Console.IsOutputRedirected;
    }

    /// <summary>
    /// Return the ANSI escape sequence when color is supported, empty string otherwise.
    /// </summary>
    private static string Ansi(string code) => UseColor ? $"\x1b[{code}m" : "";

    // Commonly used ANSI sequences (resolved once)
    private static readonly string Reset    = Ansi("0");
    private static readonly string Bold     = Ansi("1");
    private static readonly string BoldCyan = Ansi("1;36");
    private static readonly string Green    = Ansi("32");
    private static readonly string Yellow   = Ansi("33");
    private static readonly string Red      = Ansi("31");
    private static readonly string Dim      = Ansi("90");
    private static readonly string Cyan     = Ansi("36");

    private static void Log(string message, LogLevel level = LogLevel.Info)
    {
        var (color, tag) = level switch
        {
            LogLevel.Header  => (BoldCyan, ">>>"),
            LogLevel.Success => (Green,    " + "),
            LogLevel.Warn    => (Yellow,   " ! "),
            LogLevel.Error   => (Red,      " x "),
            LogLevel.Detail  => (Dim,      " . "),
            _                => ("",       "   "),
        };

        if (level == LogLevel.Header)
            Console.WriteLine($"{color}{tag}{Reset} {Bold}{message}{Reset}");
        else if (level == LogLevel.Info)
            Console.WriteLine($"    {message}");
        else
            Console.WriteLine($"{color}{tag} {message}{Reset}");
    }

    private static void PrintInfoPanel(string title, (string label, string value)[] rows)
    {
        var contentRows = rows.Where(r => !string.IsNullOrEmpty(r.label) || !string.IsNullOrEmpty(r.value)).ToArray();
        var maxLabel = contentRows.Length > 0 ? contentRows.Max(r => r.label.Length) : 0;
        var maxContent = contentRows.Length > 0
            ? contentRows.Max(r => r.label.Length + r.value.Length + 2)
            : 0;
        var innerWidth = Math.Max(Math.Max(maxContent, title.Length) + 4, 44);
        var totalWidth = innerWidth + 6; // "***" on each side

        var fullBorder = new string('*', totalWidth);
        var emptyLine = $"***{new string(' ', innerWidth)}***";

        Console.WriteLine();
        Console.WriteLine($"{Cyan}{fullBorder}{Reset}");
        Console.WriteLine($"{Cyan}{emptyLine}{Reset}");

        var titlePad = $"  {title}".PadRight(innerWidth);
        Console.WriteLine($"{Cyan}***{Reset}{Bold}{titlePad}{Reset}{Cyan}***{Reset}");
        Console.WriteLine($"{Cyan}{emptyLine}{Reset}");

        foreach (var (label, value) in rows)
        {
            if (string.IsNullOrEmpty(label) && string.IsNullOrEmpty(value))
            {
                Console.WriteLine($"{Cyan}{emptyLine}{Reset}");
                continue;
            }
            var content = $"  {label.PadRight(maxLabel)}  {value}";
            Console.WriteLine($"{Cyan}***{Reset}{content.PadRight(innerWidth)}{Cyan}***{Reset}");
        }

        Console.WriteLine($"{Cyan}{emptyLine}{Reset}");
        Console.WriteLine($"{Cyan}{fullBorder}{Reset}");
        Console.WriteLine();
    }

    // ---------------------------------------------------------------------------
    //  Lifecycle
    // ---------------------------------------------------------------------------

    public async Task InitializeAsync()
    {
        Log("Starting integration test environment", LogLevel.Header);
        Log($"Repo: {ServerRepoDir}", LogLevel.Detail);

        // Prepare test artifacts output directory (clean slate each run)
        TestArtifacts.InitializeScreenshotsDir();

        // Check if server is already running (for local development)
        var serverApi = new ServerApiClient();
        if (await IsServerResponding(serverApi))
        {
            Log($"Server already running on port {ServerApiPort}, reusing", LogLevel.Success);
            _usingExistingServer = true;
            ServerPort = ServerApiPort;
        }
        else
        {
            await StartServerContainers();
        }
        serverApi.Dispose();

        // Check if game client is already running
        if (await IsGameClientResponding())
        {
            Log("Game client already running, reusing", LogLevel.Success);
            _usingExistingGameClient = true;
        }
        else
        {
            await StartGameClient();
        }

        IsReady = true;

        PrintInfoPanel("Integration Test Environment Ready", new[]
        {
            ("Server API:", ServerBaseUrl),
            ("Server VNC:", ServerVncUrl),
            ("Game Client:", GameClientBaseUrl),
            ("Invite Code:", InviteCode ?? "N/A"),
            ("", ""),
            ("Image Tag:", ImageTag),
            ("Server:", _usingExistingServer ? "Reused (existing)" : "Fresh container"),
            ("Game Client:", _usingExistingGameClient ? "Reused (existing)" : "Fresh process"),
        });
    }

    /// <summary>
    /// Remove containers, networks, and volumes left behind by previous test runs that crashed
    /// or were killed before cleanup could run.
    /// </summary>
    private static async Task CleanupStaleTestResources()
    {
        Log("Checking for stale test resources", LogLevel.Detail);

        await CleanupDockerResources("container", "ls -a --filter name=sdvd-server-test- --filter name=sdvd-steam-auth-test- -q", "rm -f");
        await CleanupDockerResources("network", "network ls --filter name=sdvd-test- -q", "network rm");
        await CleanupDockerResources("volume", "volume ls --filter name=sdvd-test-saves- -q", "volume rm");
    }

    private static async Task CleanupDockerResources(string resourceType, string listArgs, string removeCmd)
    {
        try
        {
            var listProc = Process.Start(new ProcessStartInfo
            {
                FileName = "docker",
                Arguments = listArgs,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            });
            if (listProc == null) return;

            var output = await listProc.StandardOutput.ReadToEndAsync();
            await listProc.WaitForExitAsync();

            var ids = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (ids.Length == 0) return;

            Log($"Found {ids.Length} stale test {resourceType}(s), removing...", LogLevel.Warn);

            var rmProc = Process.Start(new ProcessStartInfo
            {
                FileName = "docker",
                Arguments = $"{removeCmd} {string.Join(' ', ids)}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            });
            if (rmProc != null)
            {
                await rmProc.WaitForExitAsync();
                Log($"Removed stale test {resourceType}(s)", LogLevel.Detail);
            }
        }
        catch (Exception ex)
        {
            Log($"Error cleaning stale {resourceType}s: {ex.Message}", LogLevel.Error);
        }
    }

    /// <summary>
    /// Build local Docker images, equivalent to running `make build` + building the steam-service image.
    /// This ensures the test always runs against the latest code, just like `make up` does.
    /// Set SDVD_SKIP_BUILD=true to skip this step when iterating on tests.
    /// </summary>
    private async Task BuildLocalImages()
    {
        Log("Building local Docker images", LogLevel.Header);

        // Build server image (equivalent to `make build`)
        await RunBuildCommand(
            "make", "build",
            ServerRepoDir,
            "server image (make build)",
            TimeSpan.FromMinutes(10));

        // Build steam-service image (equivalent to `docker compose build steam-auth`)
        await RunBuildCommand(
            "docker", $"compose build steam-auth",
            ServerRepoDir,
            "steam-service image",
            TimeSpan.FromMinutes(5));

        Log("Local Docker images built", LogLevel.Success);
    }

    private async Task RunBuildCommand(string command, string arguments, string workingDirectory, string description, TimeSpan timeout)
    {
        Log($"Building {description}...", LogLevel.Detail);

        var startInfo = new ProcessStartInfo
        {
            FileName = command,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start '{command} {arguments}'");

        // Stream output to console (dim so it doesn't dominate)
        var stdoutTask = Task.Run(async () => {
            while (await process.StandardOutput.ReadLineAsync() is { } line)
            {
                Console.WriteLine($"{Dim}  [Build] {line}{Reset}");
            }
        });

        var stderrTask = Task.Run(async () => {
            while (await process.StandardError.ReadLineAsync() is { } line)
            {
                Console.WriteLine($"{Dim}  [Build] {line}{Reset}");
            }
        });

        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"Building {description} timed out after {timeout.TotalMinutes:0} minutes");
        }

        await Task.WhenAll(stdoutTask, stderrTask);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Building {description} failed with exit code {process.ExitCode}. " +
                $"Check build output above for details.");
        }

        Log($"{description} built", LogLevel.Success);
    }

    private async Task StartServerContainers()
    {
        Log("Starting server containers", LogLevel.Header);
        Log($"Image tag: {ImageTag} (local: {UseLocalImages})", LogLevel.Detail);

        // Clean up stale resources from previous test runs that may have crashed
        await CleanupStaleTestResources();

        // Build local images automatically (like `make up` does)
        if (UseLocalImages && !SkipBuild)
        {
            await BuildLocalImages();
        }
        else if (SkipBuild)
        {
            Log("Skipping image build (SDVD_SKIP_BUILD=true)", LogLevel.Detail);
        }

        // Load environment variables from .env file if it exists
        var envFilePath = FindEnvFile();
        var envVars = LoadEnvFile(envFilePath);

        // Check for Steam credentials
        var hasSteamCreds = envVars.ContainsKey("STEAM_REFRESH_TOKEN") && !string.IsNullOrEmpty(envVars["STEAM_REFRESH_TOKEN"])
            || (envVars.ContainsKey("STEAM_USERNAME") && !string.IsNullOrEmpty(envVars["STEAM_USERNAME"])
                && envVars.ContainsKey("STEAM_PASSWORD") && !string.IsNullOrEmpty(envVars["STEAM_PASSWORD"]));

        if (!hasSteamCreds)
        {
            Log("No Steam credentials found in .env file!", LogLevel.Warn);
            Log("Steam-auth container may fail to authenticate", LogLevel.Warn);
            Log("Set STEAM_REFRESH_TOKEN or STEAM_USERNAME/STEAM_PASSWORD in .env", LogLevel.Warn);
        }

        // Create a network for the containers
        var networkName = $"sdvd-test-{Guid.NewGuid():N}";
        _network = new NetworkBuilder()
            .WithName(networkName)
            .Build();

        await _network.CreateAsync();
        Log($"Created network: {networkName}", LogLevel.Detail);

        // Build steam-auth container
        // Mount existing Docker volumes to reuse downloaded game files and Steam session
        // Volume names are prefixed with docker-compose project name (typically "server")
        var volumePrefix = Environment.GetEnvironmentVariable("SDVD_VOLUME_PREFIX") ?? "server";

        // Use a fresh saves volume for each test run (clean slate)
        var testRunId = Guid.NewGuid().ToString("N")[..8];
        var testSavesVolume = $"sdvd-test-saves-{testRunId}";

        Log($"Volume prefix: {volumePrefix}", LogLevel.Detail);
        Log($"Fresh saves volume: {testSavesVolume}", LogLevel.Detail);
        Log($"Reusing volumes: {volumePrefix}_steam-session, {volumePrefix}_game-data", LogLevel.Detail);

        var steamAuthBuilder = new ContainerBuilder()
            .WithImage($"sdvd/steam-service:{ImageTag}")
            .WithImagePullPolicy(UseLocalImages ? PullPolicy.Never : PullPolicy.Missing)
            .WithName($"sdvd-steam-auth-test-{Guid.NewGuid():N}")
            .WithNetwork(_network)
            .WithNetworkAliases("steam-auth")
            .WithPortBinding(SteamAuthPort, true)
            // Mount existing volumes for game data and steam session
            .WithVolumeMount($"{volumePrefix}_steam-session", "/data/steam-session")
            .WithVolumeMount($"{volumePrefix}_game-data", "/data/game")
            .WithEnvironment("PORT", SteamAuthPort.ToString())
            .WithEnvironment("GAME_DIR", "/data/game")
            .WithEnvironment("SESSION_DIR", "/data/steam-session")
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilHttpRequestIsSucceeded(r => r
                    .ForPort(SteamAuthPort)
                    .ForPath("/health")
                    .ForStatusCode(System.Net.HttpStatusCode.OK)));

        // Add Steam credentials from .env
        if (envVars.TryGetValue("STEAM_USERNAME", out var steamUser) && !string.IsNullOrEmpty(steamUser))
            steamAuthBuilder = steamAuthBuilder.WithEnvironment("STEAM_USERNAME", steamUser);
        if (envVars.TryGetValue("STEAM_PASSWORD", out var steamPass) && !string.IsNullOrEmpty(steamPass))
            steamAuthBuilder = steamAuthBuilder.WithEnvironment("STEAM_PASSWORD", steamPass);
        if (envVars.TryGetValue("STEAM_REFRESH_TOKEN", out var steamToken) && !string.IsNullOrEmpty(steamToken))
            steamAuthBuilder = steamAuthBuilder.WithEnvironment("STEAM_REFRESH_TOKEN", steamToken);

        _steamAuthContainer = steamAuthBuilder.Build();

        Log("Starting steam-auth container...");
        try
        {
            // Use a CancellationToken with timeout for container startup
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await _steamAuthContainer.StartAsync(cts.Token);
            var steamAuthMappedPort = _steamAuthContainer.GetMappedPublicPort(SteamAuthPort);
            Log($"Steam-auth ready on port {steamAuthMappedPort}", LogLevel.Success);
        }
        catch (OperationCanceledException)
        {
            var logs = await _steamAuthContainer.GetLogsAsync();
            throw new TimeoutException(
                $"Steam-auth container failed to start within 60 seconds.\n" +
                $"This usually means Steam credentials are missing or invalid.\n" +
                $"Check your .env file for STEAM_REFRESH_TOKEN or STEAM_USERNAME/STEAM_PASSWORD.\n\n" +
                $"Logs:\n{logs.Stdout}\n\nErrors:\n{logs.Stderr}");
        }

        // Store test saves volume name for cleanup
        _testSavesVolume = testSavesVolume;

        // Build server container
        var serverBuilder = new ContainerBuilder()
            .WithImage($"sdvd/server:{ImageTag}")
            .WithImagePullPolicy(UseLocalImages ? PullPolicy.Never : PullPolicy.Missing)
            .WithName($"sdvd-server-test-{Guid.NewGuid():N}")
            .WithNetwork(_network)
            .WithPortBinding(ServerApiPort, true)
            .WithPortBinding(VncPort, true)
            // Mount game data (existing) and fresh saves volume
            .WithVolumeMount($"{volumePrefix}_game-data", "/data/game")
            .WithVolumeMount(testSavesVolume, "/config/xdg/config/StardewValley")
            .WithEnvironment("STEAM_AUTH_URL", $"http://steam-auth:{SteamAuthPort}")
            .WithEnvironment("API_ENABLED", "true")
            .WithEnvironment("API_PORT", ServerApiPort.ToString())
            .WithEnvironment("DISABLE_RENDERING", "true")
            .WithEnvironment("ALLOW_IP_CONNECTIONS", "false")
            // SYS_TIME capability for GOG Galaxy auth
            .WithCreateParameterModifier(p => {
                p.HostConfig.CapAdd ??= new List<string>();
                p.HostConfig.CapAdd.Add("SYS_TIME");
            })
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilHttpRequestIsSucceeded(r => r
                    .ForPort(ServerApiPort)
                    .ForPath("/health")
                    .ForStatusCode(System.Net.HttpStatusCode.OK)));

        // Add optional env vars
        if (envVars.TryGetValue("VNC_PASSWORD", out var vncPass) && !string.IsNullOrEmpty(vncPass))
            serverBuilder = serverBuilder.WithEnvironment("VNC_PASSWORD", vncPass);

        _serverContainer = serverBuilder.Build();

        Log("Starting server container...");
        await _serverContainer.StartAsync();

        ServerPort = _serverContainer.GetMappedPublicPort(ServerApiPort);
        ServerVncPort = _serverContainer.GetMappedPublicPort(VncPort);

        // Wait for server to be fully ready (with invite code)
        Log("Waiting for server to produce a valid invite code...");
        var serverApi = new ServerApiClient($"http://localhost:{ServerPort}");
        var status = await serverApi.WaitForServerOnline(
            TimeSpan.FromSeconds(ServerReadyTimeoutSeconds),
            TimeSpan.FromSeconds(2));
        serverApi.Dispose();

        if (status == null)
        {
            // Get container logs for debugging
            var logs = await _serverContainer.GetLogsAsync();
            throw new TimeoutException(
                $"Server did not become ready within {ServerReadyTimeoutSeconds} seconds.\n" +
                $"Logs:\n{logs.Stdout}\n\nErrors:\n{logs.Stderr}");
        }

        InviteCode = status.InviteCode;
        Log($"Server ready (invite code: {InviteCode})", LogLevel.Success);
    }

    private async Task StartGameClient()
    {
        Log("Starting game test client", LogLevel.Header);

        // Use file-based logging to avoid pipe buffer deadlocks when debugging.
        // When hitting a breakpoint, pipe readers pause, the buffer fills, and the
        // game process blocks on writes - causing "Not Responding". Files don't block.
        _gameLogFile = Path.Combine(Path.GetTempPath(), $"sdvd-test-client-{Guid.NewGuid():N}.log");
        Log($"Game log file: {_gameLogFile}", LogLevel.Detail);

        // Start process with output redirected to file via cmd shell
        var startInfo = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c make run-bg > \"{_gameLogFile}\" 2>&1",
            WorkingDirectory = TestClientDir,
            UseShellExecute = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
            CreateNoWindow = true
        };

        _gameProcess = new Process { StartInfo = startInfo };
        _gameProcess.Start();

        // Start background task to tail the log file
        _logTailCts = new CancellationTokenSource();
        _logTailTask = Task.Run(() => TailLogFile(_gameLogFile, _logTailCts.Token));

        Log("Waiting for game client to be ready...");

        var deadline = DateTime.UtcNow.AddSeconds(GameReadyTimeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            if (await IsGameClientResponding())
            {
                Log("Game client is ready", LogLevel.Success);
                return;
            }
            await Task.Delay(2000);
        }

        throw new TimeoutException(
            $"Game client did not become ready within {GameReadyTimeoutSeconds} seconds.\n" +
            $"Output:\n{_outputLog}\n\nErrors:\n{_errorLog}");
    }

    /// <summary>
    /// Tail a log file and output to console. Runs in background, independent of debugger.
    /// Uses FileShare.ReadWrite to read while the process writes.
    /// </summary>
    private void TailLogFile(string filePath, CancellationToken ct)
    {
        try
        {
            // Wait for file to be created
            while (!File.Exists(filePath) && !ct.IsCancellationRequested)
            {
                Thread.Sleep(100);
            }

            if (ct.IsCancellationRequested) return;

            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs);

            while (!ct.IsCancellationRequested)
            {
                var line = sr.ReadLine();
                if (line != null)
                {
                    var cleanedLine = CleanLine(line);
                    if (cleanedLine.Length > 0)
                    {
                        _outputLog.AppendLine(cleanedLine);
                        Console.WriteLine($"{Dim}  [Game] {Reset}{cleanedLine}");
                    }
                }
                else
                {
                    // No more data, wait a bit before checking again
                    Thread.Sleep(50);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log($"Log tail error: {ex.Message}", LogLevel.Error);
        }
    }

    public async Task DisposeAsync()
    {
        Log("Cleaning up", LogLevel.Header);

        // Stop game client (only if we started it) - always try even if exceptions occur
        if (!_usingExistingGameClient)
        {
            try
            {
                await StopGameClient();
            }
            catch (Exception ex)
            {
                Log($"Error in StopGameClient: {ex.Message}", LogLevel.Error);
                // Still try to kill processes as last resort
                try { await KillGameProcesses(); } catch { }
            }
        }

        // Stop server containers (only if we started them)
        if (!_usingExistingServer)
        {
            // Dispose containers — DisposeAsync handles stop + remove.
            // If that fails, fall back to docker rm -f.
            await DisposeContainerSafely(_serverContainer, "server");
            await DisposeContainerSafely(_steamAuthContainer, "steam-auth");

            if (_network != null)
            {
                try
                {
                    Log("Removing test network...", LogLevel.Detail);
                    await _network.DisposeAsync();
                    Log("Test network removed", LogLevel.Detail);
                }
                catch (Exception ex)
                {
                    Log($"Error removing network: {ex.Message}", LogLevel.Error);
                }
            }

            // Clean up test saves volume
            await RemoveDockerVolume(_testSavesVolume);
        }

        Log("Cleanup complete", LogLevel.Success);
    }

    /// <summary>
    /// Dispose a container safely with a docker rm -f fallback.
    /// Testcontainers' DisposeAsync handles stop + remove in one call.
    /// If that fails for any reason, we force-remove via Docker CLI.
    /// </summary>
    private static async Task DisposeContainerSafely(IContainer? container, string label)
    {
        if (container == null) return;

        string? containerName = null;
        try
        {
            containerName = container.Name;
        }
        catch
        {
            // Container may not have been created yet
        }

        try
        {
            Log($"Disposing {label} container...", LogLevel.Detail);
            await container.DisposeAsync();
            Log($"{label} container disposed", LogLevel.Detail);
        }
        catch (Exception ex)
        {
            Log($"Error disposing {label} container: {ex.Message}", LogLevel.Error);

            // Fallback: force-remove via Docker CLI
            if (containerName != null)
            {
                await ForceRemoveContainer(containerName, label);
            }
        }
    }

    /// <summary>
    /// Force-remove a container by name using the Docker CLI.
    /// </summary>
    private static async Task ForceRemoveContainer(string containerName, string label)
    {
        try
        {
            Log($"Force-removing {label} container: {containerName}", LogLevel.Warn);
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName = "docker",
                Arguments = $"rm -f {containerName}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            });
            if (proc != null)
            {
                await proc.WaitForExitAsync();
                Log($"Force-removed {label} container (exit code {proc.ExitCode})", LogLevel.Detail);
            }
        }
        catch (Exception ex2)
        {
            Log($"Force-remove also failed for {label}: {ex2.Message}", LogLevel.Error);
        }
    }

    /// <summary>
    /// Remove a Docker volume by name, ignoring errors.
    /// </summary>
    private static async Task RemoveDockerVolume(string? volumeName)
    {
        if (string.IsNullOrEmpty(volumeName)) return;
        try
        {
            Log($"Removing volume: {volumeName}", LogLevel.Detail);
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName = "docker",
                Arguments = $"volume rm {volumeName}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            });
            if (proc != null)
            {
                await proc.WaitForExitAsync();
                Log($"Volume {volumeName} removed", LogLevel.Detail);
            }
        }
        catch (Exception ex)
        {
            Log($"Error removing volume {volumeName}: {ex.Message}", LogLevel.Error);
        }
    }

    private async Task<bool> IsServerResponding(ServerApiClient client)
    {
        try
        {
            var health = await client.GetHealth();
            return health?.Status == "ok";
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> IsGameClientResponding()
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var response = await client.GetAsync($"http://localhost:{TestClientApiPort}/ping");
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Stop the game client using multiple strategies to ensure it closes.
    /// </summary>
    private async Task StopGameClient()
    {
        Log("Stopping game client...", LogLevel.Detail);

        // Stop the log tail task first
        if (_logTailCts != null)
        {
            try
            {
                _logTailCts.Cancel();
                if (_logTailTask != null)
                {
                    await _logTailTask.WaitAsync(TimeSpan.FromSeconds(2));
                }
            }
            catch (TimeoutException)
            {
                Log("Log tail task didn't stop in time", LogLevel.Warn);
            }
            catch (Exception ex)
            {
                Log($"Error stopping log tail: {ex.Message}", LogLevel.Error);
            }
            finally
            {
                _logTailCts.Dispose();
                _logTailCts = null;
            }
        }

        // Strategy 1: Try 'make stop' with timeout
        var stopped = await TryMakeStop();

        // Strategy 2: If make stop failed or timed out, kill processes directly
        if (!stopped)
        {
            Log("make stop failed, killing processes directly...", LogLevel.Warn);
            await KillGameProcesses();
        }

        // Strategy 3: Kill the launcher process (cmd.exe) that may hold the log file
        if (_gameProcess != null)
        {
            try
            {
                if (!_gameProcess.HasExited)
                {
                    Log("Killing launcher process...", LogLevel.Detail);
                    _gameProcess.Kill(entireProcessTree: true);
                    await _gameProcess.WaitForExitAsync();
                }
            }
            catch (Exception ex)
            {
                Log($"Error killing launcher: {ex.Message}", LogLevel.Error);
            }
            finally
            {
                _gameProcess.Dispose();
                _gameProcess = null;
            }
        }

        // Strategy 4: Verify the game is actually stopped
        await Task.Delay(1000);
        if (await IsGameClientResponding())
        {
            Log("Game still responding after stop, force killing...", LogLevel.Warn);
            await KillGameProcesses();
        }

        // Clean up log file (retry with delay — file handles may take a moment to release)
        if (!string.IsNullOrEmpty(_gameLogFile))
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    if (File.Exists(_gameLogFile))
                    {
                        File.Delete(_gameLogFile);
                        Log($"Deleted log file: {_gameLogFile}", LogLevel.Detail);
                    }
                    break;
                }
                catch (IOException) when (attempt < 2)
                {
                    Log("Log file still locked, retrying in 1s...", LogLevel.Detail);
                    await Task.Delay(1000);
                }
                catch (Exception ex)
                {
                    Log($"Error deleting log file: {ex.Message}", LogLevel.Error);
                    break;
                }
            }
            _gameLogFile = null;
        }

        Log("Game client stopped", LogLevel.Detail);
    }

    private async Task<bool> TryMakeStop()
    {
        try
        {
            var stopInfo = new ProcessStartInfo
            {
                FileName = "make",
                Arguments = "stop",
                WorkingDirectory = TestClientDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var stopProcess = Process.Start(stopInfo);
            if (stopProcess == null) return false;

            // Wait max 10 seconds for make stop
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                await stopProcess.WaitForExitAsync(cts.Token);
                return stopProcess.ExitCode == 0;
            }
            catch (OperationCanceledException)
            {
                Log("make stop timed out", LogLevel.Warn);
                try { stopProcess.Kill(); } catch { }
                return false;
            }
        }
        catch (Exception ex)
        {
            Log($"make stop error: {ex.Message}", LogLevel.Error);
            return false;
        }
    }

    private async Task KillGameProcesses()
    {
        var processNames = new[] { "StardewModdingAPI", "Stardew Valley" };

        foreach (var name in processNames)
        {
            try
            {
                var processes = Process.GetProcessesByName(name);
                foreach (var proc in processes)
                {
                    try
                    {
                        Log($"Killing process: {proc.ProcessName} (PID: {proc.Id})", LogLevel.Detail);
                        proc.Kill(entireProcessTree: true);
                        await proc.WaitForExitAsync();
                    }
                    catch (Exception ex)
                    {
                        Log($"Failed to kill {proc.ProcessName}: {ex.Message}", LogLevel.Error);
                    }
                    finally
                    {
                        proc.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"Error finding processes '{name}': {ex.Message}", LogLevel.Error);
            }
        }
    }

    /// <summary>
    /// Find the .env file by searching up the directory tree.
    /// </summary>
    private static string? FindEnvFile()
    {
        // Try multiple possible locations
        var searchPaths = new[]
        {
            Path.Combine(ServerRepoDir, ".env"),
            Path.Combine(Directory.GetCurrentDirectory(), ".env"),
            // Search up from current directory
            FindFileUpwards(".env", Directory.GetCurrentDirectory()),
            // Search up from test assembly location
            FindFileUpwards(".env", AppContext.BaseDirectory)
        };

        foreach (var path in searchPaths.Where(p => p != null))
        {
            if (File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    private static string? FindFileUpwards(string filename, string startDir)
    {
        var dir = startDir;
        while (dir != null)
        {
            var filePath = Path.Combine(dir, filename);
            if (File.Exists(filePath))
                return filePath;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    /// <summary>
    /// Load environment variables from a .env file.
    /// </summary>
    private static Dictionary<string, string> LoadEnvFile(string? path)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (path == null || !File.Exists(path))
        {
            Log("No .env file found", LogLevel.Detail);
            return result;
        }

        Log($"Loading environment from {path}", LogLevel.Detail);

        foreach (var line in File.ReadAllLines(path))
        {
            var trimmed = line.Trim();

            // Skip empty lines and comments
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#'))
                continue;

            var eqIndex = trimmed.IndexOf('=');
            if (eqIndex <= 0) continue;

            var key = trimmed[..eqIndex].Trim();
            var value = trimmed[(eqIndex + 1)..].Trim();

            // Remove surrounding quotes
            if ((value.StartsWith('"') && value.EndsWith('"')) ||
                (value.StartsWith('\'') && value.EndsWith('\'')))
            {
                value = value[1..^1];
            }

            result[key] = value;
        }

        return result;
    }

    private static string CleanLine(string? input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return "";
        }

        // Remove ANSI escape sequences
        var result = Regex.Replace(input, @"\x1B\[[0-9;]*[a-zA-Z]", "");

        // Remove carriage returns and other control characters
        result = result.Replace("\r", "").Replace("\n", "");

        // Remove any remaining non-printable characters
        result = new string(result.Where(c => !char.IsControl(c)).ToArray());

        // Shorten internal game paths for readability
        result = result.Replace(@"D:\GitlabRunner\builds\Gq5qA5P4\0\ConcernedApe\stardewvalley", "StardewValley");

        return result.Trim();
    }
}

/// <summary>
/// Collection definition for sharing the integration test fixture across test classes.
/// All tests using [Collection("Integration")] will share the same server and game client.
/// </summary>
[CollectionDefinition("Integration")]
public class IntegrationTestCollection : ICollectionFixture<IntegrationTestFixture>
{
}
