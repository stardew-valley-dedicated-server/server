using System.Buffers;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using StardewValley;
using StardewValley.Network;
using StardewModdingAPI;
using System;
using Netcode;
using System.Xml.Serialization;
using StardewValley.SaveSerialization;
using HarmonyLib;
using JunimoServer.Services.Lobby;
using JunimoServer.Util;
using Microsoft.Xna.Framework;

namespace JunimoServer.Services.Auth
{
    /// <summary>
    ///  The official documentation and community discussions confirm:
    ///     - Each cabin/farmhand is "tied to a specific player" via their platform userID
    ///     - This tie only works with Steam/GOG connections
    ///     - IP connections bypass this system entirely
    /// Note: Farmhands are visible to all users, see description of `/unlinkplayer` command
    /// </summary>
    public class FarmhandSenderService : ModService
    {
        /// <summary>
        /// Static reference for Harmony patches
        /// </summary>
        private static FarmhandSenderService _instance;

        private static IMonitor _monitor;
        private static IModHelper _helper;
        private static LobbyService _lobbyService;

        // Cache the serializer globally
        private static readonly object SerializerLock = new object();
        private static XmlSerializer FarmerSerializer;

        private static List<string> pendingAvailableFarmhands = new();

        public FarmhandSenderService(IMonitor monitor, IModHelper helper, Harmony harmony, LobbyService lobbyService)
        {
            if (_instance != null)
                throw new InvalidOperationException("FarmhandSenderService already initialized - only one instance allowed");

            // Assign instance first to avoid race conditions with Harmony patches
            // that may fire before constructor completes
            _instance = this;
            _monitor = monitor;
            _helper = helper;
            _lobbyService = lobbyService;

            harmony.Patch(
                original: AccessTools.Method(typeof(GameServer), nameof(GameServer.sendAvailableFarmhands)),
                prefix: new HarmonyMethod(typeof(FarmhandSenderService), nameof(SendAvailableFarmhands_Prefix)));
        }

        /// <summary>
        /// Parses the transport type from a connection ID.
        /// Connection ID formats: "GN_..." (Galaxy), "SN_..." (Steam SDR), "L_..." (LAN/Lidgren)
        /// </summary>
        private static string GetTransportName(string connectionId)
        {
            if (string.IsNullOrEmpty(connectionId))
                return "Unknown";

            if (connectionId.StartsWith("GN_"))
                return "Galaxy P2P";
            if (connectionId.StartsWith("SN_"))
                return "Steam SDR";
            if (connectionId.StartsWith("L_"))
                return "LAN";

            return "Unknown";
        }

