using JunimoServer.Services.MessageInterceptors;
using JunimoServer.Services.PersistentOption;
using JunimoServer.Util;
using Netcode;
using StardewValley;
using StardewValley.Buildings;
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
            if (!options.IsCabinStack)
            {
                return;
            }

            var forceCurrentLocation = context.Reader.ReadBoolean();
            var location = NetRoot<GameLocation>.Connect(context.Reader);

            // Using a local copy of GameLocation, so changes only apply
            // when we pass it along the intercepted outgoing message
            if (location.Value is Farm farm)
            {
                if (farm.GetCabinHidden(context.PeerId, out Building cabin))
                {
                    cabin.Relocate(StackLocation.Create().ToPoint());

                    // Replace the outgoing message with the modified data
                    context.ModifiedMessage = NetworkHelper.CreateMessageLocationIntroduction(context.PeerId, location, forceCurrentLocation);

                    // TODO: Would be nice to also have at least one cabin visible on server, but code
                    // below won't work as it physically moves and persists the cabin for all players 
                    //Game1.getFarm().GetCabinHidden(context.PeerId).Relocate(StackLocation.Create().ToPoint());
                }
            }
        }

        public static void OnLocationDeltaMessage(MessageContext context)
        {
            if (options.IsCabinStack && NetworkHelper.IsLocationDeltaMessageForLocation(context, out Cabin cabin))
            {
                cabin.ParentBuilding.updateInteriorWarps(cabin);
            }
        }
    }
}
