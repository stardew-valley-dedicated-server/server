using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using JunimoServer.Services.SteamGameServer;
using JunimoServer.Shared;
using StardewModdingAPI;

namespace JunimoServer.Util;

/// <summary>
/// Handles printing the server startup banner.
/// Ensures the banner is only printed once per session.
/// </summary>
public static class ServerBanner
{
    private static bool _hasPrinted = false;
    private static bool _printedWithCode = false;
    private static readonly object _lock = new object();

    /// <summary>
    /// Prints the server startup banner with IP addresses and invite code (if available).
    /// Prints once, PLUS exactly one refresh the first time an invite code becomes available — so a
    /// banner printed by the ~5s startup fallback (before the code exists) does not lock forever on
    /// "not yet available" (the old wart). Once printed with a code, it is idempotent.
    /// </summary>
    public static void Print(IMonitor monitor, IModHelper helper)
    {
        lock (_lock)
        {
            var codeAvailable = InviteCodes.Joinable != null;

            // Skip only when there is nothing new to show: already printed the final (with-code)
            // banner, or already printed and still no code to add.
            if (_hasPrinted && (_printedWithCode || !codeAvailable))
            {
                return;
            }

            _hasPrinted = true;
            _printedWithCode = codeAvailable;
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

        // The code lets anyone join, so it's masked in the banner (which is captured into
        // the public report). The real code is served verbatim by the API and the CLI.
        var inviteCode = InviteCodes.Joinable;
        bannerLines.Add(
            $"Invite Code: {(inviteCode != null ? ChatRedaction.MaskValue(inviteCode) : "not yet available")}"
        );

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
            _printedWithCode = false;
        }
    }
}
