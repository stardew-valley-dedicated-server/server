using System;
using HarmonyLib;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using Steamworks;

namespace JunimoServer.Services.SteamGameServer
{
    /// <summary>
    /// Service that initializes Steam in GameServer mode for SDR (Steam Datagram Relay) support.
    /// This allows vanilla Steam clients to connect via Valve's relay network, bypassing NAT issues.
    ///
    /// Key differences from client mode:
    /// - Uses GameServer.Init() instead of SteamAPI.Init()
    /// - Uses SteamGameServerNetworkingSockets instead of SteamNetworkingSockets
    /// - Supports anonymous login (no Steam credentials needed for the server)
    /// - Does NOT have SteamMatchmaking access (lobbies handled separately via Galaxy)
    /// </summary>
    public class SteamGameServerService : ModService
    {
        private static IMonitor _monitor;

        private static bool _initialized = false;
        private static CSteamID _serverSteamId;

        // Callbacks
        private static Callback<SteamServersConnected_t> _steamServersConnectedCallback;
        private static Callback<SteamServerConnectFailure_t> _steamServersConnectFailureCallback;
        private static Callback<SteamServersDisconnected_t> _steamServersDisconnectedCallback;

        /// <summary>
        /// Whether Steam GameServer is initialized and connected to Steam.
        /// </summary>
        public static bool IsInitialized => _initialized;

        /// <summary>
        /// The Steam ID of this game server. Clients use this to connect via P2P.
        /// </summary>
        public static CSteamID ServerSteamId => _serverSteamId;

        public SteamGameServerService(
            Harmony harmony,
            IMonitor monitor,
            IModHelper helper)
        {
            _monitor = monitor;

            _monitor.Log("Steam GameServer mode enabled - initializing SDR support", LogLevel.Info);

            // Register event handlers
            helper.Events.GameLoop.GameLaunched += OnGameLaunched;
            helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
            helper.Events.GameLoop.ReturnedToTitle += OnReturnedToTitle;
        }

        private void OnGameLaunched(object sender, GameLaunchedEventArgs e)
        {
            InitializeGameServer();
        }

        private void OnUpdateTicked(object sender, UpdateTickedEventArgs e)
        {
            if (!_initialized)
                return;

            // Process GameServer callbacks every frame
            // This is required for connection handling and SDR status updates
            try
            {
                GameServer.RunCallbacks();
            }
            catch (Exception ex)
            {
                // Only log occasionally to avoid spam
                if (e.IsMultipleOf(SteamConstants.CallbackErrorLogIntervalFrames)) // Every 5 seconds at 60fps
                {
                    _monitor.Log($"GameServer callback error: {ex.Message}", LogLevel.Trace);
                }
            }
        }

        private void OnReturnedToTitle(object sender, ReturnedToTitleEventArgs e)
        {
            // GameServer continues running even when returning to title
            // This allows the server to stay online between save loads
        }

        private bool InitializeGameServer()
        {
            if (_initialized)
            {
                _monitor.Log("Steam GameServer already initialized", LogLevel.Debug);
                return true;
            }

            _monitor.Log("Initializing Steam GameServer...", LogLevel.Info);

            try
            {
                // Set up callbacks before init
                _steamServersConnectedCallback = Callback<SteamServersConnected_t>.CreateGameServer(OnSteamServersConnected);
                _steamServersConnectFailureCallback = Callback<SteamServerConnectFailure_t>.CreateGameServer(OnSteamServersConnectFailure);
                _steamServersDisconnectedCallback = Callback<SteamServersDisconnected_t>.CreateGameServer(OnSteamServersDisconnected);

                // Initialize GameServer (older Steamworks.NET API - returns bool)
                bool initResult = GameServer.Init(
                    unIP: 0,                                    // Auto-select IP (0 = INADDR_ANY)
                    usGamePort: SteamConstants.DefaultGamePort,
                    usQueryPort: SteamConstants.DefaultQueryPort,
                    eServerMode: EServerMode.eServerModeAuthenticationAndSecure,
                    pchVersionString: GetGameVersion()
                );

                if (!initResult)
                {
                    _monitor.Log("GameServer.Init() failed", LogLevel.Error);
                    return false;
                }

                // Configure GameServer identity
                Steamworks.SteamGameServer.SetProduct("Stardew Valley");
                Steamworks.SteamGameServer.SetGameDescription("Stardew Valley Dedicated Server");
                Steamworks.SteamGameServer.SetDedicatedServer(true);
                Steamworks.SteamGameServer.SetMaxPlayerCount(SteamConstants.DefaultMaxPlayers);

                // Anonymous logon - no Steam account needed!
                _monitor.Log("Logging on to Steam anonymously...", LogLevel.Info);
                Steamworks.SteamGameServer.LogOnAnonymous();

                // Initialize SDR (Steam Datagram Relay)
                _monitor.Log("Initializing Steam Datagram Relay (SDR)...", LogLevel.Info);
                SteamGameServerNetworkingUtils.InitRelayNetworkAccess();

                _initialized = true;
                _monitor.Log("Steam GameServer initialized successfully!", LogLevel.Info);

                return true;
            }
            catch (DllNotFoundException ex)
            {
                _monitor.Log($"Steam SDK DLLs not found: {ex.Message}", LogLevel.Error);
                _monitor.Log("Make sure Steamworks SDK redistributables are installed", LogLevel.Error);
                return false;
            }
            catch (Exception ex)
            {
                _monitor.Log($"GameServer initialization failed: {ex.Message}", LogLevel.Error);
                _monitor.Log(ex.ToString(), LogLevel.Debug);
                return false;
            }
        }

