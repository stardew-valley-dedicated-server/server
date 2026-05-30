using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Images;
using DotNet.Testcontainers.Networks;
using JunimoServer.Tests.Helpers;
using JunimoServer.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace JunimoServer.Tests.Fixtures;

/// <summary>
/// Manages a standalone steam-auth container for download-validation tests.
/// Isolated from the main test fixture: this container's Steam login would
/// otherwise interfere with the Steam lobby used by integration tests.
///
/// Prerequisites: Run 'make setup' first to download the game and save Steam session.
/// </summary>
public class DownloadValidationFixture : IAsyncLifetime
{
    private const string CollectionName = "DownloadValidation";

    private IContainer? _steamAuthContainer;
    private INetwork? _network;
    /// <summary>
    /// Tracks whether we have acquired a server-slot reservation against the
    /// coordinator host (released in <see cref="DisposeAsync"/>). The fixture
    /// pins to the coordinator because the steam-auth container it builds
    /// must live on the same daemon as Steam-using clients.
    /// </summary>
    private bool _slotHeld;
    private DockerHost CoordinatorHost => HostPool.Instance.First;

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
    /// Marks a test as dispatched, grouped by class name. Mirrors the assembly-level
    /// fixture's <see cref="TestSummaryFixture.MarkDispatched"/>.
    /// </summary>
    public void MarkDispatched(string className, string? testName = null)
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
        TestSummaryFixture.Instance?.MarkDispatched(CollectionName, className, testName);
    }

    /// <summary>
    /// Marks a test as completed, recording its duration.
    /// </summary>
    public void MarkCompleted(string className, string? testName, TimeSpan duration)
    {
        TestSummaryFixture.Instance?.MarkCompleted(CollectionName, className, testName, duration);
    }

    /// <summary>
    /// Marks a test as failed with optional details for the failure summary.
    /// </summary>
    public void MarkFailed(string className, string? testName, string error,
        string? phase = null, string? screenshotPath = null)
    {
        TestSummaryFixture.Instance?.MarkFailed(CollectionName, className, testName, error, phase, screenshotPath);
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

    private static void Log(string message, LogLevel level = LogLevel.Info)
    {
        if (string.IsNullOrEmpty(message)) return;
        var status = level switch
        {
            LogLevel.Success => SetupStepStatus.Completed,
            LogLevel.Warn => SetupStepStatus.Warning,
            LogLevel.Error => SetupStepStatus.Failed,
            _ => SetupStepStatus.InProgress,
        };
        SetupEventBus.EmitStep("Setup", message, status, collectionName: CollectionName);
    }

    public async ValueTask InitializeAsync()
    {
        _testRunStartTime = DateTime.UtcNow;

        // Acquire a coordinator-host server slot BEFORE any Docker resource creation
        // so this fixture doesn't outpace the per-host scheduler. Pinned to coordinator
        // because the steam-auth container we build below must live on the same daemon
        // as the Steam-using clients in this fixture.
        await CoordinatorHost.ServerCapacity.AcquireAsync(1, CollectionName, default);
        _slotHeld = true;

        try
        {
            SetupEventBus.EmitPhaseStarted("Setup", "Download Validation", CollectionName);
            var stepStart = DateTime.UtcNow;

            // Build images if needed (reuses shared build-once logic)
            await DockerImageBuilder.EnsureImagesExistAsync(
                includeTestClient: false,
                new SetupEventBusBuildProgressSink("Setup", CollectionName));

            Log("Setting up download validation test environment...");
            Log($"Using shared volumes: {GameDataVolume}, {SteamSessionVolume}", LogLevel.Detail);

            // Create isolated network for this test collection
            var networkName = $"sdvd-dltest-{Guid.NewGuid():N}";
            _network = new NetworkBuilder()
                .WithDockerEndpoint(Infrastructure.HostPool.Instance.First.EndpointConfig)
                .WithName(networkName)
                .Build();

            await _network.CreateAsync();

            // Build steam-auth container using shared volumes
            _steamAuthContainer = new ContainerBuilder()
                .WithDockerEndpoint(Infrastructure.HostPool.Instance.First.EndpointConfig)
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
                .WithCreateParameterModifier(p =>
                {
                    p.HostConfig.CapAdd ??= new List<string>();
                    p.HostConfig.CapAdd.Add("SYS_TIME");
                    p.Labels ??= new Dictionary<string, string>();
                    p.Labels["sdvd.test"] = "true";
                    p.Labels["sdvd.run-id"] = Guid.NewGuid().ToString("N")[..8];
                })
                .WithWaitStrategy(Wait.ForUnixContainer()
                    .UntilHttpRequestIsSucceeded(r => r
                        .ForPort(SteamAuthPort)
                        .ForPath("/health")
                        .ForStatusCode(System.Net.HttpStatusCode.OK)))
                .Build();

            Log("Starting steam-auth container...");
            SetupEventBus.EmitStep("Setup", "Starting steam-auth container", SetupStepStatus.Started, collectionName: CollectionName);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await _steamAuthContainer.StartAsync(cts.Token);
            Log("Steam-auth container started", LogLevel.Success);
            SetupEventBus.EmitStep("Setup", "Starting steam-auth container", SetupStepStatus.Completed, collectionName: CollectionName);

            LogStepDuration(stepStart);
            SetupEventBus.EmitPhaseCompleted("Setup", "Download Validation", true, collectionName: CollectionName);
        }
        catch
        {
            // xUnit does NOT call DisposeAsync when InitializeAsync throws. Release slot here.
            ReleaseSlotIfHeld();
            throw;
        }
    }

    private void ReleaseSlotIfHeld()
    {
        if (_slotHeld)
        {
            CoordinatorHost.ServerCapacity.Release(1);
            _slotHeld = false;
        }
    }

    private static void LogStepDuration(DateTime startTime)
    {
        var duration = DateTime.UtcNow - startTime;
        Log(FormattableString.Invariant($"Step took {duration.TotalSeconds:F1}s"), LogLevel.Detail);
    }

    public async ValueTask DisposeAsync()
    {
        SetupEventBus.EmitPhaseStarted("Setup", "Cleanup", CollectionName);
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
                Log($"Error disposing container: {ex}", LogLevel.Error);
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
                Log($"Error disposing network: {ex}", LogLevel.Error);
            }
        }

        LogStepDuration(stepStart);

        // Release server-slot reservation (idempotent, safe if already released in InitializeAsync catch)
        ReleaseSlotIfHeld();

        // Propagate abort state to assembly fixture (unified summary)
        if (_testRunAborted)
        {
            TestSummaryFixture.Instance?.SetAborted(_abortReason ?? "Unknown abort");
        }
    }

}

/// <summary>
/// Collection definition for download validation tests.
/// This collection runs separately from the main Integration collection to avoid
/// Steam session conflicts.
/// </summary>
[CollectionDefinition("DownloadValidation")]
[CollectionPriority(100)] // Must run last: Steam session invalidation
public class DownloadValidationTestCollection : ICollectionFixture<DownloadValidationFixture>
{
}
