using JunimoServer.Services.MessageInterceptors;
using Netcode;
using StardewValley;
using StardewValley.Network;
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;

namespace JunimoServer.Util
{
    public enum MessageTypes : byte
    {
        FarmerDelta = Multiplayer.farmerDelta,
        ServerIntroduction = Multiplayer.serverIntroduction,
        PlayerIntroduction = Multiplayer.playerIntroduction,
        LocationIntroduction = Multiplayer.locationIntroduction,
        ForceEvent = Multiplayer.forceEvent,
        WarpFarmer = Multiplayer.warpFarmer,
        LocationDelta = Multiplayer.locationDelta,
        LocationSprites = Multiplayer.locationSprites,
        CharacterWarp = Multiplayer.characterWarp,
        AvailableFarmhands = Multiplayer.availableFarmhands,
        ChatMessage = Multiplayer.chatMessage,
        ConnectionMessage = Multiplayer.connectionMessage,
        WorldDelta = Multiplayer.worldDelta,
        TeamDelta = Multiplayer.teamDelta,
        NewDaySync = Multiplayer.newDaySync,
        ChatInfoMessage = Multiplayer.chatInfoMessage,
        UserNameUpdate = Multiplayer.userNameUpdate,
        FarmerGainExperience = Multiplayer.farmerGainExperience,
        ServerToClientsMessage = Multiplayer.serverToClientsMessage,
        Disconnecting = Multiplayer.disconnecting,
        SharedAchievement = Multiplayer.sharedAchievement,
        GlobalMessage = Multiplayer.globalMessage,
        PartyWideMail = Multiplayer.partyWideMail,
        ForceKick = Multiplayer.forceKick,
        RemoveLocationFromLookup = Multiplayer.removeLocationFromLookup,
        FarmerKilledMonster = Multiplayer.farmerKilledMonster,
        RequestGrandpaReevaluation = Multiplayer.requestGrandpaReevaluation,
        DigBuriedNut = Multiplayer.digBuriedNut,
        RequestPassout = Multiplayer.requestPassout,
        Passout = Multiplayer.passout,
        StartNewDaySync = Multiplayer.startNewDaySync,
        ReadySync = Multiplayer.readySync,
        ChestHitSync = Multiplayer.chestHitSync,
        DedicatedServerSync = Multiplayer.dedicatedServerSync
    }

