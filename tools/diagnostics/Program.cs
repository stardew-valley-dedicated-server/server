using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Spectre.Console;
using Spectre.Console.Rendering;

class Program
{
    private static readonly string ApiPort =
        Environment.GetEnvironmentVariable("API_PORT") ?? "8080";
    private static readonly string ApiKey = Environment.GetEnvironmentVariable("API_KEY") ?? "";
    private static readonly bool ApiEnabled =
        (Environment.GetEnvironmentVariable("API_ENABLED") ?? "true").ToLowerInvariant() != "false";
    private static readonly string GitSha =
        Environment.GetEnvironmentVariable("SDVD_GIT_SHA") ?? "unknown";
    private static readonly string SmapiVersion =
        Environment.GetEnvironmentVariable("SMAPI_VERSION") ?? "unknown";
    private static readonly string BaseUrl = $"http://127.0.0.1:{ApiPort}";

    // Steam auth sidecar URL the server itself uses (docker-compose STEAM_AUTH_URL).
    private static readonly string SteamAuthUrl =
        Environment.GetEnvironmentVariable("STEAM_AUTH_URL") ?? "http://steam-auth:3001";

    // Paths inside the server container.
    private const string ConsoleLogPath = "/tmp/server-output.log";
    private const string ConfigRoot = "/config/xdg/config/StardewValley";
    private const string ModsPath = "/data/Mods";
    private const string OutputDir = "/data/diagnostics";

    // Volumes worth reporting free space for (game download, saves, settings).
    private static readonly string[] DiskPaths = { "/data/game", ConfigRoot, "/data/settings" };

    // Endpoints the report collects. /stats, /diagnostics/state, /health are public; the rest need
    // the key (sending Bearer on all is harmless).
    private static readonly string[] Endpoints =
    {
        "/status",
        "/stats",
        "/diagnostics/state",
        "/settings",
        "/players",
        "/farmhands",
        "/cabins",
    };

    private static readonly List<string> FailedSections = new();

    // Track why live reads failed so the report can explain an empty snapshot instead of showing
    // bare "unknown" everywhere: a connection-level failure on every attempt means the HTTP listener
    // isn't up yet (server still starting), which reads very differently from an HTTP error status.
    private static int _readsAttempted;
    private static int _connectionFailures;

    /// <summary>What the collection run observed about the server, used to caption empty sections.</summary>
    private enum ServerState
    {
        Reachable, // Listener answered; live data is present (may still be pre-save — see NoWorldLoaded).
        NotAccepting, // Every read failed to connect — listener not up yet (server still starting).
        NoWorldLoaded, // Listener up, but no save is loaded (booting, or a runtime day/farm-map change).
    }

    private static readonly JsonDocumentOptions ManifestJsonOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    static async Task<int> Main(string[] args)
    {
        if (args.Contains("--help") || args.Contains("-h"))
        {
            PrintHelp();
            return 0;
        }

        AnsiConsole.Write(new Rule("[bold cyan]Server Diagnostics[/]").LeftJustified());
        AnsiConsole.MarkupLine("[dim]Collecting server state into a single attachable file.[/]");
        AnsiConsole.WriteLine();

        // With `docker compose exec -it` both streams are a PTY → interactive.
        // Without `-it` both are pipes → non-interactive (skip the wizard, write a template).
        bool interactive = !Console.IsInputRedirected && !Console.IsOutputRedirected;

        // Prompt first, then do the machine work in one burst — so the wizard wait doesn't sit
        // between the state capture and the report timestamp, keeping them within ~a second.
        var reported = interactive ? RunWizard() : null;

        var json = new Dictionary<string, string?>();
        string sidecarStatus = "not checked";
        await AnsiConsole
            .Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync(
                "Reading live server state...",
                async ctx =>
                {
                    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

                    if (ApiEnabled)
                    {
                        if (!string.IsNullOrEmpty(ApiKey))
                        {
                            client.DefaultRequestHeaders.Authorization =
                                new AuthenticationHeaderValue("Bearer", ApiKey);
                        }
                        foreach (var path in Endpoints)
                        {
                            ctx.Status($"[dim]GET {Markup.Escape(path)}...[/]");
                            json[path] = await TryGet(client, path);
                        }
                    }

                    ctx.Status("[dim]Probing steam-auth sidecar...[/]");
                    sidecarStatus = await ProbeSidecar();
                }
            );

        var report = BuildReport(json, reported, sidecarStatus);

        var zipPath = await AnsiConsole
            .Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("Building diagnostics zip...", _ => Task.FromResult(WriteZip(report)));

        PrintDone(zipPath, interactive);
        return 0;
    }

