using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Images;
using DotNet.Testcontainers.Networks;
using Microsoft.Extensions.Logging.Abstractions;
using Spectre.Console;
using System.Diagnostics;
using Xunit;

namespace JunimoServer.Tests.Fixtures;

/// <summary>
/// Test fixture for download validation tests that manages its own steam-auth container.
///
/// This fixture runs in isolation from IntegrationTestFixture to avoid Steam session conflicts.
/// Download validation tests use a separate steam-auth container that logs into Steam,
/// which can interfere with the Steam lobby created by the main integration tests.
///
/// Prerequisites: Run 'make setup' first to download the game and save Steam session.
/// </summary>
public class DownloadValidationFixture : IAsyncLifetime
{
    private const string CollectionName = "DownloadValidation";

    private IContainer? _steamAuthContainer;
    private INetwork? _network;

    // Use shared volumes (game already downloaded) to avoid session conflicts
    private static readonly string VolumePrefix = Environment.GetEnvironmentVariable("SDVD_VOLUME_PREFIX") ?? "server";
    private string GameDataVolume => $"{VolumePrefix}_game-data";
    private string SteamSessionVolume => $"{VolumePrefix}_steam-session";

    // Image configuration
    private static readonly string ImageTag = Environment.GetEnvironmentVariable("SDVD_IMAGE_TAG") ?? "local";
    private static readonly bool UseLocalImages = ImageTag == "local";
    private static readonly bool SkipBuild = string.Equals(
        Environment.GetEnvironmentVariable("SDVD_SKIP_BUILD"), "true", StringComparison.OrdinalIgnoreCase);

    // Ports
    private const int SteamAuthPort = 3001;

    // Test output formatting configuration
    private static readonly bool UseIcons = !string.Equals(
        Environment.GetEnvironmentVariable("SDVD_TEST_ICONS"), "false", StringComparison.OrdinalIgnoreCase);

    // Unicode status icons
    private static readonly string IconSuccess = UseIcons ? "✓" : "[OK]";
    private static readonly string IconError = UseIcons ? "✗" : "[ERROR]";
    private static readonly string IconDetail = UseIcons ? "→" : "->";

    // Test run tracking
    private DateTime _testRunStartTime;
    private readonly Dictionary<string, List<string>> _testsByClass = new();
    private readonly object _testCountLock = new();
    private volatile bool _testRunAborted;
    private string? _abortReason;
    private readonly object _abortLock = new();

    /// <summary>
    /// Gets the steam-auth container for test methods to execute commands.
    /// </summary>
    public IContainer? SteamAuthContainer => _steamAuthContainer;

    /// <summary>
    /// Gets the game data volume name.
    /// </summary>
    public string GameDataVolumeName => GameDataVolume;

    /// <summary>
    /// Gets the Steam session volume name.
    /// </summary>
    public string SteamSessionVolumeName => SteamSessionVolume;

    /// <summary>
    /// Returns true if the test run has been aborted.
    /// </summary>
    public bool IsTestRunAborted => _testRunAborted;

    /// <summary>
    /// Gets the reason for the test run abort, if any.
    /// </summary>
    public string? AbortReason => _abortReason;

    /// <summary>
    /// Signals that all remaining tests should be aborted.
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
    /// </summary>
    public void ThrowIfAborted()
    {
        if (_testRunAborted)
        {
            throw new TestRunAbortedException(_abortReason ?? "Test run was aborted");
        }
    }

    /// <summary>
    /// Registers a test for counting, grouped by class name.
    /// </summary>
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
    /// Gets the total test count.
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

    private enum LogLevel { Header, Info, Success, Warn, Error, Detail }

    private const string FixturePrefix = "[Setup]";

    private static void Log(string message, LogLevel level = LogLevel.Info)
    {
        var (style, icon) = level switch
        {
            LogLevel.Header => ("bold", ""),
            LogLevel.Success => ("green", IconSuccess + " "),
            LogLevel.Warn => ("yellow", "! "),
            LogLevel.Error => ("red", IconError + " "),
            LogLevel.Detail => ("grey", IconDetail + " "),
            _ => ("default", ""),
        };

        AnsiConsole.MarkupLine($"{Markup.Escape(FixturePrefix)} [{style}]{icon}{Markup.Escape(message)}[/]");
    }

    public async Task InitializeAsync()
    {
        _testRunStartTime = DateTime.UtcNow;

        IntegrationTestFixture.LogTestPhase("Setup", "Download Validation Tests");
        var stepStart = DateTime.UtcNow;

        // Build images if needed (same logic as IntegrationTestFixture)
        if (UseLocalImages && !SkipBuild)
        {
            await BuildSteamServiceImage();
        }
        else if (SkipBuild)
        {
            Log("Skipping image build (SDVD_SKIP_BUILD=true)", LogLevel.Detail);
        }

        Log("Setting up download validation test environment...");
        Log($"Using shared volumes: {GameDataVolume}, {SteamSessionVolume}", LogLevel.Detail);

        // Create isolated network for this test collection
        var networkName = $"sdvd-dltest-{Guid.NewGuid():N}";
        _network = new NetworkBuilder()
            .WithName(networkName)
            .Build();

        await _network.CreateAsync();

        // Build steam-auth container using shared volumes
        _steamAuthContainer = new ContainerBuilder()
            .WithLogger(NullLogger.Instance)
            .WithImage($"sdvd/steam-service:{ImageTag}")
            .WithImagePullPolicy(UseLocalImages ? PullPolicy.Never : PullPolicy.Missing)
            .WithName($"sdvd-steam-auth-dltest-{Guid.NewGuid():N}")
            .WithNetwork(_network)
            .WithNetworkAliases("steam-auth")
            .WithPortBinding(SteamAuthPort, true)
            .WithVolumeMount(SteamSessionVolume, "/data/steam-session")
            .WithVolumeMount(GameDataVolume, "/data/game")
            .WithEnvironment("PORT", SteamAuthPort.ToString())
            .WithEnvironment("GAME_DIR", "/data/game")
            .WithEnvironment("SESSION_DIR", "/data/steam-session")
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilHttpRequestIsSucceeded(r => r
                    .ForPort(SteamAuthPort)
                    .ForPath("/health")
                    .ForStatusCode(System.Net.HttpStatusCode.OK)))
            .Build();

