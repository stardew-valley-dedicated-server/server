/*
 * Steam Service - Authentication & Game Download
 *
 * A unified tool for Steam authentication and game management.
 * Keeps credentials and refresh tokens isolated from the game container.
 * Supports multiple Steam accounts for multi-container setups (server + client).
 *
 * Commands:
 *   setup        - Interactive login + download game (first-time setup)
 *   login        - Interactive login, saves session
 *   download     - Download/update game depot (uses saved session or token)
 *   ticket       - Output encrypted app ticket to stdout
 *   export-token - Output saved refresh token for CI use
 *   serve        - Run HTTP API for runtime ticket requests (default)
 *
 * Environment Variables (JSON format - preferred for multi-account):
 *   STEAM_ACCOUNTS        - JSON array: [{"user":"...","pass":"...","token":"..."},...]
 *                           Each entry needs "user" + either "pass" or "token".
 *
 * Environment Variables (production format - single account, mapped to account 0):
 *   STEAM_USERNAME        - Username (fallback if STEAM_ACCOUNTS not set)
 *   STEAM_PASSWORD        - Password (fallback if STEAM_ACCOUNTS not set)
 *   STEAM_REFRESH_TOKEN   - Refresh token (fallback if STEAM_ACCOUNTS not set)
 *
 * HTTP API Query Parameters:
 *   ?account=N            - Use account N for the request (default: 0)
 *
 * Usage:
 *   docker compose run -it steam-auth setup    # First time (interactive)
 *   docker compose up -d                        # Normal operation
 */

using System.Text.Json;
using SteamService;

// Configuration
var sessionDir = Environment.GetEnvironmentVariable("SESSION_DIR") ?? "/data/steam-session";
var gameDir = Environment.GetEnvironmentVariable("GAME_DIR") ?? "/data/game";
var port = int.Parse(Environment.GetEnvironmentVariable("PORT") ?? "3001");

const uint StardewValleyAppId = 413150;
const uint SteamworksSdkAppId = 1007; // Steamworks SDK Redistributable (steamclient.so)

// Parse command
var command = args.Length > 0 ? args[0].ToLower() : "serve";

string? GetEnvTrimmed(string name)
{
    var value = Environment.GetEnvironmentVariable(name)?.Trim();
    return string.IsNullOrEmpty(value) ? null : value;
}

// STEAM_KEEP_LANGUAGES: comma-separated language codes (e.g. "pt-BR,ru-RU") whose
// fonts/content to keep in the download. Default (unset) strips all localized content
// for the smallest image; the server itself runs English-only. An unknown code is
// warned-and-ignored (not fatal) so a typo can't abort the whole download.
var keepLanguages = SteamAuthService.ParseKeepLanguages(GetEnvTrimmed("STEAM_KEEP_LANGUAGES"));
if (keepLanguages.Count > 0)
{
    Logger.Log($"[SteamService] Keeping localized content for: {string.Join(", ", keepLanguages)}");
}

// ============================================================================
// Account Discovery
// ============================================================================

