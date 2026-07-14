namespace Diagnostics;

/// <summary>
/// Environment inputs and fixed in-container paths. All configuration the tool reads lives here so
/// there's one place to see what it depends on from the surrounding container.
/// </summary>
internal static class Config
{
    public static readonly string ApiPort = Env("API_PORT") ?? "8080";
    public static readonly string ApiKey = Env("API_KEY") ?? "";

    public static readonly bool ApiEnabled =
        (Env("API_ENABLED") ?? "true").ToLowerInvariant() != "false";

    public static readonly string GitSha = Env("SDVD_GIT_SHA") ?? "unknown";
    public static readonly string SmapiVersion = Env("SMAPI_VERSION") ?? "unknown";
    public static readonly string BaseUrl = $"http://127.0.0.1:{ApiPort}";

    /// <summary>Steam auth sidecar URL the server itself uses (docker-compose STEAM_AUTH_URL).</summary>
    public static readonly string SteamAuthUrl = Env("STEAM_AUTH_URL") ?? "http://steam-auth:3001";

    public const string ConsoleLogPath = "/tmp/server-output.log";
    public const string ConfigRoot = "/config/xdg/config/StardewValley";
    public const string ModsPath = "/data/Mods";
    public const string OutputDir = "/data/diagnostics";
    public static readonly string CrashLogPath = $"{ConfigRoot}/ErrorLogs/SMAPI-crash.txt";
    public static readonly string SmapiLogPath = $"{ConfigRoot}/ErrorLogs/SMAPI-latest.txt";

    /// <summary>Volumes worth reporting free space for (game download, saves, settings).</summary>
    public static readonly string[] DiskPaths = { "/data/game", ConfigRoot, "/data/settings" };

    /// <summary>
    /// Endpoints the report collects. /stats and /diagnostics/state are public; the rest need the
    /// key (sending Bearer on all is harmless).
    /// </summary>
    public static readonly string[] Endpoints =
    {
        "/status",
        "/stats",
        "/diagnostics/state",
        "/settings",
        "/players",
        "/farmhands",
        "/cabins",
    };

    private static string? Env(string name) => Environment.GetEnvironmentVariable(name);
}
