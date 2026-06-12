namespace JunimoServer.Tests.Helpers;

/// <summary>
/// Filenames and directory names that appear in the run-artifact tree. Centralized so a layout
/// change touches one place. Used by both the test assembly and the test runner.
///
/// Layout under <see cref="TestArtifacts.OutputDir"/> / runs / {id}:
///   summary.json, ctrf-report.json, run-metadata.json, run-output.json
///   diagnostics/infrastructure.jsonl
///   flakiness.jsonl  (also exists at OutputDir for cross-run aggregation)
///   tests/{Class}.{Method}/
///   _workers/{worker_id}/   (distributed mode only)
///   _recovered/{worker_id}/ (distributed mode only, rsync-on-Lost)
/// </summary>
public static class RunArtifactNames
{
    public const string SummaryJson = "summary.json";
    public const string CtrfReport = "ctrf-report.json";
    public const string RunMetadataJson = "run-metadata.json";
    public const string RunOutputJson = "run-output.json";
    public const string LatestPointer = "latest.txt";

    public const string InfrastructureJsonl = "infrastructure.jsonl";
    public const string FlakinessJsonl = "flakiness.jsonl";

    /// <summary>
    /// Cross-run sidecar (<c>{hostId → bytes}</c>) at <see cref="TestArtifacts.OutputDir"/>
    /// estimating the next image transfer's size for progress display.
    /// </summary>
    public const string ImageTransferBaseline = "image-transfer-baseline.json";

    /// <summary>
    /// Filename used by parent processes (TestRunner / DistributedRunner) for their
    /// own structured-event log. Distinct from <see cref="InfrastructureJsonl"/>
    /// because the test-child opens the canonical log with <c>append: false</c>
    /// (truncates) — a parent writing concurrently would race the child's truncate.
    /// The merger concatenates this into the canonical log at end of run.
    /// </summary>
    public const string ParentInfrastructureJsonl = "infrastructure.parent.jsonl";

    public const string DiagnosticsDir = "diagnostics";
    public const string TestsDir = "tests";
    public const string ContainersDir = "containers";

    /// <summary>
    /// Env var that, when set, makes <see cref="RunMetadata.BeginRun"/> honor an
    /// externally-supplied run directory instead of generating a fresh one.
    /// Used so a runner-parent and its test-child agree on the same runDir —
    /// the parent calls <c>BeginRun</c> once, sets this env var on the child's
    /// process, and the child's <c>BeginRun</c> reuses the same directory.
    /// (The env var name is also exported as <c>DistributedEnv.RunDir</c> for
    /// runner-side consumers; same string, two namespace anchors.)
    /// </summary>
    public const string RunDirEnv = "SDVD_RUN_DIR";

    /// <summary>
    /// Env var that aligns the worker's <see cref="RunMetadata.RunClock"/> to
    /// the coordinator's epoch in distributed mode (constant offset added to
    /// all <c>GetRunMs()</c> reads).
    /// </summary>
    public const string RunStartMsEnv = "SDVD_RUN_START_MS";

    /// <summary>
    /// Env var carrying a JSON map of <c>{hostId → coordinatorPort}</c> for
    /// every remote host whose <c>ssh -N -L</c> daemon-socket forward is open.
    /// Written by <c>HostPool.PreflightAsync</c> in the parent process and
    /// inherited by xUnit's child test process so its lazy <c>DockerHost</c>
    /// getters can dial the parent's loopback listener directly. Set to
    /// <c>"{}"</c> at preflight entry so a partial preflight can't leak stale
    /// ports from a prior run in the same shell session.
    /// </summary>
    public const string HostTunnelsEnv = "SDVD_HOST_TUNNELS";

    /// <summary>
    /// Env var carrying the absolute path to the SSH binary the parent
    /// resolved at preflight (banner-checked Cygwin/Git-for-Windows on
    /// Windows, system OpenSSH on POSIX). Inherited by xUnit's child test
    /// process so its <c>TunnelManager</c> reuses the same binary for
    /// <c>ssh -O forward</c>/<c>cancel</c>/<c>exit</c> calls against the
    /// parent's ControlMaster sockets.
    /// </summary>
    public const string SshPathEnv = "SDVD_SSH_BINARY";

    /// <summary>
    /// Env var carrying a JSON map of <c>{hostId → {sshDestination,
    /// sshKeyPath, controlPath}}</c> for every remote host whose
    /// <c>ssh -M</c> ControlMaster is open. Written by
    /// <c>HostPool.PreflightAsync</c> in the parent process and inherited
    /// by xUnit's child test process so its <c>TunnelManager</c> can run
    /// <c>ssh -O forward</c> against the parent's existing master without
    /// spawning its own.
    /// </summary>
    public const string SshHostMastersEnv = "SDVD_SSH_HOST_MASTERS";

    /// <summary>{runDir}/diagnostics/infrastructure.jsonl</summary>
    public static string InfrastructureLog(string runDir) =>
        Path.Combine(runDir, DiagnosticsDir, InfrastructureJsonl);
}
