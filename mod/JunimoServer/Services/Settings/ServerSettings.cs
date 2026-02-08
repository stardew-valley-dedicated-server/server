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
        public int FarmType { get; set; } = 0;
        public float ProfitMargin { get; set; } = 1.0f;
        public int StartingCabins { get; set; } = 1;
        public string SpawnMonstersAtNight { get; set; } = "auto";
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
    }
}
