using System.Net.Sockets;
using JunimoServer.Tests.Helpers;
using JunimoServer.Tests.Schema.Events;

namespace JunimoServer.Tests.Infrastructure;

/// <summary>How a fault relates to the SSH transport that carries every harness connection.</summary>
internal enum TransportFaultKind
{
    /// <summary>Not a transport fault: the peer answered, or the exception is application-level.</summary>
    None,

    /// <summary>
    /// A loopback <c>ssh -L</c> forward dropped (refused / reset / aborted connect, or
    /// a stream cut inside an owned transport action). Heal the forward and retry;
    /// poison the host only after <c>ssh -O check</c> says the master is gone.
    /// </summary>
    ForwardScoped,

    /// <summary>The host or network itself is unreachable. Poison directly; no heal can fix it.</summary>
    HostScoped,

    /// <summary>
    /// A transport-layer exception whose typed code is not in the tables. Treated as
    /// application-level by every caller (never poisons, never heals) and reported via
    /// <see cref="TransportEventNames.TransportFaultUnclassified"/> so the table can grow.
    /// </summary>
    Unclassified,
}

/// <summary>
/// <see cref="Reason"/> is a short human label for events and poison records; null only
/// when <see cref="Kind"/> is <see cref="TransportFaultKind.None"/>.
/// </summary>
internal readonly record struct TransportFaultVerdict(TransportFaultKind Kind, string? Reason)
{
    public static readonly TransportFaultVerdict None = new(TransportFaultKind.None, null);

    public bool ForwardScoped => Kind == TransportFaultKind.ForwardScoped;

    /// <summary>Forward- or host-scoped: the transport, not the application, failed.</summary>
    public bool IsTransportFault =>
        Kind is TransportFaultKind.ForwardScoped or TransportFaultKind.HostScoped;
}

/// <summary>
/// Decides whether an exception was a host <i>transport</i> fault (SSH forward
/// dropped, daemon socket gone) — a candidate to poison the host (see
/// <see cref="DockerHost.Poison"/>) — or an <i>application</i> fault, which must
/// not (poisoning a healthy host on a slow server would cascade unrelated tests).
/// Classification uses typed signals only: <see cref="SocketException.SocketErrorCode"/>,
/// <see cref="HttpRequestException.HttpRequestError"/>,
/// <see cref="HttpIOException.HttpRequestError"/> and the exception type. Message text
/// varies by .NET version, OS and culture and is never consulted.
/// Both mid-run failure seams consult this one classifier via
/// <see cref="DockerHost.PoisonIfTransportFaultAsync"/> so the decision matches.
///
/// <para>
/// Not the same as <see cref="Fixtures.TestSummaryFixture.ClassifyFailureCategory"/>:
/// that maps an exception type string to a report category for
/// <c>summary.json</c>; this maps a live <see cref="Exception"/> (inner chain +
/// error codes) to a poison/heal decision. A transport fault is a subset of that
/// classifier's <c>infrastructure</c> bucket, so the two agree.
/// </para>
/// </summary>
internal static class TransportFaultClassifier
{
    /// <summary>
    /// Marker embedded in the message of the daemon-responsiveness `TimeoutException` thrown by
    /// ServerContainer/GameClientContainer.StartAsync when a remote `docker create+start` exceeds
    /// the tight daemon deadline (a wedged daemon-socket forward). Unlike a generic
    /// <see cref="TimeoutException"/> (ambiguous — could be a slow server or a real hang), THIS
    /// timeout is unambiguously infrastructure: we threw it specifically for a wedged forward. The
    /// throw sites embed this marker and <see cref="IsDaemonResponsivenessTimeout"/> recognizes it,
    /// so the acquire-time infrastructure-skip catches it even when the master stayed alive (the
    /// mux-accept-exhaustion case, where the broker doesn't poison the host).
    /// </summary>
    public const string DaemonResponsivenessTimeoutMarker =
        "docker start exceeded the remote daemon-responsiveness deadline";

