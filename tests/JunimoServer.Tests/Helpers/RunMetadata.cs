using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using JunimoServer.Tests.Infrastructure;
using JunimoServer.Tests.Schema.Json;

namespace JunimoServer.Tests.Helpers;

/// <summary>
/// Manages run-isolated output directories and provides a monotonic run clock
/// for cross-log correlation. Initialized once per test run from
/// <see cref="TestResourceBroker.StartPrestart"/>.
/// </summary>
public static class RunMetadata
{
    private static readonly Stopwatch _clock = new();
    private static long _offsetMs;

    /// <summary>
    /// Monotonic stopwatch started at run initialization. Event emitters use
    /// this for the <c>runMs</c> field, enabling cross-log correlation.
    /// </summary>
    public static Stopwatch RunClock => _clock;

    /// <summary>
    /// Returns the run-time milliseconds offset by <c>SDVD_RUN_START_MS</c> when
    /// running as a distributed worker. Used by emit-path code (per-event runMs)
    /// so cross-worker timeline ordering aligns with the coordinator's epoch.
    /// On the coordinator and in local mode, the offset is zero and this is
    /// equivalent to <c>RunClock.ElapsedMilliseconds</c>.
    ///
    /// <para>
    /// Duration-only paths (subtracting two reads to measure a span) must keep
    /// using <see cref="RunClock"/> directly; the offset cancels out, and using
    /// <see cref="GetRunMs"/> there would be no-op churn (<c>simplest-solution</c>).
    /// </para>
    /// </summary>
    public static long GetRunMs() => _clock.ElapsedMilliseconds + _offsetMs;

    /// <summary>
    /// The unique run ID (directory name), e.g. "2026-03-14T12-00-00Z_abc123".
    /// </summary>
    public static string? RunId { get; private set; }

    private static DateTime _timestamp;

    /// <summary>
    /// Whether <see cref="BeginRun"/> has been called.
    /// </summary>
    public static bool IsInitialized => RunId != null;

