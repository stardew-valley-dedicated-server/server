using System.Text;
using System.Text.Json;

namespace Diagnostics;

/// <summary>
/// Assembles the markdown report from the collected server responses, the host inspection, and the
/// wizard answers. Each section degrades gracefully: when live data is missing it prints why (from
/// <see cref="ServerState"/>) rather than a bare "unknown".
/// </summary>
internal sealed class ReportBuilder
{
    private readonly StringBuilder _sb = new();
    private readonly ServerClient _server;
    private readonly ReportedDetails? _reported;
    private readonly string _sidecarStatus;
    private readonly ServerState _state;

    public ReportBuilder(ServerClient server, ReportedDetails? reported, string sidecarStatus)
    {
        _server = server;
        _reported = reported;
        _sidecarStatus = sidecarStatus;
        _state = server.DeriveState();
    }

    public string Build()
    {
        _sb.AppendLine("# Server Diagnostics Report");
        _sb.AppendLine();
        _sb.AppendLine($"Generated: {DateTime.UtcNow:o}");
        _sb.AppendLine();

        BuildIdentity();
        ReportedDetails(); // The human's account, up top — the first thing a triager reads.
        Uptime();
        Performance();
        Storage();
        Services();
        JsonSection("Server settings", _server.Get("/settings"));
        Mods();
        Farmhands();
        Cabins();
        JsonSection("Server state", _server.Get("/diagnostics/state"));

        return _sb.ToString();
    }

    private void BuildIdentity()
    {
        var status = _server.Get("/status");
        Heading("Build identity");
        _sb.AppendLine($"- Server version: `{Json.String(status, "serverVersion") ?? "unknown"}`");
        var gameVersion = Json.String(status, "gameVersion");
        _sb.AppendLine(
            $"- Game version: `{(string.IsNullOrEmpty(gameVersion) ? "unknown" : gameVersion)}`"
        );
        _sb.AppendLine($"- Git commit: `{Config.GitSha}`");
        _sb.AppendLine($"- SMAPI version: `{Config.SmapiVersion}`");

        if (!Config.ApiEnabled)
        {
            _sb.AppendLine(
                "- HTTP API disabled (API_ENABLED=false) — live-state sections skipped."
            );
        }
        else if (_state == ServerState.NotAccepting)
        {
            _sb.AppendLine(
                "- **Server still starting** — the HTTP API isn't accepting connections yet. Re-run in a few seconds."
            );
        }
        else if (_state == ServerState.NoWorldLoaded)
        {
            _sb.AppendLine(
                "- **No save loaded** — the server is booting or between saves (e.g. a day transition or farm-map change). Live world sections below reflect this."
            );
        }
        else if (_server.FailedReads.Count > 0)
        {
            _sb.AppendLine($"- Failed live-state reads: {string.Join(", ", _server.FailedReads)}");
        }
        _sb.AppendLine();
    }

    private void ReportedDetails()
    {
        if (_reported != null)
        {
            Heading("Reported details");
            _sb.AppendLine($"- Client-side mods: {Format.BlankOr(_reported.ClientMods)}");
            if (!string.IsNullOrWhiteSpace(_reported.ClientModList))
            {
                _sb.AppendLine($"  - Which: {_reported.ClientModList}");
            }
            _sb.AppendLine(
                $"- Affected player / platform: {Format.BlankOr(_reported.AffectedPlayer)}"
            );
            _sb.AppendLine($"- Reproducibility: {Format.BlankOr(_reported.Reproducibility)}");
            _sb.AppendLine(
                $"- Started after a change: {Format.BlankOr(_reported.StartedAfterChange)}"
            );
        }
        else
        {
            Heading("Technical details to include");
            _sb.AppendLine("Fill these in when you attach this report:");
            _sb.AppendLine();
            _sb.AppendLine(
                "- **Client-side mods:** which mods (name + version) do you run locally?"
            );
            _sb.AppendLine(
                "- **Affected player / platform:** your name on the server, and Steam / GOG / OS."
            );
            _sb.AppendLine(
                "- **Reproducibility:** every time or once? Did it start after a change (mod added, update, setting)?"
            );
        }
        _sb.AppendLine();
    }

