namespace JunimoServer.Tests.Helpers;

/// <summary>
/// Process exit codes of the test runner, so shell callers (Makefile, CI)
/// can distinguish outcomes from <c>$?</c>.
/// </summary>
public static class ExitCodes
{
    /// <summary>Every dispatched test passed.</summary>
    public const int Success = 0;

    /// <summary>At least one test failed.</summary>
    public const int TestsFailed = 1;

    /// <summary>The run itself broke: xUnit execution errors or a runner exception.</summary>
    public const int RunnerError = 2;

    /// <summary>
    /// Operator-interrupted (Ctrl+C, UI Stop, SIGHUP): 128 + SIGINT(2), what a
    /// shell reports for a process killed by Ctrl+C — kept machine-readable so
    /// an interrupt is distinguishable from a real failure.
    /// </summary>
    public const int Interrupted = 130;
}
