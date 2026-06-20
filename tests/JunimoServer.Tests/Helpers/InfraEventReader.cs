using System.Text.Json;

namespace JunimoServer.Tests.Helpers;

/// <summary>
/// Reads mod-emitted diagnostic events back from <c>infrastructure.jsonl</c>.
///
/// Mod events (<c>auth_galaxy_lost</c>, <c>steam_session_*</c>, …) are NOT a live
/// test-assertion surface — there is no "wait for mod event X" API by design
/// (see .claude/rules/tests-assert-via-http-api.md). They travel mod stdout →
/// <c>SimpleContainerLogStreamer</c> → an async writer → <c>infrastructure.jsonl</c>
/// on disk. So the disk file IS the read surface, which is exactly what the
/// Galaxy-reinit repro needs: during total connectivity loss the HTTP API is dark, but
/// stdout (and therefore this file) keeps flowing.
///
/// Each line is a JSON object: <c>{ ts, requestId, service, tickMs, event, data,
/// forwardedVia }</c> (the mod's <c>ModEventLog.Emit</c> envelope plus the
/// harness-added <c>forwardedVia</c> slug, e.g. <c>server-0</c>). Events are
/// returned in file order, which is arrival order (the writer appends
/// sequentially).
///
/// Because the writer is async, a freshly-emitted event can lag the file briefly.
/// <see cref="WaitForEventAsync"/> polls with a bounded budget so asserting the
/// ABSENCE of a post-restore event (the "Design B / SDK silent" verdict) is not a
/// false negative from an unflushed buffer.
/// </summary>
internal static class InfraEventReader
{
    /// <summary>One parsed diagnostic event line.</summary>
    internal sealed record Event(string Name, string? ForwardedVia, DateTime? Ts, JsonElement Data);

    private static string LogPath => RunArtifactNames.InfrastructureLog(TestArtifacts.RunDir);

    /// <summary>
    /// Reads every event whose name is in <paramref name="eventNames"/>, optionally
    /// restricted to a single origin container (<paramref name="forwardedVia"/>,
    /// e.g. <c>server-0</c> — pass it to avoid cross-server contamination in a shared
    /// run). Returns them in file (arrival) order. Tolerates a missing file (returns
    /// empty) and skips malformed lines, mirroring <c>FlakinessTracker</c>'s reader.
    /// </summary>
    public static List<Event> Read(IReadOnlySet<string> eventNames, string? forwardedVia = null)
    {
        var results = new List<Event>();
        var path = LogPath;
        if (!File.Exists(path))
        {
            return results;
        }

        // Read with shared access — the async writer holds the file open for append.
        string[] lines;
        try
        {
            using var fs = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite
            );
            using var reader = new StreamReader(fs);
            lines = reader.ReadToEnd().Split('\n');
        }
        catch (IOException)
        {
            return results;
        }

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (
                    !root.TryGetProperty("event", out var ev)
                    || ev.GetString() is not { } name
                    || !eventNames.Contains(name)
                )
                {
                    continue;
                }

                var via = root.TryGetProperty("forwardedVia", out var v) ? v.GetString() : null;
                if (forwardedVia != null && via != forwardedVia)
                {
                    continue;
                }

                DateTime? ts =
                    root.TryGetProperty("ts", out var tsEl) && tsEl.TryGetDateTime(out var parsed)
                        ? parsed
                        : null;

                // Clone the data element so it survives the JsonDocument's disposal.
                var data = root.TryGetProperty("data", out var d) ? d.Clone() : default;

                results.Add(new Event(name, via, ts, data));
            }
            catch (JsonException)
            { /* skip malformed lines */
            }
        }

        return results;
    }

    /// <summary>
    /// Polls <see cref="Read"/> until an event matching <paramref name="predicate"/>
    /// appears, or <paramref name="timeout"/> elapses. Returns the first match, or
    /// null on timeout. Use a SHORT timeout (a flush-settle window, a few seconds)
    /// when confirming absence; a LONGER one when waiting for an expected event to
    /// land (e.g. the post-restore Steam reconnect).
    /// </summary>
    public static async Task<Event?> WaitForEventAsync(
        IReadOnlySet<string> eventNames,
        Func<Event, bool> predicate,
        TimeSpan timeout,
        string? forwardedVia = null,
        TimeSpan? pollInterval = null,
        CancellationToken ct = default
    )
    {
        var interval = pollInterval ?? TimeSpan.FromMilliseconds(500);
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var match = Read(eventNames, forwardedVia).FirstOrDefault(predicate);
            if (match != null)
            {
                return match;
            }

            if (DateTime.UtcNow >= deadline)
            {
                return null;
            }

            await Task.Delay(interval, ct);
        }
    }
}
