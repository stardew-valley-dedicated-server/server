namespace JunimoServer.Tests.Infrastructure;

/// <summary>
/// Thrown to tests waiting on a server queue when the broker cannot produce a
/// server for them: every creation attempt failed, or the target host is
/// poisoned. <see cref="Fixtures.TestSummaryFixture.ClassifyFailureCategory"/>
/// maps this type to <c>"infrastructure"</c> so queue faults are classified as
/// infrastructure failures rather than test crashes.
/// </summary>
public sealed class ServerUnavailableException : Exception
{
    public ServerUnavailableException(string message)
        : base(message) { }
}
