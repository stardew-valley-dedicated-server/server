using HarmonyLib;
using JunimoServer.Services.Settings;
using JunimoServer.Util;
using Microsoft.Xna.Framework;
using Newtonsoft.Json;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.Locations;
using StardewValley.Objects;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace JunimoServer.Services.Lobby
{
    /// <summary>
    /// Manages lobby cabins for password protection.
    /// Players waiting to authenticate are held in lobby cabins.
    /// </summary>
    public class LobbyService : ModService
    {
        private static LobbyService _instance;

        private const string LobbyDataKey = "JunimoServer.LobbyData";

        /// <summary>
        /// The time value sent to editing clients instead of the real time.
        /// 1200 = 12:00 PM - safe from the 2600 passout threshold (2AM = 2600).
        /// </summary>
        private const int FrozenEditingTime = 1200;

        /// <summary>
        /// Invalid item ID used to create a "no entry" indicator at the door.
        /// Invalid IDs render as the error sprite (red circle with diagonal line).
        /// Uses (O) prefix for a 1x1 object instead of (BC) which is 1x2.
        /// </summary>
        private const string DoorBlockerInvalidItemId = "(O)JunimoServer.NoEntry";

        /// <summary>
        /// Maximum decompressed size for layout imports (512KB).
        /// Prevents zip bomb attacks during import.
        /// </summary>
        private const int MaxLayoutDecompressedSize = 512 * 1024;

        /// <summary>
        /// Layout export format prefix and version.
        /// "SDVL" = Stardew Valley Lobby, "0" = format version.
        /// </summary>
        private const string LayoutExportPrefix = "SDVL";
        private const char LayoutExportVersion = '0';

        /// <summary>
        /// Hidden position for lobby cabins (offset from cabin stack at -20,-20).
        /// </summary>
        public static readonly Point HiddenLobbyLocation = new Point(-21, -21);

        /// <summary>
        /// Door blocker tile position for upgrade level 0 cabin.
        /// Placed at the door entry point to show a "no entry" indicator.
        /// </summary>
        private static readonly Vector2 DoorBlockerTileLevel0 = new Vector2(3, 11);

        /// <summary>
        /// Checks if a cabin building is a lobby cabin (at the hidden lobby location).
        /// Used to filter lobby cabins from the farmhand selection screen.
        /// </summary>
        public static bool IsLobbyCabin(Building building)
        {
            if (building == null || !building.isCabin) return false;
            return building.tileX.Value == HiddenLobbyLocation.X && building.tileY.Value == HiddenLobbyLocation.Y;
        }

        /// <summary>
        /// Regex for valid layout names: alphanumeric, dash, underscore only.
        /// </summary>
        private static readonly Regex ValidLayoutNameRegex = new Regex(@"^[a-zA-Z0-9_-]+$", RegexOptions.Compiled);

        /// <summary>
        /// Maximum length for layout names.
        /// </summary>
        private const int MaxLayoutNameLength = 32;

        /// <summary>
        /// Default lobby layout export string. Used when creating the "default" layout for the first time.
        /// This provides a pre-decorated lobby instead of an empty cabin.
        ///
        /// Format: "SDVL" + version char ('0') + base64(gzip(JSON))
        /// The JSON is a LobbyLayout object containing: Name, CabinSkin, UpgradeLevel, Furniture[], Objects[], Wallpapers{}, Floors{}.
        ///
        /// If you wish to inspect this string for debugging or security validation:
        /// 1. Remove the "SDVL0" prefix (5 chars)
        /// 2. Base64 decode the remainder
        /// 3. Gzip decompress the result
        /// 4. Read the resulting JSON
        /// </summary>
        private const string DefaultLayoutExportString = "SDVL0H4sIAAAAAAAAA62VUU+DMBDHv4rpkyaYjMGG8Dji3MyiZmDUGB86uLG60mJt1WXZd7eDxOlSjcDeetfcj+Puf9c1usI5oAClMMeKSmShEM8Ii5aEae+EZ0elrf23RSZwChN4A4qCjoWGSjAildDhj2s0lpCPUx1zPDyxHa+vI2JC4R4F3er0gAJbH6dcYkk4KxEjoOn17BkSuQ1litKN9RfKORzKbYXyHGeH6n2h3NqkS8VIzmPAMZ5R2DH9Fswox5RW4JGSO6bdgml7/Y6J1Kuf3YvCAqr0pioz6cRrWMZIYmEUS0PeAH9L76x1l28oZtIkG7vTkBhylSxMovlRQec/wFAJuooFgKnLjRPc+2WnDVFLsGtC9WuTLgReRRSgICyr8jTthfptDolIFMXCIO5ei3TLedbaTpaQnlPyQUS0ADo39b2+0m3fdo0tb0Iy79cmpJ5xTg6yF7wWPMOU+L9o+rTbeEy8WmPyZKHK86ofYm3cabUUuACh7TUaQCo4z/XXXLTRjzblfP/C8bc3UYHfWVXu8rjdIptP4YQWHCAIAAA=";

        /// <summary>
        /// Validates a layout name.
        /// </summary>
        /// <returns>Null if valid, or an error message if invalid.</returns>
        public static string ValidateLayoutName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return "Layout name cannot be empty";

            if (name.Length > MaxLayoutNameLength)
                return $"Layout name too long (max {MaxLayoutNameLength} characters)";

            if (!ValidLayoutNameRegex.IsMatch(name))
                return "Layout name can only contain letters, numbers, dash (-), and underscore (_)";

            return null; // Valid
        }

        private readonly IModHelper _helper;
        private readonly IMonitor _monitor;
        private readonly ServerSettingsLoader _settings;

        private LobbyData _data;

        /// <summary>
        /// The shared lobby cabin building (used in Shared mode).
        /// </summary>
        private Building _sharedLobbyCabin;

        /// <summary>
        /// Individual lobby cabins per player (used in Individual mode).
        /// Key: player ID, Value: cabin building.
        /// Thread-safe: accessed from multiple event handlers.
        /// </summary>
        private readonly ConcurrentDictionary<long, Building> _individualLobbies = new();

        /// <summary>
        /// Tracks which layout is currently being edited by which admin (for !lobby create/save workflow).
        /// Key: player ID, Value: (layout name, is new layout, editing cabin building).
        /// Thread-safe: accessed from OnUpdateTicked and OnPeerDisconnected concurrently.
        /// </summary>
        private readonly ConcurrentDictionary<long, (string LayoutName, bool IsNew, Building Cabin)> _layoutEditingSessions = new();

        /// <summary>
        /// Tracks player's previous location before entering edit mode.
        /// Key: player ID, Value: (location name, tile X, tile Y).
        /// Thread-safe: accessed from multiple event handlers.
        /// </summary>
        private readonly ConcurrentDictionary<long, (string Location, int X, int Y)> _previousLocations = new();

        /// <summary>
        /// Tracks whether layout editing mode is currently active.
        /// Volatile to ensure visibility across threads.
        /// </summary>
        private volatile bool _isEditingModeActive;

        /// <summary>
        /// Cache original lighting colors per cabin to restore later.
        /// Key: cabin NameOrUniqueName, Value: (day color, night color).
        /// Thread-safe: accessed during lighting operations.
        /// </summary>
        private readonly ConcurrentDictionary<string, (Color day, Color night)> _originalCabinLighting = new();

        /// <summary>
        /// Lock object for atomic multi-dictionary operations (e.g., cleanup).
        /// </summary>
        private readonly object _stateLock = new();

        /// <summary>
        /// Player IDs that are in the lobby awaiting authentication.
        /// These players should be excluded from sleep ready-checks.
        /// Thread-safe: modified by PasswordProtectionService, read during ready-check updates.
        /// </summary>
        private readonly ConcurrentDictionary<long, bool> _unauthenticatedPlayers = new();

        public LobbyMode Mode => _settings.LobbyMode;
        public bool IsEnabled => !string.IsNullOrEmpty(Env.ServerPassword);

        public LobbyService(IModHelper helper, IMonitor monitor, ServerSettingsLoader settings, Harmony harmony) : base(helper, monitor)
        {
            if (_instance != null)
                throw new InvalidOperationException("LobbyService already initialized - only one instance allowed");
            _instance = this;

            _helper = helper;
            _monitor = monitor;
            _settings = settings;

            // Patch broadcastWorldStateDeltas to send frozen time to editing clients.
            // This prevents the editing client from seeing timeOfDay >= 2600 and triggering passout.
            var originalMethod = AccessTools.Method(typeof(Multiplayer), nameof(Multiplayer.broadcastWorldStateDeltas));
            if (originalMethod == null)
            {
                _monitor.Log("[Lobby] CRITICAL: Could not find Multiplayer.broadcastWorldStateDeltas method for patching. " +
                    "Time isolation for layout editors will NOT work - editors may experience passout at 2AM.", LogLevel.Error);
            }
            else
            {
                var patchResult = harmony.Patch(
                    original: originalMethod,
                    prefix: new HarmonyMethod(typeof(LobbyService), nameof(BroadcastWorldStateDeltas_Prefix))
                );

                if (patchResult == null)
                {
                    _monitor.Log("[Lobby] CRITICAL: Failed to apply Harmony patch for broadcastWorldStateDeltas. " +
                        "Time isolation for layout editors will NOT work - editors may experience passout at 2AM.", LogLevel.Error);
                }
                else
                {
                    _monitor.Log("[Lobby] Successfully patched Multiplayer.broadcastWorldStateDeltas for time isolation", LogLevel.Debug);
                }
            }

            _helper.Events.GameLoop.SaveLoaded += OnSaveLoaded;
            _helper.Events.GameLoop.Saving += OnSaving;
            _helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
            _helper.Events.GameLoop.TimeChanged += OnTimeChanged;
            _helper.Events.GameLoop.DayStarted += OnDayStarted;
            _helper.Events.Multiplayer.PeerConnected += OnPeerConnected;
            _helper.Events.Multiplayer.PeerDisconnected += OnPeerDisconnected;
        }

        #region Time Isolation for Editing Clients

        /// <summary>
        /// Harmony prefix that replaces Multiplayer.broadcastWorldStateDeltas().
        /// When editors or unauthenticated lobby players are present, sends them a modified
        /// world state delta with frozen timeOfDay (1200) and IsPaused=false, so their client
        /// never triggers the 2AM passout and never pauses when host sleeps.
        /// Non-lobby players receive the real time and pause state as normal.
        ///
        /// Flow:
        /// 1. Serialize normal delta (real time/pause) → send to non-lobby players → MarkClean
        /// 2. Set timeOfDay=1200, IsPaused=false → marks both dirty
        /// 3. Serialize lobby delta (frozen time, unpaused) → send to lobby players → MarkClean
        /// 4. Restore real timeOfDay and IsPaused → marks dirty for next tick
        /// </summary>
        /// <param name="__instance">The Multiplayer instance (Harmony injects this).</param>
        /// <returns>false to skip the original method, true to run it.</returns>
        private static bool BroadcastWorldStateDeltas_Prefix(Multiplayer __instance)
        {
            // Build set of players who need frozen time (editors + unauthenticated lobby players)
            var frozenTimePlayers = new HashSet<long>();

            if (_instance != null)
            {
                // Add layout editors
                if (_instance._isEditingModeActive)
                {
                    foreach (var playerId in _instance._layoutEditingSessions.Keys)
                    {
                        frozenTimePlayers.Add(playerId);
                    }
                }

                // Add unauthenticated players in lobby
                foreach (var playerId in _instance._unauthenticatedPlayers.Keys)
                {
                    frozenTimePlayers.Add(playerId);
                }
            }

            // Fast path: no players need frozen time → run the original method unchanged
            if (frozenTimePlayers.Count == 0)
                return true;

            if (!Game1.netWorldState.Dirty)
                return false; // Nothing to broadcast

            // Step 1: Serialize the normal delta (real time) for normal players
            byte[] normalDelta = __instance.writeObjectDeltaBytes(Game1.netWorldState);
            // After this call, all dirty flags are cleared (MarkClean was called by NetRoot.Write)

            // Step 2: Queue normal delta to normal players
            foreach (var kvp in Game1.otherFarmers)
            {
                if (kvp.Value != Game1.player && !frozenTimePlayers.Contains(kvp.Key))
                {
                    kvp.Value.queueMessage(Multiplayer.worldDelta, Game1.player, normalDelta);
                }
            }

            // Step 3: Override timeOfDay and IsPaused for lobby players, then re-serialize
            int realTime = Game1.timeOfDay;
            bool realPaused = Game1.netWorldState.Value.IsPaused;

            try
            {
                Game1.timeOfDay = FrozenEditingTime;
                Game1.netWorldState.Value.IsPaused = false; // Lobby players should never be paused
                Game1.netWorldState.Value.UpdateFromGame1(); // Pushes frozen time → marks timeOfDay dirty

                byte[] frozenDelta = __instance.writeObjectDeltaBytes(Game1.netWorldState);
                // After this call, dirty flags are cleared again

                // Step 4: Queue frozen delta to lobby players (editors + unauthenticated)
                foreach (var kvp in Game1.otherFarmers)
                {
                    if (kvp.Value != Game1.player && frozenTimePlayers.Contains(kvp.Key))
                    {
                        kvp.Value.queueMessage(Multiplayer.worldDelta, Game1.player, frozenDelta);
                    }
                }
            }
            finally
            {
                // Step 5: Restore real time and pause state so the server continues normally.
                // CRITICAL: This must happen even if an exception occurs above, otherwise
                // the server would be stuck at frozen time (1200) permanently.
                Game1.timeOfDay = realTime;
                Game1.netWorldState.Value.IsPaused = realPaused;
                Game1.netWorldState.Value.UpdateFromGame1(); // Pushes real time back → marks timeOfDay dirty
                // timeOfDay and isPaused are now dirty again, which is correct: next tick will re-broadcast
            }

            return false; // Skip the original method
        }

        #endregion

        #region Lifecycle

        private void OnSaveLoaded(object sender, SaveLoadedEventArgs e)
        {
            _monitor.Log($"[Lobby] OnSaveLoaded triggered, IsEnabled={IsEnabled}, ServerPassword={(string.IsNullOrEmpty(Env.ServerPassword) ? "(empty)" : "(set)")}", LogLevel.Info);

            LoadData();

            // Clean up orphaned individual lobby cabins from previous sessions (e.g., server crash)
            CleanupOrphanedIndividualLobbies();

            // Recover any interrupted editing sessions (e.g., server crash during layout editing)
            RecoverInterruptedEditingSessions();

            if (!IsEnabled)
            {
                _monitor.Log("[Lobby] Password protection disabled, skipping lobby setup", LogLevel.Debug);
                return;
            }

            EnsureDefaultLayout();
            SyncActiveLayoutFromSettings();
            // Lobby cabins are created lazily on first player join via GetOrCreateLobbyForPlayer

            _monitor.Log($"[Lobby] Initialized (mode={Mode}, activeLayout={_data.ActiveLayoutName})", LogLevel.Info);
        }

        /// <summary>
        /// Recovers interrupted editing sessions after server restart.
        /// Restores player inventories from persisted backups.
        /// </summary>
        private void RecoverInterruptedEditingSessions()
        {
            if (_data.EditingSessionBackups == null || _data.EditingSessionBackups.Count == 0)
                return;

            _monitor.Log($"[Lobby] Found {_data.EditingSessionBackups.Count} interrupted editing session(s) to recover", LogLevel.Info);

            foreach (var kvp in _data.EditingSessionBackups.ToList())
            {
                var backup = kvp.Value;
                _monitor.Log($"[Lobby] Recovering editing session for player {backup.PlayerId} (layout: {backup.LayoutName})", LogLevel.Info);

                // Schedule inventory restoration when the player reconnects
                // For now, we'll restore immediately if they're online, otherwise keep the backup
                // The backup will be applied when they next connect and are found in Game1.otherFarmers
                if (TryRestoreInventoryFromBackup(backup))
                {
                    _data.EditingSessionBackups.Remove(kvp.Key);
                    _monitor.Log($"[Lobby] Restored inventory for player {backup.PlayerId}", LogLevel.Info);
                }
                else
                {
                    _monitor.Log($"[Lobby] Player {backup.PlayerId} not online - inventory backup retained for reconnection", LogLevel.Info);
                }

                // Clean up any unsaved new layouts
                if (backup.IsNewLayout && _data.Layouts.TryGetValue(backup.LayoutName, out var layout))
                {
                    if (layout.Furniture.Count == 0 && layout.Objects.Count == 0)
                    {
                        _data.Layouts.Remove(backup.LayoutName);
                        _monitor.Log($"[Lobby] Removed unsaved new layout '{backup.LayoutName}'", LogLevel.Debug);
                    }
                }
            }

            SaveData();
        }

        /// <summary>
        /// Attempts to restore a player's inventory from a persisted backup.
        /// </summary>
        /// <returns>True if restored successfully, false if player not found.</returns>
        private bool TryRestoreInventoryFromBackup(EditingSessionBackup backup)
        {
            Farmer player = null;
            if (Game1.player?.UniqueMultiplayerID == backup.PlayerId)
                player = Game1.player;
            else if (Game1.otherFarmers.TryGetValue(backup.PlayerId, out var otherFarmer))
                player = otherFarmer;

            if (player == null)
                return false;

            // Clear current inventory
            player.Items.Clear();

            // Restore items from backup
            foreach (var serialized in backup.InventoryBackup)
            {
                if (serialized?.ItemId == null)
                {
                    player.Items.Add(null);
                    continue;
                }

                try
                {
                    var item = ItemRegistry.Create(serialized.ItemId, serialized.Stack, serialized.Quality);
                    player.Items.Add(item);
                }
                catch (Exception ex)
                {
                    _monitor.Log($"[Lobby] Failed to restore item {serialized.ItemId}: {ex.Message}", LogLevel.Warn);
                    player.Items.Add(null);
                }
            }

            // Warp player back to their previous location
            if (!string.IsNullOrEmpty(backup.PreviousLocation))
            {
                Game1.server.sendMessage(backup.PlayerId, Multiplayer.passout, Game1.player, new object[]
                {
                    backup.PreviousLocation, backup.PreviousX, backup.PreviousY, false
                });
            }

            return true;
        }

        /// <summary>
        /// Cleans up orphaned individual lobby cabins that may have persisted from a previous session
        /// (e.g., server crash while players were in individual lobbies).
        /// Individual lobbies are placed at X positions -22, -23, -24, etc. (offset from shared lobby at -21).
        /// </summary>
        private void CleanupOrphanedIndividualLobbies()
        {
            var farm = Game1.getFarm();
            if (farm == null) return;

            var orphanedLobbies = farm.buildings
                .Where(b => b.isCabin &&
                           b.tileY.Value == HiddenLobbyLocation.Y &&
                           b.tileX.Value < HiddenLobbyLocation.X) // Individual lobbies are at X < -21
                .ToList();

            if (orphanedLobbies.Count == 0) return;

            foreach (var cabin in orphanedLobbies)
            {
                // Delete any farmhand that might have been assigned
                var indoors = cabin.GetIndoors<Cabin>();
                if (indoors?.HasOwner == true)
                {
                    indoors.DeleteFarmhand();
                }

                farm.buildings.Remove(cabin);
            }

            _monitor.Log($"[Lobby] Cleaned up {orphanedLobbies.Count} orphaned individual lobby cabin(s) from previous session", LogLevel.Info);
        }

        /// <summary>
        /// Syncs the active layout from server-settings.json on startup.
        /// If the configured layout doesn't exist, falls back to "default".
        /// </summary>
        private void SyncActiveLayoutFromSettings()
        {
            var configuredLayout = _settings.ActiveLobbyLayout;

            if (_data.Layouts.ContainsKey(configuredLayout))
            {
                if (_data.ActiveLayoutName != configuredLayout)
                {
                    _monitor.Log($"[Lobby] Setting active layout from config: '{configuredLayout}'", LogLevel.Debug);
                    _data.ActiveLayoutName = configuredLayout;
                    SaveData();
                }
            }
            else if (configuredLayout != "default")
            {
                _monitor.Log($"[Lobby] Configured layout '{configuredLayout}' not found, using 'default'", LogLevel.Warn);
            }
        }

        private void OnSaving(object sender, SavingEventArgs e)
        {
            SaveData();
        }

        /// <summary>
        /// Maintains editor immunity during layout editing:
        /// - Full stamina/health (only if depleted, to avoid overwriting legitimate states)
        /// - Excludes editors from sleep/ready_for_save/wakeup ready-checks
        /// - Time isolation via BroadcastWorldStateDeltas_Prefix (sends frozen time to editors)
        ///
        /// Immunity only applies while editor is inside their editing cabin.
        /// If they leave the cabin, immunity is suspended (normal passout can occur).
        /// </summary>
        private void OnUpdateTicked(object sender, UpdateTickedEventArgs e)
        {
            if (!_isEditingModeActive || _layoutEditingSessions.IsEmpty)
                return;

            // Update ready-check exclusion periodically (every 60 ticks = 1 second)
            if (e.IsMultipleOf(60))
            {
                UpdateSleepReadyCheckExclusion();
            }

            // Take a snapshot of current editing sessions for safe iteration
            // ConcurrentDictionary's enumerator is safe but we want consistent state
            foreach (var kvp in _layoutEditingSessions.ToArray())
            {
                var playerId = kvp.Key;
                var session = kvp.Value;
                Farmer editor = GetFarmerById(playerId);

                if (editor == null)
                    continue;

                // Only apply immunity if editor is inside their editing cabin
                var editingCabinIndoors = session.Cabin?.GetIndoors<Cabin>();
                bool isInEditingCabin = editor.currentLocation != null &&
                                        editingCabinIndoors != null &&
                                        editor.currentLocation.NameOrUniqueName == editingCabinIndoors.NameOrUniqueName;

                if (isInEditingCabin)
                {
                    // Keep stamina full (only restore if depleted to avoid overwriting legitimate states)
                    if (editor.stamina < editor.MaxStamina)
                        editor.stamina = editor.MaxStamina;

                    // Keep health full (only restore if depleted)
                    if (editor.health < editor.maxHealth)
                        editor.health = editor.maxHealth;

                    // Clear exhaustion flag
                    editor.exhausted.Value = false;
                }
            }
        }

        /// <summary>
        /// Gets a farmer by their unique multiplayer ID.
        /// </summary>
        private Farmer GetFarmerById(long playerId)
        {
            if (Game1.player.UniqueMultiplayerID == playerId)
                return Game1.player;
            if (Game1.otherFarmers.TryGetValue(playerId, out var farmer))
                return farmer;
            return null;
        }

        /// <summary>
        /// Updates the ready-checks to exclude lobby players from day transition.
        /// Players to exclude:
        /// - Layout editors (in _layoutEditingSessions)
        /// - Unauthenticated players (in _unauthenticatedPlayers)
        ///
        /// This allows the day to end while these players remain isolated.
        /// Guards against edge cases: if all players are excluded, reset to require all.
        ///
        /// Modifies all three ready-checks atomically:
        /// - "sleep" - when players go to bed
        /// - "ready_for_save" - before the save process starts
        /// - "wakeup" - after save completes, before new day starts
        ///
        /// All three must be excluded together to allow day transition to complete.
        /// </summary>
        private void UpdateSleepReadyCheckExclusion()
        {
            // Nothing to exclude if no editors and no unauthenticated players
            if (_layoutEditingSessions.Count == 0 && _unauthenticatedPlayers.Count == 0)
                return;

            try
            {
                // Build set of player IDs to exclude (editors + unauthenticated)
                var excludedPlayerIds = new HashSet<long>(_layoutEditingSessions.Keys);
                foreach (var playerId in _unauthenticatedPlayers.Keys)
                {
                    excludedPlayerIds.Add(playerId);
                }

                // Get all REMOTE online farmers (exclude the server host entirely — it's automated
                // by AlwaysOn and should never be counted for ready-check exclusion logic).
                var remoteFarmers = Game1.otherFarmers.Values.ToList();
                var requiredFarmers = remoteFarmers
                    .Where(f => !excludedPlayerIds.Contains(f.UniqueMultiplayerID))
                    .ToList();

                // Add the server host to required farmers — it must always be included
                // for HandleAutoSleep's "required - ready == 1" logic to work correctly.
                requiredFarmers.Add(Game1.player);

                // Set required farmers for all three day-transition ready-checks.
                // If all remote players are excluded, only the host is required.
                Game1.netReady.SetLocalRequiredFarmers("sleep", requiredFarmers);
                Game1.netReady.SetLocalRequiredFarmers("ready_for_save", requiredFarmers);
                Game1.netReady.SetLocalRequiredFarmers("wakeup", requiredFarmers);

                _monitor.Log($"[Lobby] Ready-check: {requiredFarmers.Count} required, {excludedPlayerIds.Count} excluded", LogLevel.Trace);
            }
            catch (Exception ex)
            {
                // This is an operational failure - lobby players may not be excluded from day transition
                _monitor.Log($"[Lobby] Failed to update sleep ready-check exclusion: {ex.Message}", LogLevel.Warn);
            }
        }

        /// <summary>
        /// Clears the ready-check exclusion, requiring all players again.
        /// Uses an empty list to signal "all players required" (per ServerReadyCheck logic).
        ///
        /// Clears all three ready-checks atomically to restore normal day transition behavior.
        /// </summary>
        private void ClearSleepReadyCheckExclusion()
        {
            try
            {
                // Empty list means "all players required" per ServerReadyCheck.IsFarmerRequired():
                // if (RequiredFarmers.Count == 0) return true;
                var emptyList = new List<Farmer>();
                Game1.netReady.SetLocalRequiredFarmers("sleep", emptyList);
                Game1.netReady.SetLocalRequiredFarmers("ready_for_save", emptyList);
                Game1.netReady.SetLocalRequiredFarmers("wakeup", emptyList);
            }
            catch (Exception ex)
            {
                // This is an operational failure - day transition may be blocked
                _monitor.Log($"[Lobby] Failed to clear sleep ready-check exclusion: {ex.Message}", LogLevel.Warn);
            }
        }

        /// <summary>
        /// Time flows normally during editing - no freeze.
        /// </summary>
        private void OnTimeChanged(object sender, TimeChangedEventArgs e)
        {
            if (!_isEditingModeActive || _layoutEditingSessions.Count == 0)
                return;

            // Just log for debugging - editors are decoupled from time
            _monitor.Log($"[Lobby] Time is {Game1.timeOfDay} (editors decoupled)", LogLevel.Trace);
        }

        /// <summary>
        /// Handles new day starting while editors are still active.
        /// Re-applies editor immunity and daylight after day transition.
        /// </summary>
        private void OnDayStarted(object sender, DayStartedEventArgs e)
        {
            if (!_isEditingModeActive || _layoutEditingSessions.Count == 0)
                return;

            _monitor.Log("[Lobby] New day started - editors still active, re-applying protections", LogLevel.Info);

            // Re-apply daylight to all editing cabins (lighting may reset on day change)
            foreach (var kvp in _layoutEditingSessions)
            {
                var cabinIndoors = kvp.Value.Cabin?.GetIndoors<Cabin>();
                if (cabinIndoors != null)
                {
                    SetCabinDaylightMode(cabinIndoors, true);
                }
            }

            // Re-apply ready-check exclusion for the new day
            UpdateSleepReadyCheckExclusion();
        }

        /// <summary>
        /// Handles new player connection:
        /// - Updates ready-check exclusion if editors are active
        /// - Checks for pending inventory restoration from interrupted editing sessions
        /// </summary>
        private void OnPeerConnected(object sender, PeerConnectedEventArgs e)
        {
            long connectedPlayerId = e.Peer.PlayerID;

            // Check if this player has a pending inventory backup from an interrupted editing session
            if (_data.EditingSessionBackups.TryGetValue(connectedPlayerId, out var backup))
            {
                _monitor.Log($"[Lobby] Player {connectedPlayerId} reconnected with pending editing session backup", LogLevel.Info);

                // Delay restoration slightly to ensure player is fully connected
                // Use DelayedAction or schedule for next tick
                _helper.Events.GameLoop.UpdateTicked += RestoreOnNextTick;

                void RestoreOnNextTick(object s, UpdateTickedEventArgs args)
                {
                    _helper.Events.GameLoop.UpdateTicked -= RestoreOnNextTick;

                    if (TryRestoreInventoryFromBackup(backup))
                    {
                        _data.EditingSessionBackups.Remove(connectedPlayerId);
                        SaveData();
                        _monitor.Log($"[Lobby] Restored inventory for reconnected player {connectedPlayerId}", LogLevel.Info);
                    }
                }
            }

            // Update ready-check exclusion if editors are active
            if (_isEditingModeActive && _layoutEditingSessions.Count > 0)
            {
                UpdateSleepReadyCheckExclusion();
            }
        }

        /// <summary>
        /// Cleans up editing session when a player disconnects.
        /// Removes them from editing tracking and updates ready-check exclusion.
        /// Thread-safe: uses lock for atomic multi-dictionary operations.
        /// </summary>
        private void OnPeerDisconnected(object sender, PeerDisconnectedEventArgs e)
        {
            long disconnectedPlayerId = e.Peer.PlayerID;

            // Use lock for atomic cleanup across multiple dictionaries
            lock (_stateLock)
            {
                // Try to remove from editing sessions atomically
                if (_layoutEditingSessions.TryRemove(disconnectedPlayerId, out var session))
                {
                    _monitor.Log($"[Lobby] Editor disconnected (ID: {disconnectedPlayerId}), cleaning up session", LogLevel.Info);

                    // Restore cabin lighting if it exists
                    var cabinIndoors = session.Cabin?.GetIndoors<Cabin>();
                    if (cabinIndoors != null)
                    {
                        SetCabinDaylightMode(cabinIndoors, false);
                    }

                    // Note: Persisted inventory backup (EditingSessionBackups) remains for reconnect recovery

                    // Clean up previous location tracking
                    _previousLocations.TryRemove(disconnectedPlayerId, out _);

                    // Clean up the editing cabin
                    CleanupEditingCabin(session.Cabin);

                    // Update ready-check exclusion
                    if (!_layoutEditingSessions.IsEmpty)
                    {
                        UpdateSleepReadyCheckExclusion();
                    }
                    else
                    {
                        // No more editors - disable editing mode entirely
                        DisableEditingMode();
                    }
                }
            }
        }

        /// <summary>
        /// Enables layout editing mode - editor becomes fully decoupled from game time/sleep.
        /// </summary>
        private void EnableEditingMode()
        {
            if (_isEditingModeActive)
                return;

            _isEditingModeActive = true;

            // Set up ready-check exclusion immediately
            UpdateSleepReadyCheckExclusion();

            _monitor.Log("[Lobby] Editing mode enabled - editor fully decoupled from time/sleep", LogLevel.Info);
        }

        /// <summary>
        /// Disables layout editing mode - clears editor immunity and ready-check exclusion.
        /// </summary>
        private void DisableEditingMode()
        {
            if (!_isEditingModeActive)
                return;

            _isEditingModeActive = false;

            // Clear ready-check exclusion (all players required again)
            ClearSleepReadyCheckExclusion();

            _monitor.Log("[Lobby] Editing mode disabled", LogLevel.Info);
        }

        /// <summary>
        /// Backs up a player's inventory and clears it to provide free space for editing.
        /// Also persists the backup to save data for crash recovery.
        /// </summary>
        private void BackupAndClearInventory(long playerId, string layoutName, bool isNewLayout)
        {
            Farmer player = null;
            if (Game1.player.UniqueMultiplayerID == playerId)
                player = Game1.player;
            else if (Game1.otherFarmers.TryGetValue(playerId, out var otherFarmer))
                player = otherFarmer;

            if (player == null)
            {
                _monitor.Log($"[Lobby] Cannot backup inventory - player {playerId} not found", LogLevel.Warn);
                return;
            }

            // Serialize all items for persistence
            var serializedBackup = new List<SerializedItem>();
            int itemCount = 0;

            for (int i = 0; i < player.Items.Count; i++)
            {
                var item = player.Items[i];
                if (item != null)
                {
                    serializedBackup.Add(new SerializedItem
                    {
                        ItemId = item.QualifiedItemId,
                        Stack = item.Stack,
                        Quality = (item as StardewValley.Object)?.Quality ?? 0
                    });
                    itemCount++;
                }
                else
                {
                    serializedBackup.Add(null);
                }
            }

            // Persist backup for crash recovery (single source of truth)
            var prevLoc = _previousLocations.TryGetValue(playerId, out var loc) ? loc : (null, 0, 0);
            _data.EditingSessionBackups[playerId] = new EditingSessionBackup
            {
                PlayerId = playerId,
                LayoutName = layoutName,
                IsNewLayout = isNewLayout,
                InventoryBackup = serializedBackup,
                PreviousLocation = prevLoc.Location,
                PreviousX = prevLoc.X,
                PreviousY = prevLoc.Y
            };
            SaveData();

            // Clear the inventory
            player.Items.Clear();
            for (int i = 0; i < player.MaxItems; i++)
            {
                player.Items.Add(null);
            }

            _monitor.Log($"[Lobby] Backed up {itemCount} items from player {playerId} (persisted for crash recovery)", LogLevel.Debug);
        }

        /// <summary>
        /// Restores a player's inventory from persisted backup.
        /// Uses EditingSessionBackups as the single source of truth.
        /// </summary>
        private void RestoreInventory(long playerId)
        {
            if (!_data.EditingSessionBackups.TryGetValue(playerId, out var backup))
            {
                _monitor.Log($"[Lobby] No inventory backup found for player {playerId}", LogLevel.Debug);
                return;
            }

            Farmer player = null;
            if (Game1.player.UniqueMultiplayerID == playerId)
                player = Game1.player;
            else if (Game1.otherFarmers.TryGetValue(playerId, out var otherFarmer))
                player = otherFarmer;

            if (player == null)
            {
                _monitor.Log($"[Lobby] Cannot restore inventory - player {playerId} not found (they may have disconnected)", LogLevel.Warn);
                // Keep the backup - player might reconnect later
                return;
            }

            // Clear current inventory first
            player.Items.Clear();

            // Restore items from serialized backup
            int restoredCount = 0;
            foreach (var serialized in backup.InventoryBackup)
            {
                if (serialized?.ItemId == null)
                {
                    player.Items.Add(null);
                    continue;
                }

                try
                {
                    var item = ItemRegistry.Create(serialized.ItemId, serialized.Stack, serialized.Quality);
                    player.Items.Add(item);
                    restoredCount++;
                }
                catch (Exception ex)
                {
                    _monitor.Log($"[Lobby] Failed to restore item {serialized.ItemId}: {ex.Message}", LogLevel.Warn);
                    player.Items.Add(null);
                }
            }

            // Clear persisted backup since we've successfully restored
            _data.EditingSessionBackups.Remove(playerId);
            SaveData();

            _monitor.Log($"[Lobby] Restored {restoredCount} items to player {playerId}", LogLevel.Debug);
        }

        /// <summary>
        /// Saves a player's current location for later restoration.
        /// </summary>
        private void SavePlayerLocation(long playerId)
        {
            Farmer player = null;
            if (Game1.player.UniqueMultiplayerID == playerId)
                player = Game1.player;
            else if (Game1.otherFarmers.TryGetValue(playerId, out var otherFarmer))
                player = otherFarmer;

            if (player?.currentLocation != null)
            {
                var location = (
                    player.currentLocation.NameOrUniqueName,
                    (int)(player.Position.X / 64f),
                    (int)(player.Position.Y / 64f)
                );
                _previousLocations[playerId] = location;
                _monitor.Log($"[Lobby] Saved location for player {playerId}: {location}", LogLevel.Debug);
            }
        }

        /// <summary>
        /// Teleports a player back to their previous location.
        /// </summary>
        public void TeleportToPreviousLocation(long playerId)
        {
            if (!_previousLocations.TryRemove(playerId, out var prevLoc))
            {
                _monitor.Log($"[Lobby] No previous location saved for player {playerId}", LogLevel.Debug);
                return;
            }

            // Send warp message to the player
            Game1.server.sendMessage(playerId, Multiplayer.passout, Game1.player, new object[]
            {
                prevLoc.Location, prevLoc.X, prevLoc.Y, false
            });

            _monitor.Log($"[Lobby] Teleported player {playerId} back to {prevLoc.Location} ({prevLoc.X}, {prevLoc.Y})", LogLevel.Debug);
        }

        private void LoadData()
        {
            _data = _helper.Data.ReadSaveData<LobbyData>(LobbyDataKey) ?? new LobbyData();
        }

        private void SaveData()
        {
            _helper.Data.WriteSaveData(LobbyDataKey, _data);
        }

        #endregion

        #region Lobby Setup

        private void EnsureDefaultLayout()
        {
            if (!_data.Layouts.ContainsKey("default"))
            {
                // Try to import the pre-decorated default layout
                var (success, message) = ImportLayout("default", DefaultLayoutExportString);
                if (success)
                {
                    _monitor.Log("[Lobby] Imported pre-decorated default layout", LogLevel.Debug);
                }
                else
                {
                    // Fallback to empty cabin if import fails
                    _monitor.Log($"[Lobby] Failed to import default layout ({message}), using empty cabin", LogLevel.Warn);
                    _data.Layouts["default"] = new LobbyLayout
                    {
                        Name = "default",
                        CabinSkin = "Log Cabin",
                        UpgradeLevel = 0
                    };
                    SaveData();
                }
            }
        }

        private Building FindOrCreateLobbyCabin(string identifier)
        {
            var farm = Game1.getFarm();
            _monitor.Log($"[Lobby] FindOrCreateLobbyCabin called, identifier={identifier}, farm buildings count={farm?.buildings?.Count ?? 0}", LogLevel.Info);

            // Look for existing lobby cabin by checking if it's at hidden location
            // and has our special naming convention
            var existing = farm.buildings.FirstOrDefault(b =>
                b.isCabin &&
                b.tileX.Value == HiddenLobbyLocation.X &&
                b.tileY.Value == HiddenLobbyLocation.Y);

            if (existing != null)
            {
                _monitor.Log($"[Lobby] Found existing lobby cabin at ({existing.tileX.Value}, {existing.tileY.Value})", LogLevel.Info);

                // Ensure existing lobby cabin is properly configured
                var cabinIndoors = existing.GetIndoors<Cabin>();
                CleanupLobbyCabinInterior(cabinIndoors);

                // Apply layout (includes door blocking furniture) - needed on server restart
                ApplyActiveLayout(existing);

                return existing;
            }

            _monitor.Log($"[Lobby] No existing lobby cabin found, creating new one", LogLevel.Info);
            // Create new lobby cabin
            return CreateLobbyCabin(farm, HiddenLobbyLocation);
        }

        private Building CreateLobbyCabin(GameLocation location, Point position)
        {
            var cabin = new Building("Cabin", position.ToVector2());
            cabin.skinId.Value = "Log Cabin";
            cabin.magical.Value = true;
            cabin.daysOfConstructionLeft.Value = 0;
            cabin.load();

            if (location.buildStructure(cabin, position.ToVector2(), Game1.player, true))
            {
                _monitor.Log($"[Lobby] Created lobby cabin at ({position.X}, {position.Y})", LogLevel.Info);

                // Clean up the lobby cabin - remove farmhand, warps, and starter gift box
                var cabinIndoors = cabin.GetIndoors<Cabin>();
                CleanupLobbyCabinInterior(cabinIndoors);

                // Apply layout (includes door blocking furniture)
                ApplyActiveLayout(cabin);

                return cabin;
            }

            _monitor.Log($"[Lobby] Failed to create lobby cabin at ({position.X}, {position.Y})", LogLevel.Error);
            return null;
        }

        /// <summary>
        /// Cleans up a lobby cabin interior - removes farmhand, warps, starter gift box, and blocks door.
        /// </summary>
        private void CleanupLobbyCabinInterior(Cabin cabin)
        {
            _monitor.Log($"[Lobby] CleanupLobbyCabinInterior called, cabin={cabin?.NameOrUniqueName ?? "null"}", LogLevel.Info);

            if (cabin == null) return;

            // Delete any farmhand that might have been auto-created
            // This prevents the welcome package (starter tools) from being assigned
            if (cabin.HasOwner)
            {
                cabin.DeleteFarmhand();
                _monitor.Log($"[Lobby] Deleted auto-created farmhand from lobby cabin", LogLevel.Debug);
            }

            // Remove warps to Farm - players shouldn't be able to leave without authenticating
            var warpsToRemove = cabin.warps.Where(w => w.TargetName == "Farm").ToList();
            foreach (var warp in warpsToRemove)
            {
                cabin.warps.Remove(warp);
            }
            if (warpsToRemove.Count > 0)
            {
                _monitor.Log($"[Lobby] Removed {warpsToRemove.Count} warps from lobby cabin", LogLevel.Debug);
            }

            // Remove the starter gift box (the small box with parsnip seeds)
            // It's a Chest object with giftboxIsStarterGift = true
            var giftBoxesToRemove = cabin.objects.Pairs
                .Where(kvp => kvp.Value is Chest chest && chest.giftboxIsStarterGift.Value)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var position in giftBoxesToRemove)
            {
                cabin.objects.Remove(position);
            }
            if (giftBoxesToRemove.Count > 0)
            {
                _monitor.Log($"[Lobby] Removed {giftBoxesToRemove.Count} starter gift box(es) from lobby cabin", LogLevel.Debug);
            }

            // Note: Door blocker is placed by ApplyActiveLayout AFTER the layout is applied
            // (because DeserializeToCabin clears all objects first)
        }

        /// <summary>
        /// Places a "no entry" indicator at the door in the lobby cabin.
        /// Uses an invalid item ID which renders as the error sprite (red circle with diagonal line).
        /// </summary>
        private void PlaceDoorBlocker(Cabin cabin)
        {
            var tile = DoorBlockerTileLevel0;

            // Check if there's already an object at this position
            if (cabin.objects.ContainsKey(tile))
            {
                _monitor.Log($"[Lobby] Door blocker already exists at {tile}", LogLevel.Debug);
                return;
            }

            try
            {
                // Create an object with an invalid ID - this renders as the error sprite
                var blocker = new StardewValley.Object(tile, DoorBlockerInvalidItemId);
                cabin.objects.Add(tile, blocker);
                _monitor.Log($"[Lobby] Placed door blocker at {tile}", LogLevel.Debug);
            }
            catch (Exception ex)
            {
                _monitor.Log($"[Lobby] Failed to place door blocker at {tile}: {ex.Message}", LogLevel.Warn);
            }
        }

        #endregion

        #region Cabin Daylight Mode

        /// <summary>
        /// Sets a cabin's lighting to permanent daylight or restores original lighting.
        /// Uses reflection to access protected indoorLightingColor fields.
        /// Caches original colors before modification, restores them on disable.
        /// </summary>
        private void SetCabinDaylightMode(Cabin cabin, bool enabled)
        {
            if (cabin == null)
                return;

            try
            {
                var indoorLightingColorField = typeof(GameLocation).GetField(
                    "indoorLightingColor",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                var indoorLightingNightColorField = typeof(GameLocation).GetField(
                    "indoorLightingNightColor",
                    BindingFlags.NonPublic | BindingFlags.Instance);

                if (indoorLightingColorField == null || indoorLightingNightColorField == null)
                {
                    _monitor.Log("[Lobby] Could not find lighting color fields via reflection", LogLevel.Warn);
                    return;
                }

                string cabinKey = cabin.NameOrUniqueName;

                if (enabled)
                {
                    // Cache original colors before overriding (use TryAdd for thread-safety)
                    var originalDay = (Color)indoorLightingColorField.GetValue(cabin);
                    var originalNight = (Color)indoorLightingNightColorField.GetValue(cabin);
                    if (_originalCabinLighting.TryAdd(cabinKey, (originalDay, originalNight)))
                    {
                        _monitor.Log($"[Lobby] Cached original lighting for {cabinKey}", LogLevel.Debug);
                    }

                    // Override to white for permanent daylight
                    indoorLightingColorField.SetValue(cabin, Color.White);
                    indoorLightingNightColorField.SetValue(cabin, Color.White);
                    _monitor.Log($"[Lobby] Set cabin to daylight mode", LogLevel.Debug);
                }
                else
                {
                    // Restore cached original colors (or fall back to defaults)
                    Color dayColor = new Color(100, 120, 30);
                    Color nightColor = new Color(150, 150, 30);

                    if (_originalCabinLighting.TryRemove(cabinKey, out var cached))
                    {
                        dayColor = cached.day;
                        nightColor = cached.night;
                        _monitor.Log($"[Lobby] Restoring cached lighting for {cabinKey}", LogLevel.Debug);
                    }

                    indoorLightingColorField.SetValue(cabin, dayColor);
                    indoorLightingNightColorField.SetValue(cabin, nightColor);
                    _monitor.Log($"[Lobby] Restored cabin to normal lighting", LogLevel.Debug);
                }
            }
            catch (Exception ex)
            {
                _monitor.Log($"[Lobby] Failed to set cabin lighting: {ex.Message}", LogLevel.Warn);
            }
        }

        #endregion

        #region Unauthenticated Player Tracking

        /// <summary>
        /// Registers a player as unauthenticated (in the lobby awaiting password).
        /// This excludes them from sleep ready-checks so they don't block day transitions.
        /// Called by PasswordProtectionService when a player joins and requires auth.
        /// </summary>
        public void RegisterUnauthenticatedPlayer(long playerId)
        {
            _unauthenticatedPlayers[playerId] = true;
            _monitor.Log($"[Lobby] Registered unauthenticated player {playerId}", LogLevel.Debug);

            // Update ready-check exclusion immediately
            UpdateSleepReadyCheckExclusion();
        }

        /// <summary>
        /// Unregisters a player as unauthenticated (they've authenticated or disconnected).
        /// Called by PasswordProtectionService when a player authenticates successfully.
        /// </summary>
        public void UnregisterUnauthenticatedPlayer(long playerId)
        {
            if (_unauthenticatedPlayers.TryRemove(playerId, out _))
            {
                _monitor.Log($"[Lobby] Unregistered unauthenticated player {playerId}", LogLevel.Debug);

                // Update ready-check exclusion immediately
                if (_unauthenticatedPlayers.IsEmpty && _layoutEditingSessions.IsEmpty)
                {
                    ClearSleepReadyCheckExclusion();
                }
                else
                {
                    UpdateSleepReadyCheckExclusion();
                }
            }
        }

        /// <summary>
        /// Gets all players currently awaiting authentication.
        /// </summary>
        public IReadOnlyCollection<long> GetUnauthenticatedPlayerIds()
        {
            return _unauthenticatedPlayers.Keys.ToList();
        }

        /// <summary>
        /// Checks if a player is currently unauthenticated (in the lobby).
        /// </summary>
        public bool IsPlayerUnauthenticated(long playerId)
        {
            return _unauthenticatedPlayers.ContainsKey(playerId);
        }

        #endregion

        #region Player Lobby Management

        /// <summary>
        /// Gets or creates a lobby cabin for a player based on the current mode.
        /// Lobby cabins are created lazily on first access.
        /// Thread-safe: uses lock to prevent race conditions when multiple players join simultaneously.
        /// </summary>
        public Building GetOrCreateLobbyForPlayer(long playerId)
        {
            // Lock to prevent race conditions:
            // - Shared mode: prevents double-creation of shared lobby
            // - Individual mode: prevents two players getting the same offset position
            lock (_stateLock)
            {
                if (Mode == LobbyMode.Shared)
                {
                    // Lazy initialization of shared lobby
                    if (_sharedLobbyCabin == null)
                    {
                        _sharedLobbyCabin = FindOrCreateLobbyCabin("Lobby_Shared");
                        _monitor.Log($"[Lobby] Shared lobby cabin created lazily at {HiddenLobbyLocation}", LogLevel.Debug);
                    }
                    return _sharedLobbyCabin;
                }

                // Individual mode
                if (_individualLobbies.TryGetValue(playerId, out var existing))
                {
                    return existing;
                }

                // Create individual lobby at a unique offset position.
                // Position overlap is safe since these cabins are at hidden off-map coordinates (X < -20)
                // and players only interact with the interior, not the building exterior.
                // This is the same approach used for cabin stacking.
                var offset = _individualLobbies.Count + 1;
                var position = new Point(HiddenLobbyLocation.X - offset, HiddenLobbyLocation.Y);

                var cabin = CreateLobbyCabin(Game1.getFarm(), position);
                if (cabin != null)
                {
                    ApplyActiveLayout(cabin);
                    _individualLobbies[playerId] = cabin;
                }

                return cabin;
            }
        }

        /// <summary>
        /// Gets the interior location name for a player's lobby.
        /// </summary>
        public string GetLobbyLocationName(long playerId)
        {
            var cabin = GetOrCreateLobbyForPlayer(playerId);
            return cabin?.GetIndoors<Cabin>()?.NameOrUniqueName;
        }

        /// <summary>
        /// Gets the entry point for the lobby cabin.
        /// Uses custom spawn point from active layout if set, otherwise default entry.
        /// </summary>
        public Point GetLobbyEntryPoint(long playerId)
        {
            // Check if active layout has a custom spawn point
            var layout = GetActiveLayout();
            if (layout?.SpawnX.HasValue == true && layout?.SpawnY.HasValue == true)
            {
                return new Point(layout.SpawnX.Value, layout.SpawnY.Value);
            }

            // Fall back to default cabin entry
            var cabin = GetOrCreateLobbyForPlayer(playerId);
            var indoors = cabin?.GetIndoors<Cabin>();
            return indoors?.getEntryLocation() ?? new Point(3, 11);
        }

        /// <summary>
        /// Warps a player to their lobby cabin.
        /// </summary>
        public void WarpToLobby(long playerId)
        {
            var locationName = GetLobbyLocationName(playerId);
            var entry = GetLobbyEntryPoint(playerId);

            if (string.IsNullOrEmpty(locationName))
            {
                _monitor.Log($"[Lobby] Cannot warp player {playerId} - no lobby location", LogLevel.Error);
                return;
            }

            _monitor.Log($"[Lobby] Warping player {playerId} to lobby {locationName} at ({entry.X}, {entry.Y})", LogLevel.Debug);

            Game1.server.sendMessage(playerId, Multiplayer.passout, Game1.player, new object[]
            {
                locationName, entry.X, entry.Y, false
            });
        }

        /// <summary>
        /// Warps a player from the lobby to their destination.
        /// </summary>
        public void WarpFromLobby(long playerId, string targetLocation, int tileX, int tileY)
        {
            _monitor.Log($"[Lobby] Warping player {playerId} from lobby to {targetLocation} at ({tileX}, {tileY})", LogLevel.Info);

            Game1.server.sendMessage(playerId, Multiplayer.passout, Game1.player, new object[]
            {
                targetLocation, tileX, tileY, false
            });

            // Clean up individual lobby if applicable
            CleanupIndividualLobby(playerId);
        }

        /// <summary>
        /// Cleans up an individual lobby cabin when a player authenticates or disconnects.
        /// Also handles cleanup of layout editing sessions.
        /// Thread-safe: uses lock for atomic multi-dictionary operations.
        /// </summary>
        public void CleanupIndividualLobby(long playerId)
        {
            // Use lock to ensure atomic cleanup across multiple dictionaries
            lock (_stateLock)
            {
                // Clean up any active editing session for this player
                if (_layoutEditingSessions.TryRemove(playerId, out var session))
                {
                    _monitor.Log($"[Lobby] Cleaned up editing session for player {playerId}", LogLevel.Debug);

                    // Restore cabin lighting before cleanup
                    var cabinIndoors = session.Cabin?.GetIndoors<Cabin>();
                    if (cabinIndoors != null)
                    {
                        SetCabinDaylightMode(cabinIndoors, false);
                    }

                    // Clean up previous location tracking
                    _previousLocations.TryRemove(playerId, out _);

                    // Remove the temporary editing cabin
                    CleanupEditingCabin(session.Cabin);

                    // Restore the player's inventory
                    RestoreInventory(playerId);

                    // If this was a new layout that was never saved, remove the empty layout entry
                    if (session.IsNew && _data.Layouts.TryGetValue(session.LayoutName, out var layout))
                    {
                        // Only remove if it's still empty (no furniture saved)
                        if (layout.Furniture.Count == 0 && layout.Objects.Count == 0)
                        {
                            _data.Layouts.Remove(session.LayoutName);
                            SaveData();
                            _monitor.Log($"[Lobby] Removed unsaved new layout '{session.LayoutName}'", LogLevel.Debug);
                        }
                    }

                    // Disable editing mode if no more active sessions
                    if (_layoutEditingSessions.IsEmpty)
                    {
                        DisableEditingMode();
                    }
                    else
                    {
                        // Update ready-check exclusion for remaining editors
                        UpdateSleepReadyCheckExclusion();
                    }
                }

                if (Mode != LobbyMode.Individual)
                    return;

                if (_individualLobbies.TryRemove(playerId, out var cabin))
                {
                    // Remove the cabin from the farm
                    var farm = Game1.getFarm();
                    farm.buildings.Remove(cabin);

                    _monitor.Log($"[Lobby] Cleaned up individual lobby for player {playerId}", LogLevel.Debug);
                }
            }
        }

        /// <summary>
        /// Removes a temporary editing cabin from the farm.
        /// </summary>
        private void CleanupEditingCabin(Building cabin)
        {
            if (cabin == null)
                return;

            var farm = Game1.getFarm();
            if (farm.buildings.Contains(cabin))
            {
                farm.buildings.Remove(cabin);
                _monitor.Log($"[Lobby] Removed temporary editing cabin", LogLevel.Debug);
            }
        }

        /// <summary>
        /// Checks if a location is a lobby cabin.
        /// </summary>
        public bool IsLobbyLocation(string locationName)
        {
            if (_sharedLobbyCabin != null)
            {
                var sharedName = _sharedLobbyCabin.GetIndoors<Cabin>()?.NameOrUniqueName;
                if (locationName == sharedName)
                    return true;
            }

            foreach (var kvp in _individualLobbies)
            {
                var name = kvp.Value.GetIndoors<Cabin>()?.NameOrUniqueName;
                if (locationName == name)
                    return true;
            }

            return false;
        }

        #endregion

        #region Layout Management

        /// <summary>
        /// Gets the active layout.
        /// </summary>
        public LobbyLayout GetActiveLayout()
        {
            if (_data.Layouts.TryGetValue(_data.ActiveLayoutName, out var layout))
                return layout;

            return _data.Layouts.GetValueOrDefault("default");
        }

        /// <summary>
        /// Gets all layout names.
        /// </summary>
        public IEnumerable<string> GetLayoutNames()
        {
            return _data.Layouts.Keys;
        }

        /// <summary>
        /// Sets the active layout by name.
        /// </summary>
        public bool SetActiveLayout(string name)
        {
            if (!_data.Layouts.ContainsKey(name))
                return false;

            _data.ActiveLayoutName = name;
            SaveData();

            // Apply to existing shared lobby
            if (_sharedLobbyCabin != null)
            {
                ApplyActiveLayout(_sharedLobbyCabin);
            }

            _monitor.Log($"[Lobby] Active layout set to '{name}'", LogLevel.Info);
            return true;
        }

        /// <summary>
        /// Creates a new layout and returns the cabin for editing.
        /// Warps the admin into the cabin to decorate it.
        /// </summary>
        public Building CreateLayoutForEditing(string name, long adminPlayerId)
        {
            if (_data.Layouts.ContainsKey(name))
            {
                _monitor.Log($"[Lobby] Layout '{name}' already exists", LogLevel.Warn);
                return null;
            }

            // Create a temporary cabin for editing at a unique position
            var editPosition = new Point(HiddenLobbyLocation.X - 100, HiddenLobbyLocation.Y);
            var cabin = CreateLobbyCabin(Game1.getFarm(), editPosition);

            if (cabin == null)
                return null;

            // Save player's current location for teleport back after saving
            SavePlayerLocation(adminPlayerId);

            // Track the editing session (isNew=true) and enable editing mode
            _layoutEditingSessions[adminPlayerId] = (name, true, cabin);
            EnableEditingMode();

            // Force daylight in the editing cabin
            var cabinIndoors = cabin.GetIndoors<Cabin>();
            if (cabinIndoors != null)
            {
                SetCabinDaylightMode(cabinIndoors, true);
            }

            // Create the layout entry first (before backup, so backup has layout name)
            _data.Layouts[name] = new LobbyLayout
            {
                Name = name,
                CabinSkin = "Log Cabin",
                UpgradeLevel = 0
            };

            // Backup and clear the admin's inventory for free space (persisted for crash recovery)
            BackupAndClearInventory(adminPlayerId, name, isNewLayout: true);

            _monitor.Log($"[Lobby] Created layout '{name}' for editing by admin {adminPlayerId}", LogLevel.Info);
            return cabin;
        }

        /// <summary>
        /// Opens an existing layout for editing.
        /// Creates a temporary cabin with the layout applied for the admin to modify.
        /// </summary>
        public Building EditLayoutForEditing(string name, long adminPlayerId)
        {
            if (!_data.Layouts.TryGetValue(name, out var existingLayout))
            {
                _monitor.Log($"[Lobby] Layout '{name}' does not exist", LogLevel.Warn);
                return null;
            }

            // Create a temporary cabin for editing at a unique position
            var editPosition = new Point(HiddenLobbyLocation.X - 100, HiddenLobbyLocation.Y);
            var cabin = CreateLobbyCabin(Game1.getFarm(), editPosition);

            if (cabin == null)
                return null;

            // Apply the existing layout to the editing cabin (without door blockers)
            var cabinIndoors = cabin.GetIndoors<Cabin>();
            if (cabinIndoors != null)
            {
                DeserializeToCabin(cabinIndoors, existingLayout);
            }

            // Save player's current location for teleport back after saving
            SavePlayerLocation(adminPlayerId);

            // Track the editing session (isNew=false) and enable editing mode
            _layoutEditingSessions[adminPlayerId] = (name, false, cabin);
            EnableEditingMode();

            // Force daylight in the editing cabin
            if (cabinIndoors != null)
            {
                SetCabinDaylightMode(cabinIndoors, true);
            }

            // Backup and clear the admin's inventory for free space (persisted for crash recovery)
            BackupAndClearInventory(adminPlayerId, name, isNewLayout: false);

            _monitor.Log($"[Lobby] Opened layout '{name}' for editing by admin {adminPlayerId}", LogLevel.Info);
            return cabin;
        }

        /// <summary>
        /// Gets a safe entry point inside a cabin (center of main room, away from furniture).
        /// </summary>
        public Point GetSafeEntryPoint(Cabin cabin)
        {
            // Default safe position for upgrade level 0 cabin is center of main room
            // The cabin is roughly 11x13 tiles, with entry at (3, 11)
            // Safe center area is around (5, 6)
            var safePoint = new Point(5, 6);

            // Verify the tile is passable
            if (cabin.isTilePlaceable(new Vector2(safePoint.X, safePoint.Y)))
                return safePoint;

            // Try alternative positions in the center area
            var alternatives = new[] {
                new Point(4, 6), new Point(6, 6),
                new Point(5, 5), new Point(5, 7),
                new Point(4, 5), new Point(6, 5),
                new Point(3, 6), new Point(7, 6)
            };

            foreach (var alt in alternatives)
            {
                if (cabin.isTilePlaceable(new Vector2(alt.X, alt.Y)))
                    return alt;
            }

            // Fallback to default entry
            return cabin.getEntryLocation();
        }

        /// <summary>
        /// Saves the current state of a cabin being edited as a layout.
        /// </summary>
        public bool SaveCurrentLayout(long adminPlayerId)
        {
            if (!_layoutEditingSessions.TryGetValue(adminPlayerId, out var session))
            {
                _monitor.Log($"[Lobby] Admin {adminPlayerId} has no active editing session", LogLevel.Warn);
                return false;
            }

            // Find the admin's current location (should be in the editing cabin)
            if (!Game1.otherFarmers.TryGetValue(adminPlayerId, out var admin))
            {
                // Check if it's the host
                if (Game1.player.UniqueMultiplayerID == adminPlayerId)
                {
                    admin = Game1.player;
                }
                else
                {
                    _monitor.Log($"[Lobby] Cannot find admin {adminPlayerId}", LogLevel.Error);
                    return false;
                }
            }

            var currentLocation = admin.currentLocation;
            if (currentLocation is not Cabin cabin)
            {
                _monitor.Log($"[Lobby] Admin {adminPlayerId} is not in a cabin", LogLevel.Warn);
                return false;
            }

            // Serialize the cabin state
            var layout = SerializeCabin(cabin);
            layout.Name = session.LayoutName;

            // Preserve spawn point from the layout data (set via SetLayoutSpawnPoint during editing)
            if (_data.Layouts.TryGetValue(session.LayoutName, out var existingLayout))
            {
                layout.SpawnX = existingLayout.SpawnX;
                layout.SpawnY = existingLayout.SpawnY;
            }

            _data.Layouts[session.LayoutName] = layout;
            SaveData();

            // Restore normal lighting
            var editingCabinIndoors = session.Cabin?.GetIndoors<Cabin>();
            if (editingCabinIndoors != null)
            {
                SetCabinDaylightMode(editingCabinIndoors, false);
            }

            // End editing session and clean up the temporary cabin
            _layoutEditingSessions.TryRemove(adminPlayerId, out _);
            CleanupEditingCabin(session.Cabin);

            // Restore the admin's inventory
            RestoreInventory(adminPlayerId);

            // Teleport admin back to their previous location
            TeleportToPreviousLocation(adminPlayerId);

            // If no more editors, disable editing mode
            if (_layoutEditingSessions.IsEmpty)
            {
                DisableEditingMode();
            }
            else
            {
                // Update ready-check exclusion for remaining editors
                UpdateSleepReadyCheckExclusion();
            }

            // Re-apply layout to shared lobby if this is the active layout
            if (_sharedLobbyCabin != null && _data.ActiveLayoutName == session.LayoutName)
            {
                ApplyActiveLayout(_sharedLobbyCabin);
                _monitor.Log($"[Lobby] Re-applied updated layout to shared lobby", LogLevel.Debug);
            }

            _monitor.Log($"[Lobby] Saved layout '{session.LayoutName}' (furniture={layout.Furniture.Count}, objects={layout.Objects.Count})", LogLevel.Info);
            return true;
        }

        /// <summary>
        /// Deletes a layout by name.
        /// </summary>
        public bool DeleteLayout(string name)
        {
            if (name == "default")
            {
                _monitor.Log("[Lobby] Cannot delete the default layout", LogLevel.Warn);
                return false;
            }

            if (name == _data.ActiveLayoutName)
            {
                _monitor.Log("[Lobby] Cannot delete the active layout", LogLevel.Warn);
                return false;
            }

            // Check if anyone is currently editing this layout
            if (_layoutEditingSessions.Values.Any(s => s.LayoutName == name))
            {
                _monitor.Log($"[Lobby] Cannot delete '{name}' - it is currently being edited", LogLevel.Warn);
                return false;
            }

            if (_data.Layouts.Remove(name))
            {
                SaveData();
                _monitor.Log($"[Lobby] Deleted layout '{name}'", LogLevel.Info);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Renames a layout.
        /// </summary>
        public bool RenameLayout(string oldName, string newName)
        {
            if (oldName == "default")
            {
                _monitor.Log("[Lobby] Cannot rename the default layout", LogLevel.Warn);
                return false;
            }

            if (!_data.Layouts.TryGetValue(oldName, out var layout))
            {
                _monitor.Log($"[Lobby] Layout '{oldName}' not found", LogLevel.Warn);
                return false;
            }

            if (_data.Layouts.ContainsKey(newName))
            {
                _monitor.Log($"[Lobby] Layout '{newName}' already exists", LogLevel.Warn);
                return false;
            }

            // Check if anyone is currently editing this layout
            if (_layoutEditingSessions.Values.Any(s => s.LayoutName == oldName))
            {
                _monitor.Log($"[Lobby] Cannot rename '{oldName}' - it is currently being edited", LogLevel.Warn);
                return false;
            }

            // Update the layout
            layout.Name = newName;
            _data.Layouts.Remove(oldName);
            _data.Layouts[newName] = layout;

            // Update active layout reference if needed
            if (_data.ActiveLayoutName == oldName)
            {
                _data.ActiveLayoutName = newName;
            }

            SaveData();
            _monitor.Log($"[Lobby] Renamed layout '{oldName}' to '{newName}'", LogLevel.Info);
            return true;
        }

        /// <summary>
        /// Creates a copy of an existing layout with a new name.
        /// </summary>
        public bool CopyLayout(string sourceName, string destName)
        {
            if (!_data.Layouts.TryGetValue(sourceName, out var sourceLayout))
            {
                _monitor.Log($"[Lobby] Source layout '{sourceName}' not found", LogLevel.Warn);
                return false;
            }

            if (_data.Layouts.ContainsKey(destName))
            {
                _monitor.Log($"[Lobby] Destination layout '{destName}' already exists", LogLevel.Warn);
                return false;
            }

            // Deep copy by serializing and deserializing
            var json = JsonConvert.SerializeObject(sourceLayout);
            var copy = JsonConvert.DeserializeObject<LobbyLayout>(json);
            copy.Name = destName;

            _data.Layouts[destName] = copy;
            SaveData();

            _monitor.Log($"[Lobby] Copied layout '{sourceName}' to '{destName}'", LogLevel.Info);
            return true;
        }

        /// <summary>
        /// Gets detailed information about a layout.
        /// </summary>
        public LobbyLayout GetLayout(string name)
        {
            return _data.Layouts.GetValueOrDefault(name);
        }

        /// <summary>
        /// Checks if admin is in an editing session.
        /// </summary>
        public bool IsEditingLayout(long adminPlayerId)
        {
            return _layoutEditingSessions.ContainsKey(adminPlayerId);
        }

        /// <summary>
        /// Checks if a layout (by name) is currently being edited by anyone.
        /// </summary>
        public bool IsLayoutBeingEdited(string layoutName)
        {
            return _layoutEditingSessions.Values.Any(s => s.LayoutName == layoutName);
        }

        /// <summary>
        /// Gets the layout name being edited by an admin.
        /// </summary>
        public string GetEditingLayoutName(long adminPlayerId)
        {
            return _layoutEditingSessions.TryGetValue(adminPlayerId, out var session) ? session.LayoutName : null;
        }

        /// <summary>
        /// Checks if the admin is editing a new (unsaved) layout.
        /// </summary>
        public bool IsEditingNewLayout(long adminPlayerId)
        {
            return _layoutEditingSessions.TryGetValue(adminPlayerId, out var session) && session.IsNew;
        }

        /// <summary>
        /// Sets the spawn point for the layout being edited to the admin's current position.
        /// </summary>
        public bool SetLayoutSpawnPoint(long adminPlayerId)
        {
            if (!_layoutEditingSessions.TryGetValue(adminPlayerId, out var session))
            {
                _monitor.Log($"[Lobby] Admin {adminPlayerId} has no active editing session", LogLevel.Warn);
                return false;
            }

            Farmer admin = null;
            if (Game1.player.UniqueMultiplayerID == adminPlayerId)
                admin = Game1.player;
            else if (Game1.otherFarmers.TryGetValue(adminPlayerId, out var otherFarmer))
                admin = otherFarmer;

            if (admin == null)
            {
                _monitor.Log($"[Lobby] Cannot find admin {adminPlayerId}", LogLevel.Error);
                return false;
            }

            // Get the admin's tile position
            var tileX = (int)(admin.Position.X / 64f);
            var tileY = (int)(admin.Position.Y / 64f);

            // Update the layout's spawn point
            if (_data.Layouts.TryGetValue(session.LayoutName, out var layout))
            {
                layout.SpawnX = tileX;
                layout.SpawnY = tileY;
                SaveData();

                _monitor.Log($"[Lobby] Set spawn point for '{session.LayoutName}' to ({tileX}, {tileY})", LogLevel.Info);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Resets the cabin being edited - clears all furniture, objects, and resets wallpaper/flooring.
        /// </summary>
        public bool ResetEditingCabin(long adminPlayerId)
        {
            if (!_layoutEditingSessions.TryGetValue(adminPlayerId, out var session))
            {
                _monitor.Log($"[Lobby] Admin {adminPlayerId} has no active editing session", LogLevel.Warn);
                return false;
            }

            Farmer admin = null;
            if (Game1.player.UniqueMultiplayerID == adminPlayerId)
                admin = Game1.player;
            else if (Game1.otherFarmers.TryGetValue(adminPlayerId, out var otherFarmer))
                admin = otherFarmer;

            if (admin?.currentLocation is not Cabin cabin)
            {
                _monitor.Log($"[Lobby] Admin {adminPlayerId} is not in a cabin", LogLevel.Warn);
                return false;
            }

            // Clear everything
            cabin.furniture.Clear();
            cabin.objects.Clear();

            // Reset to default wallpaper/flooring (blank)
            foreach (var key in cabin.appliedWallpaper.Keys.ToList())
            {
                cabin.SetWallpaper("0", key);
            }
            foreach (var key in cabin.appliedFloor.Keys.ToList())
            {
                cabin.SetFloor("0", key);
            }

            _monitor.Log($"[Lobby] Reset editing cabin for layout '{session.LayoutName}'", LogLevel.Info);
            return true;
        }

        /// <summary>
        /// Cancels the current editing session without saving changes.
        /// </summary>
        public bool CancelEditing(long adminPlayerId)
        {
            if (!_layoutEditingSessions.TryGetValue(adminPlayerId, out var session))
            {
                _monitor.Log($"[Lobby] Admin {adminPlayerId} has no active editing session", LogLevel.Warn);
                return false;
            }

            // Restore normal lighting
            var editingCabinIndoors = session.Cabin?.GetIndoors<Cabin>();
            if (editingCabinIndoors != null)
            {
                SetCabinDaylightMode(editingCabinIndoors, false);
            }

            // End editing session and clean up the temporary cabin
            _layoutEditingSessions.TryRemove(adminPlayerId, out _);
            CleanupEditingCabin(session.Cabin);

            // If this was a new layout, remove the empty layout entry
            if (session.IsNew)
            {
                _data.Layouts.Remove(session.LayoutName);
                SaveData();
                _monitor.Log($"[Lobby] Removed cancelled new layout '{session.LayoutName}'", LogLevel.Debug);
            }

            // Restore the admin's inventory
            RestoreInventory(adminPlayerId);

            // Teleport admin back to their previous location
            TeleportToPreviousLocation(adminPlayerId);

            // If no more editors, disable editing mode
            if (_layoutEditingSessions.IsEmpty)
            {
                DisableEditingMode();
            }
            else
            {
                // Update ready-check exclusion for remaining editors
                UpdateSleepReadyCheckExclusion();
            }

            _monitor.Log($"[Lobby] Cancelled editing session for layout '{session.LayoutName}'", LogLevel.Info);
            return true;
        }

        /// <summary>
        /// Exports a layout to a shareable base64 string (like Factorio blueprints).
        /// Format: "SDVL0" prefix + gzip-compressed JSON in base64.
        /// </summary>
        public string ExportLayout(string name)
        {
            if (!_data.Layouts.TryGetValue(name, out var layout))
            {
                _monitor.Log($"[Lobby] Cannot export - layout '{name}' not found", LogLevel.Warn);
                return null;
            }

            try
            {
                // Serialize to JSON
                var json = JsonConvert.SerializeObject(layout, Formatting.None);
                var jsonBytes = Encoding.UTF8.GetBytes(json);

                // Compress with gzip
                using var outputStream = new MemoryStream();
                using (var gzipStream = new GZipStream(outputStream, CompressionLevel.Optimal))
                {
                    gzipStream.Write(jsonBytes, 0, jsonBytes.Length);
                }

                // Convert to base64 with prefix
                var base64 = Convert.ToBase64String(outputStream.ToArray());
                var exportString = LayoutExportPrefix + LayoutExportVersion + base64;

                _monitor.Log($"[Lobby] Exported layout '{name}' ({exportString.Length} chars)", LogLevel.Info);
                return exportString;
            }
            catch (Exception ex)
            {
                _monitor.Log($"[Lobby] Failed to export layout '{name}': {ex.Message}", LogLevel.Error);
                return null;
            }
        }

        /// <summary>
        /// Imports a layout from a base64 string and saves it with the given name.
        /// </summary>
        public (bool Success, string Message) ImportLayout(string name, string exportString)
        {
            if (string.IsNullOrWhiteSpace(exportString))
            {
                return (false, "Import string is empty");
            }

            if (_data.Layouts.ContainsKey(name))
            {
                return (false, $"Layout '{name}' already exists");
            }

            // Check prefix and version
            if (!exportString.StartsWith(LayoutExportPrefix))
            {
                return (false, "Invalid format - not a lobby layout string");
            }

            var prefixLength = LayoutExportPrefix.Length;
            var version = exportString.Length > prefixLength ? exportString[prefixLength] : '?';
            if (version != LayoutExportVersion)
            {
                return (false, $"Unsupported layout version: {version}");
            }

            try
            {
                // Remove prefix (including version char) and decode base64
                var base64 = exportString.Substring(prefixLength + 1);
                var compressedBytes = Convert.FromBase64String(base64);

                // Decompress gzip with size limit to prevent zip bomb attacks
                // A typical lobby layout with 50 furniture items is ~5KB, so limit is very generous
                using var inputStream = new MemoryStream(compressedBytes);
                using var gzipStream = new GZipStream(inputStream, CompressionMode.Decompress);
                using var outputStream = new MemoryStream();

                var buffer = new byte[4096];
                int totalRead = 0;
                int read;
                while ((read = gzipStream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    totalRead += read;
                    if (totalRead > MaxLayoutDecompressedSize)
                    {
                        return (false, $"Layout data too large (max {MaxLayoutDecompressedSize / 1024}KB decompressed)");
                    }
                    outputStream.Write(buffer, 0, read);
                }

                var json = Encoding.UTF8.GetString(outputStream.ToArray());

                // Deserialize
                var layout = JsonConvert.DeserializeObject<LobbyLayout>(json);
                if (layout == null)
                {
                    return (false, "Failed to parse layout data");
                }

                // Override the name with the provided one
                layout.Name = name;

                // Save the layout
                _data.Layouts[name] = layout;
                SaveData();

                _monitor.Log($"[Lobby] Imported layout '{name}' (furniture={layout.Furniture.Count}, objects={layout.Objects.Count})", LogLevel.Info);
                return (true, $"Imported layout '{name}' with {layout.Furniture.Count} furniture and {layout.Objects.Count} objects");
            }
            catch (FormatException)
            {
                return (false, "Invalid base64 encoding");
            }
            catch (InvalidDataException)
            {
                return (false, "Invalid compressed data");
            }
            catch (JsonException ex)
            {
                return (false, $"Invalid JSON: {ex.Message}");
            }
            catch (Exception ex)
            {
                _monitor.Log($"[Lobby] Failed to import layout: {ex}", LogLevel.Error);
                return (false, $"Import failed: {ex.Message}");
            }
        }

        #endregion

        #region Serialization

        /// <summary>
        /// Applies the active layout to a cabin.
        /// </summary>
        private void ApplyActiveLayout(Building cabinBuilding)
        {
            var layout = GetActiveLayout();
            if (layout == null)
                return;

            var cabin = cabinBuilding.GetIndoors<Cabin>();
            if (cabin == null)
                return;

            DeserializeToCabin(cabin, layout);

            // Place door blocker AFTER layout is applied (layout clears objects)
            PlaceDoorBlocker(cabin);
        }

        /// <summary>
        /// Serializes a cabin's furniture and objects into a LobbyLayout.
        /// </summary>
        public LobbyLayout SerializeCabin(Cabin cabin)
        {
            var layout = new LobbyLayout
            {
                CabinSkin = cabin.ParentBuilding?.skinId.Value ?? "Log Cabin",
                UpgradeLevel = cabin.upgradeLevel
            };

            // Serialize furniture
            foreach (var furniture in cabin.furniture)
            {
                var serialized = new SerializedFurniture
                {
                    ItemId = furniture.QualifiedItemId,
                    TileX = (int)furniture.TileLocation.X,
                    TileY = (int)furniture.TileLocation.Y,
                    Rotation = furniture.currentRotation.Value
                };

                // Check for held object (e.g., items on tables)
                if (furniture.heldObject.Value != null)
                {
                    serialized.HeldObjectId = furniture.heldObject.Value.QualifiedItemId;
                }

                layout.Furniture.Add(serialized);
            }

            // Serialize placed objects
            foreach (var kvp in cabin.objects.Pairs)
            {
                var obj = kvp.Value;
                layout.Objects.Add(new SerializedObject
                {
                    ItemId = obj.QualifiedItemId,
                    TileX = (int)kvp.Key.X,
                    TileY = (int)kvp.Key.Y
                });
            }

            // Serialize wallpaper per room
            foreach (var kvp in cabin.appliedWallpaper.Pairs)
            {
                layout.Wallpapers[kvp.Key] = kvp.Value;
            }

            // Serialize flooring per room
            foreach (var kvp in cabin.appliedFloor.Pairs)
            {
                layout.Floors[kvp.Key] = kvp.Value;
            }

            return layout;
        }

        /// <summary>
        /// Deserializes a LobbyLayout into a cabin, restoring furniture and objects.
        /// </summary>
        public void DeserializeToCabin(Cabin cabin, LobbyLayout layout)
        {
            // Clear existing furniture and objects
            cabin.furniture.Clear();
            cabin.objects.Clear();

            // Restore furniture
            foreach (var serialized in layout.Furniture)
            {
                try
                {
                    var furniture = ItemRegistry.Create<Furniture>(serialized.ItemId);
                    if (furniture != null)
                    {
                        furniture.TileLocation = new Vector2(serialized.TileX, serialized.TileY);
                        furniture.currentRotation.Value = serialized.Rotation;
                        furniture.updateRotation();

                        // Restore held object if any
                        if (!string.IsNullOrEmpty(serialized.HeldObjectId))
                        {
                            var heldItem = ItemRegistry.Create(serialized.HeldObjectId);
                            if (heldItem is StardewValley.Object heldObj)
                            {
                                furniture.heldObject.Value = heldObj;
                            }
                        }

                        cabin.furniture.Add(furniture);
                    }
                }
                catch (Exception ex)
                {
                    _monitor.Log($"[Lobby] Failed to restore furniture {serialized.ItemId}: {ex.Message}", LogLevel.Warn);
                }
            }

            // Restore objects
            foreach (var serialized in layout.Objects)
            {
                try
                {
                    var obj = ItemRegistry.Create<StardewValley.Object>(serialized.ItemId);
                    if (obj != null)
                    {
                        var position = new Vector2(serialized.TileX, serialized.TileY);
                        cabin.objects.Add(position, obj);
                    }
                }
                catch (Exception ex)
                {
                    _monitor.Log($"[Lobby] Failed to restore object {serialized.ItemId}: {ex.Message}", LogLevel.Warn);
                }
            }

            // Restore wallpaper per room
            foreach (var kvp in layout.Wallpapers)
            {
                cabin.SetWallpaper(kvp.Value, kvp.Key);
            }

            // Restore flooring per room
            foreach (var kvp in layout.Floors)
            {
                cabin.SetFloor(kvp.Value, kvp.Key);
            }

            _monitor.Log($"[Lobby] Applied layout (furniture={layout.Furniture.Count}, objects={layout.Objects.Count}, wallpapers={layout.Wallpapers.Count}, floors={layout.Floors.Count})", LogLevel.Debug);
        }

        #endregion
    }
}
