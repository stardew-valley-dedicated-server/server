using System.Text.Json.Serialization;

namespace JunimoServer.Tests.Schema.Events;

/// <summary>
/// Event names for the SSH-transport incident records below. Emitted as the
/// <c>data</c> payload of <see cref="Helpers.InfrastructureEventLog.Emit"/>;
/// camelCase wire except <c>host_id</c>, which every host-scoped event shares.
/// </summary>
public static class TransportEventNames
{
    /// <summary>First wedged canary poll of a streak (stall start).</summary>
    public const string SshMasterCanaryStall = "ssh_master_canary_stall";

    /// <summary>Non-wedged canary after one or more wedged polls (stall end + duration).</summary>
    public const string SshMasterCanaryRecovered = "ssh_master_canary_recovered";

    /// <summary>Wedge streak reached the action threshold; full observation before any action.</summary>
    public const string SshMasterWedgeObserved = "ssh_master_wedge_observed";

    /// <summary>Old master torn down ahead of a respawn; carries how it ended.</summary>
    public const string SshMasterRespawnAttempt = "ssh_master_respawn_attempt";

    /// <summary>One heal-and-retry cycle of <c>ForwardHealingHandler</c>.</summary>
    public const string ForwardHealAttempt = "forward_heal_attempt";

    /// <summary>
    /// A forward heal outside <c>ForwardHealingHandler</c> (the host's daemon-socket
    /// forward in <c>DockerHost</c>, the watchdog's API-forward heal in
    /// <c>ManagedServer</c>) threw instead of returning a verdict.
    /// </summary>
    public const string ForwardHealThrew = "forward_heal_threw";

    /// <summary>A transport-layer exception whose typed code is not in the classifier's tables.</summary>
    public const string TransportFaultUnclassified = "transport_fault_unclassified";

    /// <summary>A container log stream delivered data again after a silent gap.</summary>
    public const string ContainerLogStreamGap = "container_log_stream_gap";

    /// <summary>A Docker stats stream delivered a sample again after a gap.</summary>
    public const string ContainerStatsStreamGap = "container_stats_stream_gap";

    /// <summary>A child reader could not read the runner's <c>transport-state.{hostId}.json</c>.</summary>
    public const string TransportStateUnreadable = "transport_state_unreadable";
}

/// <summary>Exception rendering shared by the transport events.</summary>
public static class TransportEventFormat
{
    /// <summary>
    /// <c>Type: message -> InnerType: message</c> down the inner chain — the
    /// one-line form that fits a JSONL row while still naming the root cause.
    /// </summary>
    public static string Chain(Exception ex)
    {
        var parts = new List<string>();
        for (var e = ex; e is not null; e = e.InnerException)
        {
            parts.Add($"{e.GetType().Name}: {e.Message}");
        }

        return string.Join(" -> ", parts);
    }

    /// <summary>
    /// Full <see cref="Exception.ToString"/> (type, message, inner chain, stack)
    /// for a heal that threw instead of returning a verdict. Bounded so the row
    /// survives the runner→UI 4096-char pipe.
    /// </summary>
    public static string StackTrace(Exception ex)
    {
        const int maxChars = 2000;
        var text = ex.ToString();
        return text.Length <= maxChars ? text : text[..maxChars] + " …";
    }
}

/// <summary>
/// One <c>/_ping</c> canary through the host's daemon-socket forward.
/// <c>Result</c> is <c>healthy</c>, <c>wedged</c>, <c>dropped</c> or
/// <c>not_applicable</c>; <c>Detail</c> names the phase that hit its deadline
/// or the exception, null when healthy.
/// </summary>
public sealed record CanaryObservation(
    DateTime AtUtc,
    string Result,
    long ConnectMs,
    long? WriteMs,
    long? ReadMs,
    string? Detail
);

public sealed record MuxCheckObservation(int ExitCode, string Stderr, long DurationMs);

