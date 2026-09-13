using JunimoServer.Services.Auth;
using StardewValley.SDKs.GogGalaxy;

namespace JunimoServer.Util;

/// <summary>
/// The server's invite code. Vanilla <c>GalaxySocket.GetInviteCode()</c> already produces an
/// S-prefixed code (<c>"S" + Base36(lobby id)</c>); the mirror in <see cref="GalaxyAuthService"/>
/// holds exactly that live-lobby value. Both prefixes address the same Galaxy lobby: a GOG client
/// decodes either and joins over Galaxy P2P, so it can join the S-code the moment the lobby exists;
/// a vanilla Steam client reads the <c>SteamLobbyId</c> stamp off the Galaxy lobby and joins over the
/// Steam relay, which only works once that stamp lands (<see cref="GalaxyAuthService.SteamLobbyPublished"/>).
/// The S-code is therefore universal; the G-code is never exposed, since a Steam player who used it
/// would join over Galaxy P2P and get a farmhand their Steam identity never sees again.
/// </summary>
public static class InviteCodes
{
    /// <summary>The live-lobby S-code, or null before the Galaxy lobby exists.</summary>
    public static string Raw => GalaxyAuthService.GalaxyInviteCode;

    private static string Base
    {
        get
        {
            var raw = Raw;
            return raw != null && raw.Length > 1 ? raw.Substring(1) : raw;
        }
    }

    /// <summary>
    /// The S-code, shown whenever a Galaxy lobby exists — independent of the Steam-relay stamp.
    /// GOG players join it immediately; Steam players retry until <see cref="GalaxyAuthService.SteamLobbyPublished"/>
    /// (surfaced separately as <c>steamRelayReady</c>). A code that occasionally can't connect for a
    /// few seconds is more consistent for players than a code that vanishes on a Steam hiccup.
    /// </summary>
    public static string Steam => Base == null ? null : GalaxyNetHelper.SteamInvitePrefix + Base;

    /// <summary>The one code to hand out to players: the universal S-code. The G-code is never exposed.</summary>
    public static string Joinable => Steam;

    /// <summary>
    /// A short human note explaining why no code is available, for command replies and the banner.
    /// Null when a code IS available. Derived from the same connectivity signals /status reports.
    /// </summary>
    public static string UnavailableReason
    {
        get
        {
            if (Joinable != null)
            {
                return null;
            }
            var galaxy = GalaxyAuthService.GalaxyLobbyState;
            if (galaxy == null)
            {
                return "invite codes are disabled in LAN-only mode";
            }
            if (!Services.SteamGameServer.SteamGameServerService.SteamSessionConnected)
            {
                return "Steam session reconnecting";
            }
            if (galaxy == "recovering")
            {
                return "Galaxy lobby reconnecting";
            }
            return "Galaxy lobby connecting";
        }
    }
}
