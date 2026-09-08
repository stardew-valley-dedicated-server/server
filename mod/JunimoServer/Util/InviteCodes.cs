using JunimoServer.Services.Auth;
using StardewValley.SDKs.GogGalaxy;

namespace JunimoServer.Util;

/// <summary>
/// The server's invite codes. One Galaxy lobby id underlies both: the game hands out the
/// G-prefixed form, and the S-prefixed form is the same id joined through Steam's relay.
/// A GOG client accepts either prefix; a vanilla Steam client completes an S-code join by
/// reading the SteamLobbyId stamp from the Galaxy lobby, so that code only works once
/// <see cref="GalaxyAuthService.SteamLobbyPublished"/> is true.
/// </summary>
public static class InviteCodes
{
    /// <summary>The code as the game generated it (G-prefixed), or null before the lobby exists.</summary>
    public static string Raw => GalaxyAuthService.GalaxyInviteCode;

    private static string Base
    {
        get
        {
            var raw = Raw;
            return raw != null && raw.Length > 1 ? raw.Substring(1) : raw;
        }
    }

    /// <summary>The GOG code; joins through Galaxy P2P and presents a Galaxy identity.</summary>
    public static string Gog => Base == null ? null : GalaxyNetHelper.GalaxyInvitePrefix + Base;

    /// <summary>The Steam code; null until the Steam lobby is published, since it fails to join before that.</summary>
    public static string Steam =>
        Base != null && GalaxyAuthService.SteamLobbyPublished
            ? GalaxyNetHelper.SteamInvitePrefix + Base
            : null;

    /// <summary>
    /// The one code to hand out to players. Only the Steam code: a Steam player who joins with the
    /// GOG code gets a Galaxy identity and a farmhand their Steam identity never sees, so nothing
    /// is shown until the Steam code is joinable.
    /// </summary>
    public static string Joinable => Steam;
}