    private static void PrintHelp()
    {
        var panel = new Panel(
            new Rows(
                new Markup("[bold]diagnostics[/]"),
                new Rule().RuleStyle("dim"),
                new Markup(
                    "\nCollects server build identity, logs, settings, installed mods, and live"
                ),
                new Markup(
                    "state into a single [white].zip[/] under [white]./diagnostics/[/] on the host."
                ),
                new Markup("\nRun with a TTY ([white]-it[/]) for the technical-details wizard.")
            )
        )
        {
            Header = new PanelHeader(" Server Diagnostics ", Justify.Center),
            Border = BoxBorder.Rounded,
            Padding = new Padding(2, 1),
        };
        AnsiConsole.Write(panel);
    }

    /// <summary>
    /// Resilient GET mirroring the mod's TryRead shape: returns raw JSON on success or null on any
    /// failure, records the path in <see cref="FailedSections"/>, never throws.
    /// </summary>
    private static async Task<string?> TryGet(HttpClient client, string path)
    {
        _readsAttempted++;
        try
        {
            var response = await client.GetAsync(BaseUrl + path);
            if (!response.IsSuccessStatusCode)
            {
                // An HTTP error status means the listener is up — not a connection failure.
                FailedSections.Add($"{path} (HTTP {(int)response.StatusCode})");
                return null;
            }
            return await response.Content.ReadAsStringAsync();
        }
        catch (Exception ex)
        {
            // Couldn't reach the listener at all (refused / timed out) — the "still starting" signal.
            _connectionFailures++;
            FailedSections.Add($"{path} ({ex.GetType().Name})");
            return null;
        }
    }

