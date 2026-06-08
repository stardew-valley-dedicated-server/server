using JunimoServer.Tests.Infrastructure;

namespace JunimoServer.Tests.Containers;

/// <summary>
/// Configuration options for server containers.
/// Maps to server-settings.json values.
/// </summary>
public class ServerContainerOptions
{
    /// <summary>
    /// Docker image tag to use. Default is "local" for locally built images.
    /// </summary>
    public string ImageTag { get; set; } = "local";

    /// <summary>
    /// Name of the Docker volume containing game files.
    /// </summary>
    public string GameDataVolume { get; set; } = "server_game-data";

    /// <summary>
    /// Name of the Docker volume containing Steam session data.
    /// </summary>
    public string SteamSessionVolume { get; set; } = "server_steam-session";

    #region Game Settings (server-settings.json -> game)

    /// <summary>
    /// Farm name. Default is "Junimo".
    /// </summary>
    public string FarmName { get; set; } = "Junimo";

    /// <summary>Boot farm type written to server-settings.json.</summary>
    public FarmTypeSetting FarmType { get; set; } = FarmTypeSetting.FromIndex(0);

    /// <summary>
    /// Profit margin multiplier (1.0 = normal).
    /// </summary>
    public float ProfitMargin { get; set; } = 1.0f;

    /// <summary>
    /// Number of starting cabins to create.
    /// </summary>
    public int StartingCabins { get; set; } = 1;

    /// <summary>
    /// Spawn monsters at night: "true", "false", or "auto".
    /// </summary>
    public string SpawnMonstersAtNight { get; set; } = "auto";

    #endregion

    #region Server Settings (server-settings.json -> server)

    /// <summary>
    /// Maximum number of players allowed.
    /// </summary>
    public int MaxPlayers { get; set; } = 10;

    /// <summary>
    /// Cabin strategy: "CabinStack", "FarmhouseStack", or "None".
    /// </summary>
    public string CabinStrategy { get; set; } = "CabinStack";

    /// <summary>
    /// Whether each player has a separate wallet.
    /// </summary>
    public bool SeparateWallets { get; set; } = false;

    /// <summary>
    /// How to handle existing visible cabins: "KeepExisting" or "MoveToStack".
    /// </summary>
    public string ExistingCabinBehavior { get; set; } = "KeepExisting";

    /// <summary>
    /// Whether to allow direct IP/LAN connections.
    /// Defaults to false (matching game default). Tests using LAN should set this explicitly.
    /// </summary>
    public bool AllowIpConnections { get; set; } = false;

    #endregion

    /// <summary>
    /// Whether this server needs Steam lobby/invite code support.
    /// When false, STEAM_AUTH_URL is omitted and no lobby is created.
    /// </summary>
    public bool WithSteam { get; set; } = false;

    /// <summary>
    /// Whether to inject the TestFarmMod fixture (adds a second Data/AdditionalFarms entry)
    /// into /data/Mods at container start. See <see cref="ServerContainer"/>.
    /// </summary>
    public bool FixtureFarmMod { get; set; } = false;

    #region Container Settings

    /// <summary>
    /// Timeout for server container startup.
    /// </summary>
    public TimeSpan StartupTimeout { get; set; } = TimeSpan.FromSeconds(180);

    /// <summary>
    /// Timeout for server to become ready (have invite code).
    /// </summary>
    public TimeSpan ReadyTimeout { get; set; } = TimeSpan.FromSeconds(180);

    /// <summary>
    /// Whether to enable fail-fast mode (exit on ERROR logs).
    /// </summary>
    public bool FailFast { get; set; } = true;

    #endregion
}
