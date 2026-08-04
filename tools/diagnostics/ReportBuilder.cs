using System.Collections;
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
        Health();
        Uptime();
        Performance();
        Storage();
        Services();
        JsonSection("Server settings", _server.Settings);
        RuntimeConfig();
        Mods();
        Farmhands();
        Cabins();
        JsonSection("Server state", _server.DiagnosticsState);

        return _sb.ToString();
    }

    private void BuildIdentity()
    {
        var status = _server.Status.Json;
        Heading("Build identity");
        Markdown.Table(
            _sb,
            new[] { "Component", "Version" },
            new List<string[]>
            {
                new[] { "Server", Format.OrUnknown(Json.Field(status, "serverVersion")) },
                new[] { "Game", Format.OrUnknown(Json.Field(status, "gameVersion")) },
                new[] { "Git commit", Format.OrUnknown(Config.GitSha) },
                new[] { "SMAPI", Format.OrUnknown(Config.SmapiVersion) },
            }
        );
        _sb.AppendLine();

        // The run-wide state note sits outside the table: it's a paragraph telling the reader how to
        // read every live section below, not another build fact.
        if (!Config.ApiEnabled)
        {
            _sb.AppendLine("HTTP API disabled (API_ENABLED=false) — live-state sections skipped.");
            _sb.AppendLine();
        }
        else if (_state == ServerState.NotAccepting)
        {
            _sb.AppendLine(
                "**HTTP API not responding** — every request was refused. The server is still "
                    + "starting, has stopped, or crashed; the logs in this zip say which. If it was "
                    + "started seconds ago, re-run."
            );
            _sb.AppendLine();
        }
        else if (_state == ServerState.NoWorldLoaded)
        {
            _sb.AppendLine(
                "**No save loaded** — the server is booting or between saves (e.g. a day transition or farm-map change). Live world sections below reflect this."
            );
            _sb.AppendLine();
        }
        else
        {
            var failures = _server.Failures;
            if (failures.Count > 0)
            {
                var listed = failures.Select(f => $"{f.Path}{f.Read.Detail}");
                _sb.AppendLine($"Failed live-state reads: {string.Join(", ", listed)}");
                _sb.AppendLine();
            }
        }
    }

    private void ReportedDetails()
    {
        if (_reported != null)
        {
            Heading("Reported details");
            var rows = new List<string[]>
            {
                new[] { "Client-side mods", Format.OrBlank(_reported.ClientMods) },
            };
            if (!string.IsNullOrWhiteSpace(_reported.ClientModList))
            {
                rows.Add(new[] { "Which mods", _reported.ClientModList });
            }
            rows.Add(new[] { "Affected player", Format.OrBlank(_reported.AffectedPlayer) });
            rows.Add(new[] { "Client platforms", Format.OrBlank(_reported.Platforms) });
            rows.Add(
                new[]
                {
                    "Shared Steam account (server + client)",
                    Format.OrBlank(_reported.SharedSteamAccount),
                }
            );
            rows.Add(new[] { "Hosting", Format.OrBlank(_reported.Hosting) });
            rows.Add(new[] { "Reproducibility", Format.OrBlank(_reported.Reproducibility) });
            rows.Add(
                new[] { "Started after a change", Format.OrBlank(_reported.StartedAfterChange) }
            );
            Markdown.Table(_sb, new[] { "Detail", "Answer" }, rows);
        }
        else
        {
            Heading("Technical details to include");
            _sb.AppendLine("Fill these in when you attach this report:");
            _sb.AppendLine();
            _sb.AppendLine(
                "- **Client-side mods:** which mods (name + version) do you run locally?"
            );
            _sb.AppendLine("- **Affected player:** your name on the server.");
            _sb.AppendLine(
                "- **Client platforms:** which platforms are the relevant clients on (e.g. PC-Steam, PC-GOG, iOS, Android, Switch)?"
            );
            _sb.AppendLine(
                "- **Shared Steam account:** do you use the same Steam account for the server and for a game client?"
            );
            _sb.AppendLine(
                "- **Hosting:** is the server on the same local network as the players, remote (VPS / cloud), or mixed?"
            );
            _sb.AppendLine(
                "- **Reproducibility:** every time or once? Did it start after a change (mod added, update, setting)?"
            );
        }
        _sb.AppendLine();
    }

    /// <summary>
    /// Game-loop liveness from /health, which answers without the game thread — so this is the
    /// section that separates a stuck server (listener up, loop stalled) from a healthy one.
    /// </summary>
    private void Health()
    {
        Heading("Health");
        var read = _server.Health;
        if (!read.Ok)
        {
            AppendUnavailable(read);
            return;
        }

        var frozen = Json.FieldBool(read.Json, "isFrozen");
        Markdown.Table(
            _sb,
            new[] { "Check", "Value" },
            new List<string[]>
            {
                new[] { "Game loop", frozen ? "**frozen**" : "ticking" },
                new[]
                {
                    "Since last tick",
                    Json.FieldLong(read.Json, "lastTickMs") is { } ms
                        ? $"{ms} ms"
                        : "no tick recorded since process start",
                },
                new[] { "Total ticks", Format.OrUnknown(Json.Field(read.Json, "tickCount")) },
                // Null when the mod's own isGameAvailable() probe threw.
                new[]
                {
                    "Game server available",
                    Format.YesNo(Json.FieldNullableBool(read.Json, "gameAvailable")),
                },
            }
        );
        _sb.AppendLine();

        if (frozen)
        {
            _sb.AppendLine(
                "**Game loop frozen** — the API answers but the game thread has stopped ticking. "
                    + "The server is stuck, not slow."
            );
            _sb.AppendLine();
        }
    }

    /// <summary>Mod uptime from /stats, plus container boot uptime from PID 1's start time.</summary>
    private void Uptime()
    {
        Heading("Uptime");
        var read = _server.Stats;
        var startedAt = Json.Field(read.Json, "startedAtUtc");
        var uptimeSeconds = Json.FieldLong(read.Json, "uptimeSeconds");
        var rows = new List<string[]>();
        if (!string.IsNullOrEmpty(startedAt) && uptimeSeconds is { } up)
        {
            rows.Add(new[] { "Server started", startedAt });
            rows.Add(new[] { "Server uptime", Format.Duration(TimeSpan.FromSeconds(up)) });
        }
        else
        {
            rows.Add(new[] { "Server uptime", $"{_state.UnavailableReason()}{read.Detail}" });
        }

        var containerUptime = HostInspector.ContainerUptime();
        rows.Add(
            new[]
            {
                "Container uptime",
                containerUptime is { } c ? Format.Duration(c) : Format.NotAvailable,
            }
        );
        Markdown.Table(_sb, new[] { "Metric", "Value" }, rows);
        _sb.AppendLine();
    }

    private void Performance()
    {
        Heading("Performance");
        var read = _server.Stats;
        if (!read.Ok)
        {
            AppendUnavailable(read);
            return;
        }
        var s = read.Json;
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
                        ? Format.NotAvailable
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
        Markdown.Table(
            _sb,
            new[] { "Service", "Endpoint", "Status" },
            new List<string[]>
            {
                // Backticked so markdown renders the URL as-is instead of autolinking it.
                new[] { "steam-auth", $"`{Config.SteamAuthUrl}`", _sidecarStatus },
            }
        );
        _sb.AppendLine();
    }

    /// <summary>
    /// Container environment settings (tick rate, API, logging) — the half of the configuration the
    /// in-game settings above don't cover.
    /// </summary>
    private void RuntimeConfig()
    {
        Heading("Runtime configuration");
        var rows = Environment
            .GetEnvironmentVariables()
            .Cast<DictionaryEntry>()
            .Select(e => e.Key.ToString() ?? "")
            .Where(Config.IsReportedEnv)
            .OrderBy(name => name, StringComparer.Ordinal)
            .Select(name => new[] { name, DescribeEnv(name) })
            .ToList();
        if (rows.Count == 0)
        {
            _sb.AppendLine("None.");
            _sb.AppendLine();
            return;
        }
        Markdown.Table(_sb, new[] { "Variable", "Value" }, rows);
        _sb.AppendLine();
    }

    private static string DescribeEnv(string name)
    {
        // Compose passes optional vars through as "" (`API_KEY: "${API_KEY:-}"`), so present-but-empty
        // is the normal shape of an unconfigured setting, not a value worth printing.
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrEmpty(value))
        {
            return Format.NotSet;
        }
        // No "set" prefix needed: an unset secret already reads as "(not set)" above.
        return Config.IsSecretEnv(name) ? Format.Redacted : value;
    }

    private void Mods()
    {
        Heading("Mods");
        var mods = HostInspector.EnumerateMods();
        if (mods.Count == 0)
        {
            _sb.AppendLine("No mods found.");
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
        var read = _server.Farmhands;
        if (!read.Ok)
        {
            AppendUnavailable(read);
            return;
        }
        var farmhands = Json.FieldArray(read.Json, "farmhands");
        if (farmhands.Count == 0)
        {
            _sb.AppendLine("None.");
            _sb.AppendLine();
            return;
        }

        var onlineIds = OnlinePlayerIds();
        var rows = new List<string[]>();
        foreach (var f in farmhands)
        {
            var id = Cell(f, "id");
            var name = Cell(f, "name");
            rows.Add(
                new[]
                {
                    // An unclaimed slot has no name yet — the Slot column says which case this is.
                    string.IsNullOrEmpty(name)
                        ? Format.Nothing
                        : name,
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
        var read = _server.Cabins;
        if (!read.Ok)
        {
            AppendUnavailable(read);
            return;
        }
        var root = read.Json;
        // "Available" counts cabins the server treats as claimable, which INCLUDES a cabin owned
        // by a player who hasn't customized their character yet (isAssigned needs owner.isCustomized).
        // The Status column spells that middle state out so it doesn't read as a contradiction.
        Markdown.Table(
            _sb,
            new[] { "Strategy", "Total", "Assigned", "Available" },
            new List<string[]>
            {
                new[]
                {
                    Cell(root, "strategy"),
                    Cell(root, "totalCount"),
                    Cell(root, "assignedCount"),
                    Cell(root, "availableCount"),
                },
            }
        );
        _sb.AppendLine();

        var rows = new List<string[]>();
        foreach (var c in Json.FieldArray(root, "cabins"))
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
                    hasOwner ? owner : Format.Nothing,
                    status,
                    Format.YesNo(Json.FieldBool(c, "isHidden")),
                }
            );
        }
        if (rows.Count > 0)
        {
            Markdown.Table(_sb, new[] { "Tile", "Type", "Owner", "Status", "Hidden" }, rows);
        }
        _sb.AppendLine();
    }

    private void JsonSection(string title, ApiRead read)
    {
        Heading(title);
        if (!read.Ok)
        {
            AppendUnavailable(read);
            return;
        }
        _sb.AppendLine("```json");
        _sb.AppendLine(Json.Pretty(read.Json));
        _sb.AppendLine("```");
        _sb.AppendLine();
    }

    private HashSet<string> OnlinePlayerIds()
    {
        var ids = new HashSet<string>();
        foreach (var p in Json.FieldArray(_server.Players.Json, "players"))
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

    /// <summary>
    /// Stands in for a section whose live data couldn't be read: the run-wide reason, plus this
    /// endpoint's own when it differs from the rest (a 401 on an authed path while the API is up).
    /// </summary>
    private void AppendUnavailable(ApiRead read)
    {
        _sb.AppendLine($"{Format.Capitalize(_state.UnavailableReason())}{read.Detail}.");
        _sb.AppendLine();
    }

    /// <summary>A table-cell value read from an already-parsed element ("" if absent).</summary>
    private static string Cell(JsonElement element, string field) => Json.Field(element, field);
}
