namespace JunimoServer.Services.SteamGameServer
{
    /// <summary>
    /// Shared constants for Steam and Galaxy integration.
    /// These values are derived from Stardew Valley's networking code and Steam best practices.
    /// </summary>
    public static class SteamConstants
    {
        #region App Identifiers

        /// <summary>Stardew Valley's Steam App ID.</summary>
        public const uint StardewValleyAppId = 413150;

        /// <summary>Steamworks SDK Redistributable depot ID (used for dedicated server downloads).</summary>
        public const uint SteamworksSdkAppId = 1007;

        #endregion

        #region Galaxy SDK Credentials

        /// <summary>
        /// GOG Galaxy client ID extracted from Stardew Valley's GalaxyHelper.cs.
        /// This is intentionally public - Galaxy SDK credentials are embedded in all games using the SDK.
        /// </summary>
        public const string GalaxyClientId = "48767653913349277";

        /// <summary>
        /// GOG Galaxy client secret extracted from Stardew Valley's GalaxyHelper.cs.
        /// This is intentionally public - Galaxy SDK credentials are embedded in all games using the SDK.
        /// </summary>
        public const string GalaxyClientSecret = "58be5c2e55d7f535cf8c4b6bbc09d185de90b152c8c42703cc13502465f0d04a";

        /// <summary>Display name used for Galaxy authentication when no Steam persona is available.</summary>
        public const string DefaultServerName = "GalaxyTest";

        /// <summary>
        /// Fallback Steam ID used when GameServer mode is not enabled or not yet initialized.
        /// This is an obviously fake ID that won't conflict with real Steam IDs.
        /// </summary>
        public const long FallbackSteamId = 123456789;

        #endregion

        #region Network Ports

        /// <summary>
        /// Stardew Valley's default game port for multiplayer connections.
        /// This is the port used by the vanilla game for LAN/direct connections.
        /// </summary>
        public const ushort DefaultGamePort = 24642;

        /// <summary>
        /// Standard Steam server browser query port.
        /// This is the conventional port (27015) used by Steam for server discovery.
        /// </summary>
        public const ushort DefaultQueryPort = 27015;

        #endregion

        #region Player Limits

        /// <summary>
        /// Maximum members allowed in a Steam/Galaxy lobby.
        /// Set to 16 to allow for spectators and some headroom above the player limit.
        /// </summary>
        public const int DefaultMaxLobbyMembers = 16;

        /// <summary>
        /// Default maximum players in a Stardew Valley multiplayer game.
        /// The vanilla game supports up to 8 players (1 host + 7 farmhands).
        /// </summary>
        public const int DefaultMaxPlayers = 8;

        #endregion

        #region Networking Tuning

        /// <summary>
        /// Messages larger than this threshold (in bytes) are compressed before sending.
        /// 1KB threshold balances CPU cost vs bandwidth savings - small messages aren't worth compressing.
        /// Matches the vanilla game's compression behavior.
        /// </summary>
        public const int CompressionThreshold = 1024;

        /// <summary>
        /// Steam networking socket send buffer size (1MB).
        /// Large buffer prevents dropped messages during world sync when many chunks are queued.
        /// Default Steam buffer (512KB) can overflow during initial world download.
        /// </summary>
        public const int SendBufferSize = 1048576;

        /// <summary>
        /// Interval (in frames) between logging repeated callback errors.
        /// At 60 FPS, 300 frames = 5 seconds. Prevents log spam from persistent issues
        /// while still alerting to problems.
        /// </summary>
        public const int CallbackErrorLogIntervalFrames = 300;

        #endregion

        #region Lobby Metadata Keys

        /// <summary>Steam's special metadata key for game server IP (used by server browser).</summary>
        public const string GameServerIpKey = "__gameserverIP";

        /// <summary>Steam's special metadata key for game server port (used by server browser).</summary>
        public const string GameServerPortKey = "__gameserverPort";

        /// <summary>Steam's special metadata key for game server Steam ID (used for SDR routing).</summary>
        public const string GameServerSteamIdKey = "__gameserverSteamID";

        /// <summary>Stardew Valley's protocol version key for client/server compatibility checking.</summary>
        public const string ProtocolVersionKey = "protocolVersion";

        #endregion
    }
}
