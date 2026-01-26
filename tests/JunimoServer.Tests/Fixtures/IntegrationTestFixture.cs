using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Images;
using DotNet.Testcontainers.Networks;
using JunimoServer.Tests.Clients;
using JunimoServer.Tests.Containers;
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
/// <h3>1. Cancellation-Based Server Error Handling</h3>
/// <para>
/// When fatal errors are detected in server logs, we use cancellation tokens to abort
/// inflight operations cleanly:
/// - SignalServerError() cancels the error cancellation token
/// - Tests pass GetErrorCancellationToken() to HTTP calls and waits
/// - AbortTestRun() is called to skip remaining tests
/// - This allows proper cleanup of containers and processes
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
    /// <summary>
    /// Gets the collection name for test registration with assembly fixture.
    /// Override in derived classes (e.g., NoPasswordFixture).
    /// </summary>
    protected virtual string CollectionName => "Integration";

    private IContainer? _serverContainer;
    private IContainer? _steamAuthContainer;
    private INetwork? _network;
    private string? _testSavesVolume;
    private Process? _gameProcess;
    private readonly StringBuilder _outputLog = new();
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

    // Containerized game clients (opt-in via SDVD_CONTAINERIZED_CLIENTS=true)
    private static readonly bool UseContainerizedClients = string.Equals(
        Environment.GetEnvironmentVariable("SDVD_CONTAINERIZED_CLIENTS"), "true", StringComparison.OrdinalIgnoreCase);
    private GameClientManager? _clientManager;

    // Password protection for tests
    // Override GetServerPassword() in derived classes to change behavior
    public const string TestServerPassword = "test-password-123";

    /// <summary>
    /// Gets the server password to use for this fixture.
    /// Override in derived classes to disable password protection (return null).
    /// </summary>
    protected virtual string? GetServerPassword() => TestServerPassword;

    /// <summary>
    /// Gets the server password for test access (calls GetServerPassword()).
    /// Used by IntegrationTestBase for auto-login functionality.
    /// </summary>
    public string? ServerPassword => GetServerPassword();

    // Ports
    private const int ServerApiPort = 8080;
    private const int VncPort = 5800;
    private const int TestClientApiPort = 5123;
    private const int SteamAuthPort = 3001;
    private const int GamePort = 24642; // UDP port for LAN/IP connections

    // Timeouts - use TestTimings for centralized values

    public bool IsReady { get; private set; }
    public int ServerPort { get; private set; } = ServerApiPort;
    public int ServerVncPort { get; private set; } = VncPort;
    public int ServerGamePort { get; private set; } = GamePort;
    public string ServerBaseUrl => $"http://localhost:{ServerPort}";
    public string ServerVncUrl => $"http://localhost:{ServerVncPort}";
    public string GameClientBaseUrl => $"http://localhost:{TestClientApiPort}";
    public IContainer? ServerContainer => _serverContainer;
    public string? InviteCode { get; private set; }
    public string OutputLog => _outputLog.ToString();

    /// <summary>
    /// Returns true if containerized game clients are enabled.
    /// Set SDVD_CONTAINERIZED_CLIENTS=true to enable.
    /// </summary>
    public bool ContainerizedClientsEnabled => UseContainerizedClients;

    /// <summary>
    /// Manager for containerized game clients.
    /// Only available when SDVD_CONTAINERIZED_CLIENTS=true.
    /// </summary>
    public GameClientManager ClientManager =>
        _clientManager ?? throw new InvalidOperationException(
            "Containerized clients not enabled. Set SDVD_CONTAINERIZED_CLIENTS=true to enable.");

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

    // Test run timing and statistics
    private DateTime _testRunStartTime;
    private readonly Dictionary<string, List<string>> _testsByClass = new();
    private readonly object _testCountLock = new();

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

        // Record the error
        lock (_serverErrorsLock)
        {
            _serverErrors.Add(error);
        }

        // Abort the test run so remaining tests are skipped
        AbortTestRun($"Server error: {error}");

        // Cancel the error token to abort any inflight HTTP calls or waits
        lock (_errorCancellationLock)
        {
            try
            {
                _errorCancellation?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Token already disposed, ignore
            }
        }

        // Stop server log streaming to prevent further error signals
        _serverLogCts?.Cancel();
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

    /// <summary>
    /// Registers a test for counting, grouped by class name.
    /// Called by IntegrationTestBase.InitializeAsync or directly by lightweight test classes.
    /// </summary>
    /// <param name="className">The test class name</param>
    /// <param name="testName">The individual test method name (optional)</param>
    public void RegisterTest(string className, string? testName = null)
    {
        lock (_testCountLock)
        {
            if (!_testsByClass.TryGetValue(className, out var tests))
            {
                tests = new List<string>();
                _testsByClass[className] = tests;
            }
            tests.Add(testName ?? "(unknown)");
        }

        // Also register with assembly fixture for unified summary
        TestSummaryFixture.Instance?.RegisterTest(CollectionName, className, testName);
    }

    /// <summary>
    /// Records the duration for a completed test.
    /// </summary>
    public void CompleteTest(string className, string? testName, TimeSpan duration)
    {
        TestSummaryFixture.Instance?.CompleteTest(CollectionName, className, testName, duration);
    }

    /// <summary>
    /// Records a test failure with optional details for the failure summary.
    /// </summary>
    public void RecordFailure(string className, string? testName, string error,
        string? phase = null, string? screenshotPath = null)
    {
        TestSummaryFixture.Instance?.RecordFailure(CollectionName, className, testName, error, phase, screenshotPath);
    }

    /// <summary>
    /// Gets the total test count across all classes.
    /// </summary>
    public int TestCount
    {
        get
        {
            lock (_testCountLock)
            {
                return _testsByClass.Values.Sum(tests => tests.Count);
            }
        }
    }

    /// <summary>
    /// Gets test names grouped by class name.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> TestsByClass
    {
        get
        {
            lock (_testCountLock)
            {
                return _testsByClass.ToDictionary(
                    kvp => kvp.Key,
                    kvp => (IReadOnlyList<string>)kvp.Value.ToList());
            }
        }
    }

    // ---------------------------------------------------------------------------
    //  Logging helpers
    // ---------------------------------------------------------------------------

    private enum LogLevel { Header, Info, Success, Warn, Error, Detail }

    private static readonly bool UseColor = ResolveColorSupport();

    /// <summary>
    /// Static constructor to configure Spectre.Console color support and terminal width
    /// based on the same detection logic we use for manual ANSI codes.
    /// </summary>
    static IntegrationTestFixture()
    {
        // Wrap Console.Out with timestamping writer (must be done BEFORE configuring AnsiConsole)
        Console.SetOut(new TimestampingTextWriter(Console.Out));

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

        // Set terminal width from COLUMNS env var (passed by Makefile)
        // This fixes Spectre.Console using 80 cols when output is piped
        var columnsEnv = Environment.GetEnvironmentVariable("COLUMNS");
        if (int.TryParse(columnsEnv, out var columns) && columns > 0)
        {
            AnsiConsole.Profile.Width = columns;
        }
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

    // Server error patterns to ignore - these are known non-fatal errors that shouldn't abort tests
    private static readonly string[] IgnoredErrorPatterns = new[]
    {
        "XACT", // XACT audio initialization fails in headless/container mode but game continues fine
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
    /// Log step duration as a grey detail message.
    /// </summary>
    private static void LogStepDuration(DateTime startTime)
    {
        var duration = DateTime.UtcNow - startTime;
        Log($"Step took {duration.TotalSeconds:F1}s", LogLevel.Detail);
    }

    // Junimo ASCII art variants with line counts for vertical centering
    // Content height is 6 lines (title row, separator, info row, image, blank, status row)
    private const int BannerContentHeight = 6;

    // Junimo ASCII art - small variant - 11 lines
    private const string JunimoArtSmall = @"[green]        ██
        ██
  ██████████████
 ██            ██
 ██            ██ █
███    ██  ██  ███
█ ██    ██  ██  ██
 ██            ██
 ██            ██
   ████████████
      ███  ███[/]
";
    private const int JunimoArtSmallHeight = 11;

    // Junimo ASCII art - medium variant - 16 lines
    private const string JunimoArtMedium = @"[green]            ██
             ████
              ███
     ████████████████████
    ████              ████
   ███                  ███
   ███                  ███ ██
  ████       █     ██   ██████
██████      ███   ████  ███
██ ███      ███    ██   ███
   ███                  ███
   ███                  ███
    ███                ███
     ████████████████████
           ██    ██
           ████  █████[/]
";
    private const int JunimoArtMediumHeight = 16;

    // Junimo ASCII art - large variant - 22 lines
    private const string JunimoArtLarge = @"[green]                 █
                 █████
                 ██████
                  █████
        ████████████████████████
      ████████████████████████████
     ████                      ████
    ████                        ████
    ████                        ████
    ████                        ████ ███
  ██████        ███      ███    ███████
████████        ████     ████   █████
███ ████        ████     ████   ████
    ████         ██       ██    ████
    ████                        ████
    ████                        ████
    ████                        ████
     █████                    █████
      ████████████████████████████
         ██████████████████████
              ███      ███
              ██████   ██████[/]
";
    private const int JunimoArtLargeHeight = 22;

    /// <summary>
    /// Get vertical padding to center content within the art height.
    /// Adds 1 extra line to shift content down for better visual balance.
    /// </summary>
    private static string GetVerticalPadding(int artHeight)
    {
        var paddingLines = (artHeight - BannerContentHeight) / 2 + 1;
        return new string('\n', paddingLines);
    }

    /// <summary>
    /// Print the main test banner.
    /// </summary>
    private static void PrintTestBanner()
    {
        Console.Out.WriteLine();

        var verboseIcon = VerboseContainerLogs ? "[green]●[/]" : "[grey]○[/]";
        var dockerClientsIcon = UseContainerizedClients ? "[green]●[/]" : "[grey]○[/]";
        var platform = Environment.OSVersion.Platform == PlatformID.Win32NT ? "Windows" : "Linux";

        // Select art variant and calculate vertical padding
        const string selectedArt = JunimoArtMedium;
        const int selectedArtHeight = JunimoArtMediumHeight;
        var verticalPadding = GetVerticalPadding(selectedArtHeight);

        var leftContent = new Markup(
            verticalPadding +
            "[bold green]Junimo Server[/] [dim]//[/] [white]Integration Tests[/]\n" +
            $"[dim]──────────────────────────────────[/]\n" +
            $"[grey]{DateTime.Now:yyyy-MM-dd HH:mm:ss}[/]  [dim]|[/]  [grey]{platform}[/]\n" +
            $"[cyan]sdvd/server:{ImageTag}[/]\n\n" +
            $"{verboseIcon} [white]Verbose Logs[/]   {dockerClientsIcon} [white]Docker Clients[/]"
        );

        // Right column: Junimo art
        var rightContent = new Markup(selectedArt);

        var table = new Table()
            .Border(TableBorder.None)
            .HideHeaders()
            .AddColumn(new TableColumn("Left").PadRight(4))
            .AddColumn(new TableColumn("Right").NoWrap())
            .AddRow(leftContent, rightContent);

        var panel = new Panel(table)
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.Green)
            .Padding(2, 1)
            .Expand();

        AnsiConsole.Write(panel);
        Console.Out.WriteLine();
        Console.Out.Flush();
    }

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

        PrintTestBanner();

        LogPhaseHeader("Setup", "Initializing Test Environment");
        var stepStart = DateTime.UtcNow;

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
            LogStepDuration(stepStart);
        }
        else
        {
            await StartServerContainers(stepStart);
        }
        serverApi.Dispose();

        // Check if game client is already running
        if (await IsGameClientResponding())
        {
            // Kill existing game client so we can start fresh with log capture
            Log("Game client already running, stopping it for fresh start with log capture...", LogLevel.Warn);
            await KillGameProcesses();
            await Task.Delay(TestTimings.ProcessExitDelay); // Wait for processes to fully exit
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
        LogPhaseHeader("Setup", "Building Images");
        var stepStart = DateTime.UtcNow;

        // Build server image (equivalent to `make build`)
        await RunBuildCommand(
            "make",
            "build",
            ServerRepoDir,
            "server image",
            TestTimings.DockerBuildServerTimeout
        );

        // Build steam-service image (equivalent to `docker compose build steam-auth`)
        await RunBuildCommand(
            "docker",
            "compose build steam-auth",
            ServerRepoDir,
            "steam-service image",
            TestTimings.DockerBuildSteamAuthTimeout
        );

        // Build test-client image when containerized clients are enabled
        if (UseContainerizedClients)
        {
            await RunBuildCommand(
                "make",
                "build-test-client",
                ServerRepoDir,
                "test-client image",
                TestTimings.DockerBuildServerTimeout // Use same timeout as server build
            );
        }

        LogStepDuration(stepStart);
    }

    private async Task RunBuildCommand(string command, string arguments, string workingDirectory, string description, TimeSpan timeout)
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

        // Capture output (always capture so we can show on failure)
        var stdoutLines = new List<string>();
        var stderrLines = new List<string>();

        var stdoutTask = Task.Run(async () => {
            while (await process.StandardOutput.ReadLineAsync() is { } line)
            {
                stdoutLines.Add(line);
                if (VerboseContainerLogs)
                {
                    AnsiConsole.MarkupLine($"[grey]{Markup.Escape(BuildPrefix)} {Markup.Escape(line)}[/]");
                }
            }
        });

        var stderrTask = Task.Run(async () => {
            while (await process.StandardError.ReadLineAsync() is { } line)
            {
                stderrLines.Add(line);
                if (VerboseContainerLogs)
                {
                    AnsiConsole.MarkupLine($"[grey]{Markup.Escape(BuildPrefix)} {Markup.Escape(line)}[/]");
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
            // Print captured output on failure
            AnsiConsole.MarkupLine($"[red]Build output for {description}:[/]");
            foreach (var line in stdoutLines)
            {
                AnsiConsole.MarkupLine($"[grey]{Markup.Escape(BuildPrefix)} {Markup.Escape(line)}[/]");
            }
            foreach (var line in stderrLines)
            {
                AnsiConsole.MarkupLine($"[red]{Markup.Escape(BuildPrefix)} {Markup.Escape(line)}[/]");
            }

            throw new InvalidOperationException(
                $"Building {description} failed with exit code {process.ExitCode}.");
        }

        Log($"Built {description}", LogLevel.Success);
    }

    private async Task StartServerContainers(DateTime initStepStart)
    {
        // Clean up stale resources from previous test runs that may have crashed
        await CleanupStaleTestResources();

        // Log duration for "Initializing Test Environment" phase (after stale cleanup)
        LogStepDuration(initStepStart);

        // Build local images automatically (like `make up` does)
        if (UseLocalImages && !SkipBuild)
        {
            await BuildLocalImages();
        }
        else if (SkipBuild)
        {
            Log("Skipping image build (SDVD_SKIP_BUILD=true)", LogLevel.Detail);
        }

        LogPhaseHeader("Setup", "Starting Containers");
        var stepStart = DateTime.UtcNow;

        // Load environment variables from .env file if it exists
        _envFilePath = FindEnvFile();
        var envVars = LoadEnvFile(_envFilePath);

        // Check for Steam credentials (only warn if not using containerized LAN clients,
        // since LAN/IP connections don't require Steam authentication)
        var hasSteamCreds = envVars.ContainsKey("STEAM_REFRESH_TOKEN") && !string.IsNullOrEmpty(envVars["STEAM_REFRESH_TOKEN"])
            || (envVars.ContainsKey("STEAM_USERNAME") && !string.IsNullOrEmpty(envVars["STEAM_USERNAME"])
                && envVars.ContainsKey("STEAM_PASSWORD") && !string.IsNullOrEmpty(envVars["STEAM_PASSWORD"]));

        if (!hasSteamCreds && !UseContainerizedClients)
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
            using var cts = new CancellationTokenSource(TestTimings.ContainerStartTimeout);
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
            .WithPortBinding(GamePort, true) // UDP port for LAN connections (dynamic host port)
                                             // Mount game data (existing) and fresh saves volume
            .WithVolumeMount($"{_volumePrefix}_game-data", "/data/game")
            .WithVolumeMount(testSavesVolume, "/config/xdg/config/StardewValley")
            .WithEnvironment("STEAM_AUTH_URL", $"http://steam-auth:{SteamAuthPort}")
            .WithEnvironment("API_ENABLED", "true")
            .WithEnvironment("API_PORT", ServerApiPort.ToString())
            .WithEnvironment("DISABLE_RENDERING", "false")
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

        // Pass Steam credentials to server for SteamKit2 lobby creation
        if (envVars.TryGetValue("STEAM_USERNAME", out var serverSteamUser) && !string.IsNullOrEmpty(serverSteamUser))
            serverBuilder = serverBuilder.WithEnvironment("STEAM_USERNAME", serverSteamUser);
        if (envVars.TryGetValue("STEAM_PASSWORD", out var serverSteamPass) && !string.IsNullOrEmpty(serverSteamPass))
            serverBuilder = serverBuilder.WithEnvironment("STEAM_PASSWORD", serverSteamPass);

        // Enable password protection for tests (if configured)
        var serverPassword = GetServerPassword();
        if (!string.IsNullOrEmpty(serverPassword))
        {
            serverBuilder = serverBuilder.WithEnvironment("SERVER_PASSWORD", serverPassword);
        }

        _serverContainer = serverBuilder.Build();

        Log("Starting server container...");
        await _serverContainer.StartAsync();
        Log("Started server container", LogLevel.Success);

        ServerPort = _serverContainer.GetMappedPublicPort(ServerApiPort);
        ServerVncPort = _serverContainer.GetMappedPublicPort(VncPort);
        ServerGamePort = _serverContainer.GetMappedPublicPort(GamePort);

        // Start streaming server logs immediately so they appear in real-time while waiting
        _serverLogCts = new CancellationTokenSource();
        _serverLogTask = Task.Run(() => StreamServerLogsAsync(_serverLogCts.Token));

        LogStepDuration(stepStart);

        // Wait for server to be fully ready (with invite code)
        // Pass error cancellation token so we abort immediately on server errors
        LogPhaseHeader("Setup", "Waiting for Server");
        stepStart = DateTime.UtcNow;

        var serverApi = new ServerApiClient($"http://localhost:{ServerPort}");
        var status = await serverApi.WaitForServerOnline(
            TestTimings.ServerReadyTimeout,
            TestTimings.ServerPollInterval,
            GetErrorCancellationToken());
        serverApi.Dispose();

        // Check if we were aborted due to server error
        if (_testRunAborted)
        {
            throw new TestRunAbortedException(_abortReason ?? "Server error during startup");
        }

        if (status == null)
        {
            // Get container logs for debugging
            var logs = await _serverContainer.GetLogsAsync();
            throw new TimeoutException(
                $"Server did not become ready within {TestTimings.ServerReadyTimeout.TotalSeconds} seconds.\n" +
                $"Logs:\n{logs.Stdout}\n\nErrors:\n{logs.Stderr}");
        }

        InviteCode = status.InviteCode;
        LogStepDuration(stepStart);

        // Initialize containerized game client manager if enabled
        if (UseContainerizedClients)
        {
            LogPhaseHeader("Setup", "Initializing Client Manager");
            stepStart = DateTime.UtcNow;

            var clientOptions = new GameClientOptions
            {
                ImageTag = ImageTag,
                GameDataVolume = $"{_volumePrefix}_game-data"
            };

            _clientManager = new GameClientManager(
                clientOptions,
                _network,
                message => Log(message));

            Log($"Containerized client manager ready (game port: {ServerGamePort})", LogLevel.Success);
            LogStepDuration(stepStart);
        }
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

                        // Write to console only in verbose mode - dark grey for server logs
                        if (VerboseContainerLogs)
                        {
                            AnsiConsole.MarkupLine($"[grey42]{Markup.Escape(ServerPrefix)} {Markup.Escape(cleanedLine)}[/]");
                        }

                        // Detect server errors (SMAPI log format: [timestamp ERROR module] or [timestamp FATAL module])
                        // Use word boundary regex for robust matching regardless of surrounding characters
                        if (Regex.IsMatch(cleanedLine, @"\b(ERROR|FATAL)\b"))
                        {
                            // Check if this error should be ignored
                            var isIgnored = IgnoredErrorPatterns.Any(pattern =>
                                cleanedLine.Contains(pattern, StringComparison.OrdinalIgnoreCase));

                            if (!isIgnored)
                            {
                                SignalServerError(cleanedLine);
                            }
                            else if (VerboseContainerLogs)
                            {
                                AnsiConsole.MarkupLine($"[grey42]{Markup.Escape(ServerPrefix)} [yellow](ignored)[/] {Markup.Escape(cleanedLine)}[/]");
                            }
                        }
                    }
                }

                _serverLogPosition = allLines.Length;

                await Task.Delay(TestTimings.ServerLogPollInterval, ct);
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
                    await Task.Delay(TestTimings.ServerLogErrorRetryDelay, ct);
                }
            }
        }
    }

    private async Task StartGameClient()
    {
        LogPhaseHeader("Setup", "Waiting for Game Client");
        var stepStart = DateTime.UtcNow;

        // Check if Steam is running (required for invite code connections)
        if (!IsSteamRunning())
        {
            throw new InvalidOperationException(
                "Steam is not running!\n\n" +
                "The E2E tests require Steam to be running and logged in for invite code connections.\n" +
                "Please start Steam, log in, and run the tests again.\n\n" +
                "Alternatively, set SDVD_CONTAINERIZED_CLIENTS=true to use LAN connections instead.");
        }
        Log("Steam is running", LogLevel.Success);

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

        var deadline = DateTime.UtcNow + TestTimings.GameReadyTimeout;
        var errorToken = GetErrorCancellationToken();
        while (DateTime.UtcNow < deadline)
        {
            // Check for abort (e.g., server error detected)
            if (_testRunAborted || errorToken.IsCancellationRequested)
            {
                throw new TestRunAbortedException(_abortReason ?? "Test run aborted during game client startup");
            }

            if (await IsGameClientResponding())
            {
                // Brief delay to let the log tailer catch up with the /ping request
                await Task.Delay(TestTimings.GameClientPollDelay);
                LogStepDuration(stepStart);
                return;
            }
            await Task.Delay(TestTimings.GameClientStartupPollDelay);
        }

        throw new TimeoutException(
            $"Game client did not become ready within {TestTimings.GameReadyTimeout.TotalSeconds} seconds.\n" +
            $"Output:\n{_outputLog}");
    }

    /// <summary>
    /// Tail a log file and output to console. Runs in background, independent of debugger.
    /// Uses FileShare.ReadWrite to read while the process writes.
    ///
    /// Behavior:
    /// - Always notifies log subscribers (routes to test output helper during tests)
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

                        // Write to console only in verbose mode - light grey for game logs
                        if (VerboseContainerLogs)
                        {
                            AnsiConsole.MarkupLine($"[grey46]{Markup.Escape(GamePrefix)} {Markup.Escape(cleanedLine)}[/]");
                        }
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
        LogPhaseHeader("Cleanup", "Tests Finished");
        var stepStart = DateTime.UtcNow;

        // Stop containerized game clients if enabled
        if (_clientManager != null)
        {
            try
            {
                Log("Disposing containerized game clients...");
                await _clientManager.DisposeAsync();
                Log("Containerized game clients disposed", LogLevel.Success);
            }
            catch (Exception ex)
            {
                Log($"Error disposing client manager: {ex.Message}", LogLevel.Error);
            }
        }

        // Stop game client - always try even if exceptions occur (for local client mode)
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
                    await _serverLogTask.WaitAsync(TestTimings.TaskCleanupTimeout);
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

        LogStepDuration(stepStart);

        // Propagate abort state to assembly fixture (unified summary)
        if (_testRunAborted)
        {
            TestSummaryFixture.Instance?.SetAborted(_abortReason ?? "Unknown abort");
        }
    }

    /// <summary>
    /// Prints a summary panel showing test run statistics.
    /// </summary>
    private void PrintTestSummary()
    {
        var totalDuration = DateTime.UtcNow - _testRunStartTime;

        Console.Out.WriteLine(BlankLine);

        var statusIcon = _testRunAborted ? IconError : IconSuccess;
        var statusColor = _testRunAborted ? "red" : "green";
        var statusText = _testRunAborted ? "Aborted" : "Passed";

        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(_testRunAborted ? Color.Red : Color.Green)
            .Title($"[bold {statusColor}]{statusIcon} Test Run {statusText}[/]")
            .AddColumn(new TableColumn("[white]Test[/]").LeftAligned())
            .AddColumn(new TableColumn("[white]Count[/]").RightAligned())
            .Expand();

        // Add rows for each test class with individual test names, sorted by class name
        var testsByClass = TestsByClass;
        foreach (var (className, tests) in testsByClass.OrderBy(x => x.Key))
        {
            // Remove "Tests" suffix for cleaner display
            var displayName = className.EndsWith("Tests")
                ? className[..^5]
                : className;

            // Add class header row with count
            table.AddRow(
                new Markup($"[white bold]{Markup.Escape(displayName)}[/]"),
                new Markup($"[cyan]{tests.Count}[/]"));

            // Add individual test names (indented)
            foreach (var testName in tests)
            {
                // Extract just the method name from full display name if present
                // Format can be "Namespace.ClassName.MethodName" or just "MethodName"
                var methodName = testName;
                if (testName.Contains('.'))
                {
                    var lastDot = testName.LastIndexOf('.');
                    methodName = testName[(lastDot + 1)..];
                }
                table.AddRow(
                    new Markup($"[grey]  {Markup.Escape(methodName)}[/]"),
                    new Markup(""));
            }
        }

        // Add separator and total
        table.AddEmptyRow();
        table.AddRow(
            new Markup("[white bold]Total[/]"),
            new Markup($"[cyan bold]{TestCount}[/]"));

        table.AddRow(
            new Markup("[white]Duration[/]"),
            new Markup($"[cyan]{totalDuration.TotalSeconds:F1}s[/]"));

        if (_testRunAborted && !string.IsNullOrEmpty(_abortReason))
        {
            // Truncate reason if too long
            var reason = _abortReason.Length > 60
                ? _abortReason[..57] + "..."
                : _abortReason;
            table.AddRow(
                new Markup("[white]Abort Reason[/]"),
                new Markup($"[red]{Markup.Escape(reason)}[/]"));
        }

        AnsiConsole.Write(table);
        Console.Out.WriteLine(BlankLine);
        Console.Out.Flush();
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

    /// <summary>
    /// Check if Steam is running by looking for the Steam process.
    /// Required for invite code connections when using local game client.
    /// </summary>
    private static bool IsSteamRunning()
    {
        try
        {
            return Process.GetProcessesByName("steam").Length > 0;
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
            using var client = new HttpClient { Timeout = TestTimings.HttpHealthCheckTimeout };
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
                    await _logTailTask.WaitAsync(TestTimings.TaskCleanupTimeout);
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
        await Task.Delay(TestTimings.KillRetryDelay);
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
                    Log("Log file still locked, retrying...", LogLevel.Detail);
                    await Task.Delay(TestTimings.KillRetryDelay);
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
            using var cts = new CancellationTokenSource(TestTimings.ContainerStopTimeout);
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

        return result.TrimEnd();
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
/// Uses xUnit's dynamic skip convention ($XunitDynamicSkip$) to report tests as skipped rather than failed.
/// See: https://github.com/xunit/xunit/issues/2073
/// </summary>
public class TestRunAbortedException : Exception
{
    /// <summary>
    /// xUnit v2 dynamic skip prefix - any exception with message starting with this is reported as "skipped".
    /// </summary>
    private const string XunitSkipPrefix = "$XunitDynamicSkip$";

    public TestRunAbortedException(string message) : base($"{XunitSkipPrefix}{message}") { }
}
