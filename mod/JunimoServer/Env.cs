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
            bool.Parse(Environment.GetEnvironmentVariable("ENABLE_MOD_INCOMPATIBLE_OPTIMIZATIONS") ?? "true");

        public static readonly int HealthCheckSeconds =
            Int32.Parse(Environment.GetEnvironmentVariable("HEALTH_CHECK_SECONDS") ?? "300");

        public static readonly bool DisableRendering =
            bool.Parse(Environment.GetEnvironmentVariable("DISABLE_RENDERING") ?? "false");

        public static readonly bool ForceNewDebugGame =
            bool.Parse(Environment.GetEnvironmentVariable("FORCE_NEW_DEBUG_GAME") ?? "false");

        public static readonly bool HasServerBypassCommandLineArg =
            Environment.GetCommandLineArgs().Any("--client".Contains);

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
