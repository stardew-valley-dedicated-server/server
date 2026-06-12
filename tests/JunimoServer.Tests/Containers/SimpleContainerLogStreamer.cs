using System.Text.Json.Nodes;
using JunimoServer.Tests.Helpers;
using JunimoServer.Tests.Infrastructure;

namespace JunimoServer.Tests.Containers;

/// <summary>
/// Wire-contract parser for <c>SDVD_EVENT </c> lines emitted on container
/// stdout. Called from the per-line callbacks of every site that streams a
/// container's logs (<see cref="ServerContainer"/>,
/// <see cref="GameClientContainer"/>, <see cref="SharedSteamAuth"/>); the
/// streaming itself is owned by <see cref="ContainerLogStreamReader"/>.
/// </summary>
internal static class SimpleContainerLogStreamer
{
    /// <summary>
    /// Checks a log line for the <c>SDVD_EVENT </c> wire-contract prefix the
    /// mod and steam-auth sidecar use to emit structured events over stdout.
    /// If found, forwards the JSON tail to
    /// <see cref="InfrastructureEventLog.ForwardRaw"/>, which decorates it
    /// with a top-level <c>forwardedVia</c> field naming the container of
    /// origin. Origin-side envelope fields (<c>ts</c>, <c>role</c>,
    /// <c>requestId</c>, <c>event</c>, <c>data</c>, <c>tickMs</c>) are
    /// preserved byte-for-byte.
    /// </summary>
    /// <param name="line">The raw stdout line, including the <c>SDVD_EVENT </c> prefix.</param>
    /// <param name="forwardedVia">Container of origin (e.g. <c>server-0</c>,
    /// <c>client-2</c>, <c>steam-auth-shared</c>).</param>
    /// <returns><c>true</c> if the line was a <c>SDVD_EVENT </c> transport line
    /// (forwarded and now spent) — callers should keep it off human-facing
    /// sinks; <c>false</c> for ordinary log lines.</returns>
    public static bool TryForwardSdvdEvent(string line, string forwardedVia)
    {
        const string prefix = "SDVD_EVENT ";
        if (line.Length <= prefix.Length || !line.StartsWith(prefix, StringComparison.Ordinal))
            return false;
        var jsonTail = line.Substring(prefix.Length);
        InfrastructureEventLog.ForwardRaw(jsonTail, forwardedVia);
        TryAnnotateModPhase(jsonTail, forwardedVia);
        return true;
    }

    /// <summary>
    /// When <paramref name="jsonTail"/> is a <c>mod_phase</c> SDVD_EVENT, fans
    /// out a per-test annotation to whichever tests are currently executing on
    /// the originating server container. Cheap fast-path: skips parsing
    /// entirely when the line doesn't mention <c>mod_phase</c>. Failures are
    /// silent — the typed event is already on disk via
    /// <see cref="InfrastructureEventLog.ForwardRaw"/>.
    /// </summary>
    private static void TryAnnotateModPhase(string jsonTail, string forwardedVia)
    {
        if (!jsonTail.Contains("\"mod_phase\"", StringComparison.Ordinal))
            return;
        try
        {
            if (JsonNode.Parse(jsonTail) is not JsonObject obj)
                return;
            if (obj["event"]?.GetValue<string>() != "mod_phase")
                return;
            var phase = obj["data"]?["phase"]?.GetValue<string>();
            if (string.IsNullOrEmpty(phase))
                return;
            TestResourceBroker.Instance.EmitModPhaseAnnotation(forwardedVia, phase);
        }
        catch
        {
            // Annotation is best-effort UI sugar; the typed event already
            // landed in infrastructure.jsonl. Swallow parse errors so they
            // don't propagate into the per-line callback.
        }
    }
}
