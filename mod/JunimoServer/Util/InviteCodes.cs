using JunimoServer.Services.Auth;
using JunimoServer.Services.SteamGameServer;
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
    public static string Raw => GalaxyAuthService.InviteCode;

    private static string Base
    {
        get
        {
            var raw = Raw;
            return raw != null && raw.Length > 1 ? raw.Substring(1) : raw;
        }
    }

    /// <summary>
    /// The one code to hand out to players: the universal S-code, shown whenever a Galaxy lobby exists
    /// (independent of the Steam-relay stamp). GOG players join it immediately; Steam players retry
    /// until <see cref="GalaxyAuthService.SteamLobbyPublished"/> (surfaced separately as
    /// <c>steamRelayReady</c>). Null before the lobby exists; the G-code is never exposed. A code that
    /// occasionally can't connect for a few seconds is more consistent for players than one that
    /// vanishes on a Steam hiccup.
    /// </summary>
    public static string Joinable => Base == null ? null : GalaxyNetHelper.SteamInvitePrefix + Base;

    /// <summary>
    /// Whether <paramref name="joinable"/> (a snapshot of <see cref="Joinable"/>) is usable and by
    /// whom, from the live connectivity signals <c>/status</c> reports. Takes the snapshot rather than
    /// re-reading <see cref="Joinable"/> so a caller's code string and code never disagree across a
    /// withdraw. Reads only the volatile statics the <c>/status</c> builder reads.
    /// </summary>
    public static ConnectionStatusCode StatusCodeOf(string joinable) =>
        ConnectionStatus.Compute(
            joinable != null,
            GalaxyAuthService.SteamLobbyPublished,
            GalaxyAuthService.GalaxyLobby,
            SteamGameServerService.SteamSession
        );

    /// <summary>The code with its connection status, per the display rule every surface shares.</summary>
    public static string Describe()
    {
        var joinable = Joinable;
        return ConnectionStatus.Render(StatusCodeOf(joinable), joinable);
    }
}
