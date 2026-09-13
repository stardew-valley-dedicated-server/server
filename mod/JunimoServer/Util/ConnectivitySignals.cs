using System;

namespace JunimoServer.Util;

/// <summary>Galaxy lobby state. The accessor reports null (not a member) when Steam auth isn't configured.</summary>
public enum GalaxyLobbyState
{
    Down,
    Recovering,
    Connected,
}

/// <summary>Steam GameServer session state.</summary>
public enum SteamSessionState
{
    Lost,
    Connected,
}

/// <summary>
/// Sidecar auth/token health. <see cref="Unknown"/> until the first poll reaches the sidecar; from
/// then on the last successful classification.
/// </summary>
public enum AuthReadiness
{
    Unknown,
    Ok,
    Expiring,
    Unavailable,
}

/// <summary>
/// Whether the invite code is usable and by whom — the one machine-readable field a display maps to
/// text. Derived by <see cref="ConnectionStatus.Compute"/>; the wire form is camelCase
/// (<see cref="ConnectivityWire.ToWire(ConnectionStatusCode)"/>).
/// </summary>
public enum ConnectionStatusCode
{
    /// <summary>The code is usable by everyone.</summary>
    Ready,

    /// <summary>The code is shown and GOG clients can join; the Steam relay is not ready yet.</summary>
    SteamRelayPending,

    /// <summary>The multiplayer session is recovering.</summary>
    Reconnecting,

    /// <summary>The session is starting up; the lobby is still forming.</summary>
    Starting,

    /// <summary>The Steam session is not up, so there is no code yet.</summary>
    SteamSessionDown,

    /// <summary>Invite codes are not configured on this server.</summary>
    InviteUnavailable,
}

/// <summary>Maps a connectivity-state enum to the string surfaced on <c>/status</c> and <c>/health</c>.</summary>
public static class ConnectivityWire
{
    public static string ToWire<T>(this T value)
        where T : struct, Enum => value.ToString().ToLowerInvariant();

    public static string? ToWire<T>(this T? value)
        where T : struct, Enum => value?.ToString().ToLowerInvariant();

    /// <summary>The camelCase wire form, which the generic lowercaser cannot produce.</summary>
    public static string ToWire(this ConnectionStatusCode code) =>
        code switch
        {
            ConnectionStatusCode.Ready => "ready",
            ConnectionStatusCode.SteamRelayPending => "steamRelayPending",
            ConnectionStatusCode.Reconnecting => "reconnecting",
            ConnectionStatusCode.Starting => "starting",
            ConnectionStatusCode.SteamSessionDown => "steamSessionDown",
            ConnectionStatusCode.InviteUnavailable => "inviteUnavailable",
            _ => throw new ArgumentOutOfRangeException(nameof(code), code, null),
        };
}