    /// <summary>
    /// True when <paramref name="ex"/> (or any inner) is the daemon-responsiveness
    /// <see cref="TimeoutException"/> identified by <see cref="DaemonResponsivenessTimeoutMarker"/>.
    /// </summary>
    public static bool IsDaemonResponsivenessTimeout(Exception? ex)
    {
        for (var cur = ex; cur is not null; cur = cur.InnerException)
        {
            if (
                cur is TimeoutException
                && cur.Message.Contains(DaemonResponsivenessTimeoutMarker, StringComparison.Ordinal)
            )
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Classifies <paramref name="ex"/> (walking the inner-exception chain) against the
    /// runner's current transport action (<see cref="TransportActionWindow.IsOpenNow"/>).
    /// A bare <see cref="TimeoutException"/> is <see cref="TransportFaultKind.None"/>
    /// (ambiguous: a slow-but-live server times out the same way a dead forward does — the
    /// caller corroborates with <c>ssh -O check</c>; see
    /// <see cref="DockerHost.PoisonIfTransportFaultAsync"/>).
    ///
    /// <para>
    /// A loopback <c>ConnectionRefused</c> means the local kernel rejected a
    /// connect to an <b>unbound</b> <c>127.0.0.1</c> port — the per-server
    /// <c>ssh -L</c> forward's listener is gone, NOT that the remote host died.
    /// One shared <c>ssh -M</c> master carries every forward, so a transient
    /// master-side channel failure can tear down in-flight <c>-L</c> channels
    /// while the master process and the daemon-socket forward keep working (a
    /// full <c>ServerAliveCountMax</c> death kills the master outright). The
    /// caller must corroborate a forward-scoped fault with <c>ssh -O check</c>
    /// before poisoning the whole host — a live master means heal the forward,
    /// not poison the host. Host-scoped faults (HostUnreachable, NetworkUnreachable,
    /// name resolution, TLS) mean the transport genuinely cannot carry the request
    /// and poison directly.
    /// </para>
    /// </summary>
    public static TransportFaultVerdict Classify(Exception? ex) =>
        Classify(ex, TransportActionWindow.IsOpenNow);

    /// <summary>
    /// Pure core of <see cref="Classify(Exception?)"/>. <paramref name="insideOwnedActionWindow"/>
    /// is consulted lazily, only for a signal whose scope depends on it
    /// (<see cref="HttpRequestError.ResponseEnded"/>). A wrapper the tables don't know
    /// (a plain <see cref="IOException"/> around a <see cref="SocketException"/>) does not
    /// hide the typed inner signal: the walk keeps going and the first definitive verdict
    /// wins; only a chain with no definitive signal at all is reported unclassified.
    /// </summary>
    public static TransportFaultVerdict Classify(Exception? ex, Func<bool> insideOwnedActionWindow)
    {
        TransportFaultVerdict? unclassified = null;
        for (var current = ex; current is not null; current = current.InnerException)
        {
            var verdict = ClassifySingle(current, insideOwnedActionWindow);
            if (verdict.IsTransportFault)
            {
                return verdict;
            }

            if (verdict.Kind == TransportFaultKind.Unclassified)
            {
                unclassified ??= verdict;
            }
        }

        if (unclassified is { } u)
        {
            EmitUnclassified(ex!, u);
            return u;
        }

        return TransportFaultVerdict.None;
    }

    private static TransportFaultVerdict ClassifySingle(
        Exception ex,
        Func<bool> insideOwnedActionWindow
    ) =>
        ex switch
        {
            SocketException se => ClassifySocket(se.SocketErrorCode),

            // Both HTTP faces of a fault share one error table; HttpIOException is an
            // IOException subtype, so it is matched before the plain-IOException arm.
            HttpIOException hio => ClassifyHttp(
                hio.HttpRequestError,
                "http io",
                insideOwnedActionWindow
            ),
            HttpRequestException hre => ClassifyHttp(
                hre.HttpRequestError,
                "http",
                insideOwnedActionWindow
            ),

            // Forward closed mid-read on a raw stream (docker log/stats streams): a dropped
            // -L channel, not a dead host.
            EndOfStreamException => new(
                TransportFaultKind.ForwardScoped,
                "daemon stream ended (transport)"
            ),

            // Any other IOException carries no typed code. Reported, never guessed from text.
            IOException => new(
                TransportFaultKind.Unclassified,
                $"unclassified: {ex.GetType().Name}"
            ),

            _ => TransportFaultVerdict.None,
        };

    /// <summary>
    /// Faults on a LOOPBACK <c>ssh -L</c> forward are forward-scoped — corroborate with
    /// <c>ssh -O check</c> before poisoning. The connection only ever talks to the local
    /// forward listener, never the remote host directly, so none of them on its own proves
    /// the host died. Under load the master logs per-channel resets routinely while the
    /// master + forwards + host stay alive; the fatal case is a master keepalive drop, which
    /// tears down ALL forwards at once — an in-flight request then gets reset/aborted and
    /// the next connect gets refused; they are the SAME event at different moments and all
    /// heal by re-opening the forward (<c>ServerContainer.ReopenApiForwardAsync</c>).
    /// Codes that mean the host/network itself is unreachable poison directly.
    /// </summary>
    private static TransportFaultVerdict ClassifySocket(SocketError code) =>
        code switch
        {
            SocketError.ConnectionRefused
            or SocketError.ConnectionReset
            or SocketError.ConnectionAborted
            // Peer stopped answering on the forwarded connection — same drop signature.
            or SocketError.TimedOut => new(
                TransportFaultKind.ForwardScoped,
                $"socket transport fault ({code})"
            ),

            SocketError.HostUnreachable
            or SocketError.NetworkUnreachable
            or SocketError.NotConnected
            or SocketError.Shutdown => new(
                TransportFaultKind.HostScoped,
                $"socket transport fault ({code})"
            ),

            _ => new(TransportFaultKind.Unclassified, $"unclassified: SocketException ({code})"),
        };

    /// <summary>
    /// One table for both HTTP faces of a fault: <see cref="HttpRequestException"/> (the
    /// request never got a response) and <see cref="HttpIOException"/> (the response
    /// stream broke). <see cref="HttpRequestError.ResponseEnded"/> is the one code whose
    /// scope depends on owned state: a forward that dies mid-response and a server process
    /// that crashes mid-response produce the identical exception. Inside the runner's
    /// transport-action window it is the forward; outside it stays application-level, so a
    /// real crash is reported as a failure rather than reopened, reset and eventually
    /// skipped as infrastructure.
    /// </summary>
    private static TransportFaultVerdict ClassifyHttp(
        HttpRequestError error,
        string face,
        Func<bool> insideOwnedActionWindow
    ) =>
        error switch
        {
            // Refused/reset connect to 127.0.0.1:port — the loopback forward listener is gone.
            HttpRequestError.ConnectionError => new(
                TransportFaultKind.ForwardScoped,
                $"{face} transport fault ({error})"
            ),

            // The transport could not carry the request for a reason the host owns.
            HttpRequestError.NameResolutionError or HttpRequestError.SecureConnectionError => new(
                TransportFaultKind.HostScoped,
                $"{face} transport fault ({error})"
            ),

            HttpRequestError.ResponseEnded when insideOwnedActionWindow() => new(
                TransportFaultKind.ForwardScoped,
                $"{face} transport fault ({error}, inside owned transport action)"
            ),
            HttpRequestError.ResponseEnded => TransportFaultVerdict.None,

            // No typed signal at all.
            HttpRequestError.Unknown => new(
                TransportFaultKind.Unclassified,
                $"unclassified: {face} ({error})"
            ),

            // The peer answered (protocol, version, auth, proxy, response shape, limits):
            // application-level.
            _ => TransportFaultVerdict.None,
        };

    private static void EmitUnclassified(Exception outermost, TransportFaultVerdict verdict)
    {
        var chain = new List<string>();
        for (var e = outermost; e is not null; e = e.InnerException)
        {
            chain.Add($"{e.GetType().FullName}: {e.Message}");
        }

        InfrastructureEventLog.Emit(
            TransportEventNames.TransportFaultUnclassified,
            new TransportFaultUnclassifiedEvent(
                outermost.GetType().Name,
                outermost.Message,
                string.Join(" -> ", chain),
                verdict.Reason!
            )
        );
    }
}
