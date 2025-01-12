using HarmonyLib;
using JunimoServer.Util;
using Netcode;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Network;
using System.Collections.Generic;

namespace JunimoServer.Services.CabinManager
{
    public class SecurityService : ModService
    {
        public SecurityService(IModHelper helper, IMonitor monitor, Harmony harmony) : base(helper, monitor)
        {
            // Note: As of now (SDV1.6.15-24356) GameServer.broadcastUserName does not appear to be used does not need to be handled

            harmony.Patch(
                original: AccessTools.Method(typeof(GameServer), nameof(GameServer.sendServerIntroduction)),
                prefix: new HarmonyMethod(typeof(SecurityService), nameof(sendServerIntroduction_Prefix))
            );

            harmony.Patch(
                original: AccessTools.Method(typeof(Multiplayer), nameof(Multiplayer.broadcastPlayerIntroduction)),
                prefix: new HarmonyMethod(typeof(SecurityService), nameof(broadcastPlayerIntroduction_Prefix))
            );
        }

        /// <summary>
        /// Send other clients introductions to the joined client.
        /// </summary>
        public static bool sendServerIntroduction_Prefix(long peer)
        {
            SendServerIntroductionMessage(peer);

            foreach (KeyValuePair<long, NetRoot<Farmer>> root in Game1.otherFarmers.Roots)
            {
                var isNotHost = root.Key != Game1.player.UniqueMultiplayerID;
                var isNotSelf = root.Key != peer;

                if (isNotHost && isNotSelf)
                {
                    SendPlayerIntroductionMessage(root.Value, peer);
                }
            }

            return false;
        }

        /// <summary>
        /// Send joined client introduction to all other clients.
        /// </summary>
        public static bool broadcastPlayerIntroduction_Prefix(NetFarmerRoot farmerRoot)
        {
            foreach (KeyValuePair<long, Farmer> otherFarmer in Game1.otherFarmers)
            {
                var isNotSelf = farmerRoot.Value.UniqueMultiplayerID != otherFarmer.Value.UniqueMultiplayerID;

                if (isNotSelf)
                {
                    SendPlayerIntroductionMessage(farmerRoot, otherFarmer.Value.UniqueMultiplayerID);
                }
            }

            return false;
        }

        private static void SendServerIntroductionMessage(long peerId)
        {
            Game1.server.sendMessage(
                peerId,
                NetworkHelper.CreateMessageServerIntroduction(peerId)
            );
        }

        private static void SendPlayerIntroductionMessage(NetRoot<Farmer> farmerSource, long peerId)
        {
            // Use UUID instead of IP
            // Original: Game1.server.getUserName(farmerRoot.Value.UniqueMultiplayerID)
            var parenthesisText = farmerSource.Value.UniqueMultiplayerID.ToString();

            Game1.server.sendMessage(
                peerId,
                NetworkHelper.CreateMessagePlayerIntroduction(farmerSource, peerId, parenthesisText)
            );
        }
    }
}