        Log("Starting steam-auth container...");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await _steamAuthContainer.StartAsync(cts.Token);
        Log("Steam-auth container started", LogLevel.Success);

        LogStepDuration(stepStart);
    }

    private async Task BuildSteamServiceImage()
    {
        Log("Building steam-service image...");
        var stepStart = DateTime.UtcNow;

        var serverRepoDir = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

        var startInfo = new ProcessStartInfo
        {
            FileName = "docker",
            Arguments = "compose build steam-auth",
            WorkingDirectory = serverRepoDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.Environment["DOCKER_PROGRESS"] = "auto";

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start docker compose build");

        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            var stderr = await process.StandardError.ReadToEndAsync();
            throw new InvalidOperationException($"Building steam-service image failed: {stderr}");
        }

        Log("Built steam-service image", LogLevel.Success);
        LogStepDuration(stepStart);
    }

    private static void LogStepDuration(DateTime startTime)
    {
        var duration = DateTime.UtcNow - startTime;
        Log($"Step took {duration.TotalSeconds:F1}s", LogLevel.Detail);
    }

    public async Task DisposeAsync()
    {
        IntegrationTestFixture.LogTestSubPhase("Cleanup");
        var stepStart = DateTime.UtcNow;

        Log("Cleaning up download validation test environment...", LogLevel.Detail);

        // Dispose container
        if (_steamAuthContainer != null)
        {
            try
            {
                await _steamAuthContainer.DisposeAsync();
                Log("Steam-auth container disposed", LogLevel.Detail);
            }
            catch (Exception ex)
            {
                Log($"Error disposing container: {ex.Message}", LogLevel.Error);
            }
        }

        // Dispose network
        if (_network != null)
        {
            try
            {
                await _network.DisposeAsync();
                Log("Network disposed", LogLevel.Detail);
            }
            catch (Exception ex)
            {
                Log($"Error disposing network: {ex.Message}", LogLevel.Error);
            }
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
    /// Kept for debugging but no longer called - unified summary is printed by TestSummaryFixture.
    /// </summary>
    private void PrintTestSummary()
    {
        var totalDuration = DateTime.UtcNow - _testRunStartTime;

        Console.Out.WriteLine("\u200B"); // Zero-width space

        var statusIcon = _testRunAborted ? IconError : IconSuccess;
        var statusColor = _testRunAborted ? "red" : "green";
        var statusText = _testRunAborted ? "Aborted" : "Passed";

        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(_testRunAborted ? Color.Red : Color.Green)
            .Title($"[bold {statusColor}]{statusIcon} Download Validation Tests {statusText}[/]")
            .AddColumn(new TableColumn("[white]Test[/]").LeftAligned())
            .AddColumn(new TableColumn("[white]Count[/]").RightAligned())
            .Expand();

        lock (_testCountLock)
        {
            foreach (var (className, tests) in _testsByClass.OrderBy(x => x.Key))
            {
                var displayName = className.EndsWith("Tests")
                    ? className[..^5]
                    : className;

                table.AddRow(
                    new Markup($"[white bold]{Markup.Escape(displayName)}[/]"),
                    new Markup($"[cyan]{tests.Count}[/]"));

                foreach (var testName in tests)
                {
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
        }

        table.AddEmptyRow();
        table.AddRow(
            new Markup("[white bold]Total[/]"),
            new Markup($"[cyan bold]{TestCount}[/]"));

        table.AddRow(
            new Markup("[white]Duration[/]"),
            new Markup($"[cyan]{totalDuration.TotalSeconds:F1}s[/]"));

        if (_testRunAborted && !string.IsNullOrEmpty(_abortReason))
        {
            var reason = _abortReason.Length > 60
                ? _abortReason[..57] + "..."
                : _abortReason;
            table.AddRow(
                new Markup("[white]Abort Reason[/]"),
                new Markup($"[red]{Markup.Escape(reason)}[/]"));
        }

        AnsiConsole.Write(table);
        Console.Out.WriteLine("\u200B");
        Console.Out.Flush();
    }
}

/// <summary>
/// Collection definition for download validation tests.
/// This collection runs separately from the main Integration collection to avoid
/// Steam session conflicts.
/// </summary>
[CollectionDefinition("DownloadValidation")]
public class DownloadValidationTestCollection : ICollectionFixture<DownloadValidationFixture>
{
}
