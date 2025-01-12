using JunimoServer.Services.CabinManager;
using StardewValley;

namespace JunimoServer.Util
{
    public static class FarmerExtensions
    {
        public static void WarpToHomeCabin(this Farmer farmer, CabinStrategy cabinStrategy)
        {
            // TODO: Check if farmers have correct `farmer.homeLocation` with our custom cabin handling,
            // and then use homeLocation instead of custom logic - note: gets indoors location, not building
            var cabin = Game1.getFarm().GetCabinHidden(farmer.UniqueMultiplayerID);
            if (cabin == null)
            {
                return;
            }

            var indoors = cabin.GetCabinIndoors();
            var indoorsName = indoors.NameOrUniqueName;
            var indoorsEntryWarpTarget = indoors.getEntryLocation();

            Game1.Multiplayer.sendChatMessage(
                LocalizedContentManager.CurrentLanguageCode,
                "Can't enter main building, porting to your own cabin",
                farmer.UniqueMultiplayerID
            );

            // Passout does a screen fade and then warps the player
            Game1.server.sendMessage(farmer.UniqueMultiplayerID, Multiplayer.passout, Game1.player, new object[] {
                indoorsName, indoorsEntryWarpTarget.X, indoorsEntryWarpTarget.Y, true
            });
        }
    }
}