    /// <summary>Mod uptime from /stats, plus container boot uptime from PID 1's start time.</summary>
    private void Uptime()
    {
        Heading("Uptime");
        var stats = _server.Get("/stats");
        var startedAt = Json.String(stats, "startedAtUtc");
        var uptimeSeconds = Json.Long(stats, "uptimeSeconds");
        if (!string.IsNullOrEmpty(startedAt) && uptimeSeconds is { } up)
        {
            _sb.AppendLine($"- Server started: {startedAt}");
            _sb.AppendLine($"- Server uptime: {Format.Duration(TimeSpan.FromSeconds(up))}");
        }
        else
        {
            _sb.AppendLine($"- Server uptime: _{_state.UnavailableReason()}_");
        }

        var containerUptime = HostInspector.ContainerUptime();
        _sb.AppendLine(
            containerUptime is { } c
                ? $"- Container uptime: {Format.Duration(c)}"
                : "- Container uptime: _not available_"
        );
        _sb.AppendLine();
    }

    private void Performance()
    {
        Heading("Performance");
        var stats = _server.Get("/stats");
        if (stats == null)
        {
            _sb.AppendLine($"_{Format.Capitalize(_state.UnavailableReason())}._");
            _sb.AppendLine();
            return;
        }
        try
        {
            using var doc = JsonDocument.Parse(stats);
            var s = doc.RootElement;
            var rows = new List<string[]>
            {
                new[] { "TPS (actual / target)", $"{Cell(s, "tps")} / {Cell(s, "targetTps")}" },
                new[] { "FPS", Cell(s, "fps") },
                new[] { "Avg tick", $"{Cell(s, "avgTickMs")} ms" },
                new[] { "Game-thread wait", $"{Cell(s, "gameThreadWaitMs")} ms" },
                new[] { "Pending actions", Cell(s, "pendingActions") },
                new[] { "Managed memory", $"{Cell(s, "memoryMb")} MB" },
                new[] { "GC gen 0 collections", Cell(s, "gcGen0") },
                new[] { "GC gen 1 collections", Cell(s, "gcGen1") },
                new[] { "GC gen 2 collections", Cell(s, "gcGen2") },
            };
            Markdown.Table(_sb, new[] { "Metric", "Value" }, rows);
        }
        catch
        {
            _sb.AppendLine("_Could not parse stats response._");
        }
        _sb.AppendLine();
    }

    private void Storage()
    {
        Heading("Storage");
        var rows = new List<string[]>();
        foreach (var path in Config.DiskPaths)
        {
            var (free, total) = HostInspector.DiskUsage(path);
            rows.Add(
                new[]
                {
                    path,
                    total == null
                        ? "_n/a_"
                        : $"{Format.Bytes(free!.Value)} free / {Format.Bytes(total.Value)}",
                }
            );
        }
        Markdown.Table(_sb, new[] { "Volume", "Free / Total" }, rows);
        _sb.AppendLine();

        var crashModified = HostInspector.CrashLogModifiedUtc();
        if (crashModified != null)
        {
            _sb.AppendLine(
                $"- SMAPI crash log: present (modified {crashModified}) — included in this zip."
            );
            _sb.AppendLine();
        }
    }

    private void Services()
    {
        Heading("Services");
        _sb.AppendLine($"- steam-auth ({Config.SteamAuthUrl}): {_sidecarStatus}");
        _sb.AppendLine();
    }

    private void Mods()
    {
        Heading("Mods");
        var mods = HostInspector.EnumerateMods();
        if (mods.Count == 0)
        {
            _sb.AppendLine("_No mods found._");
            _sb.AppendLine();
            return;
        }
        var rows = mods.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .Select(m => new[] { m.Name, m.UniqueId, m.Version, m.Author })
            .ToList();
        Markdown.Table(_sb, new[] { "Name", "UniqueID", "Version", "Author" }, rows);
        _sb.AppendLine();
    }