        private static string GetGameVersion()
        {
            // Return the game version for Steam server browser
            return Game1.version ?? "1.6.15";
        }

        /// <summary>
        /// Event fired when the GameServer receives its Steam ID from Valve.
        /// Subscribe to this to know when ServerSteamId is valid.
        /// </summary>
        public static event Action<ulong> OnServerSteamIdReceived;

        private static void OnSteamServersConnected(SteamServersConnected_t callback)
        {
            _serverSteamId = Steamworks.SteamGameServer.GetSteamID();
            _monitor.Log($"Connected to Steam servers!", LogLevel.Info);
            _monitor.Log($"Server Steam ID: {_serverSteamId.m_SteamID}", LogLevel.Info);

            // Log the public IP if available
            var publicIp = Steamworks.SteamGameServer.GetPublicIP();
            if (publicIp.IsSet())
            {
                _monitor.Log($"Server public IP: {publicIp}", LogLevel.Info);
            }

            // Check relay network status
            var relayStatus = SteamGameServerNetworkingUtils.GetRelayNetworkStatus(out var details);
            _monitor.Log($"SDR relay status: {relayStatus}", LogLevel.Info);

            // Notify subscribers that the Steam ID is now available
            OnServerSteamIdReceived?.Invoke(_serverSteamId.m_SteamID);
        }

        private static void OnSteamServersConnectFailure(SteamServerConnectFailure_t callback)
        {
            _monitor.Log($"Failed to connect to Steam servers: {callback.m_eResult}", LogLevel.Error);
            if (callback.m_bStillRetrying)
            {
                _monitor.Log("Still retrying connection...", LogLevel.Info);
            }
        }

        private static void OnSteamServersDisconnected(SteamServersDisconnected_t callback)
        {
            _monitor.Log($"Disconnected from Steam servers: {callback.m_eResult}", LogLevel.Warn);
            _monitor.Log("SDR connections may be affected until reconnected", LogLevel.Warn);
        }

        /// <summary>
        /// Shuts down the Steam GameServer. Called when the mod is unloaded.
        /// </summary>
        public static void Shutdown()
        {
            if (!_initialized)
                return;

            _monitor.Log("Shutting down Steam GameServer...", LogLevel.Info);

            try
            {
                // Dispose callbacks to prevent memory leaks and potential crashes
                _steamServersConnectedCallback?.Dispose();
                _steamServersConnectFailureCallback?.Dispose();
                _steamServersDisconnectedCallback?.Dispose();
                _steamServersConnectedCallback = null;
                _steamServersConnectFailureCallback = null;
                _steamServersDisconnectedCallback = null;

                Steamworks.SteamGameServer.LogOff();
                GameServer.Shutdown();
                _initialized = false;
                _monitor.Log("Steam GameServer shutdown complete", LogLevel.Info);
            }
            catch (Exception ex)
            {
                _monitor.Log($"Error during GameServer shutdown: {ex.Message}", LogLevel.Warn);
            }
        }
    }
}