        /// <summary>
        /// Prevent sending farmhands to users without an userID, i.e. on direct IP connections or or client side tampering.<br/><br/>
        /// Adds additional condition `IsFarmhandSelectableByUserId` for sending available farmhands and more detailed logging.
        /// </summary>
        public static bool SendAvailableFarmhands_Prefix(GameServer __instance, string userId, string connectionId, Action<OutgoingMessage> sendMessage)
        {
            var isGameAvailable = __instance.isGameAvailable;
            var whenGameAvailable = __instance.whenGameAvailable;
            var isConnectionActive = __instance.isConnectionActive;
            var IsFarmhandAvailable = __instance.IsFarmhandAvailable;

            var transport = GetTransportName(connectionId);
            var connectionInfoDump = _monitor.Dump(new { userId, connectionId, transport });

            // Log the incoming connection with transport info prominently
            _monitor.Log($"Client connected via {transport}", LogLevel.Info);

            // When the game is not ready,
            if (!isGameAvailable())
            {
                sendMessage(new OutgoingMessage(11, Game1.player, "Strings\\UI:Client_WaitForHostAvailability"));

                if (pendingAvailableFarmhands.Contains(connectionId))
                {
                    _monitor.Log($"Connection is already waiting for available farmhands\n{connectionInfoDump}", LogLevel.Debug);
                    return false;
                }

                _monitor.Log($"Postponing sending available farmhands\n{connectionInfoDump}", LogLevel.Debug);
                pendingAvailableFarmhands.Add(connectionId);

                whenGameAvailable(() =>
                {
                    pendingAvailableFarmhands.Remove(connectionId);
                    if (isConnectionActive(connectionId))
                    {
                        SendAvailableFarmhands_Prefix(__instance, userId, connectionId, sendMessage);
                    }
                    else
                    {
                        _monitor.Log($"Failed to send available farmhands: Connection not active.\n{connectionInfoDump}", LogLevel.Debug);
                    }
                });

                return false;
            }

            // Ensure serializer is initialized once
            if (FarmerSerializer == null)
            {
                lock (SerializerLock)
                {
                    if (FarmerSerializer == null)
                    {
                        FarmerSerializer = SaveSerializer.GetSerializer(typeof(Farmer));
                    }
                }
            }

            // Filter farmhands efficiently
            IEnumerable<NetRef<Farmer>> farmhandRefsAll = Game1.netWorldState.Value.farmhandData.FieldDict.Values;

            List<NetRef<Farmer>> availableFarmers = new List<NetRef<Farmer>>();
            foreach (var farmhandRef in farmhandRefsAll)
            {
                var farmhand = farmhandRef.Value;

                var isOffline = !farmhand.isActive() || Game1.Multiplayer.isDisconnecting(farmhand.UniqueMultiplayerID);
                var isWithCabinAndInventoryUnlocked = IsFarmhandAvailable(farmhand);
                var isSelectable = IsFarmhandSelectableByUserId(farmhand, userId);
                var isLobbyCabin = IsLobbyCabinFarmhand(farmhand);
                var isValid = isOffline && isWithCabinAndInventoryUnlocked && isSelectable && !isLobbyCabin;

                if (isValid)
                {
                    availableFarmers.Add(farmhandRef);
                }
            }

            _monitor.Log($"Sending {availableFarmers.Count}/{farmhandRefsAll.Count()} farmhands to {transport}", LogLevel.Debug);

            // Prepare outgoing message
            using var memoryStream = new MemoryStream();
            using var writer = new BinaryWriter(memoryStream);
            writer.Write(Game1.year);
            writer.Write(Game1.seasonIndex);
            writer.Write(Game1.dayOfMonth);
            writer.Write((byte)availableFarmers.Count);

            // Serialize farmhands data
            // If lobby redirect is enabled, temporarily modify spawn data and restore after serialization
            var shouldApplyLobbyRedirect = _lobbyService != null && _lobbyService.IsEnabled;

            foreach (var item in availableFarmers)
            {
                var farmhand = item.Value;
                FarmhandSpawnData originalData = null;

                try
                {
                    // Save original spawn data and apply lobby redirect if enabled
                    if (shouldApplyLobbyRedirect)
                    {
                        originalData = SaveFarmhandSpawnData(farmhand);
                        ApplyLobbyRedirectToFarmhand(farmhand);
                    }

                    item.Serializer = FarmerSerializer;
                    item.WriteFull(writer);
                }
                finally
                {
                    item.Serializer = null;

                    // Restore original spawn data to prevent save corruption
                    if (originalData != null)
                    {
                        RestoreFarmhandSpawnData(farmhand, originalData);
                    }
                }
            }

            // Send message
            memoryStream.Seek(0L, SeekOrigin.Begin);
            sendMessage(new OutgoingMessage(9, Game1.player, memoryStream.ToArray()));

            // Skip original method
            return false;
        }

        /// <summary>
        /// Holds original farmhand spawn data for restoration after serialization.
        /// </summary>
        private class FarmhandSpawnData
        {
            public Vector2 Position;
            public GameLocation CurrentLocation;
            public int DisconnectDay;
            public string DisconnectLocation;
            public Vector2 DisconnectPosition;
            public string LastSleepLocation;
            public Point LastSleepPoint;
            public bool SleptInTemporaryBed;
        }

        /// <summary>
        /// Saves farmhand spawn data before modification.
        /// </summary>
        private static FarmhandSpawnData SaveFarmhandSpawnData(Farmer farmhand)
        {
            return new FarmhandSpawnData
            {
                Position = farmhand.Position,
                CurrentLocation = farmhand.currentLocation,
                DisconnectDay = farmhand.disconnectDay.Value,
                DisconnectLocation = farmhand.disconnectLocation.Value,
                DisconnectPosition = farmhand.disconnectPosition.Value,
                LastSleepLocation = farmhand.lastSleepLocation.Value,
                LastSleepPoint = farmhand.lastSleepPoint.Value,
                SleptInTemporaryBed = farmhand.sleptInTemporaryBed.Value
            };
        }