/// <summary>
/// Coordinator-side TCP state for connections to the SSH host. <c>Sampler</c>
/// is the tool that produced <c>Output</c> (<c>Get-NetTCPConnection</c>,
/// <c>ss</c>); <c>Error</c> says why sampling produced no output, null on success.
/// </summary>
public sealed record TcpSnapshot(string Sampler, string? Output, string? Error, long DurationMs);

/// <summary>
/// Plain TCP connect to the SSH host's port, independent of the master.
/// <c>Result</c> is <c>connected</c>, <c>timeout</c> or <c>failed</c>.
/// </summary>
public sealed record ReachabilityProbe(
    string Host,
    int Port,
    string Result,
    long ConnectMs,
    string? Error
);

public sealed record SshMasterCanaryStallEvent(
    [property: JsonPropertyName("host_id")] string HostId,
    CanaryObservation Canary
);

public sealed record SshMasterCanaryRecoveredEvent(
    [property: JsonPropertyName("host_id")] string HostId,
    long StallMs,
    int WedgedPolls,
    CanaryObservation Canary
);

public sealed record SshMasterWedgeObservedEvent(
    [property: JsonPropertyName("host_id")] string HostId,
    int Streak,
    IReadOnlyList<CanaryObservation> Canaries,
    MuxCheckObservation MuxCheck,
    TcpSnapshot Tcp,
    ReachabilityProbe Reachability,
    string MasterLogTail
);

/// <summary><c>Termination</c> values: see <see cref="Json.TransportState.Termination"/>.</summary>
public sealed record SshMasterRespawnAttemptEvent(
    [property: JsonPropertyName("host_id")] string HostId,
    string IncidentId,
    string Cause,
    int? OldPid,
    string Termination,
    int ExitCode,
    string ExitStderr,
    string KillOutcome,
    long ElapsedMs,
    DateTime TerminatedAtUtc,
    string? MasterLogArchivePath
);

/// <summary>
/// <c>FaultChain</c> lists exception types outermost to innermost, joined by
/// <c> -> </c>. <c>Outcome</c> is <c>healed</c>, <c>heal_failed</c>,
/// <c>heal_threw</c>, <c>budget_exhausted</c> or <c>not_retry_safe</c> (forward-scoped
/// fault on a request not declared safe to re-send; it propagated unhealed).
/// </summary>
public sealed record ForwardHealAttemptEvent(
    int Attempt,
    int? Port,
    string FaultType,
    string FaultMessage,
    string FaultChain,
    string? Classification,
    long? HealMs,
    string Outcome,
    string? HealError,
    string? HealStackTrace
);

/// <summary>
/// <see cref="TransportEventNames.ForwardHealThrew"/>: <c>Site</c> names the heal
/// (<c>daemon_forward</c> / <c>server_api_forward</c>); <c>Trigger</c> is the fault
/// that prompted it, <c>HealError</c> the chain of what the heal threw.
/// </summary>
public sealed record ForwardHealThrewEvent(
    [property: JsonPropertyName("host_id")] string HostId,
    string Site,
    string? Label,
    string? Trigger,
    string HealError,
    string HealStackTrace
);

public sealed record StreamGapEvent(
    string Label,
    [property: JsonPropertyName("host_id")] string? HostId,
    DateTime GapStartUtc,
    DateTime GapEndUtc,
    long GapMs
);

/// <summary>
/// <see cref="TransportEventNames.TransportStateUnreadable"/>: a read of the runner's
/// transport-state.{hostId}.json failed. <c>Label</c>/<c>HostId</c> identify the reader when it
/// has them, null otherwise. <c>ExceptionType</c>/<c>Error</c> are the failure.
/// </summary>
public sealed record TransportStateUnreadableEvent(
    string Path,
    string ExceptionType,
    string Error,
    string? Label = null,
    [property: JsonPropertyName("host_id")] string? HostId = null
);

/// <summary>
/// <c>ExceptionChain</c> lists <c>FullName: Message</c> outermost to innermost, joined
/// by <c> -> </c>; <c>Classification</c> is the <c>unclassified: ...</c> reason.
/// </summary>
public sealed record TransportFaultUnclassifiedEvent(
    string ExceptionType,
    string Message,
    string ExceptionChain,
    string Classification
);
