using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Images;
using DotNet.Testcontainers.Networks;
using JunimoServer.Tests.Clients;
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

    // Ports
    private const int ServerApiPort = 8080;
    private const int TestClientApiPort = 5123;
    private const int SteamAuthPort = 3001;

    // Timeouts
    private const int ServerReadyTimeoutSeconds = 180;
    private const int GameReadyTimeoutSeconds = 120;

    public bool IsReady { get; private set; }
    public int ServerPort { get; private set; } = ServerApiPort;
    public string ServerBaseUrl => $"http://localhost:{ServerPort}";
    public string GameClientBaseUrl => $"http://localhost:{TestClientApiPort}";
    public string OutputLog => _outputLog.ToString();
    public string ErrorLog => _errorLog.ToString();

    public async Task InitializeAsync()
    {
        Console.WriteLine("[IntegrationTestFixture] Starting integration test environment...");
        Console.WriteLine($"[IntegrationTestFixture] Server repo dir: {ServerRepoDir}");

        // Check if server is already running (for local development)
        var serverApi = new ServerApiClient();
        if (await IsServerResponding(serverApi))
        {
            Console.WriteLine("[IntegrationTestFixture] Server already running on port 8080, reusing");
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
            Console.WriteLine("[IntegrationTestFixture] Game client already running, reusing");
            _usingExistingGameClient = true;
        }
        else
        {
            await StartGameClient();
        }

        IsReady = true;
        Console.WriteLine("[IntegrationTestFixture] Integration test environment is ready!");
    }

    private async Task StartServerContainers()
    {
        Console.WriteLine("[IntegrationTestFixture] Starting server containers...");
        Console.WriteLine($"[IntegrationTestFixture] Using image tag: {ImageTag} (local images: {UseLocalImages})");
        Console.WriteLine($"[IntegrationTestFixture] Server repo dir: {ServerRepoDir}");

        // Load environment variables from .env file if it exists
        var envFilePath = FindEnvFile();
        var envVars = LoadEnvFile(envFilePath);

        // Check for Steam credentials
        var hasSteamCreds = envVars.ContainsKey("STEAM_REFRESH_TOKEN") && !string.IsNullOrEmpty(envVars["STEAM_REFRESH_TOKEN"])
            || (envVars.ContainsKey("STEAM_USERNAME") && !string.IsNullOrEmpty(envVars["STEAM_USERNAME"])
                && envVars.ContainsKey("STEAM_PASSWORD") && !string.IsNullOrEmpty(envVars["STEAM_PASSWORD"]));

        if (!hasSteamCreds)
        {
            Console.WriteLine("[IntegrationTestFixture] WARNING: No Steam credentials found in .env file!");
            Console.WriteLine("[IntegrationTestFixture] Steam-auth container may fail to authenticate.");
            Console.WriteLine("[IntegrationTestFixture] Set STEAM_REFRESH_TOKEN or STEAM_USERNAME/STEAM_PASSWORD in .env");
        }

        // Create a network for the containers
        var networkName = $"sdvd-test-{Guid.NewGuid():N}";
        _network = new NetworkBuilder()
            .WithName(networkName)
            .Build();

        await _network.CreateAsync();
        Console.WriteLine($"[IntegrationTestFixture] Created network: {networkName}");

        // Build steam-auth container
        // Mount existing Docker volumes to reuse downloaded game files and Steam session
        // Volume names are prefixed with docker-compose project name (typically "server")
        var volumePrefix = Environment.GetEnvironmentVariable("SDVD_VOLUME_PREFIX") ?? "server";

        // Use a fresh saves volume for each test run (clean slate)
        var testRunId = Guid.NewGuid().ToString("N")[..8];
        var testSavesVolume = $"sdvd-test-saves-{testRunId}";

        Console.WriteLine($"[IntegrationTestFixture] Using volume prefix: {volumePrefix}");
        Console.WriteLine($"[IntegrationTestFixture] Using fresh saves volume: {testSavesVolume}");
        Console.WriteLine($"[IntegrationTestFixture] Reusing volumes: {volumePrefix}_steam-session, {volumePrefix}_game-data");

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
            .WithEnvironment("PORT", "3001")
            .WithEnvironment("GAME_DIR", "/data/game")
            .WithEnvironment("SESSION_DIR", "/data/steam-session")
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilHttpRequestIsSucceeded(r => r
                    .ForPort(3001)
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

        Console.WriteLine("[IntegrationTestFixture] Starting steam-auth container...");
        try
        {
            // Use a CancellationToken with timeout for container startup
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await _steamAuthContainer.StartAsync(cts.Token);
            var steamAuthMappedPort = _steamAuthContainer.GetMappedPublicPort(3001);
            Console.WriteLine($"[IntegrationTestFixture] Steam-auth container started on port {steamAuthMappedPort}");
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
            // Mount game data (existing) and fresh saves volume
            .WithVolumeMount($"{volumePrefix}_game-data", "/data/game")
            .WithVolumeMount(testSavesVolume, "/config/xdg/config/StardewValley")
            .WithEnvironment("STEAM_AUTH_URL", "http://steam-auth:3001")
            .WithEnvironment("API_ENABLED", "true")
            .WithEnvironment("API_PORT", ServerApiPort.ToString())
            .WithEnvironment("DISABLE_RENDERING", "true")
            .WithEnvironment("ALLOW_IP_CONNECTIONS", "true")
            // SYS_TIME capability for GOG Galaxy auth
            .WithCreateParameterModifier(p =>
            {
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

        Console.WriteLine("[IntegrationTestFixture] Starting server container...");
        await _serverContainer.StartAsync();

        ServerPort = _serverContainer.GetMappedPublicPort(ServerApiPort);
        Console.WriteLine($"[IntegrationTestFixture] Server container started on port {ServerPort}");

        // Wait for server to be fully ready (with invite code)
        Console.WriteLine("[IntegrationTestFixture] Waiting for server to have valid invite code...");
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

        Console.WriteLine($"[IntegrationTestFixture] Server ready with invite code: {status.InviteCode}");
    }

    private async Task StartGameClient()
    {
        Console.WriteLine("[IntegrationTestFixture] Starting game test client...");

        // Use file-based logging to avoid pipe buffer deadlocks when debugging.
        // When hitting a breakpoint, pipe readers pause, the buffer fills, and the
        // game process blocks on writes - causing "Not Responding". Files don't block.
        _gameLogFile = Path.Combine(Path.GetTempPath(), $"sdvd-test-client-{Guid.NewGuid():N}.log");
        Console.WriteLine($"[IntegrationTestFixture] Game client log file: {_gameLogFile}");

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

        Console.WriteLine("[IntegrationTestFixture] Waiting for game client to be ready...");

        var deadline = DateTime.UtcNow.AddSeconds(GameReadyTimeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            if (await IsGameClientResponding())
            {
                Console.WriteLine("[IntegrationTestFixture] Game client is ready!");
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
                        Console.WriteLine($"[TestClient] {cleanedLine}");
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
            Console.WriteLine($"[IntegrationTestFixture] Log tail error: {ex.Message}");
        }
    }

    public async Task DisposeAsync()
    {
        Console.WriteLine("[IntegrationTestFixture] Cleaning up integration test environment...");

        // Stop game client (only if we started it) - always try even if exceptions occur
        if (!_usingExistingGameClient)
        {
            try
            {
                await StopGameClient();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[IntegrationTestFixture] Error in StopGameClient: {ex.Message}");
                // Still try to kill processes as last resort
                try { await KillGameProcesses(); } catch { }
            }
        }

        // Stop server containers (only if we started them)
        if (!_usingExistingServer)
        {
            if (_serverContainer != null)
            {
                try
                {
                    Console.WriteLine("[IntegrationTestFixture] Stopping server container...");
                    await _serverContainer.StopAsync();
                    await _serverContainer.DisposeAsync();
                    Console.WriteLine("[IntegrationTestFixture] Server container stopped");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[IntegrationTestFixture] Error stopping server container: {ex.Message}");
                }
            }

            if (_steamAuthContainer != null)
            {
                try
                {
                    Console.WriteLine("[IntegrationTestFixture] Stopping steam-auth container...");
                    await _steamAuthContainer.StopAsync();
                    await _steamAuthContainer.DisposeAsync();
                    Console.WriteLine("[IntegrationTestFixture] Steam-auth container stopped");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[IntegrationTestFixture] Error stopping steam-auth container: {ex.Message}");
                }
            }

            if (_network != null)
            {
                try
                {
                    Console.WriteLine("[IntegrationTestFixture] Removing test network...");
                    await _network.DeleteAsync();
                    await _network.DisposeAsync();
                    Console.WriteLine("[IntegrationTestFixture] Test network removed");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[IntegrationTestFixture] Error removing network: {ex.Message}");
                }
            }

            // Clean up test saves volume
            if (!string.IsNullOrEmpty(_testSavesVolume))
            {
                try
                {
                    Console.WriteLine($"[IntegrationTestFixture] Removing test saves volume: {_testSavesVolume}");
                    var removeVolume = new ProcessStartInfo
                    {
                        FileName = "docker",
                        Arguments = $"volume rm {_testSavesVolume}",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    };
                    using var proc = Process.Start(removeVolume);
                    if (proc != null)
                    {
                        await proc.WaitForExitAsync();
                        Console.WriteLine("[IntegrationTestFixture] Test saves volume removed");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[IntegrationTestFixture] Error removing saves volume: {ex.Message}");
                }
            }
        }

        Console.WriteLine("[IntegrationTestFixture] Cleanup complete");
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
        Console.WriteLine("[IntegrationTestFixture] Stopping game client...");

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
                Console.WriteLine("[IntegrationTestFixture] Log tail task didn't stop in time");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[IntegrationTestFixture] Error stopping log tail: {ex.Message}");
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
            Console.WriteLine("[IntegrationTestFixture] make stop failed, killing processes directly...");
            await KillGameProcesses();
        }

        // Strategy 3: Verify the game is actually stopped
        await Task.Delay(1000);
        if (await IsGameClientResponding())
        {
            Console.WriteLine("[IntegrationTestFixture] Game still responding, force killing...");
            await KillGameProcesses();
        }

        // Clean up log file
        if (!string.IsNullOrEmpty(_gameLogFile))
        {
            try
            {
                if (File.Exists(_gameLogFile))
                {
                    File.Delete(_gameLogFile);
                    Console.WriteLine($"[IntegrationTestFixture] Deleted log file: {_gameLogFile}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[IntegrationTestFixture] Error deleting log file: {ex.Message}");
            }
            _gameLogFile = null;
        }

        Console.WriteLine("[IntegrationTestFixture] Game client stopped");
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
                Console.WriteLine("[IntegrationTestFixture] make stop timed out");
                try { stopProcess.Kill(); } catch { }
                return false;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[IntegrationTestFixture] make stop error: {ex.Message}");
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
                        Console.WriteLine($"[IntegrationTestFixture] Killing process: {proc.ProcessName} (PID: {proc.Id})");
                        proc.Kill(entireProcessTree: true);
                        await proc.WaitForExitAsync();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[IntegrationTestFixture] Failed to kill {proc.ProcessName}: {ex.Message}");
                    }
                    finally
                    {
                        proc.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[IntegrationTestFixture] Error finding processes '{name}': {ex.Message}");
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
            Console.WriteLine($"[IntegrationTestFixture] No .env file found");
            return result;
        }

        Console.WriteLine($"[IntegrationTestFixture] Loading environment from {path}");

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
        if (string.IsNullOrEmpty(input)) return "";

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
