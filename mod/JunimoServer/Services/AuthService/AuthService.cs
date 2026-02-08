using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using Galaxy.Api;
using Steamworks;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Network;
using StardewValley.SDKs;
using StardewValley.SDKs.Steam;
using StardewValley.SDKs.GogGalaxy;
using StardewValley.SDKs.GogGalaxy.Internal;
using StardewValley.Menus;
using StardewValley.SDKs.GogGalaxy.Listeners;
using JunimoServer.Util;
using JunimoServer.Services.SteamGameServer;

namespace JunimoServer.Services.Auth
{
    public class GalaxyAuthService : ModService
    {
        /// <summary>
        /// Static reference for Harmony patches
        /// </summary>
        private static GalaxyAuthService _instance;
        private static IMonitor _monitor;
        private static IModHelper _helper;

        /// <summary>
        /// Stored SteamHelper instance for deferred initialization.
        /// Set in SteamHelperInitialize_Prefix, used when OnServerSteamIdReceived fires.
        /// </summary>
        private static SteamHelper _pendingSteamHelper;
        private static bool _galaxyInitComplete;

        // Use constants from SteamConstants for consistency
        private static string ServerName => SteamConstants.DefaultServerName;
        private static long FallbackSteamId => SteamConstants.FallbackSteamId;
        private static string GalaxyClientId => SteamConstants.GalaxyClientId;
        private static string GalaxyClientSecret => SteamConstants.GalaxyClientSecret;

        private static IOperationalStateChangeListener stateChangeListener;
        private static IAuthListener authListener;

        /// <summary>
        /// Backing field for memoization
        /// </summary>
        private CSteamID ServerCSteamId;

        /// <summary>
        /// Steam app ticket fetcher instance (reused after authentication)
        /// </summary>
        private static SteamAppTicketFetcherHttp _steamAppTicketFetcher;

        #region Steam lobby fields (managed via steam-auth HTTP service)

        /// <summary>
        /// The Steam lobby ID created via steam-auth service
        /// </summary>
        private static ulong _steamLobbyId;

        /// <summary>
        /// Whether lobby creation has been attempted
        /// </summary>
        private static bool _lobbyCreationAttempted;

        /// <summary>
        /// Cached Steam Auth API client (singleton pattern to avoid socket exhaustion)
        /// </summary>
        private static SteamAuthApiClient _cachedApiClient;

        /// <summary>
        /// Validated STEAM_AUTH_URL (cached after first validation)
        /// </summary>
        private static string _validatedSteamAuthUrl;

        #endregion

        #region URL Validation and API Client

