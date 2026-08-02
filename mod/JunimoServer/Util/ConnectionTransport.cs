namespace JunimoServer.Util;

/// <summary>
/// The transport-authenticated platform identity of a connection, parsed from its
/// connection id. <see cref="Platform"/> is one of the <see cref="ConnectionTransport"/>
/// platform constants; <see cref="Id"/> is the platform's numeric id as a decimal string
/// (Steam64 for SDR, Galaxy uint64 for GOG).
/// </summary>
public readonly struct TransportIdentity
{
    public string Platform { get; }
    public string Id { get; }

    public TransportIdentity(string platform, string id)
    {
        Platform = platform;
        Id = id;
    }
}

/// <summary>
/// Canonical parser for the per-transport connection-id formats. Every consumer of a
/// connection id's prefix or embedded platform id goes through here.
///
/// Format owners (keep in sync — the formats are produced elsewhere and only parsed here):
/// <list type="bullet">
/// <item><c>SN_{steam64}_{connHandle}</c> — <c>SteamGameServerNetServer.ConnectionDataToId</c>
/// (our SDR server). The Steam64 is the connection's cryptographically authenticated identity.</item>
/// <item><c>GN_{galaxyUint64}</c> — vanilla <c>GalaxyNetServer.getConnectionId</c>
/// (<c>GalaxyNetServer.cs:192</c>). The same value vanilla passes as the userId.</item>
/// <item><c>L_{remoteUniqueIdentifier}</c> — vanilla <c>LidgrenServer.getConnectionId</c>
/// (<c>LidgrenServer.cs:298</c>). Carries no platform identity.</item>
/// </list>
/// </summary>
public static class ConnectionTransport
{
    public const string SteamPrefix = "SN_";
    public const string GalaxyPrefix = "GN_";
    public const string LanPrefix = "L_";

    /// <summary>Platform tag for Steam SDR connections (id = Steam64).</summary>
    public const string PlatformSteam = "steam";

    /// <summary>Platform tag for GOG Galaxy connections (id = Galaxy uint64).</summary>
    public const string PlatformGalaxy = "galaxy";

    /// <summary>
    /// Parses the full <c>SN_{steam64}_{connHandle}</c> shape. The identity view
    /// (<see cref="TryResolveIdentity"/>) and the outbound routing lookup
    /// (<c>SteamGameServerNetServer.IdToConnectionData</c>) both go through this parse.
    /// </summary>
    public static bool TryParseSteamConnectionId(
        string connectionId,
        out ulong steamId,
        out uint connHandle
    )
    {
        steamId = 0;
        connHandle = 0;
        if (
            string.IsNullOrEmpty(connectionId)
            || !connectionId.StartsWith(SteamPrefix)
            || connectionId.Length <= SteamPrefix.Length
        )
        {
            return false;
        }

        var rest = connectionId.Substring(SteamPrefix.Length);
        var separatorIndex = rest.IndexOf('_');
        if (separatorIndex <= 0 || separatorIndex >= rest.Length - 1)
        {
            return false;
        }

        return ulong.TryParse(rest.Substring(0, separatorIndex), out steamId)
            && uint.TryParse(rest.Substring(separatorIndex + 1), out connHandle);
    }

    /// <summary>
    /// Resolves the transport-authenticated platform identity embedded in a connection id.
    /// Returns false for LAN (Lidgren carries no identity), unknown prefixes, and malformed ids.
    /// </summary>
    public static bool TryResolveIdentity(string connectionId, out TransportIdentity identity)
    {
        identity = default;
        if (string.IsNullOrEmpty(connectionId))
        {
            return false;
        }

        if (connectionId.StartsWith(SteamPrefix))
        {
            if (!TryParseSteamConnectionId(connectionId, out var steamId, out _))
            {
                return false;
            }

            identity = new TransportIdentity(
                PlatformSteam,
                steamId.ToString(System.Globalization.CultureInfo.InvariantCulture)
            );
            return true;
        }

        if (connectionId.StartsWith(GalaxyPrefix))
        {
            var galaxyIdPart = connectionId.Substring(GalaxyPrefix.Length);
            if (!ulong.TryParse(galaxyIdPart, out _))
            {
                return false;
            }

            identity = new TransportIdentity(PlatformGalaxy, galaxyIdPart);
            return true;
        }

        return false;
    }

    /// <summary>Human-readable transport label for logs.</summary>
    public static string GetTransportName(string connectionId)
    {
        if (string.IsNullOrEmpty(connectionId))
        {
            return "Unknown";
        }

        if (connectionId.StartsWith(GalaxyPrefix))
        {
            return "Galaxy P2P";
        }

        if (connectionId.StartsWith(SteamPrefix))
        {
            return "Steam SDR";
        }

        if (connectionId.StartsWith(LanPrefix))
        {
            return "LAN";
        }

        return "Unknown";
    }
}
