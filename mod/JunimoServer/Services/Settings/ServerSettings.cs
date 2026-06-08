using JunimoServer.Services.GameCreator;
using System;

namespace JunimoServer.Services.Settings
{
    /// <summary>
    /// Lobby mode for password protection.
    /// </summary>
    public enum LobbyMode
    {
        /// <summary>All unauthenticated players wait in the same lobby cabin.</summary>
        Shared,
        /// <summary>Each player gets their own isolated lobby cabin.</summary>
        Individual
    }

    public class ServerSettings
    {
        public GameSettings Game { get; set; } = new();
        public ServerRuntimeSettings Server { get; set; } = new();
    }

    public class GameSettings
    {
        public string FarmName { get; set; } = "Junimo";
        public FarmTypeSetting FarmType { get; set; } = FarmTypeSetting.Default;
        public float ProfitMargin { get; set; } = 1.0f;
        public int StartingCabins { get; set; } = 1;
        public string SpawnMonstersAtNight { get; set; } = "auto";
        // advanced creation options
        public bool RemixBundles { get; set; } = false;
        public bool RemixMines { get; set; } = false;
        public bool CommunityCenterYear1 { get; set; } = false;
        public bool CabinLayoutNearby { get; set; } = false;
        public bool UseLegacyRandom { get; set; } = false;
        public ulong? RandomSeed { get; set; } = null;
        public int PetBreed { get; set; } = 1;
        public string PetName { get; set; } = "Apples";
        public bool MushroomCave { get; set; } = true;
        public bool BuyJoja { get; set; } = false;
    }

    public class ServerRuntimeSettings
    {
        public int MaxPlayers { get; set; } = 10;
        public string CabinStrategy { get; set; } = "CabinStack";
        public bool SeparateWallets { get; set; } = false;
        public string ExistingCabinBehavior { get; set; } = "KeepExisting";
        public bool VerboseLogging { get; set; } = false;
        public bool AllowIpConnections { get; set; } = false;

        /// <summary>
        /// Lobby mode for password protection: "Shared" or "Individual".
        /// Shared: All unauthenticated players wait in the same lobby cabin.
        /// Individual: Each player gets their own isolated lobby cabin.
        /// Default: "Shared"
        /// </summary>
        public string LobbyMode { get; set; } = "Shared";

        /// <summary>
        /// Name of the active lobby layout to use for new players.
        /// Layouts can be created and customized with !lobby commands.
        /// Default: "default"
        /// </summary>
        public string ActiveLobbyLayout { get; set; } = "default";

        /// <summary>
        /// List of Steam IDs that are automatically granted admin on join.
        /// Example: ["76561198012345678", "76561198087654321"]
        /// </summary>
        public string[] AdminSteamIds { get; set; } = Array.Empty<string>();

        /// <summary>
        /// How often (in game ticks) the server broadcasts farmer, location, and
        /// world-state deltas to peers. Lower = lower latency, higher bandwidth.
        /// Vanilla default is 3; the mod historically used 1. Clamped to [1, 60].
        /// </summary>
        public int NetworkBroadcastPeriod { get; set; } = 1;
    }
}
