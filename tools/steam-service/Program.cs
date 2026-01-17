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

const uint AppId = 413150; // Stardew Valley

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
            await steamService.DownloadGameAsync(AppId);
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
        await steamService.DownloadGameAsync(AppId);
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
        var ticket = await steamService.GetAppTicketAsync(AppId);
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
        break;
}

steamService.Disconnect();
return;

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

            var ticketBase64 = await service.GetAppTicketAsync(AppId);
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
