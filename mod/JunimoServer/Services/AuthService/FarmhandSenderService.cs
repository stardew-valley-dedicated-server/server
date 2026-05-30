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
using JunimoServer.Services.CabinManager;
using JunimoServer.Services.Lobby;
using JunimoServer.Util;
using Microsoft.Xna.Framework;

namespace JunimoServer.Services.Auth
{
    /// <summary>
    /// Harmony prefix replacing GameServer.sendAvailableFarmhands to add lobby redirect
    /// and farmhand slot management (reservations, filtering, single-slot limiting).
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
        private static CabinManagerService _cabinManager;

        // Cache the serializer globally
        private static readonly object SerializerLock = new object();
        private static XmlSerializer FarmerSerializer;

        private static List<string> pendingAvailableFarmhands = new();

        /// <summary>
        /// Tracks which uncustomized farmhand IDs have been sent to pending (not-yet-joined) clients.
        /// Prevents concurrent clients from receiving the same uncustomized slot, eliminating
        /// rejection round-trips. Key = connectionId, Value = (reserved farmhand IDs, timestamp).
        /// All access is on the game thread, so no locks are needed.
        /// </summary>
        private static readonly Dictionary<string, (HashSet<long> FarmhandIds, DateTime ReservedAt)> _reservedFarmhands = new();

        /// <summary>
        /// Reservations older than this are pruned. The full connect -> select -> join flow
        /// takes ~10-15s. 30s provides ample margin for slow connections.
        /// </summary>
        private static readonly TimeSpan ReservationExpiry = TimeSpan.FromSeconds(30);

