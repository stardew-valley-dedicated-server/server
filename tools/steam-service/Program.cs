/**
 * Steam Service - Authentication & Game Download
 *
 * A unified tool for Steam authentication and game management.
 * Keeps credentials and refresh tokens isolated from the game container.
 *
 * Commands:
 *   setup        - Interactive login + download game (first-time setup)
 *   login        - Interactive login, saves session
 *   download     - Download/update game depot (uses saved session or STEAM_REFRESH_TOKEN)
 *   ticket       - Output encrypted app ticket to stdout
 *   export-token - Output saved refresh token for CI use
 *   serve        - Run HTTP API for runtime ticket requests (default)
 *
 * Environment Variables:
 *   STEAM_REFRESH_TOKEN - Use this token instead of saved session (for CI)
 *   STEAM_USERNAME      - Username for token or credential auth
 *   STEAM_PASSWORD          - Password for credential-based auth
 *   FORCE_REDOWNLOAD    - Set to "1" to re-download all files
 *
 * Usage:
 *   docker compose run -it steam-auth setup    # First time (interactive)
 *   docker compose up -d                        # Normal operation
 *
 * CI Usage:
 *   # Export token after local setup
 *   docker compose run steam-auth export-token > token.json
 *
 *   # Use in CI build (with secret)
 *   STEAM_REFRESH_TOKEN=xxx STEAM_USERNAME=user steam-service download
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

var steamService = new SteamAuthService(sessionDir, gameDir);

// Check for token from environment (CI mode)
// Use helper that treats whitespace-only as null (for optional GitHub secrets)
var envToken = GetEnvTrimmed("STEAM_REFRESH_TOKEN");
var envUsername = GetEnvTrimmed("STEAM_USERNAME");

string? GetEnvTrimmed(string name)
{
    var value = Environment.GetEnvironmentVariable(name)?.Trim();
    return string.IsNullOrEmpty(value) ? null : value;
}

switch (command)
{
    case "healthcheck":
        // Simple HTTP healthcheck (for Docker HEALTHCHECK, no curl needed)
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
        await steamService.LoginInteractiveAsync();
        if (steamService.IsLoggedIn)
        {
            await DownloadAllAsync();
        }
        break;

    case "login":
        await steamService.LoginInteractiveAsync();
        break;

    case "download":
        // Support: refresh token, saved session, or username+password
        var envPass = GetEnvTrimmed("STEAM_PASSWORD");
        if (envToken != null && envUsername != null)
        {
            Logger.Log("[SteamService] Using STEAM_REFRESH_TOKEN from environment (CI mode)");
            await steamService.LoginWithTokenAsync(envUsername, envToken);
        }
        else if (steamService.HasSavedSession())
        {
            await steamService.LoginWithSavedSessionAsync();
        }
        else if (envUsername != null && envPass != null)
        {
            Logger.Log("[SteamService] Using STEAM_USERNAME + STEAM_PASSWORD from environment");
            await steamService.LoginWithCredentialsAsync(envUsername, envPass);
        }
        else
        {
            Logger.Log("[SteamService] No authentication method available. Provide one of:");
            Logger.Log("[SteamService]   - STEAM_REFRESH_TOKEN + STEAM_USERNAME");
            Logger.Log("[SteamService]   - STEAM_USERNAME + STEAM_PASSWORD");
            Logger.Log("[SteamService]   - Or run 'login' first to save a session");
            Environment.Exit(1);
        }
        await DownloadAllAsync();
        break;

    case "ticket":
        var envPassTicket = GetEnvTrimmed("STEAM_PASSWORD");
        if (envToken != null && envUsername != null)
        {
            await steamService.LoginWithTokenAsync(envUsername, envToken);
        }
        else if (steamService.HasSavedSession())
        {
            await steamService.LoginWithSavedSessionAsync();
        }
        else if (envUsername != null && envPassTicket != null)
        {
            Logger.Log("[SteamService] Using STEAM_USERNAME + STEAM_PASSWORD from environment");
            await steamService.LoginWithCredentialsAsync(envUsername, envPassTicket);
        }
        else
        {
            Logger.Log("[SteamService] No authentication method available. Provide one of:");
            Logger.Log("[SteamService]   - STEAM_REFRESH_TOKEN + STEAM_USERNAME");
            Logger.Log("[SteamService]   - STEAM_USERNAME + STEAM_PASSWORD");
            Logger.Log("[SteamService]   - Or run 'login' first to save a session");
            Environment.Exit(1);
        }
        var ticket = await steamService.GetAppTicketAsync(StardewValleyAppId);
        Console.WriteLine(ticket); // Just the base64 ticket to stdout
        break;

    case "export-token":
        var session = steamService.GetSavedSession();
        if (session == null)
        {
            Console.Error.WriteLine("No saved session found. Run 'login' or 'setup' first.");
            Environment.Exit(1);
        }
        // Output as JSON for easy parsing in CI
        var exportJson = JsonSerializer.Serialize(new
        {
            username = session.Value.username,
            refreshToken = session.Value.refreshToken
        }, new JsonSerializerOptions { WriteIndented = true });
        Console.WriteLine(exportJson);
        break;

    case "serve":
        await RunHttpServerAsync(steamService, port);
        break;

    default:
        Console.WriteLine("Usage: steam-service <command>");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine("  setup        Interactive login + download game");
        Console.WriteLine("  login        Interactive login, saves session");
        Console.WriteLine("  download     Download/update game depot");
        Console.WriteLine("  ticket       Output encrypted app ticket to stdout");
        Console.WriteLine("  export-token Export saved refresh token (for CI)");
        Console.WriteLine("  serve        Run HTTP API (default)");
        Console.WriteLine();
        Console.WriteLine("Environment Variables:");
        Console.WriteLine("  STEAM_REFRESH_TOKEN  Use token instead of saved session");
        Console.WriteLine("  STEAM_USERNAME       Username for the token or credentials");
        Console.WriteLine("  STEAM_PASSWORD           Password for credential-based auth");
        Console.WriteLine("  FORCE_REDOWNLOAD     Set to '1' to force re-download");
        Environment.Exit(1);
        return; // Required by compiler, but Exit never returns
}

steamService.Disconnect();
return;

// ============================================================================
// Helper functions
// ============================================================================

async Task DownloadAllAsync()
{
    await steamService.DownloadGameAsync(StardewValleyAppId);

    // Also download Steamworks SDK for GameServer mode (unless --skip-sdk)
    // This provides steamclient.so needed for SteamGameServerNetworkingSockets
    // Downloaded to .steam-sdk subfolder; server container symlinks to /root/.steam/sdk64/
    if (!args.Contains("--skip-sdk"))
    {
        var steamSdkDir = Path.Combine(gameDir, ".steam-sdk");
        await steamService.DownloadGameAsync(SteamworksSdkAppId, steamSdkDir);
    }
}

// ============================================================================
// HTTP Server for runtime
// ============================================================================

async Task RunHttpServerAsync(SteamAuthService service, int httpPort)
{
    var builder = WebApplication.CreateBuilder();
    builder.WebHost.ConfigureKestrel(opts => opts.ListenAnyIP(httpPort));
    builder.Logging.ClearProviders();

    var app = builder.Build();

    app.MapGet("/health", async () =>
    {
        // Lazy login: if not logged in but session exists, try to login.
        // This handles the case where setup was run in a separate container
        // via "make setup".
        if (!service.IsLoggedIn && service.HasSavedSession())
        {
            try
            {
                Logger.Log("[SteamService] Session found, attempting login...");
                await service.LoginWithSavedSessionAsync();
                Logger.Log("[SteamService] Login successful");
            }
            catch (Exception ex)
            {
                Logger.Log($"[SteamService] Login attempt failed: {ex.Message}");
            }
        }

        return Results.Json(new
        {
            status = "ok",
            logged_in = service.IsLoggedIn,
            timestamp = DateTime.UtcNow.ToString("o")
        });
    });

    app.MapGet("/steam/app-ticket", async () =>
    {
        try
        {
            if (!service.IsLoggedIn)
            {
                if (!service.HasSavedSession())
                {
                    return Results.Json(new { error = "No session. Run setup first." }, statusCode: 503);
                }
                await service.LoginWithSavedSessionAsync();
            }

            var ticketBase64 = await service.GetAppTicketAsync(StardewValleyAppId);
            return Results.Json(new
            {
                app_ticket = ticketBase64,
                steam_id = service.SteamId
            });
        }
        catch (Exception ex)
        {
            Logger.Log($"[HTTP] Error getting ticket: {ex.Message}");
            return Results.Json(new { error = ex.Message }, statusCode: 500);
        }
    });

    // Endpoint for SteamKit2 lobby creation - returns refresh token for login
    app.MapGet("/steam/refresh-token", () =>
    {
        try
        {
            var session = service.GetSavedSession();
            if (session == null)
            {
                return Results.Json(new { error = "No session. Run setup first." }, statusCode: 503);
            }

            return Results.Json(new
            {
                username = session.Value.username,
                refresh_token = session.Value.refreshToken
            });
        }
        catch (Exception ex)
        {
            Logger.Log($"[HTTP] Error getting refresh token: {ex.Message}");
            return Results.Json(new { error = ex.Message }, statusCode: 500);
        }
    });

    // ========================================================================
    // Lobby Management Endpoints
    // Uses the same SteamClient session as auth - no separate login needed
    // ========================================================================

    app.MapPost("/steam/lobby/create", async (HttpContext ctx) =>
    {
        try
        {
            if (!service.IsLoggedIn)
            {
                if (!service.HasSavedSession())
                    return Results.Json(new { error = "No session. Run setup first." }, statusCode: 503);
                await service.LoginWithSavedSessionAsync();
            }

            var body = await ctx.Request.ReadFromJsonAsync<CreateLobbyRequest>();
            if (body == null)
                return Results.Json(new { error = "Invalid request body" }, statusCode: 400);

            var lobbyId = await service.CreateLobbyAsync(
                appId: StardewValleyAppId,
                maxMembers: body.max_members,
                gameServerSteamId: body.game_server_steam_id,
                protocolVersion: body.protocol_version);

            return Results.Json(new
            {
                lobby_id = lobbyId.ToString(),
                app_id = StardewValleyAppId
            });
        }
        catch (Exception ex)
        {
            Logger.Log($"[HTTP] Error creating lobby: {ex.Message}");
            return Results.Json(new { error = ex.Message }, statusCode: 500);
        }
    });

    app.MapPost("/steam/lobby/set-data", async (HttpContext ctx) =>
    {
        try
        {
            if (!service.IsLoggedIn)
            {
                if (!service.HasSavedSession())
                    return Results.Json(new { error = "No session. Run setup first." }, statusCode: 503);
                await service.LoginWithSavedSessionAsync();
            }

            var body = await ctx.Request.ReadFromJsonAsync<SetLobbyDataRequest>();
            if (body == null)
                return Results.Json(new { error = "Invalid request body" }, statusCode: 400);

            await service.SetLobbyDataAsync(
                appId: StardewValleyAppId,
                lobbyId: body.lobby_id,
                additionalMetadata: body.metadata);

            return Results.Json(new { success = true });
        }
        catch (Exception ex)
        {
            Logger.Log($"[HTTP] Error setting lobby data: {ex.Message}");
            return Results.Json(new { error = ex.Message }, statusCode: 500);
        }
    });

    app.MapPost("/steam/lobby/set-privacy", async (HttpContext ctx) =>
    {
        try
        {
            if (!service.IsLoggedIn)
            {
                if (!service.HasSavedSession())
                    return Results.Json(new { error = "No session. Run setup first." }, statusCode: 503);
                await service.LoginWithSavedSessionAsync();
            }

            var body = await ctx.Request.ReadFromJsonAsync<SetLobbyPrivacyRequest>();
            if (body == null)
                return Results.Json(new { error = "Invalid request body" }, statusCode: 400);

            var lobbyType = body.privacy?.ToLower() switch
            {
                "private" => SteamKit2.ELobbyType.Private,
                "friendsonly" => SteamKit2.ELobbyType.FriendsOnly,
                "invisible" => SteamKit2.ELobbyType.Invisible,
                _ => SteamKit2.ELobbyType.Public
            };

            await service.SetLobbyPrivacyAsync(
                appId: StardewValleyAppId,
                lobbyId: body.lobby_id,
                lobbyType: lobbyType);

            return Results.Json(new { success = true });
        }
        catch (Exception ex)
        {
            Logger.Log($"[HTTP] Error setting lobby privacy: {ex.Message}");
            return Results.Json(new { error = ex.Message }, statusCode: 500);
        }
    });

    app.MapGet("/steam/lobby/status", () =>
    {
        return Results.Json(new
        {
            lobby_id = service.CurrentLobbyId == 0 ? null : service.CurrentLobbyId.ToString(),
            is_logged_in = service.IsLoggedIn
        });
    });

    Logger.Log($"[SteamService] HTTP API listening on port {httpPort}");
    Console.WriteLine("[SteamService] Endpoints:");
    Console.WriteLine("  GET /health - Health check");
    Console.WriteLine("  GET /steam/app-ticket - Get encrypted app ticket");

    // Try to auto-login with saved session
    if (service.HasSavedSession())
    {
        Logger.Log("[SteamService] Found saved session, logging in...");
        try
        {
            await service.LoginWithSavedSessionAsync();
            Logger.Log("[SteamService] Logged in successfully");
        }
        catch (Exception ex)
        {
            Logger.Log($"[SteamService] Auto-login failed: {ex.Message}");
            Logger.Log("[SteamService] Run 'setup' to re-authenticate");
        }
    }
    else
    {
        Logger.Log("[SteamService] No saved session - run 'setup' first");
    }

    await app.RunAsync();
}

// ============================================================================
// Request DTOs for Lobby Endpoints
// ============================================================================

record CreateLobbyRequest(
    ulong game_server_steam_id,
    string protocol_version,
    int max_members);

record SetLobbyDataRequest(
    ulong lobby_id,
    Dictionary<string, string>? metadata = null);

record SetLobbyPrivacyRequest(
    ulong lobby_id,
    string? privacy = "public");
