using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using JunimoServer.Services.SteamGameServer;
using JunimoServer.Shared;
using StardewModdingAPI;
using StardewValley.SDKs.GogGalaxy;

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

            _ = PrintAsync(monitor, helper);
        }

        private static async Task PrintAsync(IMonitor monitor, IModHelper helper)
        {
            var modInfo = helper.ModRegistry.Get("JunimoHost.Server");
            var version = modInfo?.Manifest?.Version?.ToString() ?? "unknown";

            var externalIp = await NetworkHelper.GetIpAddressExternalAsync().ConfigureAwait(false);
            var externalIpValue = externalIp == IPAddress.None ? "n/a" : externalIp.ToString();
            var externalIcon = externalIp == IPAddress.None ? "х" : "✓";

            var inviteCode = InviteCodeFile.Read(monitor);

            var networkingLines = GetNetworkingStatus();

            // IPs are masked: the banner is captured into the public E2E report, so it
            // must not disclose the host's real addresses (keep the last octet for shape).
            var bannerLines = new List<string>
            {
                $"JunimoServer {version}",
                "",
                $"✓ Local:   {ChatRedaction.MaskIp(NetworkHelper.GetIpAddressLocal().ToString())}",
                $"{externalIcon} Network: {ChatRedaction.MaskIp(externalIpValue)}",
                "",
            };

            bannerLines.AddRange(networkingLines);
            bannerLines.Add("");

            // Invite codes let anyone join, so they're masked in the banner (which is
            // captured into the public report). The real code is still served verbatim
            // via /tmp/invite-code.txt and the API for legitimate clients.
            if (SteamGameServerService.IsInitialized && inviteCode != null)
            {
                var baseCode = inviteCode.Length > 1 ? inviteCode.Substring(1) : inviteCode;
                bannerLines.Add(
                    $"Invite Code (Steam): {GalaxyNetHelper.SteamInvitePrefix}{ChatRedaction.MaskValue(baseCode)}"
                );
                bannerLines.Add(
                    $"Invite Code (GOG):   {GalaxyNetHelper.GalaxyInvitePrefix}{ChatRedaction.MaskValue(baseCode)}"
                );
            }
            else
            {
                bannerLines.Add(
                    $"Invite Code: {(inviteCode != null ? ChatRedaction.MaskValue(inviteCode) : "n/a")}"
                );
            }

            monitor.LogBanner(bannerLines.ToArray());
        }

        private static List<string> GetNetworkingStatus()
        {
            var lines = new List<string>();

            // Steam GameServer (SDR) status
            if (SteamGameServerService.IsInitialized)
            {
                // Masked: the SDR ID identifies the hosting Steam account and the banner
                // is captured into the public report.
                var steamId = SteamGameServerService.ServerSteamId.m_SteamID;
                lines.Add($"✓ Steam SDR: {ChatRedaction.MaskValue(steamId.ToString())}");
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
