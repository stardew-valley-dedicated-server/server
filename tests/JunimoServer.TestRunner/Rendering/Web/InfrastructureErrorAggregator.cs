using System.Text.Json;
using JunimoServer.Tests.Helpers;

namespace JunimoServer.TestRunner.Rendering.Web;

/// <summary>
/// Reads <c>{runDir}/diagnostics/infrastructure.jsonl</c> at end-of-run and projects
/// <c>failure_context</c> and <c>host_disconnected</c> events into a lean shape
/// suitable for embedding in <c>summary.json.infrastructureErrors</c>. Each entry
/// carries an <c>event</c> discriminator. <c>failure_context</c> entries drop the
/// bulky <c>serverState</c> blob — triagers needing full state should read the
/// JSONL directly. <c>host_disconnected</c> entries keep the full payload
/// (<c>host_id, reason, sshMasterLogTail</c>) so the run summary names the host
/// outage's cause without a trip to the JSONL.
///
/// Tolerates a missing/unreadable file (returns empty) and per-line parse errors
/// (skips the line). Only the most-recent <see cref="MaxEntries"/> entries are
/// retained — the read keeps a ring buffer so memory stays bounded regardless of
/// file size (the log has a 20 MB advisory cap). On overflow the writer emits a
/// sibling <c>infrastructureErrorsTruncated</c> flag.
/// </summary>
internal static class InfrastructureErrorAggregator
{
    public const int MaxEntries = 200;

    public sealed record Result(IReadOnlyList<Dictionary<string, object?>> Entries, bool Truncated);

    private static readonly Result EmptyResult =
        new(Array.Empty<Dictionary<string, object?>>(), Truncated: false);

    public static Result Read(string runDir)
    {
        var path = RunArtifactNames.InfrastructureLog(runDir);
        if (!File.Exists(path))
            return EmptyResult;

        var ring = new Queue<Dictionary<string, object?>>(MaxEntries);
        var totalProjected = 0;
        try
        {
            foreach (var line in File.ReadLines(path))
            {
                if (line.Length == 0) continue;
                var entry = TryProject(line);
                if (entry == null) continue;

                totalProjected++;
                if (ring.Count == MaxEntries) ring.Dequeue();  // keep only the newest MaxEntries
                ring.Enqueue(entry);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return EmptyResult;
        }

        return new Result(ring.ToArray(), Truncated: totalProjected > MaxEntries);
    }

    private static Dictionary<string, object?>? TryProject(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            if (!root.TryGetProperty("event", out var ev))
                return null;
            var eventName = ev.GetString();
            if (eventName is not ("failure_context" or "host_disconnected"))
                return null;

            // Envelope + data.reason are common to both events; the switch adds the
            // event-specific data.* fields. data is absent on malformed lines only.
            var entry = ProjectEnvelope(root, eventName);
            if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
            {
                if (data.TryGetProperty("reason", out var reason))
                    entry["reason"] = reason.GetString();

                switch (eventName)
                {
                    case "host_disconnected": AddHostDisconnectedFields(entry, data); break;
                    case "failure_context": AddFailureContextFields(entry, data); break;
                }
            }

            return entry.Count > 1 ? entry : null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            // InvalidOperationException: an unguarded GetString() hit a non-string
            // value on a malformed/hand-edited line. Skip the line per the
            // class contract rather than aborting the whole ring-buffer read.
            return null;
        }
    }

    private static Dictionary<string, object?> ProjectEnvelope(JsonElement root, string eventName)
    {
        var entry = new Dictionary<string, object?> { ["event"] = eventName };
        if (root.TryGetProperty("ts", out var ts)) entry["ts"] = ts.GetString();
        if (root.TryGetProperty("runMs", out var runMs) && runMs.ValueKind == JsonValueKind.Number)
            entry["runMs"] = runMs.GetInt64();
        if (root.TryGetProperty("test", out var test) && test.ValueKind == JsonValueKind.Object)
            entry["test"] = test.Clone();
        return entry;
    }

    private static void AddHostDisconnectedFields(Dictionary<string, object?> entry, JsonElement data)
    {
        if (data.TryGetProperty("host_id", out var hostId))
            entry["hostId"] = hostId.GetString();
        if (data.TryGetProperty("sshMasterLogTail", out var tail) && tail.ValueKind == JsonValueKind.String)
            entry["sshMasterLogTail"] = tail.GetString();
    }

    private static void AddFailureContextFields(Dictionary<string, object?> entry, JsonElement data)
    {
        if (data.TryGetProperty("extras", out var extras) && extras.ValueKind != JsonValueKind.Null)
            entry["extras"] = extras.Clone();
        if (data.TryGetProperty("diagnosticsError", out var diag) && diag.ValueKind == JsonValueKind.String)
            entry["diagnosticsError"] = diag.GetString();
        // serverState is intentionally dropped — too large for summary.json.
    }
}
