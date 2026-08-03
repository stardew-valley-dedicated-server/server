namespace Diagnostics;

/// <summary>
/// Environment inputs and fixed in-container paths. All configuration the tool reads lives here so
/// there's one place to see what it depends on from the surrounding container.
/// </summary>
internal static class Config
{
    public static readonly string ApiKey = Env("API_KEY") ?? "";

    public static readonly bool ApiEnabled =
        (Env("API_ENABLED") ?? "true").ToLowerInvariant() != "false";

    public static readonly string GitSha = Env("SDVD_GIT_SHA") ?? "unknown";
    public static readonly string SmapiVersion = Env("SMAPI_VERSION") ?? "unknown";
    public static readonly string BaseUrl = $"http://127.0.0.1:{Env("API_PORT") ?? "8080"}";

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
    /// Whether the report echoes this variable, by prefix — so a var added to Env.cs or compose shows
    /// up without a list to maintain, including a stray test-mode flag. Scoped to prefixes this
    /// project owns because an operator's compose can hold anything and the report goes on a public
    /// issue; STEAM_* is excluded as the sidecar's account config.
    /// </summary>
    public static bool IsReportedEnv(string name) =>
        ReportedPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal));

    private static readonly string[] ReportedPrefixes =
    {
        "SDVD_",
        "SERVER_",
        "API_",
        "AUTH_",
        "SMAPI_",
        "VNC_",
        "DISPLAY_",
        "ALLOW_",
        "ENABLE_",
        "FORCE_",
        "HEALTH_",
        "VERBOSE_",
        "SETTINGS_",
        "MAX_LOGIN_",
        "TEST_",
    };

    /// <summary>
    /// Whether a value is replaced by set/not-set: that auth is configured is diagnostic, the secret
    /// isn't. Matched by name pattern, not a fixed list, so a newly added secret is covered before
    /// anyone thinks to list it.
    /// </summary>
    public static bool IsSecretEnv(string name) =>
        name.Contains("KEY", StringComparison.OrdinalIgnoreCase)
        || name.Contains("PASSWORD", StringComparison.OrdinalIgnoreCase)
        || name.Contains("TOKEN", StringComparison.OrdinalIgnoreCase)
        || name.Contains("SECRET", StringComparison.OrdinalIgnoreCase);

    private static string? Env(string name) => Environment.GetEnvironmentVariable(name);
}
