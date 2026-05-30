namespace JunimoServer.Tests.Containers;

/// <summary>
/// Configuration options for containerized game clients.
/// </summary>
public class GameClientOptions
{
    /// <summary>
    /// Connection method to use when joining the server.
    /// Default is LAN for reliability (no Steam accounts needed).
    /// </summary>
    public ConnectionMethod ConnectionMethod { get; set; } = ConnectionMethod.Lan;

    /// <summary>
    /// Docker image tag to use for the test client container.
    /// Default is "local" for locally built images.
    /// </summary>
    public string ImageTag { get; set; } = "local";

    /// <summary>
    /// Name of the Docker volume containing game files (shared with server).
    /// </summary>
    public string GameDataVolume { get; set; } = "server_game-data";

    /// <summary>
    /// Timeout for container startup (waiting for API to respond).
    /// </summary>
    public TimeSpan StartupTimeout { get; set; } = TimeSpan.FromSeconds(120);

    /// <summary>
    /// Timeout for connection attempts.
    /// </summary>
    public TimeSpan ConnectionTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Whether to expose the VNC port (5800) for visual observation.
    /// Default is true. Set to false in CI to avoid binding unnecessary ports.
    /// </summary>
    public bool ExposeVnc { get; set; } = true;

    /// <summary>
    /// URL of the steam-auth service for Steam/Galaxy connections.
    /// When set, the client container will use this to authenticate via Galaxy SDK.
    /// </summary>
    public string? SteamAuthUrl { get; set; }

    /// <summary>
    /// Steam account index to use for authentication (maps to ?account=N on steam-auth).
    /// Default is 1 (account 0 is reserved for the server).
    /// </summary>
    public int SteamAccountIndex { get; set; } = 1;
}

/// <summary>
/// Method used to connect game clients to the server.
/// </summary>
public enum ConnectionMethod
{
    /// <summary>
    /// Connect via Steam/Galaxy invite code.
    /// Requires valid Steam credentials and tests the production connection path.
    /// </summary>
    InviteCode,

    /// <summary>
    /// Connect via direct IP/LAN using Lidgren networking.
    /// More reliable for testing, doesn't require multiple Steam accounts.
    /// </summary>
    Lan
}
