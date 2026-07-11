using System.Net.Sockets;

namespace JunimoServer.Tests.Infrastructure;

/// <summary>
/// Decides whether an exception was a host <i>transport</i> fault (SSH forward
/// dropped, daemon socket gone, pipe broken) — a candidate to poison the host (see
/// <see cref="DockerHost.Poison"/>) — or an <i>application</i> fault, which must
/// not (poisoning a healthy host on a slow server would cascade unrelated tests).
/// A host-scoped transport fault poisons directly; a forward-scoped one (loopback
/// ConnectionRefused) only after <c>ssh -O check</c> confirms the host is gone.
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
    /// Classifies <paramref name="ex"/> (walking the inner-exception chain) into a
    /// host-transport-fault reason and whether that fault was <i>forward-scoped</i>
    /// — a loopback "no listener on this port" signal
    /// (<see cref="SocketError.ConnectionRefused"/> /
    /// <see cref="HttpRequestError.ConnectionError"/>) — rather than host-scoped.
    /// Returns <c>(null, false)</c> for application-level faults, including a bare
    /// <see cref="TimeoutException"/> (ambiguous: a slow-but-live server times out
    /// the same way a dead forward does — the caller corroborates with
    /// <c>ssh -O check</c>; see <see cref="DockerHost.PoisonIfTransportFaultAsync"/>).
    ///
    /// <para>
    /// A loopback <c>ConnectionRefused</c> means the local kernel rejected a
    /// connect to an <b>unbound</b> <c>127.0.0.1</c> port — the per-server
    /// <c>ssh -L</c> forward's listener is gone, NOT that the remote host died.
    /// One shared <c>ssh -M</c> master carries every forward, so a transient
    /// master keepalive blip (<c>ServerAliveCountMax</c> exceeded) tears down all
    /// in-flight <c>-L</c> channels at once while the master process survives
    /// (<c>ControlPersist</c>) and the daemon-socket forward keeps working. The
    /// caller must corroborate a forward-scoped fault with <c>ssh -O check</c>
    /// before poisoning the whole host — a live master means heal the forward,
    /// not poison the host. Host-scoped faults (RST, HostUnreachable, broken
    /// pipe) mean the connection genuinely broke and poison directly.
    /// </para>
    /// </summary>
    public static (string? Reason, bool ForwardScoped) Classify(Exception? ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            var result = ClassifySingle(current);
            if (result.Reason is not null)
            {
                return result;
            }
        }
        return (null, false);
    }

    private static (string? Reason, bool ForwardScoped) ClassifySingle(Exception ex) =>
        ex switch
        {
            // Faults on a LOOPBACK `ssh -L` forward — all forward-scoped, corroborate
            // with `ssh -O check` before poisoning. The connection only ever talks to the
            // local forward listener, never the remote host directly, so NONE of these on
            // their own proves the host died — only a failed -O check does. Reproduced
            // 2026-06-26: under load the master logs `channel N: read failed ... Broken
            // pipe` / per-channel resets routinely while the master + forwards + host stay
            // fully alive (Docker stats never stopped). The fatal case is a master keepalive
            // drop, which tears down ALL forwards at once — an in-flight request then gets
            // RST/ConnectionReset/broken-pipe and the next connect gets ConnectionRefused;
            // they are the SAME event at different moments and all heal by re-opening the
            // forward (see host-poison-deadlocks-run.md / ServerContainer.ReopenApiForwardAsync).
            SocketException se when IsForwardScopedSocketError(se.SocketErrorCode) => (
                $"socket transport fault ({se.SocketErrorCode})",
                true
            ),

            // Remaining socket codes are genuinely host-level (the host/network itself is
            // unreachable, not just a forward channel) — poison directly, no heal possible.
            SocketException se when IsHostTransportSocketError(se.SocketErrorCode) => (
                $"socket transport fault ({se.SocketErrorCode})",
                false
            ),

            // ConnectionError is the HttpClient-layer face of the same loopback forward
            // faults (refused/reset connect to 127.0.0.1:port). Forward-scoped.
            HttpRequestException hre
                when hre.HttpRequestError == HttpRequestError.ConnectionError => (
                $"http transport fault ({hre.HttpRequestError})",
                true
            ),

            // Other *connection* HttpRequestErrors (name resolution, TLS) mean the
            // daemon was unreachable for a host-level reason; not loopback-recoverable.
            HttpRequestException hre when IsHostTransportHttpError(hre.HttpRequestError) => (
                $"http transport fault ({hre.HttpRequestError})",
                false
            ),

            // Forward closed mid-response: a dropped -L channel, not a dead host.
            // Forward-scoped — re-open and retry, corroborated by -O check.
            EndOfStreamException => ("daemon stream ended (transport)", true),

            IOException io when LooksLikeBrokenConnection(io.Message) => (
                "io transport fault (broken pipe / connection reset)",
                true
            ),

            _ => (null, false),
        };

    /// <summary>
    /// Socket error codes that, on a loopback <c>ssh -L</c> forward, indicate a dropped
    /// forward CHANNEL rather than a dead host: the connect was refused (no listener after
    /// a forward drop) or an established connection was reset/aborted (in-flight when the
    /// forward tore down). All recoverable by re-opening the forward once <c>ssh -O check</c>
    /// confirms the master is alive — proven survivable in the 2026-06-26 repro.
    /// </summary>
    private static bool IsForwardScopedSocketError(SocketError code) =>
        code switch
        {
            SocketError.ConnectionRefused => true,
            SocketError.ConnectionReset => true,
            SocketError.ConnectionAborted => true,
            // Peer stopped answering on the forwarded connection — same drop signature.
            SocketError.TimedOut => true,
            _ => false,
        };

    /// <summary>
    /// Socket error codes that mean the host/network itself is unreachable — distinct from
    /// a recoverable loopback-forward drop (<see cref="IsForwardScopedSocketError"/>). These
    /// poison the host directly: re-opening a forward can't fix an unreachable network.
    /// </summary>
    private static bool IsHostTransportSocketError(SocketError code) =>
        code switch
        {
            SocketError.HostUnreachable => true,
            SocketError.NetworkUnreachable => true,
            SocketError.NotConnected => true,
            SocketError.Shutdown => true,
            _ => false,
        };

    /// <summary>
    /// Host-level <see cref="HttpRequestError"/> values (name resolution, TLS)
    /// that indicate the transport could not carry the request for a reason the
    /// host itself owns — distinct from a loopback
    /// <see cref="HttpRequestError.ConnectionError"/> (handled as forward-scoped).
    /// </summary>
    private static bool IsHostTransportHttpError(HttpRequestError error) =>
        error switch
        {
            HttpRequestError.SecureConnectionError => true,
            HttpRequestError.NameResolutionError => true,
            _ => false,
        };

    private static bool LooksLikeBrokenConnection(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return false;
        }

        return message.Contains("broken pipe", StringComparison.OrdinalIgnoreCase)
            || message.Contains("connection reset", StringComparison.OrdinalIgnoreCase)
            || message.Contains("connection refused", StringComparison.OrdinalIgnoreCase)
            || message.Contains("connection closed", StringComparison.OrdinalIgnoreCase)
            || message.Contains("transport connection", StringComparison.OrdinalIgnoreCase);
    }
}
