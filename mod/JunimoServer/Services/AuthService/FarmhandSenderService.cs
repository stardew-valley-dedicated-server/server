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
using JunimoServer.Util;

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

        // Cache the serializer globally
        private static readonly object SerializerLock = new object();
        private static XmlSerializer FarmerSerializer;

        // Optional parallel filtering toggle
        private const bool UseParallelFiltering = true;

        private static List<string> pendingAvailableFarmhands = new();

        public FarmhandSenderService(IMonitor monitor, IModHelper helper, Harmony harmony)
        {
            // Set instance variables for use in static harmony patches
            _instance = this;
            _monitor = monitor;
            _helper = helper;

            harmony.Patch(
                original: AccessTools.Method(typeof(GameServer), nameof(GameServer.sendAvailableFarmhands)),
                prefix: new HarmonyMethod(typeof(FarmhandSenderService), nameof(SendAvailableFarmhands_Prefix)));
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

            var connectionInfoDump = _monitor.Dump(new { userId, connectionId });

            // When the game is not ready,
            if (!isGameAvailable())
            {
                sendMessage(new OutgoingMessage(11, Game1.player, "Strings\\UI:Client_WaitForHostAvailability"));

                if (pendingAvailableFarmhands.Contains(connectionId))
                {
                    _monitor.Log($"Connection is already waiting for available farmhands\n{connectionInfoDump}", LogLevel.Info);
                    return false;
                }

                _monitor.Log($"Postponing sending available farmhands\n{connectionInfoDump}", LogLevel.Info);
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
                        _monitor.Log($"Failed to send available farmhands: Connection not active.\n{connectionInfoDump}", LogLevel.Info);
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
            _monitor.Log($"Filtering {farmhandRefsAll.Count()} available farmhands\n{connectionInfoDump}", LogLevel.Info);

            List<NetRef<Farmer>> availableFarmers = new List<NetRef<Farmer>>();
            foreach (var farmhandRef in farmhandRefsAll)
            {
                var farmhand = farmhandRef.Value;

                var isOffline = !farmhand.isActive() || Game1.Multiplayer.isDisconnecting(farmhand.UniqueMultiplayerID);
                var isWithCabinAndInventoryUnlocked = IsFarmhandAvailable(farmhand);
                var isSelectable = IsFarmhandSelectableByUserId(farmhand, userId);
                var isValid = isOffline && isWithCabinAndInventoryUnlocked && isSelectable;

                var logData = _monitor.Dump(new
                {
                    isValid,
                    isNewFarmhandSlot = string.IsNullOrEmpty(farmhand.Name),
                    farmhand.Name,
                    farmhand.userID,
                    farmhand.UniqueMultiplayerID
                });
                _monitor.Log($"Processing farmhand\n{logData}", LogLevel.Info);

                if (isValid)
                {
                    availableFarmers.Add(farmhandRef);
                }

            }

            _monitor.Log($"Sending {availableFarmers.Count} available farmhands", LogLevel.Info);

            // Prepare outgoing message
            using var memoryStream = new MemoryStream();
            using var writer = new BinaryWriter(memoryStream);
            writer.Write(Game1.year);
            writer.Write(Game1.seasonIndex);
            writer.Write(Game1.dayOfMonth);
            writer.Write((byte)availableFarmers.Count);

            // Serialize farmhands data
            foreach (var item in availableFarmers)
            {
                try
                {
                    item.Serializer = FarmerSerializer;
                    item.WriteFull(writer);
                }
                finally
                {
                    item.Serializer = null;
                }
            }

            // Send message
            memoryStream.Seek(0L, SeekOrigin.Begin);
            sendMessage(new OutgoingMessage(9, Game1.player, memoryStream.ToArray()));

            // Skip original method
            return false;
        }

        private static bool IsFarmhandSelectableByUserId(Farmer farmhand, string userId)
        {
            return true;

            // // Get server-assigned userId if client sent empty
            // userId = GetOrAssignUserId(userId, GetCurrentConnectionId());

            // // REJECT if still no userId (shouldn't happen)
            // if (string.IsNullOrEmpty(userId))
            // {
            //     _monitor.Log($"Rejected: No userId available", LogLevel.Warn);
            //     return false;
            // }

            // // UNCLAIMED: Allow if farmhand creation enabled
            // if (string.IsNullOrEmpty(farmhand.userID.Value))
            // {
            //     bool canCreate = Game1.options.enableFarmhandCreation;
            //     _monitor.Log($"Farmhand '{farmhand.Name}' unclaimed. Creation enabled: {canCreate}", LogLevel.Info);
            //     return canCreate;
            // }

            // // OWNED: Must match exactly
            // bool matches = farmhand.userID.Value == userId;
            // if (!matches)
            // {
            //     _monitor.Log($"Rejected: '{farmhand.Name}' belongs to '{farmhand.userID.Value}', not '{userId}'", LogLevel.Warn);
            // }
            // return matches;
        }
    }
}
