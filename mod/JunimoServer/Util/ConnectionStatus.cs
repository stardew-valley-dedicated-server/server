using System;

namespace JunimoServer.Util;

/// <summary>
/// Derives the invite code's <see cref="ConnectionStatusCode"/> from the connectivity signals and
/// renders it for the mod's own replies. Game-free so the unit tests can link this file and cover
/// every derivation row. Consumers of <c>/status</c> keep their own code → text maps.
/// </summary>
public static class ConnectionStatus
{
    /// <summary>
    /// The code for one snapshot of the signals. With a code present, a lobby that is not connected
    /// is recovering; otherwise the relay stamp decides between usable-by-all and GOG-only. Without a
    /// code, a null lobby means Steam auth isn't configured, the Steam session is checked first (it is
    /// upstream of the lobby), and a recovering lobby is distinguished from one still forming.
    /// </summary>
    public static ConnectionStatusCode Compute(
        bool codePresent,
        bool relayReady,
        GalaxyLobbyState? galaxy,
        SteamSessionState steam
    )
    {
        if (codePresent)
        {
            if (galaxy != GalaxyLobbyState.Connected)
            {
                return ConnectionStatusCode.Reconnecting;
            }
            if (!relayReady)
            {
                return ConnectionStatusCode.SteamRelayPending;
            }
            return steam == SteamSessionState.Connected
                ? ConnectionStatusCode.Ready
                : ConnectionStatusCode.Reconnecting;
        }
        if (galaxy == null)
        {
            return ConnectionStatusCode.InviteUnavailable;
        }
        if (steam != SteamSessionState.Connected)
        {
            return ConnectionStatusCode.SteamSessionDown;
        }
        return galaxy == GalaxyLobbyState.Recovering
            ? ConnectionStatusCode.Reconnecting
            : ConnectionStatusCode.Starting;
    }

    /// <summary>
    /// The display text for a code; null for <see cref="ConnectionStatusCode.Ready"/>, which shows the
    /// code alone. The trailing dots mark in-progress states; the settled unavailable state has none.
    /// ASCII only: in-game chat renders in the game's SpriteFont, which draws only its baked glyphs.
    /// </summary>
    public static string? Text(ConnectionStatusCode code) =>
        code switch
        {
            ConnectionStatusCode.Ready => null,
            ConnectionStatusCode.SteamRelayPending => "GOG ready - Steam connecting...",
            ConnectionStatusCode.Reconnecting => "reconnecting...",
            ConnectionStatusCode.Starting => "starting up...",
            ConnectionStatusCode.SteamSessionDown => "connecting to Steam...",
            ConnectionStatusCode.InviteUnavailable => "not used on this server",
            _ => throw new ArgumentOutOfRangeException(nameof(code), code, null),
        };

    /// <summary>
    /// The display rule shared by every surface: a ready code alone; otherwise <c>code (text)</c>
    /// when a code is present, else the text standalone.
    /// </summary>
    public static string Render(ConnectionStatusCode code, string? inviteCode)
    {
        var text = Text(code);
        if (inviteCode == null)
        {
            return text ?? "not yet available";
        }
        return text == null ? inviteCode : $"{inviteCode} ({text})";
    }
}
