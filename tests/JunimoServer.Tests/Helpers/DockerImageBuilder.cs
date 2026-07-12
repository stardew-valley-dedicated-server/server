using System.Diagnostics;
using System.Text.RegularExpressions;
using JunimoServer.Tests.Schema.Json;

namespace JunimoServer.Tests.Helpers;

/// <summary>
/// Static helper that builds Docker images at most once per test run.
/// Uses an in-process semaphore + cross-process file lock to deduplicate builds
/// across concurrent fixtures and standalone tests.
///
/// Progress is reported through the caller-supplied
/// <see cref="IBuildProgressSink"/>. The parent runner constructs a
/// renderer-direct sink (no IPC); the test child constructs a sink that
/// forwards through <see cref="SetupEventBus"/> over the named pipe.
/// </summary>
public static class DockerImageBuilder
{
    // If you add a new sdvd/* image to the build (Dockerfile/Makefile), add it
    // here so the parent-side distributor copies it to remote hosts.
    public static readonly string[] DistributableImageNames = new[]
    {
        "sdvd/server",
        "sdvd/test-client",
        "sdvd/steam-service",
    };

    // In-process semaphore for concurrent fixture synchronization
    private static readonly SemaphoreSlim BuildSemaphore = new(1, 1);
    private const int FileLockMaxRetries = 5;
    private static readonly TimeSpan FileLockRetryDelay = TimeSpan.FromSeconds(2);

    // Recent-output tail attached to build-failure messages. The tail is only the
    // console/annotation excerpt (the full log is on disk); 50 ≈ one terminal
    // screen, and `buildx --progress=plain` emits the failing step's output and
    // its ERROR: block in the final lines.
    private const int FailureTailLines = 50;

    // make's "*** [target] Error N" recipe trailer — restates the exit code
    // without adding signal; filtered out of the failure tail.
    private static readonly Regex MakeErrorTrailer = new(
        @"^\s*make(\[\d+\])?: \*\*\*",
        RegexOptions.Compiled
    );

