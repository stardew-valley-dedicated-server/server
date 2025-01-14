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
    }
}