        /// <summary>
        /// Restores farmhand spawn data after serialization.
        /// </summary>
        private static void RestoreFarmhandSpawnData(Farmer farmhand, FarmhandSpawnData data)
        {
            farmhand.Position = data.Position;
            farmhand.currentLocation = data.CurrentLocation;
            farmhand.disconnectDay.Value = data.DisconnectDay;
            farmhand.disconnectLocation.Value = data.DisconnectLocation;
            farmhand.disconnectPosition.Value = data.DisconnectPosition;
            farmhand.lastSleepLocation.Value = data.LastSleepLocation;
            farmhand.lastSleepPoint.Value = data.LastSleepPoint;
            farmhand.sleptInTemporaryBed.Value = data.SleptInTemporaryBed;
        }

        /// <summary>
        /// Applies lobby redirect to a farmhand's spawn data before sending to client.
        /// This makes the client spawn in the lobby cabin instead of their regular location.
        /// </summary>
        private static void ApplyLobbyRedirectToFarmhand(Farmer farmhand)
        {
            var farmerId = farmhand.UniqueMultiplayerID;
            var lobbyLocation = _lobbyService.GetLobbyLocationName(farmerId);
            var lobbyEntry = _lobbyService.GetLobbyEntryPoint(farmerId);

            if (string.IsNullOrEmpty(lobbyLocation))
            {
                _monitor.Log($"[Auth] Cannot apply lobby redirect to {farmerId} - no lobby location", LogLevel.Warn);
                return;
            }

            // Get the actual lobby GameLocation object
            var lobbyGameLocation = Game1.getLocationFromName(lobbyLocation);
            if (lobbyGameLocation == null)
            {
                _monitor.Log($"[Auth] Cannot find lobby GameLocation: {lobbyLocation}", LogLevel.Warn);
                return;
            }

            _monitor.Log($"[Auth] Applying lobby redirect to farmhand {farmerId}: {lobbyLocation} @ ({lobbyEntry.X}, {lobbyEntry.Y})", LogLevel.Debug);

            // Modify spawn position to lobby cabin
            farmhand.Position = new Vector2(lobbyEntry.X * 64f, lobbyEntry.Y * 64f);

            // Set currentLocation to the lobby
            farmhand.currentLocation = lobbyGameLocation;

            // Set disconnect day to current day so the game's spawn logic uses our lobby location.
            // Setting to 0 would skip the disconnectLocation branch entirely, which is incorrect.
            // The game checks: if (disconnectDay == currentDay) -> use disconnectLocation
            farmhand.disconnectDay.Value = (int)Game1.MasterPlayer.stats.DaysPlayed;
            farmhand.disconnectLocation.Value = lobbyLocation;
            farmhand.disconnectPosition.Value = new Vector2(lobbyEntry.X * 64f, lobbyEntry.Y * 64f);

            // Set sleep location to lobby (for ApplyWakeUpPosition fallback)
            farmhand.lastSleepLocation.Value = lobbyLocation;
            farmhand.lastSleepPoint.Value = lobbyEntry;

            // Allow spawning in lobby without a bed
            farmhand.sleptInTemporaryBed.Value = true;
        }

        private static bool IsFarmhandSelectableByUserId(Farmer farmhand, string userId)
        {
            // UNCLAIMED: Allow if farmhand creation enabled
            if (string.IsNullOrEmpty(farmhand.userID.Value))
                return Game1.options.enableFarmhandCreation;

            // OWNED: Show to all clients - authCheck() during join handles actual verification.
            // This matches vanilla behavior where SteamNetServer passes empty userId.
            // Note: Steam SDR connections provide Steam ID, but farmhand.userID may be a Galaxy ID
            // (set by GalaxyNetClient when player joined via invite code). These IDs are from
            // different ID spaces and cannot be compared directly.
            return true;
        }

        /// <summary>
        /// Checks if a farmhand belongs to a lobby cabin (should be hidden from selection).
        /// </summary>
        private static bool IsLobbyCabinFarmhand(Farmer farmhand)
        {
            var cabin = Game1.getFarm()?.GetCabin(farmhand.UniqueMultiplayerID);
            if (cabin == null) return false;
            return LobbyService.IsLobbyCabin(cabin);
        }
    }
}