    public class NetworkHelper
    {
        public static IPAddress GetIpAddressLocal()
        {
            var interfaces = NetworkInterface
                .GetAllNetworkInterfaces()
                .Where(a => a.OperationalStatus == OperationalStatus.Up);

            return interfaces
                .First()
                .GetIPProperties()
                .UnicastAddresses
                .Where(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                .First()
                .Address;
        }

        public static IPAddress GetIpAddressExternal()
        {
            IPAddress address;

            try
            {
                string pubIp = new WebClient().DownloadString("https://api.ipify.org");
                return IPAddress.Parse(pubIp);
            }
            catch (Exception ex)
            {
                address = IPAddress.None;
            }

            return address;
        }

        public static IncomingMessage ParseOutgoingMessage(OutgoingMessage message)
        {
            IncomingMessage incMsg = new IncomingMessage();

            using (MemoryStream memoryStream = new MemoryStream())
            {
                using (BinaryWriter writer = new BinaryWriter(memoryStream))
                {
                    message.Write(writer);
                    memoryStream.Position = 0;
                    using (BinaryReader reader = new BinaryReader(memoryStream))
                    {
                        incMsg.Read(reader);
                    }
                }
            }

            return incMsg;
        }

        public static bool IsLocationDeltaMessageForLocation<T>(MessageContext context, out T location) where T : GameLocation
        {
            var isStructure = context.Reader.ReadByte() > 0;
            var locationName = context.Reader.ReadString();

            if (locationName.StartsWith(typeof(T).Name))
            {
                // Uses global location, so changes affect server + all clients
                if (Game1.getLocationFromName(locationName, isStructure) is T locationGeneric)
                {
                    location = locationGeneric;
                    return true;
                }
            }

            location = null;
            return false;
        }

        public static OutgoingMessage CreateMessagePlayerIntroduction(NetRoot<Farmer> farmerRoot, Farmer otherFarmer, string farmerRootIpText)
        {
            return new OutgoingMessage(
                Multiplayer.playerIntroduction,
                farmerRoot.Value,
                farmerRootIpText,
                Game1.Multiplayer.writeObjectFullBytes(farmerRoot, otherFarmer.UniqueMultiplayerID)
            );
        }

        public static OutgoingMessage CreateMessageServerIntroduction(long peerId)
        {
            return new OutgoingMessage(
                Multiplayer.serverIntroduction,
                Game1.serverHost.Value,
                Game1.Multiplayer.writeObjectFullBytes(Game1.serverHost, peerId),
                Game1.Multiplayer.writeObjectFullBytes(Game1.player.teamRoot, peerId),
                Game1.Multiplayer.writeObjectFullBytes(Game1.netWorldState, peerId)
            );
        }

        public static OutgoingMessage CreateMessagePlayerIntroduction(NetRoot<Farmer> farmerRoot, long peerId, string farmerIpText)
        {
            return new OutgoingMessage(
                Multiplayer.playerIntroduction,
                farmerRoot.Value,
                farmerIpText,
                Game1.Multiplayer.writeObjectFullBytes(farmerRoot, peerId)
            );
        }

        public static OutgoingMessage CreateMessageLocationIntroduction(long peerId, NetRoot<GameLocation> location, bool forceCurrentLocation)
        {
            return new OutgoingMessage(
                Multiplayer.locationIntroduction,
                Game1.serverHost.Value,
                forceCurrentLocation,
                Game1.Multiplayer.writeObjectFullBytes(location, peerId)
            );
        }

        public static OutgoingMessage CreateMessageUserNameUpdate(long farmerId, string userName)
        {
            return new OutgoingMessage(
                Multiplayer.userNameUpdate,
                Game1.serverHost.Value,
                farmerId,
                userName
            );
        }
    }

    /// <summary>
    /// Not sure if we want to implement per-message structs like this, it's nice to use but probably less so to maintain them.
    /// For now leaveing it here for thought, usage:
    /// var message = new LocationIntroductionMessage(Game1.serverHost, context);
    /// if (context.MessageType == Multiplayer.xxxMessageType) 
    /// context.ModifiedMessage = message.ToOutgoingMessage();
    /// </summary>
    public struct LocationIntroductionMessage
    {
        public NetRoot<GameLocation> Location;

        public bool ForceCurrentLocation;

        public long PeerId;

        private NetRoot<Farmer> farmerRoot;

        public LocationIntroductionMessage(NetRoot<Farmer> farmerRoot, MessageContext context)
        {
            this.farmerRoot = farmerRoot;
            PeerId = context.PeerId;

            ForceCurrentLocation = context.IncomingMessage.Reader.ReadBoolean();
            Location = NetRoot<GameLocation>.Connect(context.IncomingMessage.Reader);
        }

        public LocationIntroductionMessage(NetRoot<Farmer> farmerRoot, long peerId, IncomingMessage message)
        {
            this.farmerRoot = farmerRoot;
            PeerId = peerId;

            ForceCurrentLocation = message.Reader.ReadBoolean();
            Location = NetRoot<GameLocation>.Connect(message.Reader);
        }

        public LocationIntroductionMessage(NetRoot<Farmer> farmerRoot, long peerId, NetRoot<GameLocation> location, bool forceCurrentLocation)
        {
            this.farmerRoot = farmerRoot;
            PeerId = peerId;

            Location = location;
            ForceCurrentLocation = forceCurrentLocation;
        }

        public OutgoingMessage ToOutgoingMessage()
        {
            return new OutgoingMessage(
                Multiplayer.playerIntroduction,
                farmerRoot.Value,
                ForceCurrentLocation,
                Game1.Multiplayer.writeObjectFullBytes(Location, PeerId)
            );
        }
    }
}
