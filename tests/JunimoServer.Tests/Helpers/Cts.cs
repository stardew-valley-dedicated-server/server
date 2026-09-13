namespace JunimoServer.Tests.Helpers;

/// <summary>
/// Cancellation-token-source factories for the harness.
/// </summary>
public static class Cts
{
    /// <summary>
    /// A source that cancels when <paramref name="ct"/> does or after
    /// <paramref name="timeout"/>, whichever comes first. Callers tell the two
    /// apart by checking <c>ct.IsCancellationRequested</c> in the catch filter.
    /// </summary>
    public static CancellationTokenSource LinkedTimeout(CancellationToken ct, TimeSpan timeout)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        return cts;
    }
}
