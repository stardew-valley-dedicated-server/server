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
