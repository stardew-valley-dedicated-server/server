using StardewModdingAPI;
using System.Net;

namespace JunimoServer.Util
{
    /// <summary>
    /// Handles printing the server startup banner.
    /// Ensures the banner is only printed once per session.
    /// </summary>
    public static class ServerBanner
    {
        private static bool _hasPrinted = false;
        private static readonly object _lock = new object();

        /// <summary>
        /// Prints the server startup banner with IP addresses and invite code (if available).
        /// This method is idempotent - it will only print once per session.
        /// </summary>
        public static void Print(IMonitor monitor, IModHelper helper)
        {
            lock (_lock)
            {
                if (_hasPrinted)
                {
                    return;
                }

                _hasPrinted = true;
            }

            var modInfo = helper.ModRegistry.Get("JunimoHost.Server");
            var version = modInfo?.Manifest?.Version?.ToString() ?? "unknown";

            var externalIp = NetworkHelper.GetIpAddressExternal();
            var externalIpValue = externalIp == IPAddress.None ? "n/a" : externalIp.ToString();
            var externalIcon = externalIp == IPAddress.None ? "х" : "✓";

            var inviteCode = InviteCodeFile.Read(monitor);
            var inviteCodeValue = inviteCode ?? "n/a";

            monitor.LogBanner(new[] {
                $"JunimoServer {version}",
                "",
                $"✓ Local:   {NetworkHelper.GetIpAddressLocal()}",
                $"{externalIcon} Network: {externalIpValue}",
                "",
                $"Invite Code: {inviteCodeValue}",
            });
        }

        /// <summary>
        /// Resets the banner state. Useful for testing or server restarts.
        /// </summary>
        public static void Reset()
        {
            lock (_lock)
            {
                _hasPrinted = false;
            }
        }
    }
}