    /// <summary>
    /// Probes the steam-auth sidecar's /health from inside the container (same URL the server uses),
    /// reporting reachability and a COUNT of configured / logged-in accounts. Reads only the array
    /// length and the per-account logged_in bools — never the usernames or steam_ids, which are
    /// sensitive and must not reach the report. Never throws.
    /// </summary>
    private static async Task<string> ProbeSidecar()
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var response = await client.GetAsync($"{SteamAuthUrl}/health");
            if (!response.IsSuccessStatusCode)
            {
                return $"reachable, but /health returned HTTP {(int)response.StatusCode}";
            }
            var body = await response.Content.ReadAsStringAsync();
            var (total, loggedIn) = CountSteamAccounts(body);
            // Derive the summary from the count, not the sidecar's top-level `logged_in`: that flag is
            // All(accounts, …), which is vacuously true when zero accounts are configured — reporting
            // it verbatim would read as "logged in" on a server with no Steam accounts at all.
            if (total == 0)
            {
                return "reachable, 0 Steam accounts configured";
            }
            return $"reachable, {loggedIn}/{total} Steam account(s) logged in";
        }
        catch (Exception ex)
        {
            return $"UNREACHABLE ({ex.GetType().Name})";
        }
    }

    /// <summary>
    /// Extracts ONLY the account count and logged-in count from the sidecar /health body. Deliberately
    /// reads just the `accounts` array length and each entry's `logged_in` bool — never `username` or
    /// `steam_id`, so no account identity can leak into the report. Returns (0, 0) on any parse issue.
    /// </summary>
    private static (int total, int loggedIn) CountSteamAccounts(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (
                !doc.RootElement.TryGetProperty("accounts", out var accounts)
                || accounts.ValueKind != JsonValueKind.Array
            )
            {
                return (0, 0);
            }
            var total = accounts.GetArrayLength();
            var loggedIn = 0;
            foreach (var account in accounts.EnumerateArray())
            {
                if (
                    account.TryGetProperty("logged_in", out var flag)
                    && flag.ValueKind == JsonValueKind.True
                )
                {
                    loggedIn++;
                }
            }
            return (total, loggedIn);
        }
        catch
        {
            return (0, 0);
        }
    }

    private static ReportedDetails RunWizard()
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine(
            "[cyan]A few technical details the server can't see on its own[/] [dim](all optional).[/]"
        );
        AnsiConsole.WriteLine();

        var details = new ReportedDetails();

        var usesClientMods = AskChoice(
            "Do you use [white]client-side mods[/]?",
            "No",
            "Yes",
            "Not sure"
        );
        details.ClientMods = usesClientMods;
        if (usesClientMods == "Yes")
        {
            details.ClientModList = AskText("Which ones ([white]name + version[/])?");
        }

        details.AffectedPlayer = AskText(
            "Which player is affected ([white]your name on the server[/]), and on what platform ([white]Steam / GOG / OS[/])?"
        );

        details.Reproducibility = AskChoice(
            "Does it happen [white]every time[/] or just [white]once[/]?",
            "Every time",
            "Once",
            "Not sure"
        );

        details.StartedAfterChange = AskText(
            "Did it start after a change ([white]mod added, update, setting[/])? (optional)"
        );

        return details;
    }

    /// <summary>
    /// Runs a selection prompt and then echoes the answer, because Spectre's SelectionPrompt erases
    /// itself once chosen (unlike TextPrompt, which persists). Echoing keeps the full Q&amp;A visible.
    /// </summary>
    private static string AskChoice(string title, params string[] choices)
    {
        var answer = AnsiConsole.Prompt(
            new SelectionPrompt<string>().Title(title).AddChoices(choices)
        );
        AnsiConsole.MarkupLine($"{title} [green]{Markup.Escape(answer)}[/]");
        return answer;
    }

    /// <summary>Runs an optional free-text prompt. TextPrompt already persists its line on screen.</summary>
    private static string AskText(string prompt) =>
        AnsiConsole.Prompt(new TextPrompt<string>(prompt).AllowEmpty());

    private static string BuildReport(
        Dictionary<string, string?> json,
        ReportedDetails? reported,
        string sidecarStatus
    )
    {
        var status = json.GetValueOrDefault("/status");
        var state = DeriveServerState(status);

        var sb = new StringBuilder();
        sb.AppendLine("# Server Diagnostics Report");
        sb.AppendLine();
        sb.AppendLine($"Generated: {DateTime.UtcNow:o}");
        sb.AppendLine();

        // Build identity
        sb.AppendLine("## Build identity");
        sb.AppendLine();
        var serverVersion = ReadStringField(status, "serverVersion") ?? "unknown";
        var gameVersion = ReadStringField(status, "gameVersion");
        gameVersion = string.IsNullOrEmpty(gameVersion) ? "unknown" : gameVersion;
        sb.AppendLine($"- Server version: `{serverVersion}`");
        sb.AppendLine($"- Game version: `{gameVersion}`");
        sb.AppendLine($"- Git commit: `{GitSha}`");
        sb.AppendLine($"- SMAPI version: `{SmapiVersion}`");
        if (!ApiEnabled)
        {
            sb.AppendLine("- HTTP API disabled (API_ENABLED=false) — live-state sections skipped.");
        }
        else if (state == ServerState.NotAccepting)
        {
            sb.AppendLine(
                "- **Server still starting** — the HTTP API isn't accepting connections yet. Re-run in a few seconds."
            );
        }
        else if (state == ServerState.NoWorldLoaded)
        {
            sb.AppendLine(
                "- **No save loaded** — the server is booting or between saves (e.g. a day transition or farm-map change). Live world sections below reflect this."
            );
        }
        else if (FailedSections.Count > 0)
        {
            sb.AppendLine($"- Failed live-state reads: {string.Join(", ", FailedSections)}");
        }
        sb.AppendLine();

        // Reported details (wizard answers, or a fill-in template) up top — it's the human's account
        // of the problem, the first thing a triager reads before the machine-collected sections.
        AppendReportedDetailsSection(sb, reported);

        // Uptime, performance, storage, services
        AppendRuntimeSection(sb, json.GetValueOrDefault("/stats"), state);
        AppendPerformanceSection(sb, json.GetValueOrDefault("/stats"), state);
        AppendHostSection(sb);
        AppendServicesSection(sb, sidecarStatus);

        // Server settings
        AppendJsonSection(sb, "Server settings", json.GetValueOrDefault("/settings"), state);

        // Mods
        sb.AppendLine("## Mods");
        sb.AppendLine();
        AppendModsTable(sb);
        sb.AppendLine();

        // Farmhands (with connected state derived from /players) and cabins
        AppendFarmhandsSection(
            sb,
            json.GetValueOrDefault("/farmhands"),
            json.GetValueOrDefault("/players"),
            state
        );
        AppendCabinsSection(sb, json.GetValueOrDefault("/cabins"), state);

        // Server state
        AppendJsonSection(sb, "Server state", json.GetValueOrDefault("/diagnostics/state"), state);

        return sb.ToString();
    }

    /// <summary>
    /// The human's account of the problem: the wizard answers when run interactively, or a fill-in
    /// template otherwise. Placed near the top of the report since it's what a triager reads first.
    /// </summary>
    private static void AppendReportedDetailsSection(StringBuilder sb, ReportedDetails? reported)
    {
        if (reported != null)
        {
            sb.AppendLine("## Reported details");
            sb.AppendLine();
            sb.AppendLine($"- Client-side mods: {Blank(reported.ClientMods)}");
            if (!string.IsNullOrWhiteSpace(reported.ClientModList))
            {
                sb.AppendLine($"  - Which: {reported.ClientModList}");
            }
            sb.AppendLine($"- Affected player / platform: {Blank(reported.AffectedPlayer)}");
            sb.AppendLine($"- Reproducibility: {Blank(reported.Reproducibility)}");
            sb.AppendLine($"- Started after a change: {Blank(reported.StartedAfterChange)}");
        }
        else
        {
            sb.AppendLine("## Technical details to include");
            sb.AppendLine();
            sb.AppendLine("Fill these in when you attach this report:");
            sb.AppendLine();
            sb.AppendLine(
                "- **Client-side mods:** which mods (name + version) do you run locally?"
            );
            sb.AppendLine(
                "- **Affected player / platform:** your name on the server, and Steam / GOG / OS."
            );
            sb.AppendLine(
                "- **Reproducibility:** every time or once? Did it start after a change (mod added, update, setting)?"
            );
        }
        sb.AppendLine();
    }

    /// <summary>
    /// Classifies what the run observed: NotAccepting when every read hit a connection failure (the
    /// listener isn't up — server still starting), NoWorldLoaded when the listener answered but no
    /// save is loaded (booting, or a runtime day/farm-map transition), else Reachable.
    /// </summary>
    private static ServerState DeriveServerState(string? statusRaw)
    {
        if (_readsAttempted > 0 && _connectionFailures == _readsAttempted)
        {
            return ServerState.NotAccepting;
        }
        // /status answers 200 even offline; isOnline is false until a save is loaded and the game
        // loop is running (also false through day transitions and farm-map changes at runtime).
        if (statusRaw != null && !ReadBoolField(statusRaw, "isOnline"))
        {
            return ServerState.NoWorldLoaded;
        }
        return ServerState.Reachable;
    }

    /// <summary>The per-state caption used for empty live sections, in prose that fits mid-sentence.</summary>
    private static string UnavailableReason(ServerState state) =>
        state switch
        {
            ServerState.NotAccepting => "server still starting",
            ServerState.NoWorldLoaded =>
                "no save loaded — the server is booting or between saves (e.g. a day transition or farm-map change)",
            _ => "not available",
        };

    private static void AppendJsonSection(
        StringBuilder sb,
        string title,
        string? raw,
        ServerState state
    )
    {
        sb.AppendLine($"## {title}");
        sb.AppendLine();
        if (raw == null)
        {
            sb.AppendLine($"_{Capitalize(UnavailableReason(state))}._");
            sb.AppendLine();
            return;
        }
        sb.AppendLine("```json");
        sb.AppendLine(PrettyJson(raw));
        sb.AppendLine("```");
        sb.AppendLine();
    }

    /// <summary>
    /// Server (mod) uptime from /stats, plus container uptime derived from PID 1's start time
    /// (PID 1 is the base image's init supervisor, so this is when the container booted).
    /// </summary>
    private static void AppendRuntimeSection(StringBuilder sb, string? statsRaw, ServerState state)
    {
        sb.AppendLine("## Uptime");
        sb.AppendLine();

        var startedAt = ReadStringField(statsRaw, "startedAtUtc");
        var uptimeSeconds = ReadLongField(statsRaw, "uptimeSeconds");
        if (!string.IsNullOrEmpty(startedAt) && uptimeSeconds is { } up)
        {
            sb.AppendLine($"- Server started: {startedAt}");
            sb.AppendLine($"- Server uptime: {FormatDuration(TimeSpan.FromSeconds(up))}");
        }
        else
        {
            sb.AppendLine($"- Server uptime: _{UnavailableReason(state)}_");
        }

        var containerUptime = ContainerUptime();
        sb.AppendLine(
            containerUptime is { } c
                ? $"- Container uptime: {FormatDuration(c)}"
                : "- Container uptime: _not available_"
        );
        sb.AppendLine();
    }

    private static void AppendPerformanceSection(
        StringBuilder sb,
        string? statsRaw,
        ServerState state
    )
    {
        sb.AppendLine("## Performance");
        sb.AppendLine();
        if (statsRaw == null)
        {
            sb.AppendLine($"_{Capitalize(UnavailableReason(state))}._");
            sb.AppendLine();
            return;
        }
        try
        {
            using var doc = JsonDocument.Parse(statsRaw);
            var s = doc.RootElement;
            var rows = new List<string[]>
            {
                new[] { "TPS (actual / target)", $"{Str(s, "tps")} / {Str(s, "targetTps")}" },
                new[] { "FPS", Str(s, "fps") },
                new[] { "Avg tick", $"{Str(s, "avgTickMs")} ms" },
                new[] { "Game-thread wait", $"{Str(s, "gameThreadWaitMs")} ms" },
                new[] { "Pending actions", Str(s, "pendingActions") },
                new[] { "Managed memory", $"{Str(s, "memoryMb")} MB" },
                new[] { "GC gen 0 collections", Str(s, "gcGen0") },
                new[] { "GC gen 1 collections", Str(s, "gcGen1") },
                new[] { "GC gen 2 collections", Str(s, "gcGen2") },
            };
            AppendTable(sb, new[] { "Metric", "Value" }, rows);
        }
        catch
        {
            sb.AppendLine("_Could not parse stats response._");
        }
        sb.AppendLine();
    }

    /// <summary>Disk free space for the mounted volumes, and a pointer to the SMAPI crash log when one is present.</summary>
    private static void AppendHostSection(StringBuilder sb)
    {
        sb.AppendLine("## Storage");
        sb.AppendLine();

        var rows = new List<string[]>();
        foreach (var path in DiskPaths)
        {
            var (free, total) = DiskUsage(path);
            rows.Add(
                new[]
                {
                    path,
                    total == null
                        ? "_n/a_"
                        : $"{FormatBytes(free!.Value)} free / {FormatBytes(total.Value)}",
                }
            );
        }
        AppendTable(sb, new[] { "Volume", "Free / Total" }, rows);
        sb.AppendLine();

        var crashLog = $"{ConfigRoot}/ErrorLogs/SMAPI-crash.txt";
        if (File.Exists(crashLog))
        {
            var when = File.GetLastWriteTimeUtc(crashLog).ToString("o");
            sb.AppendLine($"- SMAPI crash log: present (modified {when}) — included in this zip.");
            sb.AppendLine();
        }
    }

    private static void AppendServicesSection(StringBuilder sb, string sidecarStatus)
    {
        sb.AppendLine("## Services");
        sb.AppendLine();
        sb.AppendLine($"- steam-auth ({SteamAuthUrl}): {sidecarStatus}");
        sb.AppendLine();
    }

    private static void AppendModsTable(StringBuilder sb)
    {
        var mods = EnumerateMods();
        if (mods.Count == 0)
        {
            sb.AppendLine("_No mods found._");
            return;
        }
        var rows = mods.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .Select(m => new[] { m.Name, m.UniqueId, m.Version, m.Author })
            .ToList();
        AppendTable(sb, new[] { "Name", "UniqueID", "Version", "Author" }, rows);
    }

    /// <summary>
    /// Farmhands are the superset of player slots (online and offline); the separate /players list
    /// only reports currently-connected sessions. Merge them into one table with a Connected column
    /// derived from the online-player id set, so there's a single uniform roster.
    /// </summary>
    private static void AppendFarmhandsSection(
        StringBuilder sb,
        string? farmhandsRaw,
        string? playersRaw,
        ServerState state
    )
    {
        sb.AppendLine("## Farmhands");
        sb.AppendLine();

        var farmhands = ReadArray(farmhandsRaw, "farmhands");
        if (farmhands == null)
        {
            sb.AppendLine($"_{Capitalize(UnavailableReason(state))}._");
            sb.AppendLine();
            return;
        }
        if (farmhands.Value.GetArrayLength() == 0)
        {
            sb.AppendLine("_None._");
            sb.AppendLine();
            return;
        }

        var onlineIds = OnlinePlayerIds(playersRaw);
        var rows = new List<string[]>();
        foreach (var f in farmhands.Value.EnumerateArray())
        {
            var id = Str(f, "id");
            // An uncustomized slot has never been claimed by a real player, so it's a free slot
            // a new player can take. "Customized" is the raw signal; surface its meaning directly.
            var claimed = GetBool(f, "isCustomized");
            rows.Add(
                new[]
                {
                    Str(f, "name"),
                    id,
                    Bool(onlineIds.Contains(id)),
                    claimed ? "claimed" : "free (unclaimed)",
                }
            );
        }
        AppendTable(sb, new[] { "Name", "ID", "Connected", "Slot" }, rows);
        sb.AppendLine();
    }

    private static void AppendCabinsSection(StringBuilder sb, string? raw, ServerState state)
    {
        sb.AppendLine("## Cabins");
        sb.AppendLine();
        if (raw == null)
        {
            sb.AppendLine($"_{Capitalize(UnavailableReason(state))}._");
            sb.AppendLine();
            return;
        }
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            sb.AppendLine($"- Strategy: {Str(root, "strategy")}");
            sb.AppendLine(
                $"- Total: {Str(root, "totalCount")} · Assigned: {Str(root, "assignedCount")} · Available: {Str(root, "availableCount")}"
            );
            // "Available" counts cabins the server treats as claimable, which INCLUDES a cabin that
            // has an owner who hasn't customized their character yet (isAssigned needs
            // owner.isCustomized). The Status column below spells that middle state out so an
            // owned-but-available cabin doesn't read as a contradiction.
            sb.AppendLine();
            if (root.TryGetProperty("cabins", out var cabins) && cabins.GetArrayLength() > 0)
            {
                var rows = new List<string[]>();
                foreach (var c in cabins.EnumerateArray())
                {
                    var owner = Str(c, "ownerName");
                    var hasOwner = !string.IsNullOrEmpty(owner);
                    var assigned = GetBool(c, "isAssigned");
                    // Three distinct states, collapsed by the API into owner + isAssigned:
                    var status =
                        assigned ? "assigned"
                        : hasOwner ? "owned, setup pending"
                        : "available";
                    rows.Add(
                        new[]
                        {
                            $"({Str(c, "tileX")}, {Str(c, "tileY")})",
                            Str(c, "type"),
                            hasOwner ? owner : "-",
                            status,
                            Bool(GetBool(c, "isHidden")),
                        }
                    );
                }
                AppendTable(sb, new[] { "Tile", "Type", "Owner", "Status", "Hidden" }, rows);
            }
        }
        catch
        {
            sb.AppendLine("_Could not parse cabins response._");
        }
        sb.AppendLine();
    }

    /// <summary>
    /// Emits a GitHub-flavored markdown table with every column padded to a uniform width across the
    /// header, separator, and body — so the raw markdown source reads as aligned columns.
    /// </summary>
    private static void AppendTable(StringBuilder sb, string[] headers, List<string[]> rows)
    {
        var widths = new int[headers.Length];
        for (int c = 0; c < headers.Length; c++)
        {
            widths[c] = headers[c].Length;
        }
        foreach (var row in rows)
        {
            for (int c = 0; c < headers.Length; c++)
            {
                widths[c] = Math.Max(widths[c], row[c].Length);
            }
        }

        sb.Append('|');
        for (int c = 0; c < headers.Length; c++)
        {
            sb.Append(' ').Append(headers[c].PadRight(widths[c])).Append(" |");
        }
        sb.AppendLine();

        sb.Append('|');
        for (int c = 0; c < headers.Length; c++)
        {
            sb.Append(' ').Append(new string('-', widths[c])).Append(" |");
        }
        sb.AppendLine();

        foreach (var row in rows)
        {
            sb.Append('|');
            for (int c = 0; c < headers.Length; c++)
            {
                sb.Append(' ').Append(row[c].PadRight(widths[c])).Append(" |");
            }
            sb.AppendLine();
        }
    }

    private static HashSet<string> OnlinePlayerIds(string? playersRaw)
    {
        var ids = new HashSet<string>();
        var players = ReadArray(playersRaw, "players");
        if (players == null)
        {
            return ids;
        }
        foreach (var p in players.Value.EnumerateArray())
        {
            if (GetBool(p, "isOnline"))
            {
                ids.Add(Str(p, "id"));
            }
        }
        return ids;
    }

    private static List<ModInfo> EnumerateMods()
    {
        var result = new List<ModInfo>();
        if (!Directory.Exists(ModsPath))
        {
            return result;
        }
        // Recurse: SMAPI's bundled mods live under /data/Mods/smapi/<Mod>/, so a non-recursive
        // scan would omit them (mirrors SMAPI's own recursive scan).
        IEnumerable<string> manifests;
        try
        {
            manifests = Directory.EnumerateFiles(
                ModsPath,
                "manifest.json",
                SearchOption.AllDirectories
            );
        }
        catch
        {
            return result;
        }
        foreach (var manifest in manifests)
        {
            try
            {
                // SMAPI tolerates comments/trailing commas in manifest.json, so match it or a
                // mod with either would be silently dropped from the table.
                using var doc = JsonDocument.Parse(File.ReadAllText(manifest), ManifestJsonOptions);
                var root = doc.RootElement;
                result.Add(
                    new ModInfo
                    {
                        Name = Str(root, "Name"),
                        UniqueId = Str(root, "UniqueID"),
                        Version = Str(root, "Version"),
                        Author = Str(root, "Author"),
                    }
                );
            }
            catch
            {
                // Tolerate malformed / partial manifests.
            }
        }
        return result;
    }

    private static string WriteZip(string report)
    {
        Directory.CreateDirectory(OutputDir);
        var timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH-mm-ssZ");
        var zipPath = Path.Combine(OutputDir, $"state-{timestamp}.zip");

        using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);

        var reportEntry = archive.CreateEntry("report.md");
        using (var writer = new StreamWriter(reportEntry.Open()))
        {
            writer.Write(report);
        }

        // SMAPI console typescript (carries early boot output; has ANSI escapes).
        AddFileIfExists(archive, ConsoleLogPath, "server-output.log");

        // SMAPI's canonical structured log (cleaner; what SMAPI's own bug-report guidance asks for).
        AddFileIfExists(archive, $"{ConfigRoot}/ErrorLogs/SMAPI-latest.txt", "SMAPI-latest.txt");

        // Crash log: real path first, then a glob fallback under the same root.
        var crashPath = $"{ConfigRoot}/ErrorLogs/SMAPI-crash.txt";
        if (!File.Exists(crashPath))
        {
            crashPath = FindFirst($"{ConfigRoot}", "SMAPI-crash.txt");
        }
        if (crashPath != null && File.Exists(crashPath))
        {
            AddFileIfExists(archive, crashPath, "SMAPI-crash.txt");
        }

        return zipPath;
    }

    private static void AddFileIfExists(ZipArchive archive, string path, string entryName)
    {
        if (File.Exists(path))
        {
            archive.CreateEntryFromFile(path, entryName);
        }
    }

    private static string? FindFirst(string root, string fileName)
    {
        if (!Directory.Exists(root))
        {
            return null;
        }
        try
        {
            return Directory
                .EnumerateFiles(root, fileName, SearchOption.AllDirectories)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static void PrintDone(string zipPath, bool interactive)
    {
        var hostPath = "./diagnostics/" + Path.GetFileName(zipPath);
        AnsiConsole.WriteLine();

        var lines = new List<IRenderable>
        {
            new Markup($"[green]Diagnostics written to[/] [white]{Markup.Escape(hostPath)}[/]"),
            new Markup("[dim]Attach this file to your support thread or GitHub issue.[/]"),
        };
        if (!interactive)
        {
            lines.Add(
                new Markup(
                    "[yellow]Note:[/] fill in the [white]Technical details to include[/] template inside report.md."
                )
            );
        }
        lines.Add(
            new Markup(
                $"[dim]If ./diagnostics isn't bind-mounted (bare deploy):[/] docker compose cp sdvd-server:{Markup.Escape(zipPath)} ."
            )
        );

        AnsiConsole.Write(
            new Panel(new Rows(lines))
            {
                Header = new PanelHeader(" Done ", Justify.Center),
                Border = BoxBorder.Rounded,
                BorderStyle = new Style(Color.Green),
                Padding = new Padding(2, 1),
            }
        );
    }

    // --- JSON helpers (System.Text.Json — no reflection, so trim-safe) ---

    private static string PrettyJson(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            using var stream = new MemoryStream();
            using (
                var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true })
            )
            {
                doc.RootElement.WriteTo(writer);
            }
            return Encoding.UTF8.GetString(stream.ToArray());
        }
        catch
        {
            return raw;
        }
    }

    private static string? ReadStringField(string? raw, string field)
    {
        if (raw == null)
        {
            return null;
        }
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.TryGetProperty(field, out var value))
            {
                return value.ValueKind == JsonValueKind.String
                    ? value.GetString()
                    : value.ToString();
            }
        }
        catch
        {
            // fall through
        }
        return null;
    }

    private static long? ReadLongField(string? raw, string field)
    {
        if (raw == null)
        {
            return null;
        }
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (
                doc.RootElement.TryGetProperty(field, out var value)
                && value.ValueKind == JsonValueKind.Number
                && value.TryGetInt64(out var n)
            )
            {
                return n;
            }
        }
        catch
        {
            // fall through
        }
        return null;
    }

    /// <summary>Reads a boolean field; treats a missing/non-bool field as false.</summary>
    private static bool ReadBoolField(string? raw, string field)
    {
        if (raw == null)
        {
            return false;
        }
        try
        {
            using var doc = JsonDocument.Parse(raw);
            return doc.RootElement.TryGetProperty(field, out var value)
                && value.ValueKind == JsonValueKind.True;
        }
        catch
        {
            return false;
        }
    }

    private static string Capitalize(string s) =>
        string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);

    /// <summary>
    /// Container uptime = system uptime minus PID 1's age. PID 1 is the base image's init supervisor,
    /// so its start marks the container boot. Uses /proc/uptime (seconds since boot) and /proc/1/stat
    /// field 22 (PID 1 start, in clock ticks since boot). Returns null off Linux or if unreadable.
    /// </summary>
    private static TimeSpan? ContainerUptime()
    {
        try
        {
            var uptimeText = File.ReadAllText("/proc/uptime");
            var seconds = double.Parse(
                uptimeText.Split(' ')[0],
                System.Globalization.CultureInfo.InvariantCulture
            );

            // /proc/1/stat field 22 is the process start time in clock ticks since system boot.
            var stat = File.ReadAllText("/proc/1/stat");
            // Skip the comm field (parenthesized, may contain spaces) before splitting on spaces.
            var afterComm = stat.Substring(stat.LastIndexOf(')') + 1).Trim();
            var fields = afterComm.Split(' ');
            // field 22 overall = index 19 after the comm field (fields 3.. map to index 0..).
            var startTicks = long.Parse(
                fields[19],
                System.Globalization.CultureInfo.InvariantCulture
            );
            var ticksPerSecond = 100.0; // USER_HZ on Linux is 100 on all supported kernels.
            var pid1AgeSeconds = seconds - (startTicks / ticksPerSecond);
            return pid1AgeSeconds >= 0 ? TimeSpan.FromSeconds(pid1AgeSeconds) : null;
        }
        catch
        {
            return null;
        }
    }

    private static (long? free, long? total) DiskUsage(string path)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                return (null, null);
            }
            // A volume may be its own mount (docker bind/volume), so match the drive whose mount
            // point is the longest prefix of the path — not the root filesystem.
            var drive = DriveInfo
                .GetDrives()
                .Where(d =>
                    d.IsReady && path.StartsWith(d.RootDirectory.FullName, StringComparison.Ordinal)
                )
                .OrderByDescending(d => d.RootDirectory.FullName.Length)
                .FirstOrDefault();
            if (drive == null)
            {
                return (null, null);
            }
            // AvailableFreeSpace (not TotalFreeSpace) is the headroom actually usable by the app —
            // it excludes root-reserved blocks, so it's the honest "how much room is left" number.
            return (drive.AvailableFreeSpace, drive.TotalSize);
        }
        catch
        {
            return (null, null);
        }
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:0.#} {units[unit]}";
    }

    private static string FormatDuration(TimeSpan span)
    {
        if (span.TotalDays >= 1)
        {
            return $"{(int)span.TotalDays}d {span.Hours}h {span.Minutes}m";
        }
        if (span.TotalHours >= 1)
        {
            return $"{(int)span.TotalHours}h {span.Minutes}m";
        }
        return $"{(int)span.TotalMinutes}m {span.Seconds}s";
    }

    private static JsonElement? ReadArray(string? raw, string field)
    {
        if (raw == null)
        {
            return null;
        }
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (
                doc.RootElement.TryGetProperty(field, out var value)
                && value.ValueKind == JsonValueKind.Array
            )
            {
                return value.Clone();
            }
        }
        catch
        {
            // fall through
        }
        return null;
    }

    private static string Str(JsonElement element, string field)
    {
        if (
            element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(field, out var value)
        )
        {
            return value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? ""
                : value.ToString();
        }
        return "";
    }

    private static bool GetBool(JsonElement element, string field) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(field, out var value)
        && value.ValueKind == JsonValueKind.True;

    private static string Bool(bool value) => value ? "yes" : "no";

    private static string Blank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "_(blank)_" : value;

    private sealed class ModInfo
    {
        public string Name { get; set; } = "";
        public string UniqueId { get; set; } = "";
        public string Version { get; set; } = "";
        public string Author { get; set; } = "";
    }

    private sealed class ReportedDetails
    {
        public string? ClientMods { get; set; }
        public string? ClientModList { get; set; }
        public string? AffectedPlayer { get; set; }
        public string? Reproducibility { get; set; }
        public string? StartedAfterChange { get; set; }
    }
}
