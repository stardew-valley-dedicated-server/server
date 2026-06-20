using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Galaxy.Api;
using HarmonyLib;
using JunimoServer.Services.SteamGameServer;
using JunimoServer.Util;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Network;
using StardewValley.SDKs;
using StardewValley.SDKs.GogGalaxy;
using StardewValley.SDKs.Steam;
using Steamworks;

namespace JunimoServer.Services.Auth;

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

    // Diagnostic-only: count GameServer-mode Galaxy auth-lost / state-change callbacks so a
    // total-connectivity-loss repro can tell whether the closed-source Galaxy SDK re-fires them after
    // reconnect. That single observation decides the Galaxy-reinit fix (callback-driven if it
    // re-fires, poll-based if it doesn't). Game thread only; remove once the design is chosen.
    private static int _galaxyAuthLostCount;
    private static int _galaxyStateChangeCount;

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
    /// Bumped on every Steam-session invalidation. A Task.Run captures it at its
    /// game-thread spawn site and commits its lobby result only if the value still
    /// matches — so a task from a torn-down session can't overwrite fresh state.
    /// </summary>
    private static volatile int _steamSessionGeneration;

    /// <summary>
    /// When true, UpdateGalaxyLobbyWithSteamLobbyId will be called from the main thread
    /// on the next game tick. Galaxy SDK calls are not thread-safe, so the background
    /// Task.Run that creates the Steam lobby sets this flag instead of calling the
    /// Galaxy API directly.
    /// </summary>
    private static volatile bool _pendingGalaxyLobbyUpdate;

    /// <summary>True while a background re-sign-in ticket fetch is in flight; see
    /// <see cref="BeginGalaxyReSignIn"/>. Task-written, game-thread read, so volatile.</summary>
    private static volatile bool _galaxyReSignInInFlight;

    /// <summary>Set by the fetch task with a fresh ticket; consumed next game tick to run the
    /// non-thread-safe <c>SignInSteam</c>.</summary>
    private static volatile bool _pendingGalaxyReSignIn;
    private static byte[] _pendingReSignInTicket;
    private static uint _pendingReSignInTicketLength;

    /// <summary>True while waiting for the async re-login to log on before re-creating the server;
    /// the consume site polls <c>IsLoggedOn()</c> each tick.</summary>
    private static bool _galaxyAwaitingReLogon;

    /// <summary>Safety ceiling: give up waiting for the re-login to log on after this many ticks.</summary>
    private const int GalaxyReLogonTimeoutTicks = 600;
    private static int _galaxyReLogonWaitedTicks;

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
        {
            return _validatedSteamAuthUrl;
        }

        var steamAuthUrl = Environment.GetEnvironmentVariable("STEAM_AUTH_URL");
        if (string.IsNullOrEmpty(steamAuthUrl))
        {
            return null;
        }

        // Validate URL format
        if (
            !Uri.TryCreate(steamAuthUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        )
        {
            _monitor.Log(
                $"STEAM_AUTH_URL is not a valid HTTP/HTTPS URL: {steamAuthUrl}",
                LogLevel.Error
            );
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
        {
            return null;
        }

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
        Diagnostics.ModEventLog.Emit(
            "auth_server_steamid_received",
            new { steamId = steamId.ToString() }
        );

        // Complete deferred Galaxy initialization if we were waiting for GameServer
        if (_pendingSteamHelper != null && !_galaxyInitComplete)
        {
            _monitor.Log(
                "GameServer now ready, completing deferred Galaxy initialization...",
                LogLevel.Info
            );
            PerformDeferredInitialization(_pendingSteamHelper);
            _pendingSteamHelper = null;
        }
        else
        {
            // RECONNECT (Steam dropped and came back): the Steam reconnect is the recovery trigger
            // because the Galaxy SDK fires no auth callback on an outage. Always recreate the Steam
            // lobby; gate the Galaxy re-login on whether its lobby actually died (see
            // TryBeginGalaxyReSignInGated).
            CreateSteamLobbyViaHttpAsync();
            if (_galaxyInitComplete)
            {
                TryBeginGalaxyReSignInGated("reconnect");
            }
        }
    }

    /// <summary>
    /// Clears cached Steam-lobby state on session loss so reconnect re-creates the lobby
    /// instead of early-returning on the stale _lobbyCreationAttempted latch. Bump must come
    /// first so an in-flight Task.Run sees the new generation. _galaxyInitComplete is left set:
    /// the Galaxy dispatch loop is gated on it and nothing re-inits Galaxy to flip it back.
    /// </summary>
    private static void OnSteamServersLost()
    {
        _monitor.Log("Steam session lost — invalidating cached Steam lobby state", LogLevel.Warn);
        _steamSessionGeneration++;
        _steamLobbyId = 0;
        _lobbyCreationAttempted = false;
        _lastSteamLobbyPrivacy = null;
        _pendingGalaxyLobbyUpdate = false;

        // Abandon a prior session's in-flight re-login — else it consumes a stale (gen-bumped) ticket
        // or re-stamps against the just-cleared lobby. The next reconnect re-arms it.
        // (_galaxyReSignInInFlight needs no reset — its Task.Run already drops on the generation bump.)
        _pendingGalaxyReSignIn = false;
        _pendingReSignInTicket = null;
        _galaxyAwaitingReLogon = false;
    }

    public GalaxyAuthService(
        IMonitor monitor,
        IModHelper helper,
        Harmony harmony,
        SteamGameServerService steamGameServerService
    ) // Dependency ensures correct init order
    {
        if (_instance != null)
        {
            throw new InvalidOperationException(
                "AuthService already initialized - only one instance allowed"
            );
        }

        // Set instance variables for use in static harmony patches
        _instance = this;
        _monitor = monitor;
        _helper = helper;

        var galaxyAuthServiceType = typeof(GalaxyAuthService);

        // Subscribe to Steam ID assignment event to create lobby at the right time
        SteamGameServerService.OnServerSteamIdReceived += OnServerSteamIdReceived;

        // Invalidate cached Steam lobby state on session loss so reconnect rebuilds it
        SteamGameServerService.OnSteamServersLost += OnSteamServersLost;

        // Handle race condition: If Steam ID was already received before we subscribed,
        // manually trigger the handler. This ensures Galaxy init happens even if the
        // SteamGameServerService initialized faster than expected.
        if (SteamGameServerService.IsInitialized && SteamGameServerService.ServerSteamId.IsValid())
        {
            _monitor.Log(
                "Steam ID already available at subscription time, triggering handler",
                LogLevel.Debug
            );
            OnServerSteamIdReceived(SteamGameServerService.ServerSteamId.m_SteamID);
        }

        // Steam GameServer mode: Patch SteamHelper to use GameServer APIs instead of Client APIs
        _monitor.Log("Registering Steam GameServer API patches for SteamHelper", LogLevel.Debug);

        // Patch SteamHelper.Initialize to use GameServer.Init() instead of SteamAPI.Init()
        harmony.Patch(
            original: AccessTools.Method(typeof(SteamHelper), nameof(SteamHelper.Initialize)),
            prefix: new HarmonyMethod(galaxyAuthServiceType, nameof(SteamHelperInitialize_Prefix))
        );

        // Patch SteamHelper.Update to use GameServer.RunCallbacks() instead of SteamAPI.RunCallbacks()
        harmony.Patch(
            original: AccessTools.Method(typeof(SteamHelper), nameof(SteamHelper.Update)),
            prefix: new HarmonyMethod(galaxyAuthServiceType, nameof(SteamHelperUpdate_Prefix))
        );

        // Patch SteamHelper.Shutdown to use GameServer.Shutdown() instead of SteamAPI.Shutdown()
        harmony.Patch(
            original: AccessTools.Method(typeof(SteamHelper), nameof(SteamHelper.Shutdown)),
            prefix: new HarmonyMethod(galaxyAuthServiceType, nameof(SteamHelperShutdown_Prefix))
        );

        // Patch SteamNetServer.initialize to skip - it uses Steam Client API (SteamMatchmaking.CreateLobby)
        // which isn't available in GameServer mode. We use SteamKit2 for lobby creation instead.
        // See decompiled: StardewValley.SDKs.Steam.SteamNetServer.initialize()
        var steamNetServerType = AccessTools.TypeByName("StardewValley.SDKs.Steam.SteamNetServer");
        if (steamNetServerType != null)
        {
            harmony.Patch(
                original: AccessTools.Method(steamNetServerType, "initialize"),
                prefix: new HarmonyMethod(
                    galaxyAuthServiceType,
                    nameof(SteamNetServer_Initialize_Prefix)
                )
            );
            _monitor.Log(
                "Patched SteamNetServer.initialize to skip (using SteamKit2 for lobbies)",
                LogLevel.Debug
            );
        }

        // Downgrade Galaxy lobby creation failure from ERROR to WARN.
        // In Docker containers, Galaxy matchmaking is often unavailable, causing
        // GalaxySocket.onGalaxyLobbyCreated to log ERROR every 20s. The retry
        // mechanism (OnLobbyCreateFailed) still runs. If Galaxy eventually connects,
        // the lobby will be created.
        harmony.Patch(
            original: AccessTools.Method(typeof(GalaxySocket), "onGalaxyLobbyCreated"),
            prefix: new HarmonyMethod(
                galaxyAuthServiceType,
                nameof(GalaxySocket_OnLobbyCreated_Prefix)
            )
        );

        // Postfix on GetInviteCode to capture invite code for file/banner
        // (More reliable than patching the private onGalaxyLobbyEnter callback)
        harmony.Patch(
            original: AccessTools.Method(typeof(GalaxySocket), nameof(GalaxySocket.GetInviteCode)),
            postfix: new HarmonyMethod(
                galaxyAuthServiceType,
                nameof(GalaxySocket_GetInviteCode_Postfix)
            )
        );

        // These patches apply to both modes - provide fake Steam IDs when Client API is unavailable
        harmony.Patch(
            original: AccessTools.Method(
                typeof(Steamworks.SteamUser),
                nameof(Steamworks.SteamUser.GetSteamID)
            ),
            prefix: new HarmonyMethod(galaxyAuthServiceType, nameof(SteamUser_GetSteamID_Prefix))
        );

        harmony.Patch(
            original: AccessTools.Method(
                typeof(Steamworks.SteamFriends),
                nameof(Steamworks.SteamFriends.GetPersonaName)
            ),
            prefix: new HarmonyMethod(
                galaxyAuthServiceType,
                nameof(SteamFriends_GetPersonaName_Prefix)
            )
        );
    }

    // SteamHelper reflection helpers
    private static void IncrementSteamConnectionProgress(SteamHelper instance) =>
        _helper
            .Reflection.GetProperty<int>(instance, "ConnectionProgress")
            .SetValue(instance.ConnectionProgress + 1);

    private static void SetSteamActive(SteamHelper instance, bool value) =>
        _helper.Reflection.GetField<bool>(instance, "active").SetValue(value);

    private static void SetSteamConnectionFinished(SteamHelper instance, bool value) =>
        _helper.Reflection.GetProperty<bool>(instance, "ConnectionFinished").SetValue(value);

    private static void SetSteamGalaxyConnected(SteamHelper instance, bool value) =>
        _helper.Reflection.GetProperty<bool>(instance, "GalaxyConnected").SetValue(value);

    private static void SetSteamNetworking(SteamHelper instance, SDKNetHelper networking) =>
        _helper.Reflection.GetField<SDKNetHelper>(instance, "networking").SetValue(networking);

    /// <summary>
    /// Gets the current SDK via reflection (Program.sdk is internal).
    /// </summary>
    private static SDKHelper GetCurrentSdk()
    {
        var sdkProperty = AccessTools.PropertyGetter(typeof(Program), "sdk");
        return (SDKHelper)sdkProperty?.Invoke(null, null);
    }

    /// <summary>
    /// The single resolution into the GameServer's internal <c>servers</c> list — returns the list and
    /// the <see cref="GalaxyNetServer"/> in it (either may be <c>null</c>). Centralizes the
    /// <c>"servers"</c> reflection string and type match so the four call sites can't drift.
    /// <c>servers</c> is null when no GameServer is active. Game-thread only.
    /// </summary>
    private static (List<Server> servers, GalaxyNetServer galaxyServer) GetGalaxyServer()
    {
        if (Game1.server is not StardewValley.Network.GameServer gameServer)
        {
            return (null, null);
        }
        var servers = _helper.Reflection.GetField<List<Server>>(gameServer, "servers").GetValue();
        return (servers, servers.OfType<GalaxyNetServer>().FirstOrDefault());
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
                _monitor.Log(
                    "Cannot late-add Galaxy server: server or networking not ready",
                    LogLevel.Debug
                );
                return;
            }

            var (servers, existing) = GetGalaxyServer();
            if (existing != null)
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
                _monitor.Log(
                    "Galaxy server added successfully, invite codes should now work.",
                    LogLevel.Info
                );

                // Now that Galaxy server exists, update it with Steam lobby ID if available
                if (_steamLobbyId != 0)
                {
                    _monitor.Log(
                        "Updating Galaxy lobby with Steam lobby ID after late-add...",
                        LogLevel.Debug
                    );
                    UpdateGalaxyLobbyWithSteamLobbyId();
                }
            }
            else
            {
                _monitor.Log(
                    "CreateServer returned null - Galaxy may not be fully connected yet",
                    LogLevel.Warn
                );
            }
        }
        catch (Exception ex)
        {
            _monitor.Log($"Failed to late-add Galaxy server: {ex.Message}", LogLevel.Error);
            _monitor.Log(ex.ToString(), LogLevel.Debug);
        }
    }

    /// <summary>
    /// Stops and removes the <c>GalaxyNetServer</c> before a recovery <c>SignOut()</c>, so its
    /// per-tick <c>receiveMessages()</c> stops hitting the SDK while signed out (those vanilla calls
    /// throw at ERROR, which poisons tests). <see cref="TryLateAddGalaxyServer"/> re-adds a fresh one
    /// after re-login. Game-thread only.
    /// </summary>
    private static void TryRemoveGalaxyServer()
    {
        try
        {
            var (servers, galaxyServer) = GetGalaxyServer();
            if (galaxyServer == null)
            {
                return;
            }
            try
            {
                galaxyServer.stopServer();
            }
            catch (Exception ex)
            {
                _monitor.Log($"GalaxyNetServer.stopServer() threw: {ex.Message}", LogLevel.Trace);
            }
            servers.Remove(galaxyServer);
            _monitor.Log("Removed dead GalaxyNetServer for re-auth", LogLevel.Info);
        }
        catch (Exception ex)
        {
            // Warn (not Error) — recoverable; the re-login still proceeds.
            _monitor.Log($"Failed to remove GalaxyNetServer: {ex.Message}", LogLevel.Warn);
        }
    }

    /// <summary>
    /// The re-login gate's discriminator: <c>GalaxySocket.Connected</c> (<c>lobby != null</c>). On a
    /// total connectivity loss the Galaxy lobby-left callback nulls <c>lobby</c> → <c>false</c>; on a healthy
    /// server (idle, with peers, or mid-re-login) → <c>true</c>. The only Galaxy-side signal that
    /// tracks the outage — the <c>IUser</c> liveness members stay stale-<c>true</c>. Reads a plain
    /// managed field (no SDK call). <c>null</c> (no server/socket, or reflection failure) is treated
    /// as dead by the gate. Game-thread only.
    /// </summary>
    private static bool? IsGalaxyLobbyConnected()
    {
        try
        {
            var (_, galaxyServer) = GetGalaxyServer();
            if (galaxyServer == null)
            {
                return null;
            }
            // GalaxySocket is a protected field on GalaxyNetServer; read it reflectively.
            var socket = _helper
                .Reflection.GetField<GalaxySocket>(galaxyServer, "server")
                .GetValue();
            if (socket == null)
            {
                return null;
            }
            return socket.Connected;
        }
        catch (Exception ex)
        {
            _monitor.Log($"IsGalaxyLobbyConnected probe threw: {ex.Message}", LogLevel.Trace);
            return null;
        }
    }

    /// <summary>
    /// Gates the DISRUPTIVE re-login (<see cref="BeginGalaxyReSignIn"/> — SignOut + rebuild the lobby)
    /// on <see cref="IsGalaxyLobbyConnected"/>. This is only the rebuild half; the Steam-lobby re-stamp
    /// runs unconditionally on every reconnect (<see cref="CreateSteamLobbyViaHttpAsync"/> →
    /// <c>_pendingGalaxyLobbyUpdate</c> → <see cref="UpdateGalaxyLobbyWithSteamLobbyId"/>), so the
    /// pointer is always fixed regardless of this gate.
    /// <list type="bullet">
    /// <item><c>false</c> → lobby dead (Galaxy's own auto-recreate hasn't restored it): re-login.</item>
    /// <item><c>true</c> → SKIP: a Steam-CM flap or auto-recreate already rebuilt the lobby; a rebuild
    ///   would only sever connected clients (re-login drops live peers).</item>
    /// <item><c>null</c> → treated as dead; can't occur on a healthy flap (reads <c>true</c>).</item>
    /// </list>
    /// Shared by the reconnect path and the test endpoint so they can't diverge. Game thread only.
    /// </summary>
    private static void TryBeginGalaxyReSignInGated(string trigger)
    {
        var connected = IsGalaxyLobbyConnected();
        if (connected == true)
        {
            // Galaxy lobby is alive — almost certainly a Steam-CM-only flap. Re-login here would kick
            // connected players for no benefit, so skip it.
            _monitor.Log(
                $"Galaxy lobby still connected on {trigger}; skipping re-login (would sever connected clients)",
                LogLevel.Info
            );
            Diagnostics.ModEventLog.Emit(
                "auth_galaxy_relogin_skipped",
                new { trigger, reason = "galaxy_lobby_connected" }
            );
            return;
        }

        _monitor.Log(
            $"Galaxy lobby not connected on {trigger} (connected={connected?.ToString() ?? "unknown"}); re-establishing Galaxy auth",
            LogLevel.Info
        );
        Diagnostics.ModEventLog.Emit(
            "auth_galaxy_relogin_attempt",
            new { trigger, galaxyConnected = connected }
        );
        BeginGalaxyReSignIn();
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
            _monitor.Log(
                "GameServer already ready, initializing Galaxy immediately",
                LogLevel.Debug
            );
            PerformDeferredInitialization(__instance);
        }
        else
        {
            // GameServer not ready yet - store instance for OnServerSteamIdReceived callback
            _monitor.Log(
                "GameServer not yet ready, deferring Galaxy init to OnServerSteamIdReceived",
                LogLevel.Debug
            );
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
            _monitor.Log(
                "Cannot create Steam lobby: GameServer Steam ID not yet assigned (waiting for OnSteamServersConnected)",
                LogLevel.Debug
            );
            return;
        }

        _lobbyCreationAttempted = true;
        var generation = _steamSessionGeneration;
        _monitor.Log(
            $"Creating Steam lobby via steam-auth service, GameServer ID: {gameServerSteamId}",
            LogLevel.Info
        );

        // Run async to avoid blocking
        Task.Run(() =>
        {
            try
            {
                var apiClient = GetOrCreateApiClient();
                if (apiClient == null)
                {
                    _monitor.Log(
                        "STEAM_AUTH_URL not set or invalid - Steam lobby creation disabled. Only Galaxy invites will work.",
                        LogLevel.Warn
                    );
                    return;
                }

                // CurrentPlayerLimit can be -1 (not initialized) or 0 (invalid)
                // Use default lobby size in those cases
                var currentLimit = Game1.netWorldState.Value?.CurrentPlayerLimit ?? -1;
                var maxLobbyMembers =
                    currentLimit > 0 ? currentLimit : SteamConstants.DefaultMaxLobbyMembers;
                _monitor.Log(
                    $"Creating Steam lobby with maxMembers={maxLobbyMembers} (CurrentPlayerLimit={currentLimit})",
                    LogLevel.Info
                );
                var result = apiClient.CreateLobby(
                    gameServerSteamId: gameServerSteamId,
                    protocolVersion: Multiplayer.protocolVersion,
                    maxMembers: maxLobbyMembers
                );

                if (result != null && !string.IsNullOrEmpty(result.lobby_id))
                {
                    if (ulong.TryParse(result.lobby_id, out var lobbyId))
                    {
                        if (generation != _steamSessionGeneration)
                        {
                            _monitor.Log(
                                $"Discarding Steam lobby {lobbyId} from invalidated session (gen {generation}, current {_steamSessionGeneration})",
                                LogLevel.Warn
                            );
                            return;
                        }
                        _steamLobbyId = lobbyId;
                        _monitor.Log(
                            $"Steam lobby created via HTTP: {_steamLobbyId}",
                            LogLevel.Info
                        );
                        Diagnostics.ModEventLog.Emit(
                            "auth_steam_lobby_created",
                            new { lobbyId = _steamLobbyId.ToString(), maxMembers = maxLobbyMembers }
                        );

                        // Schedule Galaxy lobby update for next game tick (Galaxy SDK is not thread-safe)
                        _pendingGalaxyLobbyUpdate = true;
                    }
                    else
                    {
                        _monitor.Log(
                            $"Failed to parse Steam lobby ID: {result.lobby_id}",
                            LogLevel.Error
                        );
                        Diagnostics.ModEventLog.Emit(
                            "auth_steam_lobby_create_failed",
                            new { reason = "parse_failed", rawLobbyId = result.lobby_id }
                        );
                    }
                }
                else
                {
                    _monitor.Log("Failed to create Steam lobby: empty response", LogLevel.Error);
                    Diagnostics.ModEventLog.Emit(
                        "auth_steam_lobby_create_failed",
                        new { reason = "empty_response" }
                    );
                }
            }
            catch (Exception ex)
            {
                _monitor.Log(
                    $"Failed to create Steam lobby via HTTP: {ex.Message}",
                    LogLevel.Error
                );
                _monitor.Log(ex.ToString(), LogLevel.Debug);
                Diagnostics.ModEventLog.Emit(
                    "auth_steam_lobby_create_failed",
                    new
                    {
                        reason = "http_error",
                        exceptionType = ex.GetType().Name,
                        message = ex.Message,
                    }
                );
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
            var (_, galaxyServer) = GetGalaxyServer();
            if (galaxyServer == null)
            {
                _monitor.Log("Could not find GalaxyNetServer to update lobby data", LogLevel.Warn);
                return;
            }
            // Set the SteamLobbyId in Galaxy lobby metadata — this is what vanilla SteamNetClient
            // reads to join the Steam lobby.
            galaxyServer.setLobbyData("SteamLobbyId", _steamLobbyId.ToString());
            _monitor.Log($"Galaxy lobby updated with SteamLobbyId: {_steamLobbyId}", LogLevel.Info);
        }
        catch (Exception ex)
        {
            _monitor.Log($"Failed to update Galaxy lobby: {ex.Message}", LogLevel.Error);
            _monitor.Log(ex.ToString(), LogLevel.Debug);
        }
    }

    private static ServerPrivacy? _lastSteamLobbyPrivacy = null;

    /// <summary>
    /// Recreates the Steam lobby after it was lost (e.g., NoMatch from Steam).
    /// Uses the same parameters as the original CreateSteamLobbyViaHttpAsync.
    /// Returns true if the lobby was successfully recreated.
    /// <paramref name="capturedGeneration"/> is captured by the caller — both already run
    /// inside a Task.Run, so capturing here would itself race OnSteamServersLost.
    /// </summary>
    private static bool RecreateSteamLobby(int capturedGeneration)
    {
        try
        {
            var apiClient = GetOrCreateApiClient();
            if (apiClient == null)
            {
                return false;
            }

            if (!SteamGameServerService.IsInitialized)
            {
                _monitor.Log(
                    "Cannot recreate Steam lobby: GameServer not initialized",
                    LogLevel.Warn
                );
                return false;
            }

            var gameServerSteamId = SteamGameServerService.ServerSteamId.m_SteamID;
            if (gameServerSteamId == 0)
            {
                _monitor.Log(
                    "Cannot recreate Steam lobby: GameServer Steam ID not assigned",
                    LogLevel.Warn
                );
                return false;
            }

            var currentLimit = Game1.netWorldState.Value?.CurrentPlayerLimit ?? -1;
            var maxLobbyMembers =
                currentLimit > 0 ? currentLimit : SteamConstants.DefaultMaxLobbyMembers;

            _monitor.Log(
                $"Recreating Steam lobby (previous lobby lost), GameServer ID: {gameServerSteamId}, maxMembers: {maxLobbyMembers}",
                LogLevel.Warn
            );

            var result = apiClient.CreateLobby(
                gameServerSteamId: gameServerSteamId,
                protocolVersion: Multiplayer.protocolVersion,
                maxMembers: maxLobbyMembers
            );

            if (
                result != null
                && !string.IsNullOrEmpty(result.lobby_id)
                && ulong.TryParse(result.lobby_id, out var newLobbyId)
            )
            {
                if (capturedGeneration != _steamSessionGeneration)
                {
                    _monitor.Log(
                        $"Discarding recreated Steam lobby {newLobbyId} from invalidated session",
                        LogLevel.Warn
                    );
                    return false;
                }
                _steamLobbyId = newLobbyId;
                _lastSteamLobbyPrivacy = null; // Reset cached privacy; new lobby needs fresh setup
                _monitor.Log($"Steam lobby recreated: {_steamLobbyId}", LogLevel.Info);
                UpdateGalaxyLobbyWithSteamLobbyId();
                return true;
            }

            _monitor.Log(
                "Failed to recreate Steam lobby: empty or invalid response",
                LogLevel.Error
            );
            return false;
        }
        catch (Exception ex)
        {
            _monitor.Log($"Failed to recreate Steam lobby: {ex.Message}", LogLevel.Error);
            _monitor.Log(ex.ToString(), LogLevel.Debug);
            return false;
        }
    }

    /// <summary>
    /// Checks if an exception indicates the lobby no longer exists on Steam.
    /// This happens when another instance invalidates our lobby, or when it expires.
    /// </summary>
    private static bool IsLobbyLostError(Exception ex)
    {
        // SteamAuthService throws "Failed to set lobby privacy: NoMatch" or
        // "Failed to set lobby data: NoMatch" when the lobby is gone.
        // This propagates through the HTTP layer as a 500 error with the message intact.
        var message = ex.Message;
        return message.Contains("NoMatch", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Sets the privacy level on the Steam lobby via the steam-auth HTTP service.
    /// Called by SteamGameServerNetServer.setPrivacy().
    /// </summary>
    public static void SetSteamLobbyPrivacy(ServerPrivacy privacy)
    {
        // Only update if privacy actually changed
        if (_lastSteamLobbyPrivacy == privacy)
        {
            return;
        }

        if (_steamLobbyId == 0)
        {
            return;
        }

        // Optimistically cache so repeated calls from the game loop don't queue redundant work
        _lastSteamLobbyPrivacy = privacy;

        var generation = _steamSessionGeneration; // captured on the game thread; see field doc

        // Run off the game thread. HTTP calls with retries would block for 7+ seconds.
        Task.Run(() =>
        {
            try
            {
                var apiClient = GetOrCreateApiClient();
                if (apiClient == null)
                {
                    return;
                }

                // Force Public for dedicated server - invite codes need joinable lobbies
                apiClient.SetLobbyPrivacy(lobbyId: _steamLobbyId, privacy: "public");

                _monitor.Log(
                    $"Steam lobby privacy set to Public (game requested: {privacy})",
                    LogLevel.Debug
                );
            }
            catch (Exception ex) when (IsLobbyLostError(ex))
            {
                _lastSteamLobbyPrivacy = null; // Reset cache: lobby lost, need to retry on next call
                _monitor.Log(
                    $"Steam lobby lost (NoMatch), attempting to recreate...",
                    LogLevel.Warn
                );

                if (RecreateSteamLobby(generation))
                {
                    // Retry with the new lobby ID
                    try
                    {
                        var apiClient = GetOrCreateApiClient();
                        apiClient?.SetLobbyPrivacy(lobbyId: _steamLobbyId, privacy: "public");
                        _lastSteamLobbyPrivacy = privacy;
                        _monitor.Log($"Steam lobby privacy set after recreation", LogLevel.Info);
                    }
                    catch (Exception retryEx)
                    {
                        _lastSteamLobbyPrivacy = null;
                        _monitor.Log(
                            $"Failed to set lobby privacy after recreation: {retryEx.Message}",
                            LogLevel.Warn
                        );
                        _monitor.Log(retryEx.ToString(), LogLevel.Debug);
                    }
                }
            }
            catch (Exception ex)
            {
                _lastSteamLobbyPrivacy = null; // Reset cache so next call retries
                _monitor.Log($"Failed to set Steam lobby privacy: {ex.Message}", LogLevel.Warn);
                _monitor.Log(ex.ToString(), LogLevel.Debug);
            }
        });
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

        var generation = _steamSessionGeneration; // captured on the game thread; see field doc

        // Run off the game thread. HTTP calls with retries would block for 7+ seconds.
        Task.Run(() =>
        {
            try
            {
                var apiClient = GetOrCreateApiClient();
                if (apiClient == null)
                {
                    return;
                }

                apiClient.SetLobbyData(
                    lobbyId: _steamLobbyId,
                    metadata: new Dictionary<string, string> { [key] = value }
                );

                _monitor.Log($"Steam lobby data set: {key}={value}", LogLevel.Debug);
            }
            catch (Exception ex) when (IsLobbyLostError(ex))
            {
                _monitor.Log(
                    $"Steam lobby lost (NoMatch) while setting '{key}', attempting to recreate...",
                    LogLevel.Warn
                );

                if (RecreateSteamLobby(generation))
                {
                    try
                    {
                        var apiClient = GetOrCreateApiClient();
                        apiClient?.SetLobbyData(
                            lobbyId: _steamLobbyId,
                            metadata: new Dictionary<string, string> { [key] = value }
                        );
                        _monitor.Log(
                            $"Steam lobby data set after recreation: {key}={value}",
                            LogLevel.Info
                        );
                    }
                    catch (Exception retryEx)
                    {
                        _monitor.Log(
                            $"Failed to set lobby data after recreation: {retryEx.Message}",
                            LogLevel.Warn
                        );
                        _monitor.Log(retryEx.ToString(), LogLevel.Debug);
                    }
                }
            }
            catch (Exception ex)
            {
                _monitor.Log($"Failed to set Steam lobby data: {ex.Message}", LogLevel.Warn);
                _monitor.Log(ex.ToString(), LogLevel.Debug);
            }
        });
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
            try
            {
                Steamworks.GameServer.RunCallbacks();
            }
            catch (InvalidOperationException)
            {
                // Callback dispatcher not initialized. Steam SDK failed to load
                // (e.g., musl/Alpine where steamclient.so is a stub)
            }

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
                    _monitor.Log(
                        $"GalaxyInstance.ProcessData() failed: {ex.Message}",
                        LogLevel.Trace
                    );
                }

                // Process deferred Galaxy lobby update (set by background Task.Run)
                if (_pendingGalaxyLobbyUpdate)
                {
                    _pendingGalaxyLobbyUpdate = false;
                    UpdateGalaxyLobbyWithSteamLobbyId();
                }

                ConsumePendingGalaxyReSignIn();
                PumpGalaxyReLogonWait(__instance);
            }
        }

        Game1.game1.IsMouseVisible = Game1.paused || Game1.options.hardwareCursor;
        return false; // Skip original
    }

    /// <summary>
    /// Game-thread half of the reconnect recovery: runs a deferred Galaxy re-sign-in once the
    /// off-thread ticket fetch (<see cref="BeginGalaxyReSignIn"/>) has armed
    /// <see cref="_pendingGalaxyReSignIn"/>. <c>SignInSteam</c> is not thread-safe, so it must run
    /// here. Arms <see cref="_galaxyAwaitingReLogon"/> for <see cref="PumpGalaxyReLogonWait"/> to
    /// finish once the login logs on. Called per tick from <see cref="SteamHelperUpdate_Prefix"/>.
    /// </summary>
    private static void ConsumePendingGalaxyReSignIn()
    {
        if (!_pendingGalaxyReSignIn)
        {
            return;
        }
        _pendingGalaxyReSignIn = false;
        try
        {
            // Remove the dead server first: its per-tick receiveMessages() would keep
            // hitting the SDK while signed out and throw at ERROR (poisons tests).
            TryRemoveGalaxyServer();

            // SignOut before SignInSteam: the SDK still reports SignedIn()==true after the
            // outage (never saw connectivity drop), so a bare SignInSteam throws "already signed
            // in". Own try so a SignOut throw doesn't skip the sign-in.
            try
            {
                GalaxyInstance.User().SignOut();
            }
            catch (Exception ex)
            {
                _monitor.Log($"Galaxy SignOut (recovery) failed: {ex.Message}", LogLevel.Trace);
            }

            GalaxyInstance
                .User()
                .SignInSteam(_pendingReSignInTicket, _pendingReSignInTicketLength, ServerName);
            _monitor.Log("Galaxy re-sign-in submitted (reconnect recovery)", LogLevel.Info);
            // SignInSteam is async — wait for logon (PumpGalaxyReLogonWait) before re-creating the
            // server (too early throws "not logged on").
            _galaxyAwaitingReLogon = true;
            _galaxyReLogonWaitedTicks = 0;
        }
        catch (Exception ex)
        {
            // The next Steam reconnect (or a manual trigger) re-attempts. Trace, not Error
            // (test poison / recoverable).
            _monitor.Log($"Galaxy SignInSteam (recovery) failed: {ex.Message}", LogLevel.Trace);
        }
        finally
        {
            _pendingReSignInTicket = null;
        }
    }

    /// <summary>
    /// Game-thread half of the reconnect recovery: once the re-sign-in started by
    /// <see cref="ConsumePendingGalaxyReSignIn"/> logs on, re-creates the GalaxyNetServer and
    /// re-stamps the Steam lobby id. Polling <c>IsLoggedOn()</c> works because a FRESH login flips it
    /// true (it was only stale during the outage, with no fresh login) — the SDK gives no callback for
    /// this. Gives up after <see cref="GalaxyReLogonTimeoutTicks"/>. Called per tick from
    /// <see cref="SteamHelperUpdate_Prefix"/>.
    /// </summary>
    private static void PumpGalaxyReLogonWait(SteamHelper __instance)
    {
        if (!_galaxyAwaitingReLogon)
        {
            return;
        }

        bool loggedOn = false;
        try
        {
            loggedOn = GalaxyInstance.User().IsLoggedOn();
        }
        catch (Exception ex)
        {
            _monitor.Log(
                $"IsLoggedOn() probe threw during re-login wait: {ex.Message}",
                LogLevel.Trace
            );
        }

        if (loggedOn)
        {
            _galaxyAwaitingReLogon = false;
            _monitor.Log("Galaxy re-login logged on; re-stamping lobby", LogLevel.Info);
            // Must re-set GalaxyConnected before re-creating: vanilla CreateServer returns
            // null when it's false (onLost cleared it; the re-login's onStateChange that
            // would set it is blocked by its Networking!=null early-return).
            SetSteamGalaxyConnected(__instance, true);
            TryLateAddGalaxyServer();
            if (_steamLobbyId != 0)
            {
                UpdateGalaxyLobbyWithSteamLobbyId();
            }
            Diagnostics.ModEventLog.Emit("auth_galaxy_recovered");
        }
        else if (++_galaxyReLogonWaitedTicks >= GalaxyReLogonTimeoutTicks)
        {
            // Gave up — re-login never logged on. Stop waiting; the next Steam reconnect
            // re-attempts. Warn (not Error) to avoid test poison.
            _galaxyAwaitingReLogon = false;
            _monitor.Log(
                "Galaxy re-login did not log on within timeout; will retry on next reconnect",
                LogLevel.Warn
            );
        }
    }

    /// <summary>
    /// Fetches the app ticket off-thread (blocking sidecar HTTP, up to 30s), then sets
    /// <see cref="_pendingGalaxyReSignIn"/> so the game thread runs the non-thread-safe
    /// <c>SignInSteam</c>. Mirrors <see cref="CreateSteamLobbyViaHttpAsync"/>. No-op if a fetch is
    /// already in flight; a fetch failure (sidecar still down) is Trace — the next reconnect retries.
    /// </summary>
    private static void BeginGalaxyReSignIn()
    {
        // _galaxyAwaitingReLogon is in the guard too: a second Steam reconnect that lands while a
        // prior re-login is still waiting for IsLoggedOn() must not re-run SignOut+SignInSteam (a
        // real second outage routes through OnSteamServersLost first, which clears all three flags).
        if (_galaxyReSignInInFlight || _pendingGalaxyReSignIn || _galaxyAwaitingReLogon)
        {
            return;
        }
        var fetcher = _steamAppTicketFetcher;
        if (fetcher == null)
        {
            return;
        }

        _galaxyReSignInInFlight = true;
        var generation = _steamSessionGeneration;
        Task.Run(() =>
        {
            try
            {
                var ticket = Convert.FromBase64String(fetcher.GetTicket().Ticket);
                if (generation != _steamSessionGeneration)
                {
                    // Session was torn down while we fetched — drop this ticket.
                    return;
                }
                _pendingReSignInTicket = ticket;
                _pendingReSignInTicketLength = Convert.ToUInt32(ticket.Length);
                _pendingGalaxyReSignIn = true;
            }
            catch (Exception ex)
            {
                // Expected while the sidecar is unreachable (the outage). Next poll retries.
                _monitor.Log(
                    $"Galaxy re-sign-in ticket fetch failed: {ex.Message}",
                    LogLevel.Trace
                );
            }
            finally
            {
                _galaxyReSignInInFlight = false;
            }
        });
    }

    /// <summary>
    /// TEST-ONLY (/test/galaxy_relogin): runs the real gate <see cref="TryBeginGalaxyReSignInGated"/>
    /// on demand with no outage, so the E2E flap test sees the gate SKIP on a healthy lobby (and the
    /// connected client survive). Returns false if Galaxy isn't initialized.
    /// </summary>
    public static bool TriggerGalaxyReSignInForTest()
    {
        if (!_galaxyInitComplete)
        {
            return false;
        }
        TryBeginGalaxyReSignInGated("test");
        return true;
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
            _monitor.Log(
                $"GameServer active with Steam ID: {SteamGameServerService.ServerSteamId.m_SteamID}",
                LogLevel.Info
            );

            // Check if steam-auth service is configured before initializing Galaxy.
            // Without STEAM_AUTH_URL there is no way to authenticate with Galaxy, so
            // skip the entire Galaxy/Steam networking init. The game will use Lidgren
            // only (LAN/IP connections). Skipping also prevents the game from logging
            // "Could not create a Galaxy server: not logged on" in SteamNetHelper.CreateServer.
            var steamAuthUrl = Environment.GetEnvironmentVariable("STEAM_AUTH_URL");
            if (string.IsNullOrEmpty(steamAuthUrl))
            {
                // Intentionally NOT calling SetSteamNetworking here. Leaving
                // SteamHelper.Networking null prevents GameServer from trying to
                // create Steam SDR or Galaxy servers (it checks Networking != null).
                // Only Lidgren (LAN/IP) transport will be available.
                // Compare: the auth-failure path below DOES set networking so Steam
                // SDR still works even when Galaxy auth fails.
                _monitor.Log(
                    "No STEAM_AUTH_URL; skipping Galaxy init (LAN/IP connections only)",
                    LogLevel.Info
                );
                SetSteamConnectionFinished(steamHelper, true);
                return;
            }

            // Initialize Galaxy SDK for lobby matchmaking
            _monitor.Log("Initializing Galaxy SDK for lobby support...", LogLevel.Debug);
            GalaxyInstance.Init(new InitParams(GalaxyClientId, GalaxyClientSecret, "."));

            // Create Galaxy auth listener
            authListener = _instance.CreateSteamHelperGalaxyAuthListener(steamHelper);
            stateChangeListener = _instance.CreateSteamHelperGalaxyStateChangeListener(steamHelper);

            // Sign into Galaxy using our Steam auth service
            IncrementSteamConnectionProgress(steamHelper);

            var isAuthenticated = _instance.UseExternalSteamAuth(steamAuthUrl);

            if (!isAuthenticated)
            {
                _monitor.Log(
                    "Steam-auth service not ready, Galaxy features unavailable",
                    LogLevel.Warn
                );
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
        _monitor.Log(
            "Skipping SteamNetServer.initialize() - using SteamKit2 for lobby creation",
            LogLevel.Debug
        );
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
        var listenerType = AccessTools.TypeByName(
            "StardewValley.SDKs.GogGalaxy.Listeners.GalaxyAuthListener"
        );

        Action onSuccess = () =>
        {
            _monitor.Log("Galaxy auth success (GameServer mode)", LogLevel.Debug);
            Diagnostics.ModEventLog.Emit("auth_galaxy_success", new { mode = "gameServer" });
            IncrementSteamConnectionProgress(steamHelper);
        };

        Action<IAuthListener.FailureReason> onFailure = (reason) =>
        {
            _monitor.Log($"Galaxy auth failure: {reason}", LogLevel.Error);
            Diagnostics.ModEventLog.Emit(
                "auth_galaxy_failed",
                new { mode = "gameServer", reason = reason.ToString() }
            );
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
            var count = ++_galaxyAuthLostCount;
            // Warn (not Error): a re-fire here is the signal we want, and Error trips test cancellation.
            _monitor.Log($"Galaxy auth lost (GameServer mode), invocation #{count}", LogLevel.Warn);
            Diagnostics.ModEventLog.Emit(
                "auth_galaxy_lost",
                new
                {
                    mode = "gameServer",
                    invocation = count,
                    networkingSet = steamHelper.Networking != null,
                }
            );
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
    private IOperationalStateChangeListener CreateSteamHelperGalaxyStateChangeListener(
        SteamHelper steamHelper
    )
    {
        var listenerType = AccessTools.TypeByName(
            "StardewValley.SDKs.GogGalaxy.Listeners.GalaxyOperationalStateChangeListener"
        );

        var onStateChange = new Action<uint>(
            (operationalState) =>
            {
                // Diagnostic: log BEFORE the early-return so a recovery state-change (which arrives
                // with Networking already set, and would otherwise be dropped silently) is still
                // observable. networkingSet distinguishes first-login from a post-reconnect re-fire.
                var count = ++_galaxyStateChangeCount;
                var networkingSet = steamHelper.Networking != null;
                _monitor.Log(
                    $"Galaxy state change (GameServer mode) #{count}: state=0x{operationalState:X}, networkingSet={networkingSet}",
                    LogLevel.Debug
                );
                Diagnostics.ModEventLog.Emit(
                    "auth_galaxy_state_change",
                    new
                    {
                        mode = "gameServer",
                        invocation = count,
                        operationalState,
                        signedIn = (operationalState & 1) != 0,
                        loggedOn = (operationalState & 2) != 0,
                        networkingSet,
                    }
                );

                if (steamHelper.Networking != null)
                {
                    return;
                }

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
            }
        );

        return (IOperationalStateChangeListener)
            Activator.CreateInstance(listenerType, onStateChange);
    }

    /// <summary>
    /// Postfix on GetInviteCode to capture invite code for file/banner.
    /// In GameServer mode, the original "S" prefix is correct.
    /// Also updates the Galaxy lobby with Steam lobby ID if available.
    /// </summary>
    /// <summary>
    /// Downgrades Galaxy lobby creation failure from ERROR to WARN.
    /// The original logs Game1.log.Error which triggers test error detection
    /// and pollutes production logs when Galaxy matchmaking is unavailable.
    /// OnLobbyCreateFailed() still runs for retry logic.
    /// </summary>
    private static bool GalaxySocket_OnLobbyCreated_Prefix(
        GalaxySocket __instance,
        GalaxyID lobbyID,
        LobbyCreateResult result
    )
    {
        if (result == LobbyCreateResult.LOBBY_CREATE_RESULT_ERROR)
        {
            _monitor.Log("Failed to create Galaxy lobby (matchmaking unavailable).", LogLevel.Warn);
            AccessTools
                .Method(typeof(GalaxySocket), "OnLobbyCreateFailed")
                ?.Invoke(__instance, null);
            return false; // Skip original (which logs at ERROR)
        }

        return true; // Success path: let original handle it
    }

    private static void GalaxySocket_GetInviteCode_Postfix(string __result)
    {
        if (string.IsNullOrEmpty(__result))
        {
            return;
        }

        try
        {
            _monitor.Log($"Galaxy invite code generated: {__result}", LogLevel.Debug);
            InviteCodeFile.Write(__result, _monitor);
            ServerBanner.Print(_monitor, _helper);

            // If Steam lobby was already created, update Galaxy lobby with Steam lobby ID
            if (_steamLobbyId != 0)
            {
                _monitor.Log(
                    "Steam lobby already exists, updating Galaxy lobby now...",
                    LogLevel.Debug
                );
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
            _monitor.Log(
                $"Using fallback Steam ID: {FallbackSteamId} (GameServer not ready)",
                LogLevel.Trace
            );
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
        var listenerType = AccessTools.TypeByName(
            "StardewValley.SDKs.GogGalaxy.Listeners.GalaxyAuthListener"
        );
        var onGalaxyAuthSuccessOriginal = _helper.Reflection.GetMethod(
            galaxyHelper,
            "onGalaxyAuthSuccess"
        );
        var onGalaxyAuthFailureOriginal = _helper.Reflection.GetMethod(
            galaxyHelper,
            "onGalaxyAuthFailure"
        );

        var onGalaxyAuthSuccess = new Action(() =>
        {
            _monitor.Log($"GalaxySDK auth success", LogLevel.Trace);
            Diagnostics.ModEventLog.Emit("auth_galaxy_success");
            onGalaxyAuthSuccessOriginal.Invoke();
        });

        var onGalaxyAuthFailure = new Action<IAuthListener.FailureReason>(
            (reason) =>
            {
                _monitor.Log($"GalaxySDK auth failed: {reason}", LogLevel.Error);
                Diagnostics.ModEventLog.Emit(
                    "auth_galaxy_failed",
                    new { reason = reason.ToString() }
                );
                onGalaxyAuthFailureOriginal.Invoke(reason);
            }
        );

        var onGalaxyAuthLost = new Action(() =>
        {
            _monitor.Log("GalaxySDK auth lost, signing in again...", LogLevel.Info);
            Diagnostics.ModEventLog.Emit("auth_galaxy_lost");

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

        return (IAuthListener)
            Activator.CreateInstance(
                listenerType,
                onGalaxyAuthSuccess,
                onGalaxyAuthFailure,
                onGalaxyAuthLost
            );
    }

    /// <summary>
    /// Replicate original code for `GalaxyHelper.Initialize`, with modification for `StardewDisplayName`.
    /// </summary>
    private IOperationalStateChangeListener CreateGalaxyStateChangeListener(
        GalaxyHelper galaxyHelper
    )
    {
        var listenerType = AccessTools.TypeByName(
            "StardewValley.SDKs.GogGalaxy.Listeners.GalaxyOperationalStateChangeListener"
        );
        var onGalaxyStateChangeOriginal = _helper.Reflection.GetMethod(
            galaxyHelper,
            "onGalaxyStateChange"
        );

        var onGalaxyStateChange = new Action<uint>(
            (num) =>
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
            }
        );

        return (IOperationalStateChangeListener)
            Activator.CreateInstance(listenerType, onGalaxyStateChange);
    }

    /// <summary>
    /// Get the encrypted app ticket from Steam without using a real Steam client.
    /// </summary>
    private byte[] GetEncryptedAppTicketSteam()
    {
        _monitor.Log("GalaxySDK retrieving steam app ticket...", LogLevel.Debug);

        if (_steamAppTicketFetcher == null)
        {
            throw new InvalidOperationException(
                "Steam authentication was not completed. Is STEAM_AUTH_URL set?"
            );
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
            timeoutMs: 30000
        );

        // Verify the service is healthy and logged in
        try
        {
            _steamAppTicketFetcher.VerifyServiceReady();
            _monitor.Log("Steam-auth service is ready ✓", LogLevel.Info);
            Diagnostics.ModEventLog.Emit("auth_steam_sidecar_ready");
            return true;
        }
        catch (Exception ex)
        {
            _monitor.Log($"Steam-auth service not ready: {ex.Message}", LogLevel.Error);
            _monitor.Log(
                "Make sure you ran: docker compose run -it steam-auth setup",
                LogLevel.Error
            );
            Diagnostics.ModEventLog.Emit(
                "auth_steam_sidecar_unreachable",
                new { exceptionType = ex.GetType().Name, message = ex.Message }
            );
            return false;
        }
    }

    #endregion
}