    private static readonly string ImageTag =
        Environment.GetEnvironmentVariable("SDVD_IMAGE_TAG") ?? "local";
    private static readonly bool UseLocalImages = ImageTag == "local";
    private static readonly bool SkipBuild = string.Equals(
        Environment.GetEnvironmentVariable("SDVD_SKIP_BUILD"),
        "true",
        StringComparison.OrdinalIgnoreCase
    );
    private static readonly string BuildLockFile = Path.Combine(
        Path.GetTempPath(),
        "sdvd-test-build.lock"
    );
    private static readonly string ServerRepoDir = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..")
    );

    /// <summary>
    /// Resolves Steam build credentials from <c>STEAM_ACCOUNTS[0]</c> in the test environment.
    /// Returns the build-time secret env vars the Makefile passes to <c>docker buildx build
    /// --secret id=steam_username,env=STEAM_USERNAME</c>. Returns empty when STEAM_ACCOUNTS is
    /// missing or malformed; the Docker build will then fail with a clear Steam-login error.
    /// </summary>
    private static Dictionary<string, string> GetSteamCredentials(IBuildProgressSink progress)
    {
        try
        {
            var accounts = SteamAccount.ParseList(
                Environment.GetEnvironmentVariable("STEAM_ACCOUNTS")
            );
            if (accounts.Count == 0)
            {
                return new Dictionary<string, string>();
            }

            return new Dictionary<string, string>
            {
                ["STEAM_USERNAME"] = accounts[0].User,
                ["STEAM_PASSWORD"] = accounts[0].Pass,
                ["STEAM_REFRESH_TOKEN"] = accounts[0].Token,
            };
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            progress.Step($"STEAM_ACCOUNTS JSON malformed: {ex.Message}", SetupStepStatus.Warning);
            return new Dictionary<string, string>();
        }
    }

    /// <summary>
    /// Ensures all required Docker images exist, building them if necessary.
    /// Returns immediately if images are not local or build is skipped.
    /// </summary>
    public static async Task EnsureImagesExistAsync(
        bool includeTestClient,
        IBuildProgressSink progress
    )
    {
        if (!UseLocalImages)
        {
            return;
        }

        if (SkipBuild)
        {
            progress.Step("Skipping image build (SDVD_SKIP_BUILD=true)", SetupStepStatus.Warning);
            return;
        }

        await BuildLocalImagesOnce(includeTestClient, progress);
    }

    /// <summary>
    /// Build images at most once across concurrent fixtures.
    /// Uses an in-process <see cref="SemaphoreSlim"/> for the common case of concurrent
    /// fixtures within one xUnit process, plus a file lock with retry for cross-process
    /// safety (e.g. concurrent <c>make test</c> invocations).
    /// </summary>
    private static async Task BuildLocalImagesOnce(
        bool includeTestClient,
        IBuildProgressSink progress
    )
    {
        // Acquire in-process semaphore to serialize concurrent fixture builds
        progress.Step("Waiting for build lock", SetupStepStatus.Started);
        await BuildSemaphore.WaitAsync();
        try
        {
            progress.Step("Waiting for build lock", SetupStepStatus.Completed);

            // Check if another fixture already built while we waited for the semaphore.
            // This is only checked after acquiring the lock (not as a fast path) because
            // for local images we always want to run `docker buildx build` at least once
            // per test run. Docker's layer cache makes this fast when nothing changed,
            // and it ensures source changes are always picked up.
            if (AlreadyBuiltThisRun)
            {
                EmitBuildSkipped(progress);
                return;
            }

            await AcquireFileLockAndBuild(includeTestClient, progress);
            AlreadyBuiltThisRun = true;
        }
        finally
        {
            BuildSemaphore.Release();
        }
    }

    /// <summary>
    /// Tracks whether we've already run the build commands in this process.
    /// Only skips for concurrent fixtures within the same test run. The first fixture
    /// always rebuilds to pick up source changes.
    /// </summary>
    private static bool AlreadyBuiltThisRun;

    /// <summary>
    /// Acquires a cross-process file lock with retry and builds images if still needed.
    /// The retry loop handles <see cref="IOException"/> when another process holds the lock.
    /// </summary>
    private static async Task AcquireFileLockAndBuild(
        bool includeTestClient,
        IBuildProgressSink progress
    )
    {
        for (int attempt = 1; attempt <= FileLockMaxRetries; attempt++)
        {
            try
            {
                using var lockStream = new FileStream(
                    BuildLockFile,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None
                );

                try
                {
                    await BuildLocalImages(includeTestClient, progress);
                }
                finally
                {
                    // Release the FileStream before deleting so the file isn't locked
                    lockStream.Close();
                    try
                    {
                        File.Delete(BuildLockFile);
                    }
                    catch
                    { /* another process may hold it */
                    }
                }
                return;
            }
            catch (IOException) when (attempt < FileLockMaxRetries)
            {
                progress.Step(
                    "Waiting for build lock",
                    SetupStepStatus.InProgress,
                    $"File lock contended, retry {attempt}/{FileLockMaxRetries}"
                );
                await Task.Delay(FileLockRetryDelay);
            }
        }
    }

    private static void EmitBuildSkipped(IBuildProgressSink progress)
    {
        progress.PhaseStarted("Docker Images");
        progress.Step("Images already built", SetupStepStatus.Completed);
        progress.PhaseCompleted("Docker Images", true);
    }

    /// <summary>
    /// Build local Docker images, equivalent to running <c>make build</c> + building the steam-service image.
    /// This ensures the test always runs against the latest code, just like <c>make up</c> does.
    /// </summary>
    private static async Task BuildLocalImages(bool includeTestClient, IBuildProgressSink progress)
    {
        progress.PhaseStarted("Docker Images");

        // Resolve Steam credentials from test environment (STEAM_ACCOUNTS JSON → account 0).
        // Passed as process environment variables so they don't appear in process listings.
        // The Makefile `export`s these vars, and Docker reads them via --secret id=x,env=STEAM_X.
        var steamCredentials = GetSteamCredentials(progress);

        // Build steam-service and server in parallel.
        // Test-client MUST wait for server to finish. Both Dockerfiles share identical
        // steam-service-builder and game-downloader stages (same Steam account). Building
        // them concurrently causes two Steam logins with the same account, and Steam's
        // single-session enforcement kicks the first session, causing TaskCanceledException.
        // Building server first populates the BuildKit layer cache so the test-client
        // build hits warm cache for all shared stages (~5s instead of minutes).
        progress.Step("Building steam-service image", SetupStepStatus.Started);
        progress.Step("Building server image", SetupStepStatus.Started);

        var steamAuthTask = BuildAndEmitStatus(
            "docker",
            "compose build steam-auth",
            "steam-service image",
            TestTimings.DockerBuildSteamAuthTimeout,
            "Building steam-service image",
            progress,
            steamCredentials
        );
        var serverTask = BuildAndEmitStatus(
            "make",
            "build",
            "server image",
            TestTimings.DockerBuildServerTimeout,
            "Building server image",
            progress,
            steamCredentials
        );

        await Task.WhenAll(steamAuthTask, serverTask);

        if (includeTestClient)
        {
            progress.Step("Building test-client image", SetupStepStatus.Started);
            await BuildAndEmitStatus(
                "make",
                "build-test-client",
                "test-client image",
                TestTimings.DockerBuildServerTimeout,
                "Building test-client image",
                progress,
                steamCredentials
            );
        }

        progress.PhaseCompleted("Docker Images", true);
    }

    /// <summary>
    /// Runs a build command and emits the appropriate status event on completion or failure.
    /// Exceptions from the build are re-thrown so Task.WhenAll propagates them.
    /// </summary>
    private static async Task BuildAndEmitStatus(
        string command,
        string arguments,
        string description,
        TimeSpan timeout,
        string stepName,
        IBuildProgressSink progress,
        Dictionary<string, string>? environmentVars = null
    )
    {
        var sw = Stopwatch.StartNew();
        InfrastructureEventLog.Emit("image_build_started", new { image = description });
        try
        {
            await RunBuildCommand(
                command,
                arguments,
                ServerRepoDir,
                description,
                timeout,
                stepName,
                progress,
                environmentVars
            );
            progress.Step(stepName, SetupStepStatus.Completed);
            sw.Stop();
            InfrastructureEventLog.Emit(
                "image_build_completed",
                new { image = description, durationMs = sw.ElapsedMilliseconds }
            );
        }
        catch (Exception ex)
        {
            progress.Step(stepName, SetupStepStatus.Failed);
            sw.Stop();
            InfrastructureEventLog.Emit(
                "image_build_failed",
                new
                {
                    image = description,
                    durationMs = sw.ElapsedMilliseconds,
                    reason = "exception",
                    exceptionType = ex.GetType().Name,
                    message = ex.Message,
                }
            );
            throw;
        }
    }

    private static async Task RunBuildCommand(
        string command,
        string arguments,
        string workingDirectory,
        string description,
        TimeSpan timeout,
        string stepName,
        IBuildProgressSink progress,
        Dictionary<string, string>? environmentVars = null
    )
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = command,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        // Use plain progress output for Docker builds so ReadLineAsync gets clean lines
        // (auto/tty modes use ANSI cursor repositioning that garbles piped output)
        startInfo.Environment["DOCKER_PROGRESS"] = "plain";

        if (environmentVars != null)
        {
            foreach (var (key, value) in environmentVars)
            {
                startInfo.Environment[key] = value;
            }
        }

        Process StartBuildProcess()
        {
            try
            {
                // Process.Start's own failure (Win32Exception when the executable is
                // missing) carries no command context — rethrow with it attached.
                return Process.Start(startInfo)
                    ?? throw new InvalidOperationException(
                        $"Failed to start '{command} {arguments}'"
                    );
            }
            catch (Exception ex) when (ex is not InvalidOperationException)
            {
                throw new InvalidOperationException(
                    $"Failed to start '{command} {arguments}': {ex.Message}"
                );
            }
        }

        using var process = StartBuildProcess();

        // Injected credential values (Steam user / pass / refresh token) are masked
        // out of every line before it is streamed or buffered — the raw form never
        // exists outside the process pipe, so the log file, infrastructure*.jsonl,
        // consoles, and both UIs all inherit masked lines. The ≥3-length guard
        // mirrors the report redactor's (and string.Replace throws on an empty
        // needle).
        var secretValues =
            environmentVars?.Values.Where(v => !string.IsNullOrEmpty(v) && v.Length >= 3).ToArray()
            ?? Array.Empty<string>();

        string MaskSecrets(string line)
        {
            foreach (var secret in secretValues)
            {
                line = line.Replace(secret, "***");
            }

            return line;
        }

        // Full masked output, buffered for the diagnostics log file; the failure
        // tail is sliced from the same buffer. The two reader loops run concurrently.
        var outputLines = new List<string>();
        var outputLock = new object();
        var logFileName = $"image-build-{description.Replace(' ', '-')}.log";

        // Diagnostics must never fail a build.
        void WriteBuildLog()
        {
            try
            {
                string[] lines;
                lock (outputLock)
                {
                    lines = outputLines.ToArray();
                }

                File.WriteAllLines(
                    Path.Combine(TestArtifacts.GetDiagnosticsDir(), logFileName),
                    lines
                );
            }
            catch
            { /* never fail the build over a log write */
            }
        }

        // Bounded failure excerpt for exception messages: the CI renderer shows
        // InProgress lines only as a transient spinner label, so without this the
        // operator sees the exit code but never the underlying error.
        string BuildFailureTail()
        {
            string[] tailLines;
            lock (outputLock)
            {
                tailLines = outputLines
                    .Where(l => !string.IsNullOrWhiteSpace(l) && !MakeErrorTrailer.IsMatch(l))
                    .TakeLast(FailureTailLines)
                    .ToArray();
            }

            var tailBlock =
                tailLines.Length > 0
                    ? $" Last output:{Environment.NewLine}  "
                        + string.Join($"{Environment.NewLine}  ", tailLines)
                    : " (no output captured)";
            return $"{tailBlock}{Environment.NewLine}Full log: {RunArtifactNames.DiagnosticsDir}/{logFileName}";
        }

        // Read stdout / stderr in parallel via local async functions. ReadLineAsync
        // is fully async on .NET 6+; wrapping in Task.Run added thread-pool overhead
        // without any concurrency benefit. Each masked line streams through the
        // progress sink; the renderer surfaces them in real time.
        async Task ReadLinesAsync(StreamReader reader)
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                var masked = MaskSecrets(line);
                progress.Step(stepName, SetupStepStatus.InProgress, masked);
                lock (outputLock)
                {
                    outputLines.Add(masked);
                }
            }
        }

        var stdoutTask = ReadLinesAsync(process.StandardOutput);
        var stderrTask = ReadLinesAsync(process.StandardError);

        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // The process (or a descendant) exited in the window between the
                // timeout firing and the kill — the TimeoutException below is the
                // real signal and must not be masked.
            }

            // The kill closes the pipes so reader EOF is imminent; bounded drain
            // per drain-before-consume-disposal. Swallow drain failures for the
            // same reason as the kill above.
            try
            {
                await Task.WhenAll(stdoutTask, stderrTask).WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch
            { /* drain best-effort */
            }

            WriteBuildLog();
            throw new TimeoutException(
                $"Building {description} timed out after {timeout.TotalMinutes:0} minutes.{BuildFailureTail()}"
            );
        }

        await Task.WhenAll(stdoutTask, stderrTask);

        // Success logs are kept too — a good/bad build diff needs both sides.
        WriteBuildLog();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Building {description} failed with exit code {process.ExitCode}.{BuildFailureTail()}"
            );
        }
    }
}
