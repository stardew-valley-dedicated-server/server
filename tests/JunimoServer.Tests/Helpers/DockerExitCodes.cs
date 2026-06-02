namespace JunimoServer.Tests.Helpers;

/// <summary>
/// Container/exec exit codes the test harness reasons about. Values are the
/// <c>long</c> the Docker daemon reports (via <c>GetExitCodeAsync</c> and
/// <c>ExecResult.ExitCode</c>), so they compare directly against those results.
/// Shared across the Tests assembly and the TestRunner (which references it).
/// </summary>
internal static class DockerExitCodes
{
    /// <summary>
    /// No exit status was reported (e.g. the daemon returned a null
    /// <c>ExecResult.ExitCode</c>). Distinct from any real code; treat as failure.
    /// </summary>
    public const long Unknown = -1;

    /// <summary>Process exited cleanly.</summary>
    public const long Success = 0;

    /// <summary>
    /// Killed by SIGKILL (<c>128 + 9</c>) — the daemon's signal for an
    /// out-of-memory kill or a forced stop.
    /// </summary>
    public const long SigKill = 137;
}
