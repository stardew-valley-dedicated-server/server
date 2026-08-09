namespace JunimoServer.Tests.Helpers;

/// <summary>
/// Phase budgets for the abort path: every phase between "abort decided" and
/// "process gone" is bounded by one of these constants, and the FailFast
/// backstop (<see cref="ComputeAbortBackstop"/>) is composed from the same
/// constants — so a changed phase budget moves the backstop automatically
/// instead of silently invalidating a magic number.
/// </summary>
public static class ShutdownBudgets
{
    /// <summary>
    /// Allowance for <c>BeginAbort</c>'s synchronous prelude: pipe drain,
    /// summary write, and report-bundle generation — the one phase that scales
    /// with artifact volume, hence an explicit allowance rather than a sum of
    /// sub-bounds.
    /// </summary>
    public static readonly TimeSpan AbortPreludeAllowance = TimeSpan.FromSeconds(30);

    /// <summary>First-Ctrl+C graceful-disposal window before force-kill.</summary>
    public static readonly TimeSpan GracefulDrainWindow = TimeSpan.FromSeconds(15);

    /// <summary>Allowance for killing the xUnit child process tree.</summary>
    public static readonly TimeSpan ChildKillAllowance = TimeSpan.FromSeconds(5);

    /// <summary>Per-sink bound on registered emergency drainables (event log flush).</summary>
    public static readonly TimeSpan DrainablePerSink = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Per-call bound for the emergency bulk sweep against a remote
    /// (<c>ssh://</c>) host — a hung SSH master must not block process exit.
    /// Applied to the list calls as well as the per-resource removes; the
    /// Docker client's own timeout is deliberately infinite.
    /// </summary>
    public static readonly TimeSpan RemoteSweepPerCall = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Per-call bound for the emergency bulk sweep against a local host. A hung
    /// Docker Desktop named pipe plus the client's infinite timeout reproduces
    /// the remote-wedge hang locally (daemon OOM / WSL crash are observed
    /// modes), so local calls are bounded too — just more generously.
    /// </summary>
    public static readonly TimeSpan LocalSweepPerCall = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Whole-sweep deadline per host in the emergency bulk sweep (3 bounded
    /// list calls plus removals on a healthy daemon; ~3 list-call timeouts on a
    /// dead tunnel).
    /// </summary>
    public static readonly TimeSpan BulkSweepPerHost = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Worst-case terminal teardown of one ControlMaster: bounded
    /// <c>ssh -O exit</c> + confirm wait + pid-kill wait + re-confirm wait.
    /// </summary>
    public static readonly TimeSpan MasterTeardownPerMaster = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Absolute deadline for the abort path, composed from the phase budgets
    /// above (× 2 margin). If the process is still alive when it elapses, the
    /// backstop calls <c>Environment.FailFast</c> — every cleanup layer has had
    /// its chance by then, and a leaked master becomes the next run's preflight
    /// reaper's job (which only works once this coordinator is actually dead).
    /// </summary>
    public static TimeSpan ComputeAbortBackstop(int hostCount, int drainableCount, int masterCount)
    {
        var sum =
            AbortPreludeAllowance
            + GracefulDrainWindow
            + ChildKillAllowance
            + drainableCount * DrainablePerSink
            + hostCount * BulkSweepPerHost
            + masterCount * MasterTeardownPerMaster;
        return sum * 2;
    }
}