/// <summary>
/// Discovers all configured Steam accounts from environment variables.
/// Tries STEAM_ACCOUNTS (JSON) first, falls back to STEAM_USERNAME/PASSWORD for account 0.
/// Returns a dictionary of account index -> (user, pass, token).
/// </summary>
Dictionary<int, (string user, string? pass, string? token)> DiscoverAccounts()
{
    var result = new Dictionary<int, (string user, string? pass, string? token)>();

    // JSON format: STEAM_ACCOUNTS='[{"user":"...","pass":"...","token":"..."}]'
    var json = GetEnvTrimmed("STEAM_ACCOUNTS");
    if (json != null)
    {
        var entries = JsonSerializer.Deserialize<List<SteamAccountConfig>>(
            json,
            new JsonSerializerOptions
            {
                // STEAM_ACCOUNTS is hand-edited in .env / docker-compose env files.
                // Tolerate trailing commas and // comments — same defaults as appsettings.json.
                AllowTrailingCommas = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
            }
        );
        if (entries != null)
        {
            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                if (string.IsNullOrEmpty(e.user))
                {
                    Logger.Log($"[SteamService] ERROR: STEAM_ACCOUNTS[{i}] missing 'user'");
                    Environment.Exit(1);
                }
                if (string.IsNullOrEmpty(e.pass) && string.IsNullOrEmpty(e.token))
                {
                    Logger.Log(
                        $"[SteamService] ERROR: STEAM_ACCOUNTS[{i}] needs 'pass' or 'token'"
                    );
                    Environment.Exit(1);
                }
                result[i] = (e.user, e.pass, e.token);
            }
        }

        // Uniqueness check
        var usernames = result.Values.Select(a => a.user).ToList();
        if (usernames.Distinct(StringComparer.OrdinalIgnoreCase).Count() != usernames.Count)
        {
            Logger.Log("[SteamService] ERROR: Duplicate Steam usernames in STEAM_ACCOUNTS");
            Environment.Exit(1);
        }

        return result;
    }

    // Production fallback: single account from STEAM_USERNAME/PASSWORD
    var user = GetEnvTrimmed("STEAM_USERNAME");
    var pass = GetEnvTrimmed("STEAM_PASSWORD");
    var token = GetEnvTrimmed("STEAM_REFRESH_TOKEN");
    if (user != null)
    {
        result[0] = (user, pass, token);
    }

    return result;
}

/// <summary>
/// Creates SteamAuthService instances for all discovered accounts.
/// </summary>
Dictionary<int, SteamAuthService> CreateAccountServices(
    Dictionary<int, (string user, string? pass, string? token)> accountConfigs
)
{
    var services = new Dictionary<int, SteamAuthService>();
    foreach (var (index, config) in accountConfigs)
    {
        services[index] = new SteamAuthService(
            index,
            config.user,
            sessionDir,
            gameDir,
            keepLanguages
        );
    }
    return services;
}

/// <summary>
/// Logs into an account using the full priority chain (token → saved session →
/// credentials). Delegates to SteamAuthService.EnsureLoggedInAsync so all login work
/// is serialized per-account via the login semaphore.
/// </summary>
async Task LoginAccountAsync(
    SteamAuthService svc,
    (string user, string? pass, string? token) config
)
{
    var loginConfig = new SteamAuthService.LoginConfig(config.user, config.pass, config.token);
    try
    {
        await svc.EnsureLoggedInAsync(loginConfig);
    }
    catch (InvalidOperationException)
    {
        // No auth method configured — match prior log message for operators.
        Logger.Log(
            $"[SteamService] A{svc.AccountIndex}: No authentication method for {config.user}"
        );
        Logger.Log($"[SteamService] Provide credentials via STEAM_ACCOUNTS JSON or run 'setup'");
    }
}

// ============================================================================
// Discover accounts early (needed for all commands)
// ============================================================================

var accountConfigs = DiscoverAccounts();
var accounts = CreateAccountServices(accountConfigs);

// ============================================================================
// Command dispatch
// ============================================================================

