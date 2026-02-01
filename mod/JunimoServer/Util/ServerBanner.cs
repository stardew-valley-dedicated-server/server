using JunimoServer.Services.SteamGameServer;
using StardewModdingAPI;
using StardewValley.SDKs.GogGalaxy;
using System.Collections.Generic;
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

            // Build networking status
            var networkingLines = GetNetworkingStatus();

            var bannerLines = new List<string>
            {
                $"JunimoServer {version}",
                "",
                $"✓ Local:   {NetworkHelper.GetIpAddressLocal()}",
                $"{externalIcon} Network: {externalIpValue}",
                "",
            };

            bannerLines.AddRange(networkingLines);
            bannerLines.Add("");

            // Show invite codes - in hybrid mode both Steam and Galaxy clients can connect
            if (SteamGameServerService.IsInitialized && inviteCode != null)
            {
                // Extract the base code (without prefix) and show both variants
                var baseCode = inviteCode.Length > 1 ? inviteCode.Substring(1) : inviteCode;
                bannerLines.Add($"Invite Code (Steam): {GalaxyNetHelper.SteamInvitePrefix}{baseCode}");
                bannerLines.Add($"Invite Code (GOG):   {GalaxyNetHelper.GalaxyInvitePrefix}{baseCode}");
            }
            else
            {
                bannerLines.Add($"Invite Code: {inviteCode ?? "n/a"}");
            }

            monitor.LogBanner(bannerLines.ToArray());
        }

        private static List<string> GetNetworkingStatus()
        {
            var lines = new List<string>();

            // Steam GameServer (SDR) status
            if (SteamGameServerService.IsInitialized)
            {
                var steamId = SteamGameServerService.ServerSteamId.m_SteamID;
                lines.Add($"✓ Steam SDR: {steamId}");
            }
            else
            {
                lines.Add("⏳ Steam SDR: initializing...");
            }

            // Galaxy is always enabled (default game networking)
            lines.Add("✓ Galaxy P2P: enabled");

            return lines;
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
