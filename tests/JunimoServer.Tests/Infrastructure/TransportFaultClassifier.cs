using System.Net.Sockets;

namespace JunimoServer.Tests.Infrastructure;

/// <summary>
/// Decides whether an exception was a host <i>transport</i> fault (SSH forward
/// dropped, daemon socket gone, pipe broken) — which must poison the host (see
/// <see cref="DockerHost.Poison"/>) — or an <i>application</i> fault, which must
/// not (poisoning a healthy host on a slow server would cascade unrelated tests).
/// Both mid-run failure seams consult this one classifier via
/// <see cref="DockerHost.PoisonIfTransportFaultAsync"/> so the decision matches.
///
/// <para>
/// Not the same as <see cref="Fixtures.TestSummaryFixture.ClassifyFailureCategory"/>:
/// that maps an exception type string to a report category for
/// <c>summary.json</c>; this maps a live <see cref="Exception"/> (inner chain +
/// <see cref="SocketError"/> codes) to a poison decision. A transport fault is a
/// subset of that classifier's <c>infrastructure</c> bucket, so the two agree.
/// </para>
/// </summary>
internal static class TransportFaultClassifier
{
    /// <summary>
    /// Returns a non-null reason when <paramref name="ex"/> (or any inner
    /// exception) indicates the Docker host's transport died. Returns null for
    /// application-level faults — including a bare <see cref="TimeoutException"/>,
    /// which is ambiguous (a slow-but-live server times out the same way a dead
    /// forward does). The caller resolves an ambiguous timeout with a second
    /// signal (an <c>ssh -O check</c> against the host's master); see
    /// <c>TestResourceBroker</c> / <c>ClientPool</c>.
    /// </summary>
    public static string? ClassifyHostTransportFault(Exception? ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            var reason = ClassifySingle(current);
            if (reason is not null)
                return reason;
        }
        return null;
    }

    private static string? ClassifySingle(Exception ex) => ex switch
    {
        // Docker.DotNet wraps HttpClient, so a dead forward surfaces as a
        // SocketException — the unambiguous transport signal.
        SocketException se when IsTransportSocketError(se.SocketErrorCode)
            => $"socket transport fault ({se.SocketErrorCode})",

        // A *connection* HttpRequestError means the daemon was unreachable (vs a
        // response-status error, which means it answered). A non-connection
        // HttpRequestException falls through to the inner-chain walk for a wrapped
        // SocketException; DockerApiException is app-level (the daemon responded).
        HttpRequestException hre when IsTransportHttpError(hre.HttpRequestError)
            => $"http transport fault ({hre.HttpRequestError})",

        // Forward closed mid-response. Explicit arm covers a bare EOF (no message);
        // the IOException arm below catches the message-bearing cases.
        EndOfStreamException
            => "daemon stream ended (transport)",

        IOException io when LooksLikeBrokenConnection(io.Message)
            => "io transport fault (broken pipe / connection reset)",

        _ => null,
    };

    /// <summary>
    /// Socket error codes that mean the connection to the daemon was lost or
    /// never established — the forward is gone, not a slow-but-live daemon.
    /// </summary>
    private static bool IsTransportSocketError(SocketError code) => code switch
    {
        SocketError.ConnectionReset => true,
        SocketError.ConnectionAborted => true,
        SocketError.ConnectionRefused => true,
        SocketError.HostUnreachable => true,
        SocketError.NetworkUnreachable => true,
        SocketError.NotConnected => true,
        SocketError.Shutdown => true,
        // TimedOut at the socket layer (not a bare TimeoutException) means the
        // peer stopped answering — a dead forward, distinct from a live daemon
        // taking a long time to produce a response.
        SocketError.TimedOut => true,
        _ => false,
    };

    /// <summary>
    /// <see cref="HttpRequestError"/> values that indicate the transport could
    /// not carry the request, as opposed to the daemon returning an error status.
    /// </summary>
    private static bool IsTransportHttpError(HttpRequestError error) => error switch
    {
        HttpRequestError.ConnectionError => true,
        HttpRequestError.SecureConnectionError => true,
        HttpRequestError.NameResolutionError => true,
        _ => false,
    };

    private static bool LooksLikeBrokenConnection(string? message)
    {
        if (string.IsNullOrEmpty(message)) return false;
        return message.Contains("broken pipe", StringComparison.OrdinalIgnoreCase)
            || message.Contains("connection reset", StringComparison.OrdinalIgnoreCase)
            || message.Contains("connection refused", StringComparison.OrdinalIgnoreCase)
            || message.Contains("connection closed", StringComparison.OrdinalIgnoreCase)
            || message.Contains("transport connection", StringComparison.OrdinalIgnoreCase);
    }
}
