using System;
using System.Linq;

namespace JunimoServer
{
    /// <summary>
    /// Environment variables for Docker infrastructure, credentials, and internal settings.
    /// Game configuration has moved to server-settings.json — see Services/Settings/.
    /// </summary>
    internal class Env
    {
        public static readonly bool EnableModIncompatibleOptimizations =
            bool.Parse(Environment.GetEnvironmentVariable("ENABLE_MOD_INCOMPATIBLE_OPTIMIZATIONS") ?? "false");

        public static readonly int HealthCheckSeconds =
            Int32.Parse(Environment.GetEnvironmentVariable("HEALTH_CHECK_SECONDS") ?? "300");

        public static readonly bool DisableRendering =
            bool.Parse(Environment.GetEnvironmentVariable("DISABLE_RENDERING") ?? "false");

        public static readonly bool ForceNewDebugGame =
            bool.Parse(Environment.GetEnvironmentVariable("FORCE_NEW_DEBUG_GAME") ?? "false");

        /// <summary>
        /// Enable the HTTP API server for external tools and automated testing.
        /// Default: true
        /// </summary>
        public static readonly bool ApiEnabled =
            bool.Parse(Environment.GetEnvironmentVariable("API_ENABLED") ?? "true");

        /// <summary>
        /// Port for the HTTP API server.
        /// Default: 8080
        /// </summary>
        public static readonly int ApiPort =
            Int32.Parse(Environment.GetEnvironmentVariable("API_PORT") ?? "8080");

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
        public static readonly string ApiKey =
            Environment.GetEnvironmentVariable("API_KEY") ?? "";

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
        public static readonly int MaxLoginAttempts =
            Int32.Parse(Environment.GetEnvironmentVariable("MAX_LOGIN_ATTEMPTS") ?? "3");

        /// <summary>
        /// Seconds before unauthenticated players are kicked.
        /// Set to 0 to disable timeout.
        /// Default: 120
        /// </summary>
        public static readonly int AuthTimeoutSeconds =
            Int32.Parse(Environment.GetEnvironmentVariable("AUTH_TIMEOUT_SECONDS") ?? "120");

        #endregion

        private static bool? ParseNullableBool(string envVar)
        {
            var value = Environment.GetEnvironmentVariable(envVar);
            if (string.IsNullOrEmpty(value))
                return null;
            if (bool.TryParse(value, out var result))
                return result;
            return null;
        }
    }
}