    /// <summary>
    /// First stage of run initialization. Computes the run ID, creates the run
    /// directory, updates <see cref="TestArtifacts.RunDir"/>, and starts the
    /// monotonic <see cref="RunClock"/>.
    ///
    /// Called exactly once per run from <see cref="Fixtures.TestSummaryFixture.InitializeAsync"/>,
    /// which xUnit guarantees runs before any test's InitializeAsync — so every
    /// artifact writer (InfrastructureEventLog, container log streamers)
    /// observes a set RunDir instead of the default TestResults root.
    /// </summary>
    public static void BeginRun()
    {
        // Idempotent: a parent process may have already called BeginRun, set
        // SDVD_RUN_DIR, and spawned a child that calls BeginRun again on the
        // same in-process state (test assemblies hosted in-process). Honor
        // the existing run rather than racing a fresh one.
        if (IsInitialized) return;

        _clock.Start();

        // Distributed worker mode: align runMs to the coordinator's epoch by adding
        // its dispatch-time RunClock value as a constant offset. The local Stopwatch
        // still starts at zero (its TestRunner process start) so duration math works
        // unchanged; only emit-path code that compares timestamps across workers
        // needs the offset, and it consumes GetRunMs() for that.
        var startMsEnv = Environment.GetEnvironmentVariable(RunArtifactNames.RunStartMsEnv);
        if (!string.IsNullOrEmpty(startMsEnv) && long.TryParse(startMsEnv, out var parsed))
        {
            _offsetMs = parsed;
        }

        _timestamp = DateTime.UtcNow;

        // Externally-supplied run directory (parent process passes its runDir down
        // to the test-child via SDVD_RUN_DIR so they share the same artifact root).
        // The child treats it as authoritative — directory name is the runId.
        var externalRunDir = Environment.GetEnvironmentVariable(RunArtifactNames.RunDirEnv);
        string runId;
        string runDir;
        if (!string.IsNullOrEmpty(externalRunDir))
        {
            runDir = externalRunDir;
            runId = Path.GetFileName(externalRunDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        }
        else
        {
            var shortSha = GetGitShortSha() ?? "unknown";
            runId = $"{_timestamp:yyyy-MM-ddTHH-mm-ssZ}_{shortSha}";
            var runsRoot = Path.Combine(TestArtifacts.OutputDir, "runs");
            runDir = Path.Combine(runsRoot, runId);
        }

        RunId = runId;
        Directory.CreateDirectory(runDir);
        TestArtifacts.RunDir = runDir;
    }

    /// <summary>
    /// Second stage of run initialization: writes <c>run-metadata.json</c> from
    /// discovered server demands and the computed instance allocation, and
    /// announces the run directory on the setup event bus. Requires
    /// <see cref="BeginRun"/> to have been called.
    ///
    /// <paramref name="instancePlan"/> is the output of the broker's Hamilton
    /// allocation. Demands not in the plan are deferred and report
    /// <c>prestartedInstanceCount = 0</c> — they're started on-demand later.
    /// </summary>
    internal static void WriteRunMetadata(List<ServerDemand> demands,
        List<(ServerDemand Demand, int Count)> instancePlan)
    {
        if (!IsInitialized)
            throw new InvalidOperationException(
                $"{nameof(WriteRunMetadata)} called before {nameof(BeginRun)}.");

        var runDir = TestArtifacts.RunDir;

        SetupEventBus.EmitStep("Setup", "Run directory", SetupStepStatus.Completed, runDir);

        WriteMetadataJson(runDir, RunId!, _timestamp, demands, instancePlan);
    }

    private static void WriteMetadataJson(string runDir, string runId, DateTime timestamp,
        List<ServerDemand> demands, List<(ServerDemand Demand, int Count)> instancePlan)
    {
        var instanceCounts = instancePlan.ToDictionary(p => p.Demand.Key, p => p.Count);

        var metadata = new RunMetadataJson
        {
            SchemaVersion = 1,
            RunId = runId,
            Timestamp = timestamp.ToString("o"),
            RunDir = runDir,
            Git = GetGitInfo(),
            Env = GetEnvInfo(),
            Runtime = GetRuntimeInfo(),
            TestCount = demands.Sum(d => d.TestCount),
            ServerConfigs = demands.Select(d => new ServerConfigInfo
            {
                Key = d.Key,
                Label = d.Requirements.GetDisplayLabel(),
                TestCount = d.TestCount,
                PrestartedInstanceCount = instanceCounts.TryGetValue(d.Key, out var c) ? c : 0
            }).ToList()
        };

        try
        {
            ArtifactPrettyJson.Write(Path.Combine(runDir, RunArtifactNames.RunMetadataJson), metadata);
        }
        catch (Exception ex)
        {
            TestLog.Test($"[RunMetadata] Failed to write run-metadata.json: {ex.Message}");
        }

        // Announce on the setup event bus so the runner-side TestRunState gets
        // run identity (and the runDir, used by the artifact writer).
        try { SetupEventBus.EmitRunMetadata(metadata); }
        catch (Exception ex) { TestLog.Test($"[RunMetadata] Failed to emit run_metadata event: {ex.Message}"); }
    }

    private static GitInfo GetGitInfo()
    {
        return new GitInfo
        {
            Sha = RunCommand("git", "rev-parse HEAD"),
            Branch = RunCommand("git", "rev-parse --abbrev-ref HEAD"),
            Dirty = RunCommand("git", "status --porcelain") is { Length: > 0 }
        };
    }

    private static Dictionary<string, string?> GetEnvInfo()
    {
        var keys = new[]
        {
            "SDVD_IMAGE_TAG", "SDVD_STOP_ON_FAIL",
            "SDVD_MAX_CONCURRENT_STARTS", "SDVD_MAX_CONCURRENT_EXTRACTIONS",
        };
        var result = new Dictionary<string, string?>();
        foreach (var key in keys)
        {
            var val = Environment.GetEnvironmentVariable(key);
            if (val != null)
                result[key] = val;
        }
        return result;
    }

    private static RuntimeInfo GetRuntimeInfo()
    {
        return new RuntimeInfo
        {
            Os = RuntimeInformation.OSDescription,
            Dotnet = RuntimeInformation.FrameworkDescription,
            Docker = TryGetLocalDockerVersion()
        };
    }

    private static string? TryGetLocalDockerVersion()
    {
        try
        {
            using var c = DockerEndpointConfig.Instance.CreateDockerClient();
            return DockerOps.TryGetServerVersionAsync(c).GetAwaiter().GetResult();
        }
        catch
        {
            return null;
        }
    }

    internal static string? GetGitShortSha()
    {
        var sha = RunCommand("git", "rev-parse --short HEAD");
        return string.IsNullOrWhiteSpace(sha) ? null : sha;
    }

    private static string? RunCommand(string command, string args)
    {
        try
        {
            var psi = new ProcessStartInfo(command, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = Process.Start(psi);
            if (process == null) return null;
            var output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(5000);
            return process.ExitCode == 0 ? output : null;
        }
        catch
        {
            return null;
        }
    }

    // ── JSON DTOs ──

    public class RunMetadataJson
    {
        public int SchemaVersion { get; init; }
        public string RunId { get; init; } = "";
        public string Timestamp { get; init; } = "";
        /// <summary>
        /// Absolute path to the run directory. Carried in the run_metadata IPC event
        /// so the runner-side artifact writer knows where to write summary.json.
        /// Excluded from the on-disk run-metadata.json (path is the run dir itself).
        /// </summary>
        [JsonIgnore] public string RunDir { get; init; } = "";
        public GitInfo? Git { get; init; }
        public Dictionary<string, string?>? Env { get; init; }
        public RuntimeInfo? Runtime { get; init; }
        public int TestCount { get; init; }
        public List<ServerConfigInfo>? ServerConfigs { get; init; }
    }

    public class GitInfo
    {
        public string? Sha { get; init; }
        public string? Branch { get; init; }
        public bool Dirty { get; init; }
    }

    public class RuntimeInfo
    {
        public string? Os { get; init; }
        public string? Dotnet { get; init; }
        public string? Docker { get; init; }
    }

    public class ServerConfigInfo
    {
        public string Key { get; init; } = "";
        public string Label { get; init; } = "";
        public int TestCount { get; init; }
        /// <summary>
        /// Number of server instances pre-started for this config under the broker's
        /// Hamilton allocation. May be 0 for configs deferred past the slot cap —
        /// those are started on-demand later, so this is not "instances that ever ran".
        /// </summary>
        public int PrestartedInstanceCount { get; init; }
    }
}