    /// <summary>
    /// One roster of all farmhand slots (online and offline), with Connected derived from the
    /// online-only /players list. A slot with no customization has never been claimed, so it's free.
    /// </summary>
    private void Farmhands()
    {
        Heading("Farmhands");
        var farmhands = Json.Array(_server.Get("/farmhands"), "farmhands");
        if (farmhands == null)
        {
            _sb.AppendLine($"_{Format.Capitalize(_state.UnavailableReason())}._");
            _sb.AppendLine();
            return;
        }
        if (farmhands.Value.GetArrayLength() == 0)
        {
            _sb.AppendLine("_None._");
            _sb.AppendLine();
            return;
        }

        var onlineIds = OnlinePlayerIds();
        var rows = new List<string[]>();
        foreach (var f in farmhands.Value.EnumerateArray())
        {
            var id = Cell(f, "id");
            rows.Add(
                new[]
                {
                    Cell(f, "name"),
                    id,
                    Format.YesNo(onlineIds.Contains(id)),
                    Json.FieldBool(f, "isCustomized") ? "claimed" : "free (unclaimed)",
                }
            );
        }
        Markdown.Table(_sb, new[] { "Name", "ID", "Connected", "Slot" }, rows);
        _sb.AppendLine();
    }

    private void Cabins()
    {
        Heading("Cabins");
        var raw = _server.Get("/cabins");
        if (raw == null)
        {
            _sb.AppendLine($"_{Format.Capitalize(_state.UnavailableReason())}._");
            _sb.AppendLine();
            return;
        }
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            _sb.AppendLine($"- Strategy: {Cell(root, "strategy")}");
            _sb.AppendLine(
                $"- Total: {Cell(root, "totalCount")} · Assigned: {Cell(root, "assignedCount")} · Available: {Cell(root, "availableCount")}"
            );
            // "Available" counts cabins the server treats as claimable, which INCLUDES a cabin owned
            // by a player who hasn't customized their character yet (isAssigned needs owner.isCustomized).
            // The Status column spells that middle state out so it doesn't read as a contradiction.
            _sb.AppendLine();
            if (root.TryGetProperty("cabins", out var cabins) && cabins.GetArrayLength() > 0)
            {
                var rows = new List<string[]>();
                foreach (var c in cabins.EnumerateArray())
                {
                    var owner = Cell(c, "ownerName");
                    var hasOwner = !string.IsNullOrEmpty(owner);
                    var status =
                        Json.FieldBool(c, "isAssigned") ? "assigned"
                        : hasOwner ? "owned, setup pending"
                        : "available";
                    rows.Add(
                        new[]
                        {
                            $"({Cell(c, "tileX")}, {Cell(c, "tileY")})",
                            Cell(c, "type"),
                            hasOwner ? owner : "-",
                            status,
                            Format.YesNo(Json.FieldBool(c, "isHidden")),
                        }
                    );
                }
                Markdown.Table(_sb, new[] { "Tile", "Type", "Owner", "Status", "Hidden" }, rows);
            }
        }
        catch
        {
            _sb.AppendLine("_Could not parse cabins response._");
        }
        _sb.AppendLine();
    }

    private void JsonSection(string title, string? raw)
    {
        Heading(title);
        if (raw == null)
        {
            _sb.AppendLine($"_{Format.Capitalize(_state.UnavailableReason())}._");
            _sb.AppendLine();
            return;
        }
        _sb.AppendLine("```json");
        _sb.AppendLine(Json.Pretty(raw));
        _sb.AppendLine("```");
        _sb.AppendLine();
    }

    private HashSet<string> OnlinePlayerIds()
    {
        var ids = new HashSet<string>();
        var players = Json.Array(_server.Get("/players"), "players");
        if (players == null)
        {
            return ids;
        }
        foreach (var p in players.Value.EnumerateArray())
        {
            if (Json.FieldBool(p, "isOnline"))
            {
                ids.Add(Cell(p, "id"));
            }
        }
        return ids;
    }

    private void Heading(string title)
    {
        _sb.AppendLine($"## {title}");
        _sb.AppendLine();
    }

    /// <summary>A table-cell value read from an already-parsed element ("" if absent).</summary>
    private static string Cell(JsonElement element, string field) => Json.Field(element, field);
}
