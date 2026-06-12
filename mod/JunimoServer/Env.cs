using System;

namespace JunimoServer;

/// <summary>
/// Environment variables for Docker infrastructure, credentials, and internal settings.
/// Game configuration has moved to server-settings.json (see Services/Settings/).
/// </summary>
internal class Env
{
    /// <summary>
    /// Runtime environment. Defaults to "production" when unset.
    /// Test harness sets this to "test".
    /// </summary>
    public static readonly string SdvdEnv = System.Environment.GetEnvironmentVariable("SDVD_ENV")
        is { Length: > 0 } v
        ? v.ToLowerInvariant()
        : "production";

    public static bool IsTest => SdvdEnv == "test";

    public static readonly bool EnableModIncompatibleOptimizations = ParseBool(
        "ENABLE_MOD_INCOMPATIBLE_OPTIMIZATIONS",
        false
    );

    public static readonly int HealthCheckSeconds = ParseInt("HEALTH_CHECK_SECONDS", 300);

    public static readonly bool ForceNewDebugGame = ParseBool("FORCE_NEW_DEBUG_GAME", false);

    /// <summary>
    /// Target game ticks per second. Lower values reduce CPU usage.
    /// Default: 60 (game default). Minimum: 1.
    /// </summary>
    public static readonly int ServerTps = Math.Max(1, ParseInt("SERVER_TPS", 60));

    /// <summary>
    /// Target frames per second for the server's draw loop.
    /// 0 (or unset) — rendering disabled at boot (NullDisplayDevice installed,
    ///                draws suppressed).
    /// N > 0        — draws capped at N fps via FpsThrottle.
    /// This is the boot-time default; SetServerFps(int) overrides at runtime.
    /// Read by ServerOptimizer.OnGameLaunched to set the initial state; bypassed
    /// during day-end saves (SaveGameMenu deadlocks otherwise).
    /// </summary>
    public static readonly int ServerFps = Math.Max(0, ParseInt("SERVER_FPS", 0));

    /// <summary>
    /// Enable the HTTP API server for external tools and automated testing.
    /// Default: true
    /// </summary>
    public static readonly bool ApiEnabled = ParseBool("API_ENABLED", true);

    /// <summary>
    /// Port for the HTTP API server.
    /// Default: 8080
    /// </summary>
    public static readonly int ApiPort = ParseInt("API_PORT", 8080);

    /// <summary>
    /// Override verbose logging setting from environment.
    /// Returns null if not set (uses config file value).
    /// Set to "true" or "false" to override.
    /// </summary>
    public static readonly bool? VerboseLogging = ParseNullableBool("VERBOSE_LOGGING");

    #region API Authentication

    /// <summary>
    /// API key for authenticating requests to protected endpoints.
    /// When set, write operations (POST, DELETE) require the X-API-Key header.
    /// Leave empty to allow unauthenticated access (not recommended for production).
    /// Recommended: At least 32 characters, alphanumeric with mixed case.
    /// Generate securely: bun -e "console.log(require('crypto').randomBytes(32).toString('base64url'))"
    /// </summary>
    public static readonly string ApiKey = Environment.GetEnvironmentVariable("API_KEY") ?? "";

    #endregion

    #region Password Protection

    /// <summary>
    /// Server password for player authentication.
    /// Leave empty to disable password protection.
    /// </summary>
    public static readonly string ServerPassword =
        Environment.GetEnvironmentVariable("SERVER_PASSWORD") ?? "";

    /// <summary>
    /// Maximum failed login attempts before kicking the player.
    /// Default: 3
    /// </summary>
    public static readonly int MaxLoginAttempts = ParseInt("MAX_LOGIN_ATTEMPTS", 3);

    /// <summary>
    /// Seconds before unauthenticated players are kicked.
    /// Set to 0 to disable timeout.
    /// Default: 120
    /// </summary>
    public static readonly int AuthTimeoutSeconds = ParseInt("AUTH_TIMEOUT_SECONDS", 120);

    #endregion

    private static int ParseInt(string envVar, int defaultValue)
    {
        var value = Environment.GetEnvironmentVariable(envVar);
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        return int.TryParse(value, out var result) ? result : defaultValue;
    }

    private static bool ParseBool(string envVar, bool defaultValue)
    {
        var value = Environment.GetEnvironmentVariable(envVar);
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        return bool.TryParse(value, out var result) ? result : defaultValue;
    }

    private static bool? ParseNullableBool(string envVar)
    {
        var value = Environment.GetEnvironmentVariable(envVar);
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        if (bool.TryParse(value, out var result))
        {
            return result;
        }

        return null;
    }
}
