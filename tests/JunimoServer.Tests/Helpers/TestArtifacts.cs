namespace JunimoServer.Tests.Helpers;

/// <summary>
/// Manages the test output directory for artifacts like screenshots, logs, and per-test events.
///
/// Output structure:
///   TestResults/
///     latest.txt                          → path to most recent run dir
///     flakiness.jsonl                     → cross-run flakiness data
///     runs/
///       {timestamp}_{sha}/
///         run-metadata.json
///         summary.json
///         ctrf-report.json
///         diagnostics/
///           infrastructure.jsonl
///         tests/
///           {Class}.{Method}/
///             screenshots/
///               failure.png
///               client_failure.png
///             {server|client}_recording.mp4
///         containers/
///           server-{N}/
///             full_recording.mp4
///             container.log               full lifecycle log, always-on
///             failure.json                creation-failure only
///           client-{N}/
///             full_recording.mp4
///             container.log
///           steam-auth-per-{N}/           per-server sidecar
///             container.log
///           steam-auth-shared/            shared sidecar across all servers
///             container.log
/// </summary>
public static class TestArtifacts
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..")
    );

    /// <summary>
    /// Stable root directory for all test results. Does not change between runs.
    /// </summary>
    public static string OutputDir { get; } = Path.Combine(RepoRoot, "TestResults");

    /// <summary>
    /// Run-specific directory. Set exactly once, at process start, by
    /// <see cref="RunMetadata.BeginRun"/>. All per-run artifacts (summary,
    /// infrastructure log, per-test dirs, container logs) write here.
    /// </summary>
    public static string RunDir { get; set; } = OutputDir;

    /// <summary>
    /// Returns (and creates) the directory for a specific test's artifacts.
    /// </summary>
    public static string GetTestDir(string testClass, string testMethod)
    {
        var dir = Path.Combine(RunDir, RunArtifactNames.TestsDir, $"{testClass}.{testMethod}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// Returns (and creates) the screenshots directory for a specific test.
    /// </summary>
    public static string GetTestScreenshotDir(string testClass, string testMethod)
    {
        var dir = Path.Combine(GetTestDir(testClass, testMethod), "screenshots");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// Returns (and creates) the directory for a specific container's artifacts
    /// (recordings, container.log, creation-failure diagnostics). Pass the
    /// container slug — e.g. "server-0", "client-1", "steam-auth-shared".
    /// </summary>
    public static string GetContainerDir(string containerSlug)
    {
        var dir = Path.Combine(RunDir, RunArtifactNames.ContainersDir, containerSlug);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// Returns (and creates) the diagnostics directory for subsystem-level logs
    /// (infrastructure.jsonl).
    /// </summary>
    public static string GetDiagnosticsDir()
    {
        var dir = Path.Combine(RunDir, RunArtifactNames.DiagnosticsDir);
        Directory.CreateDirectory(dir);
        return dir;
    }
}