switch (command)
{
    case "healthcheck":
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var response = await http.GetAsync($"http://localhost:{port}/health");
            Environment.Exit(response.IsSuccessStatusCode ? 0 : 1);
        }
        catch
        {
            Environment.Exit(1);
        }
        break;

    case "setup":
        if (accounts.Count == 0)
        {
            // No accounts configured; run interactive setup for account 0
            Logger.Log("[SteamService] No accounts configured, running interactive setup...");
            var svc = new SteamAuthService(0, "setup", sessionDir, gameDir);
            await svc.LoginInteractiveAsync();
            if (svc.IsLoggedIn)
            {
                await DownloadAllAsync(svc);
            }
            svc.Disconnect();
        }
        else
        {
            // Authenticate ALL configured accounts (each may need Steam Guard on first run)
            Logger.Log($"[SteamService] Setting up {accounts.Count} account(s)...");
            SteamAuthService? downloadAccount = null;
            foreach (var (idx, svc) in accounts.OrderBy(kv => kv.Key))
            {
                Logger.Log($"[SteamService] --- Account {idx}: {svc.Username} ---");
                if (svc.HasSavedSession())
                {
                    Logger.Log($"[SteamService] Account {idx}: Found saved session, logging in...");
                    await svc.EnsureLoggedInAsync(
                        new SteamAuthService.LoginConfig(svc.Username, null, null)
                    );
                }
                else
                {
                    Logger.Log(
                        $"[SteamService] Account {idx}: No saved session, starting interactive login..."
                    );
                    await svc.LoginInteractiveAsync();
                }

                if (svc.IsLoggedIn)
                {
                    Logger.Log($"[SteamService] Account {idx}: Logged in as {svc.SteamId}");
                    if (downloadAccount == null)
                    {
                        downloadAccount = svc;
                    }
                }
                else
                {
                    Logger.Log($"[SteamService] Account {idx}: Login failed");
                }
            }

            // Download game files using the first successfully logged-in account
            if (downloadAccount != null)
            {
                await DownloadAllAsync(downloadAccount);
            }
            else
            {
                Logger.Log("[SteamService] No accounts logged in, skipping game download");
            }
        }
        break;

    case "login":
        if (accounts.Count == 0)
        {
            Logger.Log("[SteamService] No accounts configured, running interactive login...");
            var svc = new SteamAuthService(0, "login", sessionDir, gameDir);
            await svc.LoginInteractiveAsync();
            svc.Disconnect();
        }
        else
        {
            Logger.Log($"[SteamService] Logging in {accounts.Count} account(s)...");
            foreach (var (idx, svc) in accounts.OrderBy(kv => kv.Key))
            {
                Logger.Log($"[SteamService] --- Account {idx}: {svc.Username} ---");
                if (svc.HasSavedSession())
                {
                    Logger.Log($"[SteamService] Account {idx}: Found saved session, logging in...");
                    await svc.EnsureLoggedInAsync(
                        new SteamAuthService.LoginConfig(svc.Username, null, null)
                    );
                }
                else
                {
                    Logger.Log(
                        $"[SteamService] Account {idx}: No saved session, starting interactive login..."
                    );
                    await svc.LoginInteractiveAsync();
                }

                if (svc.IsLoggedIn)
                {
                    Logger.Log($"[SteamService] Account {idx}: Logged in as {svc.SteamId}");
                }
                else
                {
                    Logger.Log($"[SteamService] Account {idx}: Login failed");
                }
            }
        }
        break;

    case "download":
        if (accounts.TryGetValue(0, out var dlSvc))
        {
            await LoginAccountAsync(dlSvc, accountConfigs[0]);
            await DownloadAllAsync(dlSvc);
        }
        else
        {
            Logger.Log("[SteamService] No account configured for download");
            Environment.Exit(1);
        }
        break;

    case "ticket":
        if (accounts.TryGetValue(0, out var ticketSvc))
        {
            await LoginAccountAsync(ticketSvc, accountConfigs[0]);
            var ticket = await ticketSvc.GetAppTicketAsync(StardewValleyAppId);
            Console.WriteLine(ticket.TicketBase64);
        }
        else
        {
            Logger.Log("[SteamService] No account configured");
            Environment.Exit(1);
        }
        break;

    case "export-token":
        var anyExported = false;
        foreach (var (idx, svc) in accounts.OrderBy(kv => kv.Key))
        {
            var session = svc.GetSavedSession();
            if (session == null)
            {
                continue;
            }

            anyExported = true;
            var exportJson = JsonSerializer.Serialize(
                new
                {
                    accountIndex = idx,
                    username = session.Value.username,
                    refreshToken = session.Value.refreshToken,
                },
                new JsonSerializerOptions { WriteIndented = true }
            );
            Console.WriteLine(exportJson);
        }
        if (!anyExported)
        {
            Console.Error.WriteLine("No saved sessions found. Run 'login' or 'setup' first.");
            Environment.Exit(1);
        }
        break;

    case "serve":
        await RunHttpServerAsync(accounts, accountConfigs, port);
        break;

    default:
        Console.WriteLine("Usage: steam-service <command>");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine("  setup        Interactive login + download game");
        Console.WriteLine("  login        Interactive login, saves session");
        Console.WriteLine("  download     Download/update game depot");
        Console.WriteLine("  ticket       Output encrypted app ticket to stdout");
        Console.WriteLine("  export-token Export saved refresh tokens (for CI)");
        Console.WriteLine("  serve        Run HTTP API (default)");
        Environment.Exit(1);
        return;
}