        public FarmhandSenderService(IMonitor monitor, IModHelper helper, Harmony harmony, LobbyService lobbyService, CabinManagerService cabinManager)
        {
            if (_instance != null)
                throw new InvalidOperationException("FarmhandSenderService already initialized - only one instance allowed");

            // Assign instance first to avoid race conditions with Harmony patches
            // that may fire before constructor completes
            _instance = this;
            _monitor = monitor;
            _helper = helper;
            _lobbyService = lobbyService;
            _cabinManager = cabinManager;

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
        /// Full replacement of GameServer.sendAvailableFarmhands. Adds lobby redirect,
        /// farmhand reservation (prevents concurrent clients claiming the same slot),
        /// single unclaimed slot limiting, and lobby cabin filtering.
        /// </summary>
        public static bool SendAvailableFarmhands_Prefix(GameServer __instance, string userId, string connectionId, Action<OutgoingMessage> sendMessage)
        {
            var isGameAvailable = __instance.isGameAvailable;
            var whenGameAvailable = __instance.whenGameAvailable;
            var isConnectionActive = __instance.isConnectionActive;
            var IsFarmhandAvailable = __instance.IsFarmhandAvailable;

            var transport = GetTransportName(connectionId);
            var connectionInfoDump = _monitor.Dump(new { userId, connectionId, transport });

            // Log the incoming connection with transport info
            _monitor.Log($"Client connected via {transport}", LogLevel.Info);

            // Queue the request until the game is ready
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

            // --- Prune stale farmhand reservations ---
            // Remove this connection's old entry (will be re-recorded below with fresh data)
            _reservedFarmhands.Remove(connectionId);

            // Prune disconnected, expired, and completed reservations
            var staleKeys = new List<string>();
            foreach (var kvp in _reservedFarmhands)
            {
                // Client disconnected
                if (!isConnectionActive(kvp.Key))
                {
                    staleKeys.Add(kvp.Key);
                    continue;
                }
                // Reservation expired (client joined or timed out)
                if (DateTime.UtcNow - kvp.Value.ReservedAt > ReservationExpiry)
                {
                    staleKeys.Add(kvp.Key);
                    continue;
                }
                // All reserved farmhands are now active or customized (client completed join)
                var allClaimed = true;
                foreach (var fhId in kvp.Value.FarmhandIds)
                {
                    var fh = Game1.netWorldState.Value.farmhandData.FieldDict.Values
                        .Select(r => r.Value)
                        .FirstOrDefault(f => f.UniqueMultiplayerID == fhId);
                    if (fh != null && !fh.isActive() && !fh.isCustomized.Value)
                    {
                        allClaimed = false;
                        break;
                    }
                }
                if (allClaimed)
                    staleKeys.Add(kvp.Key);
            }
            foreach (var key in staleKeys)
                _reservedFarmhands.Remove(key);

            // Collect all currently reserved farmhand IDs (from OTHER pending clients)
            var reservedIds = new HashSet<long>();
            foreach (var kvp in _reservedFarmhands)
                foreach (var fhId in kvp.Value.FarmhandIds)
                    reservedIds.Add(fhId);

            // Ensure enough cabins exist for pending clients + 1 for this connection
            _cabinManager?.EnsureAtLeastXCabins(reservedIds.Count + 1);

            IEnumerable<NetRef<Farmer>> farmhandRefsAll = Game1.netWorldState.Value.farmhandData.FieldDict.Values;

            List<NetRef<Farmer>> availableFarmers = new List<NetRef<Farmer>>();
            foreach (var farmhandRef in farmhandRefsAll)
            {
                var farmhand = farmhandRef.Value;

                var isOffline = !farmhand.isActive() || Game1.Multiplayer.isDisconnecting(farmhand.UniqueMultiplayerID);
                var isWithCabinAndInventoryUnlocked = IsFarmhandAvailable(farmhand);
                var isSelectable = IsFarmhandSelectableByUserId(farmhand, userId);
                var isLobbyCabin = IsLobbyCabinFarmhand(farmhand);
                var isReservedByOther = !farmhand.isCustomized.Value && reservedIds.Contains(farmhand.UniqueMultiplayerID);
                var isValid = isOffline && isWithCabinAndInventoryUnlocked && isSelectable && !isLobbyCabin && !isReservedByOther;

                if (isValid)
                {
                    availableFarmers.Add(farmhandRef);
                }
            }

            // Limit to exactly 1 unclaimed slot per client; returning players (customized farmhands) are always included.
            // Null-check via .Value because NetRef's implicit conversions are unintuitive (smapi.io/package/avoid-implicit-net-field-cast).
            NetRef<Farmer> firstUnclaimed = null;
            var claimedFarmers = new List<NetRef<Farmer>>();
            foreach (var farmhandRef in availableFarmers)
            {
                if (farmhandRef.Value.isCustomized.Value)
                {
                    claimedFarmers.Add(farmhandRef);
                }
                else if (firstUnclaimed?.Value == null)
                {
                    firstUnclaimed = farmhandRef;
                }
            }
            availableFarmers = claimedFarmers;
            if (firstUnclaimed?.Value != null)
                availableFarmers.Add(firstUnclaimed);

            var unclaimedIds = availableFarmers
                .Where(r => !r.Value.isCustomized.Value)
                .Select(r => r.Value.UniqueMultiplayerID)
                .ToHashSet();

            if (unclaimedIds.Count > 0)
                _reservedFarmhands[connectionId] = (unclaimedIds, DateTime.UtcNow);

            _monitor.Log($"Sending {availableFarmers.Count}/{farmhandRefsAll.Count()} farmhands to {transport} (reserved={reservedIds.Count})", LogLevel.Debug);

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
            public string HomeLocation;
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
                SleptInTemporaryBed = farmhand.sleptInTemporaryBed.Value,
                HomeLocation = farmhand.homeLocation.Value
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
            farmhand.homeLocation.Value = data.HomeLocation;
        }

        /// <summary>
        /// Applies lobby redirect to a farmhand's spawn data before sending to client.
        /// This makes the client spawn in the lobby cabin instead of their regular location.
        /// Note: vanilla checkFarmhandRequest skips sendLocation for lobby cabins because
        /// isAlwaysActiveLocation() returns true for Farm-nested interiors. The explicit
        /// send is handled by PasswordProtectionService's sendServerIntroduction prefix.
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

            var lobbyGameLocation = Game1.getLocationFromName(lobbyLocation);
            if (lobbyGameLocation == null)
            {
                _monitor.Log($"[Auth] Cannot find lobby GameLocation: {lobbyLocation}", LogLevel.Warn);
                return;
            }

            _monitor.Log($"[Auth] Applying lobby redirect to farmhand {farmerId}: {lobbyLocation} @ ({lobbyEntry.X}, {lobbyEntry.Y})", LogLevel.Debug);

            // Set spawn position and location to the lobby cabin
            farmhand.Position = new Vector2(lobbyEntry.X * 64f, lobbyEntry.Y * 64f);
            farmhand.currentLocation = lobbyGameLocation;

            // Both ApplyWakeUpPosition (client-side spawn) and checkFarmhandRequest
            // (server-side location sync) gate on: disconnectDay == DaysPlayed.
            // Setting this to the current day activates that branch, making the game use our
            // lobby as the disconnect location instead of falling through to sleep/home defaults.
            farmhand.disconnectDay.Value = (int)Game1.MasterPlayer.stats.DaysPlayed;
            farmhand.disconnectLocation.Value = lobbyLocation;
            farmhand.disconnectPosition.Value = new Vector2(lobbyEntry.X * 64f, lobbyEntry.Y * 64f);

            // ApplyWakeUpPosition Branch 2 fallback: if disconnectDay doesn't match (e.g. day
            // advanced between send and join), the game falls back to lastSleepLocation.
            farmhand.lastSleepLocation.Value = lobbyLocation;
            farmhand.lastSleepPoint.Value = lobbyEntry;

            // Branch 2 requires CanWakeUpHere() which checks for a bed or AllowWakeUpWithoutBed
            // map property. Setting sleptInTemporaryBed bypasses that bed check.
            farmhand.sleptInTemporaryBed.Value = true;

            // ApplyWakeUpPosition Branch 3 (final fallback): uses homeLocation to find a
            // FarmHouse bed spot. Point it at the lobby cabin so all three branches converge.
            farmhand.homeLocation.Value = lobbyLocation;
        }

        /// <summary>
        /// Farmhands are tied to players via platform userID, but only for Steam/GOG
        /// connections (IP connections bypass it entirely). Steam SDR provides a Steam ID
        /// while farmhand.userID may be a Galaxy ID (different ID spaces), so we show all
        /// owned farmhands and let authCheck() verify during join.
        /// </summary>
        private static bool IsFarmhandSelectableByUserId(Farmer farmhand, string userId)
        {
            // UNCLAIMED: allow if farmhand creation enabled
            if (string.IsNullOrEmpty(farmhand.userID.Value))
                return Game1.options.enableFarmhandCreation;

            // OWNED: show to all clients, authCheck() during join handles verification
            return true;
        }

        private static bool IsLobbyCabinFarmhand(Farmer farmhand)
        {
            var cabin = Game1.getFarm()?.GetCabin(farmhand.UniqueMultiplayerID);
            if (cabin == null) return false;
            return LobbyService.IsLobbyCabin(cabin);
        }
    }
}
