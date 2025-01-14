using JunimoServer.Services.MessageInterceptors;
using JunimoServer.Services.PersistentOption;
using JunimoServer.Util;
using Netcode;
using StardewValley;
using StardewValley.Locations;

namespace JunimoServer.Services.CabinManager
{
    public class CabinManagerOverrides
    {
        public static CabinManagerData cabinManagerData;
        private static PersistentOptions options;
        private static ServerJoinedHandler onServerJoined;

        public static void Initialize(PersistentOptions options, ServerJoinedHandler onServerJoined)
        {
            CabinManagerOverrides.options = options;
            CabinManagerOverrides.onServerJoined = onServerJoined;
        }

        public static void sendServerIntroduction_Postfix(long peer)
        {
            onServerJoined?.Invoke(null, new ServerJoinedEventArgs(peer));
        }

        public static void OnLocationIntroductionMessage(MessageContext context)
        {
            // Parse message
            var forceCurrentLocation = context.Reader.ReadBoolean();
            var location = NetRoot<GameLocation>.Connect(context.Reader);

            // Check location
            if (location.Value is not Farm)
            {
                return;
            }

            GameLocation farm;

            if (options.IsFarmHouseStack)
            {
                // Update warps on server
                farm = Game1.getFarm();
                var cabin = farm.GetCabin(context.PeerId);

                cabin.SetWarpsToFarmFarmhouseDoor();
            }
            else
            {
                // Update position and warps for this client only
                farm = location.Value;
                var cabin = farm.GetCabin(context.PeerId);

                cabin.Relocate(StackLocation.Create().ToPoint());
            }

            context.ModifiedMessage = NetworkHelper.CreateMessageLocationIntroduction(context.PeerId, farm.Root, forceCurrentLocation);
        }

        public static void OnLocationDeltaMessage(MessageContext context)
        {
            if (NetworkHelper.IsLocationDeltaMessageForLocation(context, out Cabin cabin))
            {
                if (options.IsFarmHouseStack)
                {
                    cabin.SetWarpsToFarmFarmhouseDoor();
                }
                else
                {
                    cabin.SetWarpsToFarmCabinDoor();

                }
            }
        }
    }
}
