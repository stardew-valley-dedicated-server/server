using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Images;
using DotNet.Testcontainers.Networks;
using JunimoServer.Tests.Clients;
using JunimoServer.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using Spectre.Console;
using Spectre.Console.Rendering;
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
/// <remarks>
/// <h2>Architecture Notes</h2>
/// <para>
/// This test infrastructure contains several non-standard patterns that exist for good reasons.
/// Please read this before modifying.
/// </para>
///
/// <h3>1. Environment.Exit(1) for Server Errors</h3>
/// <para>
/// When fatal errors are detected in server logs, we call Environment.Exit(1) to abort the
/// entire test process. This is unusual but necessary because:
/// - Server log monitoring runs in a background task
/// - Background tasks cannot throw exceptions that fail the current test
/// - Continuing tests after server failure produces cascading failures
/// - This provides fast, clean abort of the test run
/// </para>
///
/// <h3>2. Reflection to Extract Test Names</h3>
/// <para>
/// IntegrationTestBase uses reflection to access the private 'test' field on ITestOutputHelper.
/// xUnit doesn't expose the test name through its public API, but we need it for logging.
/// This could break on xUnit updates - if it does, wrap in try/catch and degrade gracefully.
/// </para>
///
/// <h3>3. File-Based Game Client Logging</h3>
/// <para>
/// Game client output is redirected to a temp file and tailed, rather than using piped stdout.
/// Piped output caused deadlocks when debugging (pipe buffer fills, process blocks).
/// File-based logging avoids this at the cost of slight complexity.
/// </para>
///
/// <h3>4. Multi-Strategy Container Cleanup</h3>
/// <para>
/// DisposeContainerSafely first tries Testcontainers' DisposeAsync, then falls back to
/// Docker CLI force-remove. Testcontainers occasionally fails to clean up properly,
/// especially on test abort. The fallback ensures we don't leak containers.
/// </para>
///
/// <h3>5. Dual Logging (Console + ITestOutputHelper)</h3>
/// <para>
/// We write to both stdout and xUnit's ITestOutputHelper intentionally:
/// - Console output is visible in real-time during test runs
/// - ITestOutputHelper is captured for Test Explorer / CI artifacts
/// Both are needed for good developer experience.
/// </para>
///
/// <h3>6. Sequential Execution Only</h3>
/// <para>
/// Tests run sequentially (ParallelizeTestCollections=false) because they share a single
/// game client and server. Parallel execution would cause race conditions. This is slower
/// but correct for E2E tests with shared mutable state.
/// </para>
/// </remarks>
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

    // File-based logging to avoid pipe buffer deadlocks when debugging
    private string? _gameLogFile;
    private CancellationTokenSource? _logTailCts;
    private Task? _logTailTask;

    // Server log streaming
    private CancellationTokenSource? _serverLogCts;
    private Task? _serverLogTask;
    private long _serverLogPosition;

    // Paths
    private static readonly string ServerRepoDir = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    private static readonly string ProjectParentDir = Path.GetFullPath(
        Path.Combine(ServerRepoDir, "..")) + Path.DirectorySeparatorChar;
    private static readonly string TestClientDir = Path.GetFullPath(
        Path.Combine(ServerRepoDir, "tools", "test-client"));

    // Image configuration
    // Use "local" for locally built images, or a specific version/latest for CI
    private static readonly string ImageTag = Environment.GetEnvironmentVariable("SDVD_IMAGE_TAG") ?? "local";
    private static readonly bool UseLocalImages = ImageTag == "local";
    private static readonly bool SkipBuild = string.Equals(
        Environment.GetEnvironmentVariable("SDVD_SKIP_BUILD"), "true", StringComparison.OrdinalIgnoreCase);

    // Test output formatting configuration
    // SDVD_TEST_VERBOSE=true to show all container logs (disables noise filtering)
    // SDVD_TEST_ICONS=false to disable unicode status icons
    private static readonly bool VerboseContainerLogs = string.Equals(
        Environment.GetEnvironmentVariable("SDVD_TEST_VERBOSE"), "true", StringComparison.OrdinalIgnoreCase);
    private static readonly bool UseIcons = !string.Equals(
        Environment.GetEnvironmentVariable("SDVD_TEST_ICONS"), "false", StringComparison.OrdinalIgnoreCase);

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

    // Test run abort state - shared across all tests in the collection
    private volatile bool _testRunAborted;
    private string? _abortReason;
    private readonly object _abortLock = new();

    // Server error detection - captures ERROR/FATAL patterns from server logs
    private readonly List<string> _serverErrors = new();
    private readonly object _serverErrorsLock = new();

    // Info for Ready table (consolidated from various init steps)
    private string? _envFilePath;
    private string? _volumePrefix;

    // Test run timing
    private DateTime _testRunStartTime;

    // Cancellation token that gets triggered when a server error is detected
    // Tests can use this to abort waiting HTTP calls immediately
    private CancellationTokenSource? _errorCancellation;
    private readonly object _errorCancellationLock = new();


    /// <summary>
    /// Returns true if the test run has been aborted due to an exception.
    /// </summary>
    public bool IsTestRunAborted => _testRunAborted;

    /// <summary>
    /// Gets the reason for the test run abort, if any.
    /// </summary>
    public string? AbortReason => _abortReason;

    /// <summary>
    /// Signals that all remaining tests should be aborted.
    /// Called when an exception is detected and AbortOnException is enabled.
    /// </summary>
    public void AbortTestRun(string reason)
    {
        lock (_abortLock)
        {
            if (_testRunAborted) return;
            _testRunAborted = true;
            _abortReason = reason;
            Log("", LogLevel.Info);
            Log("TEST RUN ABORTED", LogLevel.Error);
            Log($"Reason: {reason}", LogLevel.Error);
            Log("All remaining tests will be skipped.", LogLevel.Error);
            Log("", LogLevel.Info);
        }
    }

    /// <summary>
    /// Throws if the test run has been aborted.
    /// Call this at the start of each test to skip remaining tests after an abort.
    /// </summary>
    public void ThrowIfAborted()
    {
        if (_testRunAborted)
        {
            throw new TestRunAbortedException(_abortReason ?? "Test run was aborted due to exception in previous test");
        }
    }

    /// <summary>
    /// Gets any server errors detected since last clear.
    /// Errors are detected by parsing server logs for ERROR/FATAL patterns.
    /// </summary>
    public IReadOnlyList<string> GetServerErrors()
    {
        lock (_serverErrorsLock)
        {
            return _serverErrors.ToList();
        }
    }

    /// <summary>
    /// Clears all detected server errors for a new test.
    /// Does NOT reset anything if test run is aborted - keeps error state.
    /// </summary>
    public void ClearServerErrors()
    {
        // If test run is aborted, don't clear anything - keep the cancelled token
        if (_testRunAborted) return;

        lock (_serverErrorsLock)
        {
            _serverErrors.Clear();
        }
        ResetErrorCancellation();
    }

    /// <summary>
    /// Returns true if any server errors have been detected.
    /// </summary>
    public bool HasServerErrors
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
    /// Gets a cancellation token that will be triggered when a server error is detected.
    /// Use this to abort waiting HTTP calls immediately when errors occur.
    /// </summary>
    public CancellationToken GetErrorCancellationToken()
    {
        lock (_errorCancellationLock)
        {
            _errorCancellation ??= new CancellationTokenSource();
            return _errorCancellation.Token;
        }
    }

    /// <summary>
    /// Signals that a server error has occurred, cancelling any operations
    /// waiting on the error cancellation token.
    /// </summary>
    private void SignalServerError(string error)
    {
        LogError("FATAL: Server error detected", error);
        Console.Error.Flush();

        // Exit immediately - this is the only way to reliably abort a blocked test
        Environment.Exit(1);
    }

    /// <summary>
    /// Resets the error cancellation token for a new test.
    /// Called by ClearServerErrors.
    /// </summary>
    private void ResetErrorCancellation()
    {
        lock (_errorCancellationLock)
        {
            _errorCancellation?.Dispose();
            _errorCancellation = new CancellationTokenSource();
        }
    }

    // ---------------------------------------------------------------------------
    //  Logging helpers
    // ---------------------------------------------------------------------------

    private enum LogLevel { Header, Info, Success, Warn, Error, Detail }

    private static readonly bool UseColor = ResolveColorSupport();

    /// <summary>
    /// Static constructor to configure Spectre.Console color support
    /// based on the same detection logic we use for manual ANSI codes.
    /// </summary>
    static IntegrationTestFixture()
    {
        if (UseColor)
        {
            AnsiConsole.Profile.Capabilities.ColorSystem = ColorSystem.TrueColor;
            AnsiConsole.Profile.Capabilities.Ansi = true;
        }
        else
        {
            AnsiConsole.Profile.Capabilities.ColorSystem = ColorSystem.NoColors;
            AnsiConsole.Profile.Capabilities.Ansi = false;
        }

        // Sample dashboard layout
        //var layout = new Layout("Root")
        //    .SplitRows(
        //        new Layout("Header").Size(3),
        //        new Layout("Main"),
        //        new Layout("Footer").Size(3));

        //layout["Header"].Update(
        //    new Panel("[bold yellow]System Dashboard[/]")
        //        .BorderColor(Color.Yellow)
        //        .RoundedBorder()
        //        .Expand());

        //layout["Main"].SplitColumns(
        //    new Layout("Left").Ratio(1),
        //    new Layout("Right").Ratio(2));

        //layout["Left"].SplitRows(
        //    new Layout("Metrics"),
        //    new Layout("Logs"));

        //layout["Metrics"].Update(
        //    new Panel("[green]CPU: 45%\nRAM: 62%\nDisk: 78%[/]")
        //        .Header("System Metrics")
        //        .BorderColor(Color.Green)
        //        .RoundedBorder()
        //        .Expand());

        //layout["Logs"].Update(
        //    new Panel("[dim]12:03 Process started\n12:05 Connected\n12:07 Ready[/]")
        //        .Header("Recent Logs")
        //        .BorderColor(Color.Blue)
        //        .RoundedBorder()
        //        .Expand());

        //layout["Right"].Update(
        //    new Panel("[white]Main application content area\nDisplays real-time data and visualizations[/]")
        //        .Header("Content")
        //        .BorderColor(Color.Cyan1)
        //        .RoundedBorder()
        //        .Expand());

        //layout["Footer"].Update(
        //    new Panel("[dim]Connected | Uptime: 2h 34m | Last Update: 12:07:45[/]")
        //        .BorderColor(Color.Grey)
        //        .RoundedBorder()
        //        .Expand());

        //AnsiConsole.Write(layout);
    }

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

    private const string FixturePrefix = "[Setup]";
    private const string ServerPrefix = "[Server]";
    private const string GamePrefix = "[Game]";
    private const string BuildPrefix = "[Build]";

    // Unicode status icons (used when SDVD_TEST_ICONS != false)
    private static readonly string IconSuccess = UseIcons ? "✓" : "[OK]";
    private static readonly string IconError = UseIcons ? "✗" : "[ERROR]";
    private static readonly string IconWarning = UseIcons ? "!" : "[WARN]";
    private static readonly string IconDetail = UseIcons ? "→" : "->";

    // Container log noise patterns - lines containing these are filtered unless SDVD_TEST_VERBOSE=true
    private static readonly string[] ContainerNoisePatterns = new[]
    {
        "[cont-env]",
        "[cont-secrets]",
        "[cont-init]",
        "[supervisor]",
        "[xvnc]",
        "[nginx]",
        "[polybar]",
        "[xrdb]",
        "[openbox]",
        "[dbus]",
    };

    private static void Log(string message, LogLevel level = LogLevel.Info)
    {
        var (style, icon) = level switch
        {
            LogLevel.Header => ("bold", ""),
            LogLevel.Success => ("green", IconSuccess + " "),
            LogLevel.Warn => ("yellow", IconWarning + " "),
            LogLevel.Error => ("red", IconError + " "),
            LogLevel.Detail => ("grey", IconDetail + " "),
            _ => ("default", ""),
        };

        AnsiConsole.MarkupLine($"{Markup.Escape(FixturePrefix)} [{style}]{icon}{Markup.Escape(message)}[/]");
    }

    // Zero-width space - invisible but not whitespace, so it won't be filtered by vstest
    // The vstest ConsoleLogger uses IsNullOrWhiteSpace() to filter lines
    private const string BlankLine = "\u200B";

    /// <summary>
    /// Print a major phase header using Spectre.Console Panel.
    /// Format: "[Category] Description" - e.g., "[Test] NavigationTests.CanConnect"
    /// </summary>
    public static void LogTestPhase(string category, string description)
    {
        Console.Out.WriteLine(BlankLine);
        Console.Out.WriteLine(BlankLine);

        var title = $"[[{category}]] {description}";
        var panel = new Panel(title)
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.White)
            .BorderStyle(Style.Parse("rgb(255,255,255)"))
            .Padding(1, 0)
            .Expand();

        AnsiConsole.Write(panel);
        Console.Out.Flush();
    }

    // Private alias for internal fixture use
    private static void LogPhaseHeader(string category, string description) => LogTestPhase(category, description);

    /// <summary>
    /// Print a sub-phase header using Spectre.Console Panel.
    /// Used for minor phases like Cleanup.
    /// </summary>
    public static void LogTestSubPhase(string title)
    {
        Console.Out.WriteLine(BlankLine);

        var panel = new Panel(title)
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.White)
            .BorderStyle(Style.Parse("rgb(255,255,255)"))
            .Padding(1, 0)
            .Expand();

        AnsiConsole.Write(panel);
        Console.Out.Flush();
    }


    /// <summary>
    /// Log a simple message to console (for test output).
    /// </summary>
    public static void LogTestMessage(string message)
    {
        Console.Out.WriteLine(message);
        Console.Out.Flush();
    }

    /// <summary>
    /// Print an error with prominent box separators.
    /// </summary>
    private static void LogError(string title, string? details = null)
    {
        Console.Out.WriteLine(BlankLine);

        var content = string.IsNullOrEmpty(details)
            ? $"[bold red]{IconError} {Markup.Escape(title)}[/]"
            : $"[bold red]{IconError} {Markup.Escape(title)}[/]\n[red]{Markup.Escape(details)}[/]";

        var panel = new Panel(new Markup(content))
            .Border(BoxBorder.Heavy)
            .BorderColor(Color.Red)
            .Padding(1, 0)
            .Expand();

        AnsiConsole.Write(panel);
        Console.Out.Flush();
    }

    /// <summary>
    /// Check if a server log line is noise that should be filtered.
    /// </summary>
    private static bool IsContainerNoise(string line)
    {
        if (VerboseContainerLogs) return false;

        foreach (var pattern in ContainerNoisePatterns)
        {
            if (line.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static void PrintInfoPanel(string title, (string label, string? value, bool isPath)[] rows)
    {
        var contentRows = rows.Where(r => !string.IsNullOrEmpty(r.label) && !string.IsNullOrEmpty(r.value)).ToArray();

        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Green)
            .Title($"[bold green]{Markup.Escape(title)}[/]")
            .AddColumn(new TableColumn("[white]Setting[/]").LeftAligned())
            .AddColumn(new TableColumn("[white]Value[/]").LeftAligned())
            .Expand();

        foreach (var (label, value, isPath) in contentRows)
        {
            IRenderable valueRenderable;

            if (isPath && !string.IsNullOrEmpty(value))
            {
                // Use TextPath for file paths with uniform cyan color like other links
                valueRenderable = new TextPath(value)
                    .RootColor(Color.Cyan1)
                    .SeparatorColor(Color.Cyan1)
                    .StemColor(Color.Cyan1)
                    .LeafColor(Color.Cyan1);
            }
            else if (value!.StartsWith("http://") || value.StartsWith("https://"))
            {
                // Make URLs clickable
                valueRenderable = new Markup($"[link={value}][cyan]{Markup.Escape(value)}[/][/]");
            }
            else
            {
                valueRenderable = new Markup($"[cyan]{Markup.Escape(value)}[/]");
            }

            table.AddRow(
                new Markup($"[white]{Markup.Escape(label)}[/]"),
                valueRenderable
            );
        }

        Console.Out.WriteLine(BlankLine);
        AnsiConsole.Write(table);
        Console.Out.Flush();
    }

    // ---------------------------------------------------------------------------
    //  Lifecycle
    // ---------------------------------------------------------------------------

    public async Task InitializeAsync()
    {
        _testRunStartTime = DateTime.UtcNow;

        LogPhaseHeader("Setup", "Integration Tests");

        // Initialize error cancellation token early (before log streaming starts)
        // This ensures it's available when server errors are detected during startup
        _errorCancellation = new CancellationTokenSource();

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
            // Kill existing game client so we can start fresh with log capture
            Log("Game client already running, stopping it for fresh start with log capture...", LogLevel.Warn);
            await KillGameProcesses();
            await Task.Delay(2000); // Wait for processes to fully exit
        }

        await StartGameClient();

        IsReady = true;

        PrintInfoPanel("Ready", new (string, string?, bool)[]
        {
            ("Server API", ServerBaseUrl, false),
            ("Server VNC", ServerVncUrl, false),
            ("Game Client", GameClientBaseUrl, false),
            ("Invite Code", InviteCode ?? "N/A", false),
            ("Screenshots", TestArtifacts.ScreenshotsDir, true),
            ("Game Log", _gameLogFile, true),
            ("Env File", _envFilePath, true),
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
        LogTestSubPhase("Building Images");

        // Build server image (equivalent to `make build`)
        await RunBuildCommand(
            "make",
            "build",
            ServerRepoDir,
            "server image",
            TimeSpan.FromMinutes(10)
        );

        // Build steam-service image (equivalent to `docker compose build steam-auth`)
        await RunBuildCommand(
            "docker",
            "compose build steam-auth",
            ServerRepoDir,
            "steam-service image",
            TimeSpan.FromMinutes(5)
        );
    }

    private async Task RunBuildCommand(string command, string arguments, string workingDirectory, string description, TimeSpan timeout, bool quiet = true)
    {
        Log($"Building {description}...");

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

        // Use compact progress output for Docker builds
        startInfo.Environment["DOCKER_PROGRESS"] = "auto";

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start '{command} {arguments}'");

        // Stream output to console (dim so it doesn't dominate)
        var stdoutTask = Task.Run(async () => {
            if (quiet) return;

            while (await process.StandardOutput.ReadLineAsync() is { } line)
            {
                if (!quiet)
                {
                    AnsiConsole.MarkupLine($"[grey]{BuildPrefix} {Markup.Escape(line)}[/]");
                }
            }
        });

        var stderrTask = Task.Run(async () => {

            while (await process.StandardError.ReadLineAsync() is { } line)
            {
                if (!quiet)
                {
                    AnsiConsole.MarkupLine($"[grey]{BuildPrefix} {Markup.Escape(line)}[/]");
                }
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

        Log($"Built {description}", LogLevel.Success);
    }

    private async Task StartServerContainers()
    {
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

        LogTestSubPhase("Starting Containers");

        // Load environment variables from .env file if it exists
        _envFilePath = FindEnvFile();
        var envVars = LoadEnvFile(_envFilePath);

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

        // Build steam-auth container
        // Mount existing Docker volumes to reuse downloaded game files and Steam session
        // Volume names are prefixed with docker-compose project name (typically "server")
        _volumePrefix = Environment.GetEnvironmentVariable("SDVD_VOLUME_PREFIX") ?? "server";

        // Use a fresh saves volume for each test run (clean slate)
        var testRunId = Guid.NewGuid().ToString("N")[..8];
        var testSavesVolume = $"sdvd-test-saves-{testRunId}";

        var steamAuthBuilder = new ContainerBuilder()
            .WithLogger(NullLogger.Instance)
            .WithImage($"sdvd/steam-service:{ImageTag}")
            .WithImagePullPolicy(UseLocalImages ? PullPolicy.Never : PullPolicy.Missing)
            .WithName($"sdvd-steam-auth-test-{Guid.NewGuid():N}")
            .WithNetwork(_network)
            .WithNetworkAliases("steam-auth")
            .WithPortBinding(SteamAuthPort, true)
            // Mount existing volumes for game data and steam session
            .WithVolumeMount($"{_volumePrefix}_steam-session", "/data/steam-session")
            .WithVolumeMount($"{_volumePrefix}_game-data", "/data/game")
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
            Log("Started steam-auth container", LogLevel.Success);
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
            .WithLogger(NullLogger.Instance)
            .WithImage($"sdvd/server:{ImageTag}")
            .WithImagePullPolicy(UseLocalImages ? PullPolicy.Never : PullPolicy.Missing)
            .WithName($"sdvd-server-test-{Guid.NewGuid():N}")
            .WithNetwork(_network)
            .WithPortBinding(ServerApiPort, true)
            .WithPortBinding(VncPort, true)
            // Mount game data (existing) and fresh saves volume
            .WithVolumeMount($"{_volumePrefix}_game-data", "/data/game")
            .WithVolumeMount(testSavesVolume, "/config/xdg/config/StardewValley")
            .WithEnvironment("STEAM_AUTH_URL", $"http://steam-auth:{SteamAuthPort}")
            .WithEnvironment("API_ENABLED", "true")
            .WithEnvironment("API_PORT", ServerApiPort.ToString())
            .WithEnvironment("DISABLE_RENDERING", "true")
            // Fail-fast mode: exit immediately on ERROR logs (for fast test failure detection)
            .WithEnvironment("TEST_FAIL_FAST", "true")
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
        Log("Started server container", LogLevel.Success);

        ServerPort = _serverContainer.GetMappedPublicPort(ServerApiPort);
        ServerVncPort = _serverContainer.GetMappedPublicPort(VncPort);

        // Start streaming server logs immediately so they appear in real-time while waiting
        _serverLogCts = new CancellationTokenSource();
        _serverLogTask = Task.Run(() => StreamServerLogsAsync(_serverLogCts.Token));

        // Wait for server to be fully ready (with invite code)
        LogTestSubPhase("Waiting for Server Container");
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
    }

    /// <summary>
    /// Stream server container logs to console with [Server] prefix.
    /// This runs in background throughout the test run to provide a unified log view.
    ///
    /// Behavior:
    /// Streams to console (visible with verbosity=detailed in runsettings).
    /// </summary>
    private async Task StreamServerLogsAsync(CancellationToken ct)
    {
        if (_serverContainer == null) return;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var logs = await _serverContainer.GetLogsAsync(
                    timestampsEnabled: false,
                    ct: ct);

                // Process both stdout and stderr
                var combinedOutput = (logs.Stdout ?? "") + (logs.Stderr ?? "");
                var allLines = combinedOutput.Split('\n');

                // Process new lines only
                for (var i = (int)_serverLogPosition; i < allLines.Length; i++)
                {
                    var line = allLines[i];
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    // Clean the line similar to game logs
                    var cleanedLine = CleanLine(line);
                    if (cleanedLine.Length > 0)
                    {
                        // Filter container initialization noise unless verbose mode
                        if (IsContainerNoise(cleanedLine))
                            continue;

                        // Write to console (visible with verbosity=detailed) - dark grey for server logs
                        AnsiConsole.MarkupLine($"[grey42]{Markup.Escape(ServerPrefix)} {Markup.Escape(cleanedLine)}[/]");

                        // Detect server errors (SMAPI log format: [timestamp ERROR module] or [timestamp FATAL module])
                        // Use word boundary regex for robust matching regardless of surrounding characters
                        if (Regex.IsMatch(cleanedLine, @"\b(ERROR|FATAL)\b"))
                        {
                            SignalServerError(cleanedLine);
                        }
                    }
                }

                _serverLogPosition = allLines.Length;

                await Task.Delay(500, ct); // Poll every 500ms
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // Log error but continue trying
                if (!ct.IsCancellationRequested)
                {
                    Log($"Error streaming server logs: {ex.Message}", LogLevel.Error);
                    await Task.Delay(2000, ct);
                }
            }
        }
    }

    private async Task StartGameClient()
    {
        LogTestSubPhase("Waiting for Game Client");

        // Use file-based logging to avoid pipe buffer deadlocks when debugging.
        // When hitting a breakpoint, pipe readers pause, the buffer fills, and the
        // game process blocks on writes - causing "Not Responding". Files don't block.
        _gameLogFile = Path.Combine(Path.GetTempPath(), $"sdvd-test-client-{Guid.NewGuid():N}.log");

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

        // Enable fail-fast mode for the game client (exit on ERROR logs)
        startInfo.Environment["TEST_FAIL_FAST"] = "true";

        _gameProcess = new Process { StartInfo = startInfo };
        _gameProcess.Start();

        // Start background task to tail the log file
        _logTailCts = new CancellationTokenSource();
        _logTailTask = Task.Run(() => TailLogFile(_gameLogFile, _logTailCts.Token));

        var deadline = DateTime.UtcNow.AddSeconds(GameReadyTimeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            if (await IsGameClientResponding())
            {
                // Brief delay to let the log tailer catch up with the /ping request
                await Task.Delay(100);
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
    ///
    /// Behavior:
    /// - Always notifies log subscribers (routes to test output helper during tests)
    /// - When SDVD_STREAM_LOGS=true: Also writes to Console for direct visibility
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

                        // Write to console (visible with verbosity=detailed) - light grey for game logs
                        AnsiConsole.MarkupLine($"[grey46]{Markup.Escape(GamePrefix)} {Markup.Escape(cleanedLine)}[/]");
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

    /// <summary>
    /// Called automatically by xUnit after all tests in the collection complete.
    /// </summary>
    public async Task DisposeAsync()
    {
        LogTestSubPhase("Tests finished");

        // Stop game client - always try even if exceptions occur
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

        // Stop server log streaming
        if (_serverLogCts != null)
        {
            try
            {
                _serverLogCts.Cancel();
                if (_serverLogTask != null)
                {
                    await _serverLogTask.WaitAsync(TimeSpan.FromSeconds(2));
                }
            }
            catch (TimeoutException)
            {
                Log("Server log stream didn't stop in time", LogLevel.Warn);
            }
            catch (Exception ex)
            {
                Log($"Error stopping server log stream: {ex.Message}", LogLevel.Error);
            }
            finally
            {
                _serverLogCts.Dispose();
                _serverLogCts = null;
            }
        }

        // Stop server containers (only if we started them)
        if (!_usingExistingServer)
        {
            // Dispose containers — DisposeAsync handles stop + remove.
            // If that fails, fall back to docker rm -f.
            Log("Stopping containers...", LogLevel.Detail);
            await DisposeContainerSafely(_serverContainer, "server");
            await DisposeContainerSafely(_steamAuthContainer, "steam-auth");

            if (_network != null)
            {
                try
                {
                    await _network.DisposeAsync();
                }
                catch (Exception ex)
                {
                    Log($"Error removing network: {ex.Message}", LogLevel.Error);
                }
            }

            // Clean up test saves volume
            await RemoveDockerVolume(_testSavesVolume);
        }

        var totalDuration = DateTime.UtcNow - _testRunStartTime;
        AnsiConsole.MarkupLine($"[[Test]] [green]{IconSuccess} Done ({totalDuration.TotalSeconds:F1}s total)[/]");
        Console.Out.WriteLine(BlankLine);
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
            return result;
        }

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

        // Remove timestamps (HH:MM:SS, HH:MM:SS.mmm, ISO 8601 datetime)
        result = Regex.Replace(result, @"\d{2}:\d{2}:\d{2}(\.\d+)?\s*", "");
        result = Regex.Replace(result, @"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?(Z|[+-]\d{2}:?\d{2})?\s*", "");

        // Remove padding inside brackets: [app         ] -> [app]
        result = Regex.Replace(result, @"\[(\S+)\s+\]", "[$1]");

        // Shorten paths for readability
        result = result.Replace(@"D:\GitlabRunner\builds\Gq5qA5P4\0\ConcernedApe\stardewvalley", "StardewValley");
        result = result.Replace(ProjectParentDir, "");

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

/// <summary>
/// Exception thrown when the test run has been aborted due to an exception in a previous test.
/// </summary>
public class TestRunAbortedException : Exception
{
    public TestRunAbortedException(string message) : base(message) { }
}