        /// <summary>
        /// Gets a validated STEAM_AUTH_URL or null if not set/invalid.
        /// Caches the result after first successful validation.
        /// </summary>
        private static string GetValidatedSteamAuthUrl()
        {
            // Return cached value if already validated
            if (_validatedSteamAuthUrl != null)
                return _validatedSteamAuthUrl;

            var steamAuthUrl = Environment.GetEnvironmentVariable("STEAM_AUTH_URL");
            if (string.IsNullOrEmpty(steamAuthUrl))
            {
                return null;
            }

            // Validate URL format
            if (!Uri.TryCreate(steamAuthUrl, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                _monitor.Log($"STEAM_AUTH_URL is not a valid HTTP/HTTPS URL: {steamAuthUrl}", LogLevel.Error);
                return null;
            }

            _validatedSteamAuthUrl = steamAuthUrl;
            return _validatedSteamAuthUrl;
        }

        /// <summary>
        /// Gets or creates a cached SteamAuthApiClient instance.
        /// Uses singleton pattern to avoid socket exhaustion from creating new HttpClients.
        /// </summary>
        private static SteamAuthApiClient GetOrCreateApiClient()
        {
            var steamAuthUrl = GetValidatedSteamAuthUrl();
            if (steamAuthUrl == null)
                return null;

            if (_cachedApiClient == null)
            {
                _cachedApiClient = new SteamAuthApiClient(steamAuthUrl);
            }

            return _cachedApiClient;
        }

        #endregion

        /// <summary>
        /// Called when SteamGameServerService receives a valid Steam ID from Valve.
        /// This completes any deferred Galaxy initialization and creates the Steam lobby.
        /// </summary>
        private static void OnServerSteamIdReceived(ulong steamId)
        {
            _monitor.Log($"Received GameServer Steam ID: {steamId}", LogLevel.Info);

            // Complete deferred Galaxy initialization if we were waiting for GameServer
            if (_pendingSteamHelper != null && !_galaxyInitComplete)
            {
                _monitor.Log("GameServer now ready, completing deferred Galaxy initialization...", LogLevel.Info);
                PerformDeferredInitialization(_pendingSteamHelper);
                _pendingSteamHelper = null;
            }
            else
            {
                // Galaxy init already done, just create the lobby
                CreateSteamLobbyViaHttpAsync();
            }
        }

        public GalaxyAuthService(
            IMonitor monitor,
            IModHelper helper,
            Harmony harmony,
            SteamGameServerService steamGameServerService)  // Dependency ensures correct init order
        {
            if (_instance != null)
                throw new InvalidOperationException("AuthService already initialized - only one instance allowed");

            // Set instance variables for use in static harmony patches
            _instance = this;
            _monitor = monitor;
            _helper = helper;

            var galaxyAuthServiceType = typeof(GalaxyAuthService);

            // Subscribe to Steam ID assignment event to create lobby at the right time
            SteamGameServerService.OnServerSteamIdReceived += OnServerSteamIdReceived;

            // Handle race condition: If Steam ID was already received before we subscribed,
            // manually trigger the handler. This ensures Galaxy init happens even if the
            // SteamGameServerService initialized faster than expected.
            if (SteamGameServerService.IsInitialized && SteamGameServerService.ServerSteamId.IsValid())
            {
                _monitor.Log("Steam ID already available at subscription time, triggering handler", LogLevel.Debug);
                OnServerSteamIdReceived(SteamGameServerService.ServerSteamId.m_SteamID);
            }

            // Steam GameServer mode: Patch SteamHelper to use GameServer APIs instead of Client APIs
            _monitor.Log("Registering Steam GameServer API patches for SteamHelper", LogLevel.Debug);

            // Patch SteamHelper.Initialize to use GameServer.Init() instead of SteamAPI.Init()
            harmony.Patch(
                original: AccessTools.Method(typeof(SteamHelper), nameof(SteamHelper.Initialize)),
                prefix: new HarmonyMethod(galaxyAuthServiceType, nameof(SteamHelperInitialize_Prefix)));

            // Patch SteamHelper.Update to use GameServer.RunCallbacks() instead of SteamAPI.RunCallbacks()
            harmony.Patch(
                original: AccessTools.Method(typeof(SteamHelper), nameof(SteamHelper.Update)),
                prefix: new HarmonyMethod(galaxyAuthServiceType, nameof(SteamHelperUpdate_Prefix)));

            // Patch SteamHelper.Shutdown to use GameServer.Shutdown() instead of SteamAPI.Shutdown()
            harmony.Patch(
                original: AccessTools.Method(typeof(SteamHelper), nameof(SteamHelper.Shutdown)),
                prefix: new HarmonyMethod(galaxyAuthServiceType, nameof(SteamHelperShutdown_Prefix)));

            // Patch SteamNetServer.initialize to skip - it uses Steam Client API (SteamMatchmaking.CreateLobby)
            // which isn't available in GameServer mode. We use SteamKit2 for lobby creation instead.
            // See decompiled: StardewValley.SDKs.Steam.SteamNetServer.initialize()
            var steamNetServerType = AccessTools.TypeByName("StardewValley.SDKs.Steam.SteamNetServer");
            if (steamNetServerType != null)
            {
                harmony.Patch(
                    original: AccessTools.Method(steamNetServerType, "initialize"),
                    prefix: new HarmonyMethod(galaxyAuthServiceType, nameof(SteamNetServer_Initialize_Prefix)));
                _monitor.Log("Patched SteamNetServer.initialize to skip (using SteamKit2 for lobbies)", LogLevel.Debug);
            }

            // Postfix on GetInviteCode to capture invite code for file/banner
            // (More reliable than patching the private onGalaxyLobbyEnter callback)
            harmony.Patch(
                original: AccessTools.Method(typeof(GalaxySocket), nameof(GalaxySocket.GetInviteCode)),
                postfix: new HarmonyMethod(galaxyAuthServiceType, nameof(GalaxySocket_GetInviteCode_Postfix)));

            // These patches apply to both modes - provide fake Steam IDs when Client API is unavailable
            harmony.Patch(
                original: AccessTools.Method(typeof(Steamworks.SteamUser), nameof(Steamworks.SteamUser.GetSteamID)),
                prefix: new HarmonyMethod(galaxyAuthServiceType, nameof(SteamUser_GetSteamID_Prefix)));

            harmony.Patch(
                original: AccessTools.Method(typeof(Steamworks.SteamFriends), nameof(Steamworks.SteamFriends.GetPersonaName)),
                prefix: new HarmonyMethod(galaxyAuthServiceType, nameof(SteamFriends_GetPersonaName_Prefix)));
        }

        // SteamHelper reflection helpers
        private static void IncrementSteamConnectionProgress(SteamHelper instance) => _helper.Reflection.GetProperty<int>(instance, "ConnectionProgress").SetValue(instance.ConnectionProgress + 1);
        private static void SetSteamActive(SteamHelper instance, bool value) => _helper.Reflection.GetField<bool>(instance, "active").SetValue(value);
        private static void SetSteamConnectionFinished(SteamHelper instance, bool value) => _helper.Reflection.GetProperty<bool>(instance, "ConnectionFinished").SetValue(value);
        private static void SetSteamGalaxyConnected(SteamHelper instance, bool value) => _helper.Reflection.GetProperty<bool>(instance, "GalaxyConnected").SetValue(value);
        private static void SetSteamNetworking(SteamHelper instance, SDKNetHelper networking) => _helper.Reflection.GetField<SDKNetHelper>(instance, "networking").SetValue(networking);

        /// <summary>
        /// Gets the current SDK via reflection (Program.sdk is internal).
        /// </summary>
        private static SDKHelper GetCurrentSdk()
        {
            var sdkProperty = AccessTools.PropertyGetter(typeof(Program), "sdk");
            return (SDKHelper)sdkProperty?.Invoke(null, null);
        }

        /// <summary>
        /// Fix for race condition: If the GameServer was created before Galaxy auth completed,
        /// it won't have a GalaxyNetServer. This method late-adds one if needed.
        /// </summary>
        private static void TryLateAddGalaxyServer()
        {
            try
            {
                var sdk = GetCurrentSdk();

                // Check if server exists and Networking is available
                if (Game1.server == null || sdk?.Networking == null)
                {
                    _monitor.Log("Cannot late-add Galaxy server: server or networking not ready", LogLevel.Debug);
                    return;
                }

                // Access the internal 'servers' list via reflection
                var serversField = _helper.Reflection.GetField<List<Server>>(Game1.server, "servers");
                var servers = serversField.GetValue();

                // Check if a GalaxyNetServer already exists
                if (servers.Any(s => s.GetType().Name == "GalaxyNetServer"))
                {
                    _monitor.Log("GalaxyNetServer already exists, skipping late-add", LogLevel.Debug);
                    return;
                }

                // Late-add the Galaxy server
                _monitor.Log("Late-adding Galaxy server (race condition recovery)...", LogLevel.Info);
                var galaxyServer = sdk.Networking.CreateServer(Game1.server);
                if (galaxyServer != null)
                {
                    servers.Add(galaxyServer);
                    galaxyServer.initialize();
                    _monitor.Log("Galaxy server added successfully, invite codes should now work.", LogLevel.Info);

                    // Now that Galaxy server exists, update it with Steam lobby ID if available
                    if (_steamLobbyId != 0)
                    {
                        _monitor.Log("Updating Galaxy lobby with Steam lobby ID after late-add...", LogLevel.Debug);
                        UpdateGalaxyLobbyWithSteamLobbyId();
                    }
                }
                else
                {
                    _monitor.Log("CreateServer returned null - Galaxy may not be fully connected yet", LogLevel.Warn);
                }
            }
            catch (Exception ex)
            {
                _monitor.Log($"Failed to late-add Galaxy server: {ex.Message}", LogLevel.Error);
                _monitor.Log(ex.ToString(), LogLevel.Debug);
            }
        }

        #region SteamHelper patches (GameServer mode)

        /// <summary>
        /// Replaces SteamHelper.Initialize to use GameServer APIs instead of Steam Client APIs.
        /// This allows the server to run without a Steam client while still using Steam networking.
        /// </summary>
        private static bool SteamHelperInitialize_Prefix(SteamHelper __instance)
        {
            _monitor.Log("Initializing SteamHelper with GameServer API...", LogLevel.Info);

            // Always set active so Update() is called (needed for Galaxy callbacks)
            SetSteamActive(__instance, true);

            // Check if GameServer is already ready with a valid Steam ID
            if (SteamGameServerService.IsInitialized && SteamGameServerService.ServerSteamId.IsValid())
            {
                // GameServer ready with Steam ID - do full init now
                _monitor.Log("GameServer already ready, initializing Galaxy immediately", LogLevel.Debug);
                PerformDeferredInitialization(__instance);
            }
            else
            {
                // GameServer not ready yet - store instance for OnServerSteamIdReceived callback
                _monitor.Log("GameServer not yet ready, deferring Galaxy init to OnServerSteamIdReceived", LogLevel.Debug);
                _pendingSteamHelper = __instance;
            }

            return false; // Skip original
        }

        /// <summary>
        /// Creates a Steam lobby via the steam-auth HTTP service.
        /// This delegates lobby creation to the steam-auth container which has the credentials.
        /// </summary>
        private static void CreateSteamLobbyViaHttpAsync()
        {
            if (_lobbyCreationAttempted)
            {
                _monitor.Log("Lobby creation already attempted", LogLevel.Debug);
                return;
            }

            if (!SteamGameServerService.IsInitialized)
            {
                _monitor.Log("Cannot create Steam lobby: GameServer not initialized", LogLevel.Warn);
                return;
            }

            var gameServerSteamId = SteamGameServerService.ServerSteamId.m_SteamID;
            if (gameServerSteamId == 0)
            {
                _monitor.Log("Cannot create Steam lobby: GameServer Steam ID not yet assigned (waiting for OnSteamServersConnected)", LogLevel.Debug);
                return;
            }

            _lobbyCreationAttempted = true;
            _monitor.Log($"Creating Steam lobby via steam-auth service, GameServer ID: {gameServerSteamId}", LogLevel.Info);

            // Run async to avoid blocking
            Task.Run(() =>
            {
                try
                {
                    var apiClient = GetOrCreateApiClient();
                    if (apiClient == null)
                    {
                        _monitor.Log("STEAM_AUTH_URL not set or invalid - Steam lobby creation disabled. Only Galaxy invites will work.", LogLevel.Warn);
                        return;
                    }

                    // CurrentPlayerLimit can be -1 (not initialized) or 0 (invalid)
                    // Use default lobby size in those cases
                    var currentLimit = Game1.netWorldState.Value?.CurrentPlayerLimit ?? -1;
                    var maxLobbyMembers = currentLimit > 0 ? currentLimit : SteamConstants.DefaultMaxLobbyMembers;
                    _monitor.Log($"Creating Steam lobby with maxMembers={maxLobbyMembers} (CurrentPlayerLimit={currentLimit})", LogLevel.Info);
                    var result = apiClient.CreateLobby(
                        gameServerSteamId: gameServerSteamId,
                        protocolVersion: Multiplayer.protocolVersion,
                        maxMembers: maxLobbyMembers);

                    if (result != null && !string.IsNullOrEmpty(result.lobby_id))
                    {
                        if (ulong.TryParse(result.lobby_id, out var lobbyId))
                        {
                            _steamLobbyId = lobbyId;
                            _monitor.Log($"Steam lobby created via HTTP: {_steamLobbyId}", LogLevel.Info);

                            // Update Galaxy lobby with Steam lobby ID
                            UpdateGalaxyLobbyWithSteamLobbyId();
                        }
                        else
                        {
                            _monitor.Log($"Failed to parse Steam lobby ID: {result.lobby_id}", LogLevel.Error);
                        }
                    }
                    else
                    {
                        _monitor.Log("Failed to create Steam lobby: empty response", LogLevel.Error);
                    }
                }
                catch (Exception ex)
                {
                    _monitor.Log($"Failed to create Steam lobby via HTTP: {ex.Message}", LogLevel.Error);
                    _monitor.Log(ex.ToString(), LogLevel.Debug);
                }
            });
        }

        /// <summary>
        /// Updates the Galaxy lobby with the Steam lobby ID so vanilla clients can find it.
        /// </summary>
        private static void UpdateGalaxyLobbyWithSteamLobbyId()
        {
            if (_steamLobbyId == 0)
            {
                _monitor.Log("Cannot update Galaxy lobby: Steam lobby ID not set", LogLevel.Warn);
                return;
            }

            try
            {
                // Find the GalaxyNetServer and set lobby data
                if (Game1.server is StardewValley.Network.GameServer gameServer)
                {
                    var servers = _helper.Reflection.GetField<List<Server>>(gameServer, "servers").GetValue();
                    foreach (var server in servers)
                    {
                        if (server is GalaxyNetServer galaxyServer)
                        {
                            // Set the SteamLobbyId in Galaxy lobby metadata
                            // This is what vanilla SteamNetClient reads to join the Steam lobby
                            galaxyServer.setLobbyData("SteamLobbyId", _steamLobbyId.ToString());
                            _monitor.Log($"Galaxy lobby updated with SteamLobbyId: {_steamLobbyId}", LogLevel.Info);
                            return;
                        }
                    }

                    _monitor.Log("Could not find GalaxyNetServer to update lobby data", LogLevel.Warn);
                }
                else
                {
                    _monitor.Log("Game1.server is not a GameServer instance", LogLevel.Warn);
                }
            }
            catch (Exception ex)
            {
                _monitor.Log($"Failed to update Galaxy lobby: {ex.Message}", LogLevel.Error);
                _monitor.Log(ex.ToString(), LogLevel.Debug);
            }
        }

        private static ServerPrivacy? _lastSteamLobbyPrivacy = null;

        /// <summary>
        /// Sets the privacy level on the Steam lobby via the steam-auth HTTP service.
        /// Called by SteamGameServerNetServer.setPrivacy().
        /// </summary>
        public static void SetSteamLobbyPrivacy(ServerPrivacy privacy)
        {
            // Only update if privacy actually changed
            if (_lastSteamLobbyPrivacy == privacy)
                return;

            if (_steamLobbyId == 0)
                return;

            try
            {
                var apiClient = GetOrCreateApiClient();
                if (apiClient == null)
                    return;

                // Force Public for dedicated server - invite codes need joinable lobbies
                apiClient.SetLobbyPrivacy(
                    lobbyId: _steamLobbyId,
                    privacy: "public");

                _lastSteamLobbyPrivacy = privacy;
                _monitor.Log($"Steam lobby privacy set to Public (game requested: {privacy})", LogLevel.Debug);
            }
            catch (Exception ex)
            {
                _monitor.Log($"Failed to set Steam lobby privacy: {ex.Message}", LogLevel.Error);
                _monitor.Log(ex.ToString(), LogLevel.Debug);
            }
        }

        /// <summary>
        /// Sets lobby data on the Steam lobby via the steam-auth HTTP service.
        /// Called by SteamGameServerNetServer.setLobbyData().
        /// </summary>
        public static void SetSteamLobbyData(string key, string value)
        {
            if (_steamLobbyId == 0)
            {
                _monitor.Log($"Cannot set Steam lobby data '{key}': lobby not ready", LogLevel.Debug);
                return;
            }

            try
            {
                var apiClient = GetOrCreateApiClient();
                if (apiClient == null)
                    return;

                apiClient.SetLobbyData(
                    lobbyId: _steamLobbyId,
                    metadata: new Dictionary<string, string> { [key] = value });

                _monitor.Log($"Steam lobby data set: {key}={value}", LogLevel.Debug);
            }
            catch (Exception ex)
            {
                _monitor.Log($"Failed to set Steam lobby data: {ex.Message}", LogLevel.Error);
                _monitor.Log(ex.ToString(), LogLevel.Debug);
            }
        }

        /// <summary>
        /// Replaces SteamHelper.Update to use GameServer.RunCallbacks instead of SteamAPI.RunCallbacks.
        /// </summary>
        private static bool SteamHelperUpdate_Prefix(SteamHelper __instance)
        {
            var active = _helper.Reflection.GetField<bool>(__instance, "active").GetValue();
            if (active)
            {
                // Use GameServer callbacks instead of SteamAPI callbacks
                Steamworks.GameServer.RunCallbacks();

                // Process Galaxy callbacks (if initialized)
                if (_galaxyInitComplete)
                {
                    try
                    {
                        GalaxyInstance.ProcessData();
                    }
                    catch (Exception ex)
                    {
                        // Galaxy may have disconnected - only log at Trace level to avoid spam
                        _monitor.Log($"GalaxyInstance.ProcessData() failed: {ex.Message}", LogLevel.Trace);
                    }
                }
            }

            Game1.game1.IsMouseVisible = Game1.paused || Game1.options.hardwareCursor;
            return false; // Skip original
        }

        /// <summary>
        /// Performs deferred initialization of Galaxy after GameServer is ready.
        /// Called either immediately from SteamHelperInitialize_Prefix (if GameServer ready)
        /// or later from OnServerSteamIdReceived (when Steam ID arrives).
        /// </summary>
        private static void PerformDeferredInitialization(SteamHelper steamHelper)
        {
            if (_galaxyInitComplete)
            {
                _monitor.Log("Galaxy already initialized, skipping", LogLevel.Debug);
                return;
            }

            try
            {
                _monitor.Log($"GameServer active with Steam ID: {SteamGameServerService.ServerSteamId.m_SteamID}", LogLevel.Info);

                // Initialize Galaxy SDK for lobby matchmaking
                _monitor.Log("Initializing Galaxy SDK for lobby support...", LogLevel.Debug);
                GalaxyInstance.Init(new InitParams(GalaxyClientId, GalaxyClientSecret, "."));

                // Create Galaxy auth listener
                authListener = _instance.CreateSteamHelperGalaxyAuthListener(steamHelper);
                stateChangeListener = _instance.CreateSteamHelperGalaxyStateChangeListener(steamHelper);

                // Sign into Galaxy using our Steam auth service
                IncrementSteamConnectionProgress(steamHelper);

                var steamAuthUrl = Environment.GetEnvironmentVariable("STEAM_AUTH_URL");
                var isAuthenticated = !string.IsNullOrEmpty(steamAuthUrl)
                    ? _instance.UseExternalSteamAuth(steamAuthUrl)
                    : _instance.ShowLoginPrompt();

                if (!isAuthenticated)
                {
                    _monitor.Log("Steam authentication skipped, Galaxy features unavailable", LogLevel.Warn);
                    SetSteamNetworking(steamHelper, CreateSteamNetHelper());
                    SetSteamConnectionFinished(steamHelper, true);
                    return;
                }

                _galaxyInitComplete = true;

                IncrementSteamConnectionProgress(steamHelper);
                _instance.SignIntoGalaxy();
                IncrementSteamConnectionProgress(steamHelper);

                // Create Steam lobby via steam-auth HTTP service
                CreateSteamLobbyViaHttpAsync();
            }
            catch (Exception ex)
            {
                _monitor.Log($"Deferred initialization failed: {ex.Message}", LogLevel.Error);
                _monitor.Log(ex.ToString(), LogLevel.Debug);
                SetSteamConnectionFinished(steamHelper, true);
            }
        }

        /// <summary>
        /// Replaces SteamHelper.Shutdown to use GameServer.Shutdown instead of SteamAPI.Shutdown.
        /// </summary>
        private static bool SteamHelperShutdown_Prefix()
        {
            _monitor.Log("Shutting down GameServer...", LogLevel.Debug);

            // Reset lobby state
            _steamLobbyId = 0;
            _lobbyCreationAttempted = false;

            // Reset Galaxy init state
            _galaxyInitComplete = false;
            _pendingSteamHelper = null;

            Steamworks.GameServer.Shutdown();
            return false; // Skip original
        }

        /// <summary>
        /// Skips SteamNetServer.initialize() in GameServer mode.
        /// SteamNetServer uses Steam Client API (SteamMatchmaking.CreateLobby) which isn't available.
        /// We use SteamKit2 for lobby creation instead via CreateSteamLobbyAsync().
        /// </summary>
        private static bool SteamNetServer_Initialize_Prefix()
        {
            _monitor.Log("Skipping SteamNetServer.initialize() - using SteamKit2 for lobby creation", LogLevel.Debug);
            return false; // Skip original - don't try to use Steam Client API
        }

        /// <summary>
        /// Creates a SteamNetHelper instance via reflection (it's internal).
        /// </summary>
        private static SDKNetHelper CreateSteamNetHelper()
        {
            var type = AccessTools.TypeByName("StardewValley.SDKs.Steam.SteamNetHelper");
            return (SDKNetHelper)Activator.CreateInstance(type);
        }

        /// <summary>
        /// Creates a Galaxy auth listener for SteamHelper mode.
        /// </summary>
        private IAuthListener CreateSteamHelperGalaxyAuthListener(SteamHelper steamHelper)
        {
            var listenerType = AccessTools.TypeByName("StardewValley.SDKs.GogGalaxy.Listeners.GalaxyAuthListener");

            Action onSuccess = () =>
            {
                _monitor.Log("Galaxy auth success (GameServer mode)", LogLevel.Debug);
                IncrementSteamConnectionProgress(steamHelper);
            };

            Action<IAuthListener.FailureReason> onFailure = (reason) =>
            {
                _monitor.Log($"Galaxy auth failure: {reason}", LogLevel.Error);
                // Still create networking even on Galaxy failure
                if (steamHelper.Networking == null)
                {
                    SetSteamNetworking(steamHelper, CreateSteamNetHelper());
                }
                SetSteamConnectionFinished(steamHelper, true);
                SetSteamGalaxyConnected(steamHelper, false);
            };

            Action onLost = () =>
            {
                _monitor.Log("Galaxy auth lost", LogLevel.Error);
                if (steamHelper.Networking == null)
                {
                    SetSteamNetworking(steamHelper, CreateSteamNetHelper());
                }
                SetSteamConnectionFinished(steamHelper, true);
                SetSteamGalaxyConnected(steamHelper, false);
            };

            return (IAuthListener)Activator.CreateInstance(listenerType, onSuccess, onFailure, onLost);
        }

        /// <summary>
        /// Creates a Galaxy state change listener for SteamHelper mode.
        /// </summary>
        private IOperationalStateChangeListener CreateSteamHelperGalaxyStateChangeListener(SteamHelper steamHelper)
        {
            var listenerType = AccessTools.TypeByName("StardewValley.SDKs.GogGalaxy.Listeners.GalaxyOperationalStateChangeListener");

            var onStateChange = new Action<uint>((operationalState) =>
            {
                if (steamHelper.Networking != null)
                    return;

                if ((operationalState & 1) != 0)
                {
                    _monitor.Log("Galaxy signed in (GameServer mode)", LogLevel.Debug);
                    IncrementSteamConnectionProgress(steamHelper);
                }

                if ((operationalState & 2) != 0)
                {
                    _monitor.Log("Galaxy logged on (GameServer mode)", LogLevel.Debug);
                    SetSteamNetworking(steamHelper, CreateSteamNetHelper());
                    IncrementSteamConnectionProgress(steamHelper);
                    SetSteamConnectionFinished(steamHelper, true);
                    SetSteamGalaxyConnected(steamHelper, true);

                    // Late-add Galaxy server if GameServer was created before Galaxy connected
                    TryLateAddGalaxyServer();
                }
            });

            return (IOperationalStateChangeListener)Activator.CreateInstance(listenerType, onStateChange);
        }

        /// <summary>
        /// Postfix on GetInviteCode to capture invite code for file/banner.
        /// In GameServer mode, the original "S" prefix is correct.
        /// Also updates the Galaxy lobby with Steam lobby ID if available.
        /// </summary>
        private static void GalaxySocket_GetInviteCode_Postfix(string __result)
        {
            if (string.IsNullOrEmpty(__result))
                return;

            try
            {
                _monitor.Log($"Galaxy invite code generated: {__result}", LogLevel.Debug);
                InviteCodeFile.Write(__result, _monitor);
                ServerBanner.Print(_monitor, _helper);

                // If Steam lobby was already created, update Galaxy lobby with Steam lobby ID
                if (_steamLobbyId != 0)
                {
                    _monitor.Log("Steam lobby already exists, updating Galaxy lobby now...", LogLevel.Debug);
                    UpdateGalaxyLobbyWithSteamLobbyId();
                }
            }
            catch (Exception ex)
            {
                _monitor.Log($"Failed to capture invite code: {ex.Message}", LogLevel.Error);
                _monitor.Log(ex.ToString(), LogLevel.Debug);
            }
        }

        #endregion

        #region Steam ID patches

        /// <summary>
        /// Provide Steam ID for `HostSteamId`, using the real GameServer Steam ID when available.
        /// </summary>
        private static bool SteamUser_GetSteamID_Prefix(ref CSteamID __result)
        {
            // Use real GameServer Steam ID if available, otherwise use fallback
            if (SteamGameServerService.IsInitialized && SteamGameServerService.ServerSteamId.IsValid())
            {
                __result = SteamGameServerService.ServerSteamId;
                _monitor.Log($"Using GameServer Steam ID: {__result.m_SteamID}", LogLevel.Trace);
            }
            else
            {
                if (_instance.ServerCSteamId == default)
                {
                    _instance.ServerCSteamId = new CSteamID((ulong)FallbackSteamId);
                }
                __result = _instance.ServerCSteamId;
                _monitor.Log($"Using fallback Steam ID: {FallbackSteamId} (GameServer not ready)", LogLevel.Trace);
            }

            return false;
        }

        /// <summary>
        /// Provide the server display name for `HostDisplayName/StardewDisplayName`, bypassing Steam Client SDK calls.<br/><br/>
        ///  - `StardewDisplayName` is only set in SteamHelper, but we replaced it with GalaxyHelper<br/>
        ///  - `HostDisplayName` is the fallback, but calls `SteamFriends.GetPersonaName()` which is not available without Steam<br/>
        /// </summary>
        private static bool SteamFriends_GetPersonaName_Prefix(ref string __result)
        {
            __result = ServerName;
            _monitor.Log($"GalaxySDK using name: {__result}", LogLevel.Trace);
            return false;
        }

        #endregion

        #region Shared helpers

        /// <summary>
        /// Replicate original code for `GalaxyHelper.Initialize`.
        /// </summary>
        private IAuthListener CreateGalaxyAuthListener(GalaxyHelper galaxyHelper)
        {
            var listenerType = AccessTools.TypeByName("StardewValley.SDKs.GogGalaxy.Listeners.GalaxyAuthListener");
            var onGalaxyAuthSuccessOriginal = _helper.Reflection.GetMethod(galaxyHelper, "onGalaxyAuthSuccess");
            var onGalaxyAuthFailureOriginal = _helper.Reflection.GetMethod(galaxyHelper, "onGalaxyAuthFailure");

            var onGalaxyAuthSuccess = new Action(() =>
            {
                _monitor.Log($"GalaxySDK auth success", LogLevel.Trace);
                onGalaxyAuthSuccessOriginal.Invoke();
            });

            var onGalaxyAuthFailure = new Action<IAuthListener.FailureReason>((reason) =>
            {
                _monitor.Log($"GalaxySDK auth failed: {reason}", LogLevel.Error);
                onGalaxyAuthFailureOriginal.Invoke(reason);
            });

            var onGalaxyAuthLost = new Action(() =>
            {
                _monitor.Log("GalaxySDK auth lost, signing in again...", LogLevel.Info);

                try
                {
                    _monitor.Log("GalaxySDK attempting sign out...", LogLevel.Info);
                    GalaxyInstance.User().SignOut();
                }
                catch (Exception e)
                {
                    _monitor.Log($"GalaxySDK sign out failed: {e.Message}", LogLevel.Error);
                    _monitor.Log(e.ToString(), LogLevel.Debug);
                }

                _instance?.SignIntoGalaxy();
            });

            return (IAuthListener)Activator.CreateInstance(listenerType, onGalaxyAuthSuccess, onGalaxyAuthFailure, onGalaxyAuthLost);
        }

        /// <summary>
        /// Replicate original code for `GalaxyHelper.Initialize`, with modification for `StardewDisplayName`.
        /// </summary>
        private IOperationalStateChangeListener CreateGalaxyStateChangeListener(GalaxyHelper galaxyHelper)
        {
            var listenerType = AccessTools.TypeByName("StardewValley.SDKs.GogGalaxy.Listeners.GalaxyOperationalStateChangeListener");
            var onGalaxyStateChangeOriginal = _helper.Reflection.GetMethod(galaxyHelper, "onGalaxyStateChange");

            var onGalaxyStateChange = new Action<uint>((num) =>
            {
                _monitor.Log($"GalaxySDK auth state changed to '{num}'", LogLevel.Info);
                onGalaxyStateChangeOriginal.Invoke(num);

                // Fix for race condition: If "Galaxy logged on" (bit 2) fires after the server
                // already started, the GameServer constructor would have skipped adding a Galaxy
                // server because Networking was null at that time. Late-add it now.
                if ((num & 2u) != 0)
                {
                    TryLateAddGalaxyServer();
                }

                // TODO: Come back to this, should have low priority
                // By default, `StardewDisplayName` is set in `SteamHelper` but not in `GalaxyHelper`.
                // We do so here to ensure that clients skip `GalaxyInstance.Friends().GetFriendPersonaName`,
                // which would fail because the server is usually not a friend of the client.
                // try
                // {
                //     var key = "StardewDisplayName";
                //     var value = ServerName;
                //     _monitor.Log("Setting user data: 'StardewDisplayName'.", LogLevel.Error);
                //     _monitor.Log(_monitor.Dump(new { key, value }), LogLevel.Error);
                //     GalaxyInstance.User().SetUserData(key, value);
                // }
                // catch (Exception exception)
                // {
                //     _monitor.Log("Failed to set 'StardewDisplayName'.", LogLevel.Error);
                //     _monitor.Log(exception.ToString(), LogLevel.Error);
                // }
            });

            return (IOperationalStateChangeListener)Activator.CreateInstance(listenerType, onGalaxyStateChange);
        }

        /// <summary>
        /// Get the encrypted app ticket from Steam without using a real Steam client.
        /// </summary>
        private byte[] GetEncryptedAppTicketSteam()
        {
            _monitor.Log("GalaxySDK retrieving steam app ticket...", LogLevel.Debug);

            // Use the already-authenticated fetcher (created in EnsureAuthenticatedNow or UseExternalSteamAuth)
            if (_steamAppTicketFetcher == null)
            {
                throw new InvalidOperationException("Steam authentication was not completed. Call EnsureAuthenticatedNow first.");
            }

            // Get the encrypted app ticket
            var steamAppTicketFetcherResult = _steamAppTicketFetcher.GetTicket();
            return Convert.FromBase64String(steamAppTicketFetcherResult.Ticket);
        }

        private bool SignIntoGalaxy()
        {
            _monitor.Log("GalaxySDK signing in...", LogLevel.Debug);

            var appTicket = GetEncryptedAppTicketSteam();
            var appTicketLength = Convert.ToUInt32(appTicket.Length);

            GalaxyInstance.User().SignInSteam(appTicket, appTicketLength, ServerName);

            return true;
        }

        /// <summary>
        /// Use external steam-auth service (two-container setup).
        /// Authentication is already done via 'docker compose run -it steam-auth setup'.
        /// </summary>
        private bool UseExternalSteamAuth(string steamAuthUrl)
        {
            // Create fetcher in "external" mode - no login flow, just fetch tickets
            _steamAppTicketFetcher = new SteamAppTicketFetcherHttp(
                monitor: _monitor,
                steamAuthUrl: steamAuthUrl,
                timeoutMs: 30000,
                externalMode: true  // Skip login flow, assume already authenticated
            );

            // Verify the service is healthy and logged in
            try
            {
                _steamAppTicketFetcher.VerifyServiceReady();
                _monitor.Log("Steam-auth service is ready ✓", LogLevel.Info);
                return true;
            }
            catch (Exception ex)
            {
                _monitor.Log($"Steam-auth service not ready: {ex.Message}", LogLevel.Error);
                _monitor.Log("Make sure you ran: docker compose run -it steam-auth setup", LogLevel.Error);
                return false;
            }
        }

        private bool ShowLoginPrompt()
        {
            _monitor.Log("***********************************************************************", LogLevel.Info);
            _monitor.Log("*                                                                     *", LogLevel.Info);
            _monitor.Log("*    Choose Steam authentication method:                              *", LogLevel.Info);
            _monitor.Log("*                                                                     *", LogLevel.Info);
            _monitor.Log("*    [1] Use credentials from config (STEAM_USERNAME/STEAM_PASSWORD)  *", LogLevel.Info);
            _monitor.Log("*    [2] Enter credentials now (prompt)                               *", LogLevel.Info);
            _monitor.Log("*    [3] Use QR code (scan with Steam mobile app)                     *", LogLevel.Info);
            _monitor.Log("*    [n] Skip authentication (no invite codes)                        *", LogLevel.Info);
            _monitor.Log("*                                                                     *", LogLevel.Info);
            _monitor.Log("***********************************************************************", LogLevel.Info);

            // Wait for expected user input
            var currentValue = "";
            var attemptCount = 0;
            while (currentValue != "1" && currentValue != "2" && currentValue != "3" && currentValue != "n")
            {
                if (attemptCount > 0)
                {
                    _monitor.Log("Type '1', '2', '3', or 'n' to continue:", LogLevel.Info);
                }
                currentValue = Console.ReadLine();
                attemptCount++;
            }

            if (currentValue == "n")
            {
                // Skip authentication - return false to indicate skip
                return false;
            }

            if (currentValue == "1")
            {
                // Verify that env vars are actually set
                var envUser = Environment.GetEnvironmentVariable("STEAM_USERNAME");
                var envPass = Environment.GetEnvironmentVariable("STEAM_PASSWORD");

                if (string.IsNullOrEmpty(envUser) || string.IsNullOrEmpty(envPass))
                {
                    _monitor.Log("", LogLevel.Info);
                    _monitor.Log("ERROR: STEAM_USERNAME and STEAM_PASSWORD environment variables are not set!", LogLevel.Error);
                    _monitor.Log("Please set these variables in your .env file or choose a different option.", LogLevel.Info);
                    _monitor.Log("", LogLevel.Info);

                    // Retry - show prompt again
                    return ShowLoginPrompt();
                }

                _monitor.Log("Using credentials from environment variables ✓", LogLevel.Info);

                // Authenticate immediately with env var credentials
                EnsureAuthenticatedNow(envUser, envPass);
                return true;
            }

            if (currentValue == "3")
            {
                // QR code login - force fresh login
                EnsureAuthenticatedNow("USE_QR_CODE", "");
                return true;
            }

            // Option 2: Prompt for credentials
            var shouldPromptForCredentials = currentValue == "2";
            if (shouldPromptForCredentials)
            {
                // Signal password mode to command-loop for the NEXT read after username
                var passwordModeFile = "/tmp/smapi-password-mode";
                try
                {
                    System.IO.File.WriteAllText(passwordModeFile, "1");
                }
                catch (Exception ex)
                {
                    _monitor.Log($"Warning: Could not create password mode signal file: {ex.Message}", LogLevel.Debug);
                }

                _monitor.Log("Enter your Steam username:", LogLevel.Info);
                var enteredUser = Console.ReadLine();
                _monitor.Log("Steam username received ✓", LogLevel.Info);

                _monitor.Log("Enter your Steam password:", LogLevel.Info);
                var enteredPass = Console.ReadLine();
                _monitor.Log("Steam password received ✓", LogLevel.Info);

                // Clean up signal file after password is entered
                try
                {
                    if (System.IO.File.Exists(passwordModeFile))
                    {
                        System.IO.File.Delete(passwordModeFile);
                    }
                }
                catch (Exception ex)
                {
                    _monitor.Log($"Warning: Could not delete password mode signal file: {ex.Message}", LogLevel.Debug);
                }

                // Authenticate immediately with entered credentials
                EnsureAuthenticatedNow(enteredUser, enteredPass);
            }

            return true;
        }

        /// <summary>
        /// Initialize the Steam fetcher and authenticate immediately
        /// </summary>
        private static void EnsureAuthenticatedNow(string username, string password)
        {
            _steamAppTicketFetcher = new SteamAppTicketFetcherHttp(
                monitor: _monitor,
                steamAuthUrl: Environment.GetEnvironmentVariable("STEAM_AUTH_URL") ?? "http://localhost:3001",
                timeoutMs: 60000
            );

            _steamAppTicketFetcher.EnsureAuthenticated(username, password);
        }

        #endregion
    }
}
