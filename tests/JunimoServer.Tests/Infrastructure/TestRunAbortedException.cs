namespace JunimoServer.Tests.Infrastructure;

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

    public TestRunAbortedException(string message)
        : base($"{XunitSkipPrefix}{message}") { }
}
