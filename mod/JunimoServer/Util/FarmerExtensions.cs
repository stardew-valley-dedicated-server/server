using StardewValley;
using StardewValley.Locations;

#nullable enable
namespace JunimoServer.Util
{
    public static class FarmerExtensions
    {
        public static void WarpHome(this Farmer farmer)
        {
            var cabin = Game1.getFarm().GetCabin(farmer.UniqueMultiplayerID);
            if (cabin == null)
            {
                return;
            }

            var indoors = cabin.GetIndoors<Cabin>();
            if (indoors != null)
            {
                var indoorsName = indoors.NameOrUniqueName;
                var indoorsEntryWarpTarget = indoors.getEntryLocation();

                // Passout does a screen fade and then warps the player
                Game1.server.sendMessage(farmer.UniqueMultiplayerID, Multiplayer.passout, Game1.player, new object[] {
                indoorsName, indoorsEntryWarpTarget.X, indoorsEntryWarpTarget.Y, true
            });
            }
        }
    }
}
