namespace JunimoServer.Tests.Infrastructure;

/// <summary>
/// Thrown from test setup (server acquisition) when the failure is an infrastructure
/// transport fault — a poisoned host or a Socket/Http/stream fault from the ssh-forwarded
/// daemon path — rather than a product bug. Uses xUnit's dynamic-skip convention
/// (<c>$XunitDynamicSkip$</c>) so xUnit reports the test as <b>skipped</b>, not failed:
/// a skip does not trigger StopOnFail and is not a red failure, so one transport blip on a
/// shared host no longer cascades an entire class (the 2026-06-25 13-test SaveImport cascade
/// from a single ConnectionRefused). A real assertion/crash still fails and trips StopOnFail,
/// so a genuine bug still aborts the run early. Distinct from <see cref="TestRunAbortedException"/>
/// (run-already-aborted) so the skip reason in reports names the actual cause.
/// </summary>
public sealed class InfrastructureSkipException : Exception
{
    private const string XunitSkipPrefix = "$XunitDynamicSkip$";

    public InfrastructureSkipException(string message)
        : base($"{XunitSkipPrefix}{message}") { }
}