// Disconnect all accounts
foreach (var svc in accounts.Values)
{
    svc.Disconnect();
}

return;

// ============================================================================
// Helper functions
// ============================================================================

async Task DownloadAllAsync(SteamAuthService svc)
{
    try
    {
        await svc.DownloadGameAsync(StardewValleyAppId);

        // Also download Steamworks SDK for GameServer mode (unless --skip-sdk)
        if (!args.Contains("--skip-sdk"))
        {
            var steamSdkDir = Path.Combine(gameDir, ".steam-sdk");
            await svc.DownloadGameAsync(SteamworksSdkAppId, steamSdkDir);
        }
    }
    catch (Exception ex)
    {
        // Convert an exhausted-retry crash (e.g. all CDN servers unreachable) into a clean
        // single-line failure + non-zero exit the build/CI sees, instead of a raw stack dump.
        Logger.Log($"[SteamService] Game download failed: {ex.Message}");
        Environment.Exit(1);
    }
}

// ============================================================================
// HTTP Server for runtime (multi-account)
// ============================================================================

async Task RunHttpServerAsync(
    Dictionary<int, SteamAuthService> accts,
    Dictionary<int, (string user, string? pass, string? token)> configs,
    int httpPort
)
{
    var builder = WebApplication.CreateBuilder();
    builder.WebHost.ConfigureKestrel(opts => opts.ListenAnyIP(httpPort));
    builder.Logging.ClearProviders();

    var app = builder.Build();

    // Correlation middleware: reads the inbound X-Request-Id header, binds it
    // to SidecarRequestContext for the request duration, and echoes it on
    // the response. Events emitted inside the pipeline (Logger.LogEvent)
    // carry this id so the test harness can stitch sidecar events to the
    // triggering mod request. Missing/blank headers leave Current == null.
    app.Use(
        async (ctx, next) =>
        {
            string? requestId = ctx.Request.Headers["X-Request-Id"].FirstOrDefault();
            if (!string.IsNullOrEmpty(requestId))
            {
                ctx.Response.Headers["X-Request-Id"] = requestId;
            }

            using var _scope = SidecarRequestContext.Begin(requestId);
            await next();
        }
    );

    // Helper: resolve account from ?account=N query param (default: 0)
    SteamAuthService GetAccount(HttpContext ctx)
    {
        var idx = int.TryParse(ctx.Request.Query["account"].FirstOrDefault(), out var n) ? n : 0;
        if (!accts.TryGetValue(idx, out var svc))
        {
            throw new KeyNotFoundException($"Account {idx} not configured");
        }

        return svc;
    }

    // Helper: ensure account is logged in (lazy login). Delegates to
    // SteamAuthService.EnsureLoggedInAsync which serializes concurrent callers via the
    // per-account login semaphore and tries the full priority chain (token → saved
    // session → credentials). Throws if no auth method is available; callers' try/catch
    // converts that to 503.
    async Task<SteamAuthService> EnsureAccountReadyAsync(HttpContext ctx)
    {
        var svc = GetAccount(ctx);
        var cfg = configs.TryGetValue(svc.AccountIndex, out var c)
            ? new SteamAuthService.LoginConfig(c.user, c.pass, c.token)
            : null;
        await svc.EnsureLoggedInAsync(cfg, ctx.RequestAborted);
        return svc;
    }

    // /health is a pure status probe: returns 200 whenever the HTTP server is up so
    // Docker healthchecks and Testcontainers wait strategies work. It does NOT trigger
    // logins -- that used to race with real callers hitting /steam/ready. Consumers
    // that care about per-account login state read the `logged_in` body fields.
    app.MapGet(
        "/health",
        (HttpContext ctx) =>
        {
            var accountQuery = ctx.Request.Query["account"].FirstOrDefault();
            var specificAccount = int.TryParse(accountQuery, out var requestedIdx);

            var accountsToCheck =
                specificAccount && accts.TryGetValue(requestedIdx, out var single)
                    ? new[] { single }
                    : accts.Values.ToArray();

            var loggedIn = accountsToCheck.All(s => s.IsLoggedIn);
            var accountList = accountsToCheck
                .OrderBy(s => s.AccountIndex)
                .Select(s => new
                {
                    index = s.AccountIndex,
                    username = s.Username,
                    logged_in = s.IsLoggedIn,
                    steam_id = s.SteamId,
                });

            return Results.Json(
                new
                {
                    status = "ok",
                    logged_in = loggedIn,
                    timestamp = DateTime.UtcNow.ToString("o"),
                    accounts = accountList,
                }
            );
        }
    );

    app.MapGet(
        "/steam/ready",
        async (HttpContext ctx) =>
        {
            try
            {
                var svc = await EnsureAccountReadyAsync(ctx);

                // Try fetching a ticket to prove full readiness
                var ticket = await svc.GetAppTicketAsync(StardewValleyAppId);

                return Results.Json(
                    new
                    {
                        ready = true,
                        account = svc.AccountIndex,
                        username = svc.Username,
                        steam_id = svc.SteamId,
                        has_ticket = !string.IsNullOrEmpty(ticket.TicketBase64),
                    }
                );
            }
            catch (Exception ex)
            {
                Logger.Log($"[HTTP] Ready check failed: {ex.Message}");
                return Results.Json(new { ready = false, error = ex.Message }, statusCode: 503);
            }
        }
    );

    app.MapGet(
        "/steam/app-ticket",
        async (HttpContext ctx) =>
        {
            try
            {
                var svc = await EnsureAccountReadyAsync(ctx);

                var ticket = await svc.GetAppTicketAsync(StardewValleyAppId);
                return Results.Json(
                    new
                    {
                        app_ticket = ticket.TicketBase64,
                        steam_id = svc.SteamId,
                        source = ticket.Source,
                        sha8 = ticket.Sha8,
                        ticket_length_bytes = ticket.TicketLengthBytes,
                    }
                );
            }
            catch (Exception ex)
            {
                Logger.Log($"[HTTP] Error getting ticket: {ex.Message}");
                return Results.Json(new { error = ex.Message }, statusCode: 500);
            }
        }
    );

    app.MapGet(
        "/steam/refresh-token",
        (HttpContext ctx) =>
        {
            try
            {
                var svc = GetAccount(ctx);
                var session = svc.GetSavedSession();
                if (session == null)
                {
                    return Results.Json(
                        new { error = "No session. Run setup first." },
                        statusCode: 503
                    );
                }

                return Results.Json(
                    new
                    {
                        username = session.Value.username,
                        refresh_token = session.Value.refreshToken,
                    }
                );
            }
            catch (Exception ex)
            {
                Logger.Log($"[HTTP] Error getting refresh token: {ex.Message}");
                return Results.Json(new { error = ex.Message }, statusCode: 500);
            }
        }
    );

    // ========================================================================
    // Lobby Management Endpoints (always use account from ?account=N, default 0)
    // ========================================================================

    app.MapPost(
        "/steam/lobby/create",
        async (HttpContext ctx) =>
        {
            try
            {
                var svc = await EnsureAccountReadyAsync(ctx);

                var body = await ctx.Request.ReadFromJsonAsync<CreateLobbyRequest>();
                if (body == null)
                {
                    return Results.Json(new { error = "Invalid request body" }, statusCode: 400);
                }

                var lobbyId = await svc.CreateLobbyAsync(
                    appId: StardewValleyAppId,
                    maxMembers: body.max_members,
                    gameServerSteamId: body.game_server_steam_id,
                    protocolVersion: body.protocol_version
                );

                return Results.Json(
                    new { lobby_id = lobbyId.ToString(), app_id = StardewValleyAppId }
                );
            }
            catch (Exception ex)
            {
                Logger.Log($"[HTTP] Error creating lobby: {ex.Message}");
                return Results.Json(new { error = ex.Message }, statusCode: 500);
            }
        }
    );

    app.MapPost(
        "/steam/lobby/set-data",
        async (HttpContext ctx) =>
        {
            try
            {
                var svc = await EnsureAccountReadyAsync(ctx);

                var body = await ctx.Request.ReadFromJsonAsync<SetLobbyDataRequest>();
                if (body == null)
                {
                    return Results.Json(new { error = "Invalid request body" }, statusCode: 400);
                }

                await svc.SetLobbyDataAsync(
                    appId: StardewValleyAppId,
                    lobbyId: body.lobby_id,
                    additionalMetadata: body.metadata
                );

                return Results.Json(new { success = true });
            }
            catch (Exception ex)
            {
                Logger.Log($"[HTTP] Error setting lobby data: {ex.Message}");
                return Results.Json(new { error = ex.Message }, statusCode: 500);
            }
        }
    );

    app.MapPost(
        "/steam/lobby/set-privacy",
        async (HttpContext ctx) =>
        {
            try
            {
                var svc = await EnsureAccountReadyAsync(ctx);

                var body = await ctx.Request.ReadFromJsonAsync<SetLobbyPrivacyRequest>();
                if (body == null)
                {
                    return Results.Json(new { error = "Invalid request body" }, statusCode: 400);
                }

                var lobbyType = body.privacy?.ToLower() switch
                {
                    "private" => SteamKit2.ELobbyType.Private,
                    "friendsonly" => SteamKit2.ELobbyType.FriendsOnly,
                    "invisible" => SteamKit2.ELobbyType.Invisible,
                    _ => SteamKit2.ELobbyType.Public,
                };

                await svc.SetLobbyPrivacyAsync(
                    appId: StardewValleyAppId,
                    lobbyId: body.lobby_id,
                    lobbyType: lobbyType
                );

                return Results.Json(new { success = true });
            }
            catch (Exception ex)
            {
                Logger.Log($"[HTTP] Error setting lobby privacy: {ex.Message}");
                return Results.Json(new { error = ex.Message }, statusCode: 500);
            }
        }
    );

    app.MapGet(
        "/steam/lobby/status",
        (HttpContext ctx) =>
        {
            var svc = GetAccount(ctx);
            return Results.Json(
                new
                {
                    lobby_id = svc.CurrentLobbyId == 0 ? null : svc.CurrentLobbyId.ToString(),
                    is_logged_in = svc.IsLoggedIn,
                }
            );
        }
    );

    Logger.Log($"[SteamService] HTTP API listening on port {httpPort}");
    Logger.Log($"[SteamService] {accts.Count} account(s) configured");
    Console.WriteLine("[SteamService] Endpoints:");
    Console.WriteLine("  GET  /health              - Health check (all accounts)");
    Console.WriteLine("  GET  /steam/ready         - Readiness check (?account=N)");
    Console.WriteLine("  GET  /steam/app-ticket    - Get encrypted app ticket (?account=N)");
    Console.WriteLine("  GET  /steam/refresh-token - Get refresh token (?account=N)");
    Console.WriteLine("  POST /steam/lobby/create  - Create lobby (?account=N)");

    // Auto-login all configured accounts in parallel
    var loginTasks = accts.Select(async kv =>
    {
        var svc = kv.Value;
        try
        {
            if (configs.TryGetValue(kv.Key, out var cfg))
            {
                await LoginAccountAsync(svc, cfg);
                if (svc.IsLoggedIn)
                {
                    Logger.Log($"[SteamService] A{svc.AccountIndex}: Logged in as {svc.SteamId}");
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"[SteamService] A{svc.AccountIndex}: Auto-login failed: {ex.Message}");
        }
    });

    await Task.WhenAll(loginTasks);

    await app.RunAsync();
}

// ============================================================================
// Request DTOs for Lobby Endpoints
// ============================================================================

record CreateLobbyRequest(ulong game_server_steam_id, string protocol_version, int max_members);

record SetLobbyDataRequest(ulong lobby_id, Dictionary<string, string>? metadata = null);

record SetLobbyPrivacyRequest(ulong lobby_id, string? privacy = "public");

// ============================================================================
// Account Discovery DTO
// ============================================================================

record SteamAccountConfig(string user, string? pass = null, string? token = null);
