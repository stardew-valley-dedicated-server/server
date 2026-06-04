using HarmonyLib;
using JunimoServer.Services.Lobby;
using JunimoServer.Shared;
using JunimoServer.Util;
using Netcode;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Locations;
using StardewValley.Network;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace JunimoServer.Services.PasswordProtection
{
    public class PasswordProtectionService : ModService
    {
        private static PasswordProtectionService _instance;

        private readonly IModHelper _helper;
        private readonly IMonitor _monitor;
        private readonly LobbyService _lobbyService;
        private static MethodInfo _sendLocationMethod;

        /// <summary>
        /// Thread-safe dictionary tracking authentication state per player.
        /// </summary>
        private readonly ConcurrentDictionary<long, PlayerAuthData> _playerAuthData = new();

        /// <summary>
        /// Players whose barrier-exclusion removal was deferred because they authenticated
        /// during an active day transition. Unregistered on DayStarted.
        /// </summary>
        private readonly ConcurrentDictionary<long, bool> _pendingPostTransitionAuth = new();

        public string ServerPassword { get; }
        public bool IsEnabled => !string.IsNullOrEmpty(ServerPassword);
        public int MaxFailedAttempts { get; }
        public int AuthTimeoutSeconds { get; set; }

        /// <summary>
        /// Seconds after join before sending the welcome message.
        /// Brief delay allows client to finish loading.
        /// </summary>
        public int WelcomeMessageDelaySeconds { get; } = 2;

        /// <summary>
        /// Seconds between authentication reminder messages.
        /// </summary>
        public int ReminderIntervalSeconds { get; } = 30;

        public PasswordProtectionService(IModHelper helper, IMonitor monitor, Harmony harmony, LobbyService lobbyService) : base(helper, monitor)
        {
            if (_instance != null)
                throw new InvalidOperationException("PasswordProtectionService already initialized - only one instance allowed");

            // Set instance variable for use in static Harmony patches
            _instance = this;

            _helper = helper;
            _monitor = monitor;
            _lobbyService = lobbyService;

            ServerPassword = Env.ServerPassword;
            MaxFailedAttempts = Env.MaxLoginAttempts;
            AuthTimeoutSeconds = Env.AuthTimeoutSeconds;

            if (!IsEnabled)
            {
                _monitor.Log("[Auth] Password protection is DISABLED", LogLevel.Info);
                return;
            }

            _monitor.Log($"[Auth] Password protection ENABLED (maxAttempts={MaxFailedAttempts}, timeout={AuthTimeoutSeconds}s)", LogLevel.Info);

            // KEY PATCH: Intercept farmhand request to capture original spawn info
            // and log client-sent spawn fields for lobby redirect diagnostics.
            harmony.Patch(
                original: AccessTools.Method(typeof(GameServer), nameof(GameServer.checkFarmhandRequest)),
                prefix: new HarmonyMethod(typeof(PasswordProtectionService), nameof(CheckFarmhandRequest_Prefix)),
                postfix: new HarmonyMethod(typeof(PasswordProtectionService), nameof(CheckFarmhandRequest_Postfix))
            );

            // Explicitly send the lobby cabin location before sendServerIntroduction.
            // Vanilla checkFarmhandRequest skips sendLocation for the lobby cabin because
            // isAlwaysActiveLocation() returns true for building interiors nested in the
            // always-active Farm. Without this, ApplyWakeUpPosition crashes on the client.
            _sendLocationMethod = AccessTools.Method(typeof(GameServer), "sendLocation",
                new[] { typeof(long), typeof(GameLocation), typeof(bool) });

            harmony.Patch(
                original: AccessTools.Method(typeof(GameServer), nameof(GameServer.sendServerIntroduction)),
                prefix: new HarmonyMethod(typeof(PasswordProtectionService),
                    nameof(SendServerIntroduction_SendLobbyLocation_Prefix))
                    { priority = Priority.First }
            );

            // Filter messages from unauthenticated players
            harmony.Patch(
                original: AccessTools.Method(typeof(GameServer), "processIncomingMessage", new[] { typeof(IncomingMessage) }),
                prefix: new HarmonyMethod(typeof(PasswordProtectionService), nameof(ProcessIncomingMessage_Prefix))
            );

            // Cleanup on disconnect
            harmony.Patch(
                original: AccessTools.Method(typeof(GameServer), nameof(GameServer.playerDisconnected)),
                postfix: new HarmonyMethod(typeof(PasswordProtectionService), nameof(PlayerDisconnected_Postfix))
            );

            // Filter outgoing messages TO unauthenticated players (e.g., newDaySync messages)
            harmony.Patch(
                original: AccessTools.Method(typeof(GameServer), nameof(GameServer.sendMessage), new[] { typeof(long), typeof(OutgoingMessage) }),
                prefix: new HarmonyMethod(typeof(PasswordProtectionService), nameof(SendMessage_Prefix))
            );

            helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
            helper.Events.GameLoop.DayStarted += OnDayStarted;
            _monitor.Log("[Auth] Password protection patches applied", LogLevel.Debug);
        }

        #region Harmony Patches

        /// <summary>
        /// PREFIX on checkFarmhandRequest - captures original spawn info before player joins.
        /// Signature: checkFarmhandRequest(string userId, string connectionId, NetFarmerRoot farmer, Action sendMessage, Action approve)
        /// </summary>
        private static void CheckFarmhandRequest_Prefix(string userId, string connectionId, NetFarmerRoot farmer)
        {
            _instance?.OnFarmhandRequest(farmer);
        }

        /// <summary>
        /// POSTFIX on checkFarmhandRequest. Runs on the SERVER after vanilla approval logic.
        /// Logs diagnostic spawn fields, then ensures the player is assigned to their real
        /// cabin (not the lobby cabin that the lobby redirect placed in homeLocation).
        /// </summary>
        private static void CheckFarmhandRequest_Postfix(NetFarmerRoot farmer)
        {
            if (_instance == null || !_instance.IsEnabled || farmer?.Value == null) return;

            var f = farmer.Value;
            var farmerId = f.UniqueMultiplayerID;
            var approved = Game1.otherFarmers.ContainsKey(farmerId);

            if (!approved) return;

            var daysPlayed = (int)Game1.MasterPlayer.stats.DaysPlayed;
            var disconnectMatch = f.disconnectDay.Value == daysPlayed;
            var lobbyName = _instance._lobbyService.GetExistingLobbyLocationName(farmerId);
            var disconnectIsLobby = f.disconnectLocation.Value == lobbyName;

            _instance._monitor.Log(
                $"[Auth] checkFarmhandRequest postfix for {farmerId}:\n" +
                $"  client-sent disconnectDay={f.disconnectDay.Value} (server DaysPlayed={daysPlayed}, match={disconnectMatch})\n" +
                $"  client-sent disconnectLocation={f.disconnectLocation.Value ?? "(null)"} (isLobby={disconnectIsLobby})\n" +
                $"  client-sent homeLocation={f.homeLocation.Value ?? "(null)"}\n" +
                $"  client-sent lastSleepLocation={f.lastSleepLocation.Value ?? "(null)"}\n" +
                $"  lobby={lobbyName ?? "(null)"}",
                LogLevel.Info);

            EnsureRealCabinAssignment(f);
        }

        /// <summary>
        /// PREFIX on sendServerIntroduction (Priority.First, before SecurityService).
        /// Sends the lobby cabin location to the client before the server intro triggers
        /// setUpGame() -> ApplyWakeUpPosition(). Vanilla skips this because
        /// isAlwaysActiveLocation() returns true for Farm-nested building interiors.
        /// Returns true so SecurityService's prefix still runs.
        /// </summary>
        private static bool SendServerIntroduction_SendLobbyLocation_Prefix(GameServer __instance, long peer)
        {
            if (_instance == null || !_instance.IsEnabled || _sendLocationMethod == null)
                return true;

            if (!Game1.otherFarmers.TryGetValue(peer, out var farmer))
                return true;

            var disconnectLocation = farmer.disconnectLocation.Value;
            if (string.IsNullOrEmpty(disconnectLocation))
                return true;

            var lobbyName = _instance._lobbyService.GetExistingLobbyLocationName(peer);
            if (lobbyName == null || disconnectLocation != lobbyName)
                return true;

            var lobbyGameLocation = Game1.getLocationFromName(disconnectLocation);
            if (lobbyGameLocation == null)
            {
                _instance._monitor.Log(
                    $"[Auth] Lobby location '{disconnectLocation}' not found for peer {peer}",
                    LogLevel.Warn);
                return true;
            }

            _instance._monitor.Log(
                $"[Auth] Sending lobby location '{disconnectLocation}' to peer {peer}",
                LogLevel.Debug);

            _sendLocationMethod.Invoke(__instance, new object[] { peer, lobbyGameLocation, true });
            return true;
        }

        /// <summary>
        /// Ensures the player has a valid, real (non-lobby) cabin assignment after
        /// vanilla's checkFarmhandRequest approves them. We treat homeLocation as
        /// vanilla's source of truth: if it resolves to a real non-lobby Cabin, accept
        /// it and return. Otherwise (empty / stale / lobby cabin), clear it and let
        /// TryAssignFarmhandHome pick a fresh cabin via AssignFarmhand (which sets
        /// both farmhandReference and homeLocation).
        ///
        /// We deliberately do NOT trigger reassignment based on farmhandReference.uid
        /// mismatches — that field can be transiently out of sync with farmhandData
        /// due to vanilla netcode timing. FindPlayerCabin handles ref desync at login.
        /// </summary>
        private static void EnsureRealCabinAssignment(Farmer farmer)
        {
            var home = farmer.homeLocation.Value;
            string reason;
            if (string.IsNullOrEmpty(home))
            {
                reason = "empty";
            }
            else
            {
                var currentHome = Game1.getLocationFromName(home);
                if (currentHome is not Cabin homeCabin)
                {
                    reason = "stale/missing cabin";
                }
                else if (homeCabin.ParentBuilding != null && LobbyService.IsLobbyCabin(homeCabin.ParentBuilding))
                {
                    reason = "lobby cabin";
                }
                else
                {
                    return; // happy path: homeLocation resolves to a real non-lobby cabin
                }
            }

            _instance._monitor.Log(
                $"[Auth] Clearing homeLocation '{home}' ({reason}) for '{ChatRedaction.MaskValue(farmer.Name)}' (id={farmer.UniqueMultiplayerID})",
                LogLevel.Info);
            farmer.homeLocation.Value = "";

            if (Game1.netWorldState.Value.TryAssignFarmhandHome(farmer))
            {
                _instance._monitor.Log(
                    $"[Auth] Reassigned '{ChatRedaction.MaskValue(farmer.Name)}' (id={farmer.UniqueMultiplayerID}) to real cabin: homeLocation='{farmer.homeLocation.Value}'",
                    LogLevel.Info);
            }
            else
            {
                _instance._monitor.Log(
                    $"[Auth] TryAssignFarmhandHome failed for '{ChatRedaction.MaskValue(farmer.Name)}' (id={farmer.UniqueMultiplayerID}) - no available cabin",
                    LogLevel.Warn);
            }
        }

        private static bool ProcessIncomingMessage_Prefix(IncomingMessage message)
        {
            return _instance?.ShouldProcessMessage(message) ?? true;
        }

        private static void PlayerDisconnected_Postfix(long disconnectee)
        {
            _instance?.OnPlayerDisconnected(disconnectee);
        }

        /// <summary>
        /// PREFIX on GameServer.sendMessage - filters outgoing messages TO unauthenticated players.
        /// Blocks newDaySync (14) and startNewDaySync (30) messages which would close the
        /// CharacterCustomization menu on clients who haven't entered their name yet.
        /// </summary>
        private static bool SendMessage_Prefix(long peerId, OutgoingMessage message)
        {
            return _instance?.ShouldSendMessage(peerId, message) ?? true;
        }

        #endregion

        #region Core Logic

        private void OnFarmhandRequest(NetFarmerRoot farmer)
        {
            if (!IsEnabled || farmer?.Value == null) return;

            // Unpause the game before sendServerIntroduction runs.
            // When no players are connected, the server is paused. If we don't unpause here,
            // the initial world state sent to the client will have IsPaused=true, causing
            // black screen on connect (the fade-in never completes while paused).
            if (Game1.netWorldState.Value.IsPaused)
            {
                Game1.netWorldState.Value.IsPaused = false;
            }

            var farmerId = farmer.Value.UniqueMultiplayerID;

            // Skip host
            if (farmerId == Game1.player.UniqueMultiplayerID) return;

            var isNewPlayer = !farmer.Value.isCustomized.Value;
            var playerType = isNewPlayer ? "new" : "returning";
            _monitor.Log($"[Auth] {ChatRedaction.MaskValue(farmer.Value.Name)} ({playerType}) connecting", LogLevel.Debug);

            // Create auth tracking
            var authData = new PlayerAuthData(farmerId)
            {
                IsNewPlayer = isNewPlayer
            };
            _playerAuthData[farmerId] = authData;

            // Register with LobbyService to exclude from sleep ready-checks
            _lobbyService.RegisterUnauthenticatedPlayer(farmerId);
        }

        private bool ShouldProcessMessage(IncomingMessage message)
        {
            if (!IsEnabled) return true;
            if (message.FarmerID == Game1.player.UniqueMultiplayerID) return true; // Host

            var authData = GetAuthData(message.FarmerID);
            if (authData == null)
                return false; // Block - no auth data means something went wrong

            if (authData.State == AuthState.Authenticated)
                return true;

            // Unauthenticated player - WHITELIST approach (only allow specific safe messages)
            switch (message.MessageType)
            {
                case Multiplayer.farmerDelta: // 0
                    // ALLOW - this contains farmer creation data (name, appearance)
                    return true;

                case Multiplayer.chatMessage: // 10
                    // Only allow !login and !help commands
                    var isAllowed = IsAllowedChatCommand(message);
                    _monitor.Log($"[Auth] Chat message from {message.FarmerID}, isAllowed={isAllowed}", LogLevel.Debug);
                    if (!isAllowed)
                    {
                        _helper.SendPrivateMessage(message.FarmerID, "Please authenticate first: !login <password>");
                        return false;
                    }
                    return true;

                case Multiplayer.playerIntroduction: // 2
                    // ALLOW - needed for player to join properly
                    return true;

                case Multiplayer.disconnecting: // 19
                    // ALLOW - needed for clean disconnect
                    return true;

                default:
                    // BLOCK everything else - world changes, location deltas, warps, etc.
                    // This prevents furniture manipulation, item pickups, and all other interactions
                    // TODO: auth verbose traces should be separately toggleable (via per module monitor)
                    //_monitor.Log($"[Auth] BLOCKED type {message.MessageType} from {message.FarmerID}", LogLevel.Trace);
                    return false;
            }
        }

        /// <summary>
        /// Determines if an outgoing message should be sent TO a specific player.
        /// Blocks newDaySync and startNewDaySync messages to unauthenticated players,
        /// which would otherwise close the CharacterCustomization menu on new farmhands.
        /// </summary>
        private bool ShouldSendMessage(long peerId, OutgoingMessage message)
        {
            if (!IsEnabled) return true;
            if (peerId == Game1.player.UniqueMultiplayerID) return true; // Host

            // Check if this player is authenticated
            if (!_playerAuthData.TryGetValue(peerId, out var authData))
            {
                // Peer has no auth data. Either disconnected (cleanup already ran) or
                // hasn't completed checkFarmhandRequest yet. Either way, allow the message
                // through; blocking would cause hangs, and the message is harmless.
                _monitor.Log($"[Auth] No auth data for peer {peerId} in ShouldSendMessage - allowing (likely disconnected)", LogLevel.Trace);
                return true;
            }

            if (authData.State == AuthState.Authenticated)
                return true;

            // Unauthenticated player - block day sync messages that would close their menu
            switch (message.MessageType)
            {
                case Multiplayer.newDaySync: // 14
                    // BLOCK - this triggers Game1.NewDay() which calls exitActiveMenu()
                    // and would close the CharacterCustomization menu
                    _monitor.Log($"[Auth] Blocked newDaySync to unauthenticated player {peerId}", LogLevel.Debug);
                    return false;

                case Multiplayer.startNewDaySync: // 30
                    // BLOCK - this signals the start of day transition
                    _monitor.Log($"[Auth] Blocked startNewDaySync to unauthenticated player {peerId}", LogLevel.Debug);
                    return false;

                default:
                    // ALLOW all other outgoing messages
                    return true;
            }
        }

        /// <summary>
        /// Gets existing auth data for a farmer.
        /// Auth data is always created in OnFarmhandRequest (checkFarmhandRequest patch),
        /// which runs before any messages can be processed for that player.
        /// </summary>
        private PlayerAuthData GetAuthData(long farmerId)
        {
            if (_playerAuthData.TryGetValue(farmerId, out var existing))
                return existing;

            // This should never happen - processIncomingMessage only runs for players
            // who have already been approved via checkFarmhandRequest, which creates auth data.
            _monitor.Log($"[Auth] BUG: No auth data for farmer {farmerId} - this should not happen", LogLevel.Error);
            return null;
        }

        private bool IsAllowedChatCommand(IncomingMessage message)
        {
            try
            {
                // Chat message format: RecipientID (long) + LanguageCode (Int16 enum) + Message (string)
                // Reset reader position and read the message
                message.Reader.BaseStream.Position = 0;
                message.Reader.ReadInt64(); // recipient ID
                message.Reader.ReadInt16(); // language code (ReadEnum uses Int16)
                var chatText = message.Reader.ReadString().TrimStart();

                // Reset for subsequent reads
                message.Reader.BaseStream.Position = 0;

                // Allow !login command for authentication
                if (chatText.StartsWith("!login", StringComparison.OrdinalIgnoreCase))
                {
                    _monitor.Log($"[Auth] Allowing !login from {message.FarmerID}", LogLevel.Debug);
                    return true;
                }

                // Allow !help so players can discover commands
                if (chatText.StartsWith("!help", StringComparison.OrdinalIgnoreCase))
                {
                    _monitor.Log($"[Auth] Allowing !help from {message.FarmerID}", LogLevel.Debug);
                    return true;
                }

                _monitor.Log($"[Auth] Blocking chat from {message.FarmerID}: '{ChatRedaction.MaskSecrets(chatText)}'", LogLevel.Debug);
                return false;
            }
            catch (Exception ex)
            {
                _monitor.Log($"[Auth] Error reading chat message: {ex.Message}", LogLevel.Error);
                // Fail-closed: block the message on parsing errors to prevent bypassing auth
                return false;
            }
        }

        private void OnPlayerDisconnected(long peerId)
        {
            if (_playerAuthData.TryRemove(peerId, out _))
            {
                _monitor.Log($"[Auth] Cleaned up auth data for {peerId}", LogLevel.Debug);
            }

            // Clean up deferred post-transition auth if player disconnected before DayStarted
            _pendingPostTransitionAuth.TryRemove(peerId, out _);

            // Also unregister from lobby exclusions (player disconnected)
            _lobbyService.UnregisterUnauthenticatedPlayer(peerId);

            // Also clean up individual lobby if applicable
            _lobbyService.CleanupIndividualLobby(peerId);
        }

        private void OnDayStarted(object sender, DayStartedEventArgs e)
        {
            foreach (var playerId in _pendingPostTransitionAuth.Keys)
            {
                if (_pendingPostTransitionAuth.TryRemove(playerId, out _))
                {
                    _lobbyService.UnregisterUnauthenticatedPlayer(playerId);
                    _monitor.Log($"[Auth] Completed deferred barrier unregister for {playerId} after day transition", LogLevel.Info);
                }
            }
        }

        private void OnUpdateTicked(object sender, UpdateTickedEventArgs e)
        {
            if (!IsEnabled || !Game1.hasLoadedGame) return;

            foreach (var kvp in _playerAuthData.ToList())
            {
                var authData = kvp.Value;
                if (authData.State != AuthState.Unauthenticated) continue;

                // Don't process players who haven't fully connected yet.
                // JoinTime is set during checkFarmhandRequest (before connection completes),
                // so we must wait until the player appears in otherFarmers before sending
                // any messages. Otherwise the client's chatbox won't exist yet.
                if (!Game1.otherFarmers.TryGetValue(authData.PlayerId, out var farmer))
                    continue;

                // For new players, wait until they finish CharacterCustomization before sending login prompts.
                // The player can't access chat while the customization menu is open anyway.
                if (authData.IsNewPlayer && !authData.CustomizationCompleteTime.HasValue)
                {
                    if (!farmer.isCustomized.Value)
                    {
                        // Still customizing - don't send messages yet, don't count towards timeout
                        continue;
                    }

                    // Player just finished customization - record the time
                    authData.CustomizationCompleteTime = DateTime.UtcNow;
                    _monitor.Log($"[Auth] Player {authData.PlayerId} finished character customization", LogLevel.Debug);
                }

                // Start the reference clock from when the player is actually ready:
                // - New players: from when they finished customization
                // - Returning players: from when they first appeared in otherFarmers
                if (!authData.IsNewPlayer && !authData.CustomizationCompleteTime.HasValue)
                {
                    // First tick where returning player is in otherFarmers. Start the clock now.
                    authData.CustomizationCompleteTime = DateTime.UtcNow;
                }
                var referenceTime = authData.CustomizationCompleteTime ?? authData.JoinTime;
                var elapsed = (DateTime.UtcNow - referenceTime).TotalSeconds;

                // Send welcome message once (after a brief delay for client to be ready)
                // Player spawns in lobby via FarmhandSenderService.ApplyLobbyRedirectToFarmhand
                if (!authData.WelcomeMessageSent && elapsed > WelcomeMessageDelaySeconds)
                {
                    SendWelcomeMessage(authData);
                    authData.WelcomeMessageSent = true;
                    _monitor.Log($"[Auth] Sent welcome message to {authData.PlayerId}", LogLevel.Debug);
                }

                // Timeout
                if (AuthTimeoutSeconds > 0 && elapsed > AuthTimeoutSeconds)
                {
                    _monitor.Log($"[Auth] Player {authData.PlayerId} timed out after {AuthTimeoutSeconds}s - kicking", LogLevel.Warn);
                    _helper.SendPrivateMessage(authData.PlayerId, "Authentication timeout. Disconnecting...");
                    // Remove auth data immediately to prevent repeated kick attempts on subsequent ticks
                    _playerAuthData.TryRemove(authData.PlayerId, out _);
                    // Also unregister from lobby exclusions
                    _lobbyService.UnregisterUnauthenticatedPlayer(authData.PlayerId);
                    Game1.server.kick(authData.PlayerId);
                    continue;
                }

                // Reminders
                if (authData.WelcomeMessageSent)
                {
                    var sinceReminder = (DateTime.UtcNow - authData.LastReminderTime).TotalSeconds;
                    if (sinceReminder > ReminderIntervalSeconds)
                    {
                        _helper.SendPrivateMessage(authData.PlayerId, "Type !login YOUR_PASSWORD to play");
                        authData.LastReminderTime = DateTime.UtcNow;
                    }
                }
            }
        }

        #endregion

        #region Authentication

        public AuthenticationResult TryAuthenticate(long playerId, string password)
        {
            if (!IsEnabled)
                return new AuthenticationResult(true, "Password protection disabled.");

            if (!_playerAuthData.TryGetValue(playerId, out var authData))
            {
                Diagnostics.ModEventLog.Emit("auth_login_attempted", new
                {
                    playerId,
                    result = "no_auth_data",
                    failedAttempts = 0
                });
                return new AuthenticationResult(true, "Already authenticated.");
            }

            // Use lock to prevent race conditions during auth state transitions
            lock (authData.StateLock)
            {
                if (authData.State == AuthState.Authenticated)
                {
                    Diagnostics.ModEventLog.Emit("auth_login_attempted", new
                    {
                        playerId,
                        result = "already_authenticated",
                        failedAttempts = authData.FailedAttempts
                    });
                    return new AuthenticationResult(true, "Already authenticated.");
                }

                if (SecureComparePasswords(password, ServerPassword))
                {
                    authData.State = AuthState.Authenticated;
                    _monitor.Log($"[Auth] {playerId} authenticated!", LogLevel.Info);
                    Diagnostics.ModEventLog.Emit("auth_login_attempted", new
                    {
                        playerId,
                        result = "success",
                        failedAttempts = authData.FailedAttempts
                    });

                    // Warp from lobby to destination (also handles barrier exclusion removal)
                    WarpToDestination(authData);

                    return new AuthenticationResult(true, "Welcome! You may now play.");
                }

                authData.FailedAttempts++;
                var failedAttempts = authData.FailedAttempts;
                _monitor.Log($"[Auth] Player {playerId} failed authentication ({failedAttempts}/{MaxFailedAttempts})", LogLevel.Warn);

                if (failedAttempts >= MaxFailedAttempts)
                {
                    _monitor.Log($"[Auth] Player {playerId} exceeded max attempts - kicking", LogLevel.Warn);
                    Diagnostics.ModEventLog.Emit("auth_login_attempted", new
                    {
                        playerId,
                        result = "kicked_max_attempts",
                        failedAttempts
                    });
                    Game1.server.kick(playerId);
                    return new AuthenticationResult(false, "Too many attempts. Disconnected.", true);
                }

                Diagnostics.ModEventLog.Emit("auth_login_attempted", new
                {
                    playerId,
                    result = "wrong_password",
                    failedAttempts
                });
                return new AuthenticationResult(false, $"Wrong password. {MaxFailedAttempts - failedAttempts} tries left.");
            }
        }

        public bool IsPlayerAuthenticated(long playerId)
        {
            if (!IsEnabled) return true;
            if (playerId == Game1.player.UniqueMultiplayerID) return true; // Host
            if (!_playerAuthData.TryGetValue(playerId, out var auth)) return false; // No data = not authenticated
            return auth.State == AuthState.Authenticated;
        }

        public bool RequiresAuthentication(long playerId) => IsEnabled && !IsPlayerAuthenticated(playerId);

        #endregion

        #region Helpers

        private void WarpToDestination(PlayerAuthData authData)
        {
            // If a day transition is in progress, send a passout message instead of normal warp.
            // This makes the client go through the sleep flow and join the day transition naturally.
            // IMPORTANT: Keep the player excluded from barriers until DayStarted fires.
            // Removing the exclusion now would make the barrier wait for a player who can't
            // participate (game thread is blocked by save), causing an infinite hang.
            if (Game1.newDaySync != null && Game1.newDaySync.hasInstance() && !Game1.newDaySync.hasFinished())
            {
                _pendingPostTransitionAuth[authData.PlayerId] = true;
                Diagnostics.ModEventLog.Emit("auth_warp_deferred", new
                {
                    playerId = authData.PlayerId,
                    reason = "newDaySync_active"
                });
                SendPassoutToPlayer(authData);
                return;
            }

            // Safe to unregister immediately; no barriers active
            _lobbyService.UnregisterUnauthenticatedPlayer(authData.PlayerId);

            // Always warp to cabin entry - matches vanilla game behavior on connect
            var cabinIndoors = FindPlayerCabin(authData.PlayerId);
            if (cabinIndoors == null)
            {
                Diagnostics.ModEventLog.Emit("auth_warp_failed", new
                {
                    playerId = authData.PlayerId,
                    reason = "no_cabin_found"
                });
                KickPlayerMissingCabin(authData.PlayerId, "warp destination");
                return;
            }
            var entry = cabinIndoors.getEntryLocation();
            var location = cabinIndoors.NameOrUniqueName;
            var tileX = entry.X;
            var tileY = entry.Y;

            _monitor.Log($"[Auth] Warping {authData.PlayerId} to {location} @ ({tileX},{tileY})", LogLevel.Info);
            Diagnostics.ModEventLog.Emit("auth_warped", new
            {
                playerId = authData.PlayerId,
                location,
                tileX,
                tileY
            });

            _lobbyService.WarpFromLobby(authData.PlayerId, location, tileX, tileY);
        }

        /// <summary>
        /// Sends a passout message to a player who authenticated during a day transition.
        /// This makes the client go through the normal passout flow (warp to bed + sleep animation),
        /// allowing them to naturally join the ongoing day transition.
        /// </summary>
        private void SendPassoutToPlayer(PlayerAuthData authData)
        {
            var cabinIndoors = FindPlayerCabin(authData.PlayerId);
            if (cabinIndoors == null)
            {
                KickPlayerMissingCabin(authData.PlayerId, "passout during day transition");
                return;
            }

            // Get the bed position in the player's cabin
            var bedSpot = cabinIndoors.GetPlayerBedSpot();
            var hasBed = cabinIndoors.GetPlayerBed() != null;
            var locationName = cabinIndoors.NameOrUniqueName;

            _monitor.Log($"[Auth] Day transition in progress - sending passout to {authData.PlayerId} (bed at {locationName} @ {bedSpot})", LogLevel.Info);

            // Send passout message (message type 29) to warp the client to bed
            // This triggers Farmer.performPassoutWarp on the client, which handles the sleep animation
            object[] passoutData = new object[] { locationName, bedSpot.X, bedSpot.Y, hasBed };
            Game1.server.sendMessage(authData.PlayerId, Multiplayer.passout, Game1.player, passoutData);

            if (Game1.otherFarmers.TryGetValue(authData.PlayerId, out var farmer))
            {
                farmer.currentLocation = cabinIndoors;
                farmer.Position = new Microsoft.Xna.Framework.Vector2(bedSpot.X * 64f, bedSpot.Y * 64f);
            }
        }

        /// <summary>
        /// Finds the player's real (non-lobby) cabin. Uses farmhandReference for lookup,
        /// which is set correctly during connection by <see cref="EnsureRealCabinAssignment"/>.
        /// Checks both the resolved owner (for online players) and the stored uid
        /// (for players in disconnect windows where getFarmer() can't resolve).
        /// </summary>
        private Cabin FindPlayerCabin(long playerId)
        {
            var farm = Game1.getFarm();
            if (farm == null) return null;

            // Primary lookup: match by farmhandReference (owner ID or uid)
            foreach (var building in farm.buildings)
            {
                if (!building.isCabin) continue;
                if (LobbyService.IsLobbyCabin(building)) continue;

                var cabin = building.GetIndoors<Cabin>();
                if (cabin == null) continue;

                if (cabin.owner?.UniqueMultiplayerID == playerId)
                    return cabin;

                if (cabin.farmhandReference.defined.Value
                    && cabin.farmhandReference.uid.Value == playerId)
                    return cabin;
            }

            // Fallback: match by homeLocation. farmhandReference.uid can become stale
            // when TryAssignFarmhandHome short-circuits or Netcode resyncs the cabin's
            // NetFarmerRef from save data. homeLocation is always set correctly by
            // AssignFarmhand and is the canonical "where does this player live" field.
            // Uses building iteration (not getLocationFromName) to avoid dependence on
            // the location lookup cache which is flushed during day transitions.
            if (Game1.otherFarmers.TryGetValue(playerId, out var player))
            {
                var homeLocation = player.homeLocation?.Value;
                if (!string.IsNullOrEmpty(homeLocation))
                {
                    foreach (var building in farm.buildings)
                    {
                        if (!building.isCabin) continue;
                        if (LobbyService.IsLobbyCabin(building)) continue;
                        var cabin = building.GetIndoors<Cabin>();
                        if (cabin != null && cabin.NameOrUniqueName == homeLocation)
                        {
                            _monitor.Log($"[Auth] FindPlayerCabin: primary lookup failed for '{ChatRedaction.MaskValue(player.Name)}' (id={playerId}), " +
                                $"found via homeLocation fallback: {homeLocation}", LogLevel.Warn);
                            return cabin;
                        }
                    }
                }
            }

            // Recovery: both lookups failed. Ask vanilla to assign a cabin (it iterates
            // farm.buildings and picks the first where Cabin.CanAssignTo is true; LobbyService's
            // CanAssignTo postfix already excludes lobby cabins). On success, re-run the primary
            // lookup so we return the freshly-assigned cabin.
            if (player != null && Game1.netWorldState.Value.TryAssignFarmhandHome(player))
            {
                _monitor.Log($"[Auth] FindPlayerCabin: recovered via TryAssignFarmhandHome " +
                    $"for '{ChatRedaction.MaskValue(player.Name)}' (id={playerId}), homeLocation now '{player.homeLocation.Value}'", LogLevel.Warn);

                foreach (var building in farm.buildings)
                {
                    if (!building.isCabin) continue;
                    if (LobbyService.IsLobbyCabin(building)) continue;
                    var cabin = building.GetIndoors<Cabin>();
                    if (cabin == null) continue;

                    if (cabin.owner?.UniqueMultiplayerID == playerId)
                        return cabin;
                    if (cabin.farmhandReference.defined.Value
                        && cabin.farmhandReference.uid.Value == playerId)
                        return cabin;
                    if (cabin.NameOrUniqueName == player.homeLocation.Value)
                        return cabin;
                }
            }

            LogCabinLookupFailure(playerId);
            return null;
        }

        private void LogCabinLookupFailure(long playerId)
        {
            var farm = Game1.getFarm();
            var cabins = farm?.buildings.Where(b => b.isCabin).ToList() ?? new();
            _monitor.Log($"[Auth] Cabin lookup failed for {playerId}. Farm has {cabins.Count} cabin(s):", LogLevel.Error);
            foreach (var c in cabins)
            {
                var ind = c.GetIndoors<Cabin>();
                var refDefined = ind?.farmhandReference?.defined?.Value;
                var refUid = ind?.farmhandReference?.uid?.Value;
                var ownerName = ind?.owner?.Name;
                var ownerId = ind?.owner?.UniqueMultiplayerID;
                var isLobby = LobbyService.IsLobbyCabin(c);
                _monitor.Log($"[Auth]   Cabin '{ind?.NameOrUniqueName}': refDefined={refDefined}, refUid={refUid}, owner='{ChatRedaction.MaskValue(ownerName)}' (id={ownerId}), isLobby={isLobby}", LogLevel.Error);
            }
            var inFarmhandData = Game1.netWorldState.Value.farmhandData.FieldDict.ContainsKey(playerId);
            var inOtherFarmers = Game1.otherFarmers.ContainsKey(playerId);
            Farmer diagFarmer = inOtherFarmers ? Game1.otherFarmers[playerId] : null;
            if (diagFarmer == null && Game1.netWorldState.Value.farmhandData.FieldDict.TryGetValue(playerId, out var diagRef))
                diagFarmer = diagRef.Value;
            _monitor.Log($"[Auth]   Player {playerId}: inFarmhandData={inFarmhandData}, inOtherFarmers={inOtherFarmers}, " +
                $"homeLocation='{diagFarmer?.homeLocation.Value}', currentLocation='{diagFarmer?.currentLocation?.NameOrUniqueName}'", LogLevel.Error);
        }

        /// <summary>
        /// Kicks a player when their cabin cannot be found after authentication.
        /// This is a recovery mechanism for edge cases where cabin state is inconsistent.
        /// On reconnect, CabinManagerService.EnsureAtLeastXCabins() will create a fresh cabin.
        /// </summary>
        private void KickPlayerMissingCabin(long playerId, string playerType)
        {
            _monitor.Log($"[Auth] Cannot find cabin for {playerType} {playerId} - kicking player", LogLevel.Error);
            _helper.SendPrivateMessage(playerId, "Error: Your cabin could not be found. Please reconnect to the server.");
            Game1.server.kick(playerId);
        }

        private void SendWelcomeMessage(PlayerAuthData authData)
        {
            var lines = new[]
            {
                "[aqua]Welcome! This server is password protected.",
                "[yellow]You're in the lobby until you authenticate.",
                "[red]DO NOT drop items here - they will be deleted!"
            };

            foreach (var line in lines)
                _helper.SendPrivateMessage(authData.PlayerId, line);
        }

        /// <summary>
        /// Compares two passwords using constant-time comparison to prevent timing attacks.
        /// </summary>
        private static bool SecureComparePasswords(string provided, string expected)
        {
            if (provided == null || expected == null)
                return false;

            var providedBytes = Encoding.UTF8.GetBytes(provided);
            var expectedBytes = Encoding.UTF8.GetBytes(expected);

            return CryptographicOperations.FixedTimeEquals(providedBytes, expectedBytes);
        }

        #endregion
    }

    public class AuthenticationResult
    {
        public bool Success { get; }
        public string Message { get; }
        public bool ShouldKick { get; }

        public AuthenticationResult(bool success, string message, bool shouldKick = false)
        {
            Success = success;
            Message = message;
            ShouldKick = shouldKick;
        }
    }
}
