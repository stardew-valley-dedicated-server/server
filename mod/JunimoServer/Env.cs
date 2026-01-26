using System;
using System.Linq;

namespace JunimoServer
{
    internal class Env
    {
        public static readonly bool EnableModIncompatibleOptimizations =
            bool.Parse(Environment.GetEnvironmentVariable("ENABLE_MOD_INCOMPATIBLE_OPTIMIZATIONS") ?? "true");

        public static readonly string JunimoBootServerAddress =
            Environment.GetEnvironmentVariable("BACKEND_HOSTPORT") ?? "";

        public static readonly int HealthCheckSeconds =
            Int32.Parse(Environment.GetEnvironmentVariable("HEALTH_CHECK_SECONDS") ?? "300");

        public static readonly bool DisableRendering =
            bool.Parse(Environment.GetEnvironmentVariable("DISABLE_RENDERING") ?? "false");

        public static readonly bool ForceNewDebugGame =
            bool.Parse(Environment.GetEnvironmentVariable("FORCE_NEW_DEBUG_GAME") ?? "false");

        // TODO: Once web UI is available, add info to docs about how "host.docker.internal:3000" can be used to connect to local web UI (dev mode)
        public static readonly string WebSocketServerAddress =
            Environment.GetEnvironmentVariable("WEB_SOCKET_SERVER_ADDRESS") ?? "stardew-dedicated-web:3000";

        public static readonly bool HasServerBypassCommandLineArg =
            Environment.GetCommandLineArgs().Any("--client".Contains);

        /// <summary>
        /// Server password for player authentication.
        /// When set, players must type !login &lt;password&gt; in chat before they can play.
        /// Leave empty to disable password protection.
        /// </summary>
        public static readonly string ServerPassword =
            Environment.GetEnvironmentVariable("SERVER_PASSWORD") ?? "";

        /// <summary>
        /// Maximum failed login attempts before a player is kicked.
        /// Default: 3
        /// </summary>
        public static readonly int MaxLoginAttempts =
            Int32.Parse(Environment.GetEnvironmentVariable("MAX_LOGIN_ATTEMPTS") ?? "3");

        /// <summary>
        /// Authentication timeout in seconds. Players who don't authenticate within this time are kicked.
        /// Set to 0 to disable timeout.
        /// Default: 600 (10 minutes)
        /// </summary>
        public static readonly int AuthTimeoutSeconds =
            Int32.Parse(Environment.GetEnvironmentVariable("AUTH_TIMEOUT_SECONDS") ?? "600");

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
    }
}
