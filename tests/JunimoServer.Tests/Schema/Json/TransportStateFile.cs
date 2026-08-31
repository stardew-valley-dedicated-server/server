using System.Text.Json;
using System.Text.Json.Serialization;
using JunimoServer.Tests.Helpers;

namespace JunimoServer.Tests.Schema.Json;

/// <summary>
/// The runner's most recent transport action on a host, published to the xUnit
/// child through <c>{runDir}/diagnostics/transport-state.{hostId}.json</c>. Owner and
/// only writer: <c>TunnelManager.TryRespawnMasterAsync</c> in the runner process
/// (the child never owns a master, so it never writes). The child reads it on
/// demand to attribute a transport fault to a runner action; env vars cannot
/// carry it because they are fixed at child spawn.
/// </summary>
public sealed record TransportState
{
    public const int CurrentSchemaVersion = 1;

    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>Unique per action: <c>{hostId}-{yyyyMMddTHHmmssfff}</c>.</summary>
    [JsonPropertyName("incidentId")]
    public required string IncidentId { get; init; }

    [JsonPropertyName("hostId")]
    public required string HostId { get; init; }

    /// <summary>
    /// What tripped the action: <c>datapath_wedged</c>, <c>mux_check_failed</c>,
    /// <c>forward_reopen_failing</c>, <c>master_check_failing</c> or
    /// <c>poison_corroboration</c>.
    /// </summary>
    [JsonPropertyName("cause")]
    public required string Cause { get; init; }

    /// <summary>Currently always <c>master_respawn</c>.</summary>
    [JsonPropertyName("action")]
    public required string Action { get; init; }

    /// <summary>
    /// How the old master ended: <c>exit_ok</c> (clean <c>-O exit</c>),
    /// <c>killed</c> (pid kill), <c>socket_gone</c> (<c>-O exit</c> failed but the
    /// socket no longer answers), <c>unconfirmed</c> (still alive after the kill).
    /// </summary>
    [JsonPropertyName("termination")]
    public required string Termination { get; init; }

    [JsonPropertyName("exitCode")]
    public required int ExitCode { get; init; }

    [JsonPropertyName("exitStderr")]
    public required string ExitStderr { get; init; }

    [JsonPropertyName("killOutcome")]
    public required string KillOutcome { get; init; }

    [JsonPropertyName("oldPid")]
    public int? OldPid { get; init; }

    /// <summary>Where the old master's <c>-E</c> log was archived (null when it had none).</summary>
    [JsonPropertyName("masterLogArchivePath")]
    public string? MasterLogArchivePath { get; init; }

    /// <summary>When the runner decided to act.</summary>
    [JsonPropertyName("actionStartedAtUtc")]
    public required DateTime ActionStartedAtUtc { get; init; }

    /// <summary>When the old master's teardown finished (its observed end, or the kill giving up).</summary>
    [JsonPropertyName("terminatedAtUtc")]
    public required DateTime TerminatedAtUtc { get; init; }

    /// <summary>Null while the respawn is still in progress.</summary>
    [JsonPropertyName("actionEndedAtUtc")]
    public DateTime? ActionEndedAtUtc { get; init; }

    /// <summary><c>in_progress</c>, <c>respawned</c>, <c>respawn_failed</c> or <c>old_master_survived</c>.</summary>
    [JsonPropertyName("outcome")]
    public required string Outcome { get; init; }

    /// <summary>
    /// End of the re-establish window: faults observed before this instant are
    /// attributable to the action. Null while the action is in progress.
    /// </summary>
    [JsonPropertyName("windowEndUtc")]
    public DateTime? WindowEndUtc { get; init; }
}

/// <summary>
/// Reader and writer for <see cref="TransportState"/>. The writer replaces the
/// file atomically (temp file + move), so a reader never sees a partial
/// document. One file per host: a host's action must stay readable for its whole
/// attribution window, and the master monitor can act on another host inside that
/// window — a shared slot would erase the first host's attribution.
/// </summary>
public static class TransportStateFile
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string PathFor(string runDir, string hostId) =>
        Path.Combine(
            runDir,
            RunArtifactNames.DiagnosticsDir,
            RunArtifactNames.TransportStateJson(hostId)
        );

    /// <summary>
    /// Every host's state file present in the run, for a reader with no host to look
    /// up (<see cref="Infrastructure.TransportActionWindow"/>).
    /// </summary>
    public static IEnumerable<string> PathsIn(string runDir)
    {
        var dir = Path.Combine(runDir, RunArtifactNames.DiagnosticsDir);
        return Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir, RunArtifactNames.TransportStateJson("*"))
            : Array.Empty<string>();
    }

    /// <summary>
    /// Returns the host's current state, or null when the runner has not performed a
    /// transport action on it this run. A malformed file throws <see cref="JsonException"/>
    /// — writer and parser disagree, which must not pass silently.
    /// </summary>
    public static TransportState? TryRead(string runDir, string hostId) =>
        ReadPath(PathFor(runDir, hostId));

    /// <summary><see cref="TryRead"/> for a path from <see cref="PathsIn"/>.</summary>
    public static TransportState? ReadPath(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        // Include FileShare.Delete so the writer's atomic File.Move (which must delete
        // the destination) can replace this file on Windows while the child holds it
        // open; without it the move throws ERROR_SHARING_VIOLATION and the reader keeps
        // seeing stale state.
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete
        );
        return JsonSerializer.Deserialize<TransportState>(stream, Options);
    }

    public static void Write(string runDir, TransportState state)
    {
        var path = PathFor(runDir, state.HostId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Unique temp name: writes are sequential today, but a shared temp path
        // would let any future concurrent writer corrupt or steal another's move.
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(state, Options));
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            File.Delete(temp); // no-op after a successful move
        }
    }
}
