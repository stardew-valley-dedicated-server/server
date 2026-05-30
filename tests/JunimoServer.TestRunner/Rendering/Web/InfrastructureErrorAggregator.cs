using System.Text.Json;
using JunimoServer.Tests.Helpers;

namespace JunimoServer.TestRunner.Rendering.Web;

/// <summary>
/// Reads <c>{runDir}/diagnostics/infrastructure.jsonl</c> at end-of-run and projects
/// <c>failure_context</c> events into a lean shape suitable for embedding in
/// <c>summary.json.infrastructureErrors</c>. Drops the bulky <c>serverState</c> blob —
/// triagers needing full state should read the JSONL directly.
///
/// Tolerates a missing file (returns empty) and per-line parse errors (skips the line).
/// Capped at <see cref="MaxEntries"/> most-recent entries; on overflow the writer
/// emits a sibling <c>infrastructureErrorsTruncated</c> flag.
/// </summary>
internal static class InfrastructureErrorAggregator
{
    public const int MaxEntries = 200;

    public sealed record Result(IReadOnlyList<Dictionary<string, object?>> Entries, bool Truncated);

    public static Result Read(string runDir)
    {
        var path = RunArtifactNames.InfrastructureLog(runDir);
        if (!File.Exists(path))
            return new Result(Array.Empty<Dictionary<string, object?>>(), Truncated: false);

        var entries = new List<Dictionary<string, object?>>();
        try
        {
            foreach (var line in File.ReadLines(path))
            {
                if (line.Length == 0) continue;
                var entry = TryProject(line);
                if (entry != null) entries.Add(entry);
            }
        }
        catch (IOException)
        {
            return new Result(Array.Empty<Dictionary<string, object?>>(), Truncated: false);
        }

        var truncated = entries.Count > MaxEntries;
        if (truncated)
            entries = entries.GetRange(entries.Count - MaxEntries, MaxEntries);

        return new Result(entries, truncated);
    }

    private static Dictionary<string, object?>? TryProject(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            if (!root.TryGetProperty("event", out var ev) || ev.GetString() != "failure_context")
                return null;

            var entry = new Dictionary<string, object?>();

            if (root.TryGetProperty("ts", out var ts)) entry["ts"] = ts.GetString();
            if (root.TryGetProperty("runMs", out var runMs) && runMs.ValueKind == JsonValueKind.Number)
                entry["runMs"] = runMs.GetInt64();
            if (root.TryGetProperty("test", out var test) && test.ValueKind == JsonValueKind.Object)
                entry["test"] = JsonSerializer.Deserialize<Dictionary<string, object?>>(test.GetRawText());

            if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
            {
                if (data.TryGetProperty("reason", out var reason))
                    entry["reason"] = reason.GetString();
                if (data.TryGetProperty("extras", out var extras) && extras.ValueKind != JsonValueKind.Null)
                    entry["extras"] = JsonSerializer.Deserialize<Dictionary<string, object?>>(extras.GetRawText());
                if (data.TryGetProperty("diagnosticsError", out var diag) && diag.ValueKind == JsonValueKind.String)
                    entry["diagnosticsError"] = diag.GetString();
                // serverState is intentionally dropped — too large for summary.json.
            }

            return entry.Count > 0 ? entry : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
