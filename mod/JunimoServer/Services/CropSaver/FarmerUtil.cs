using Microsoft.Xna.Framework;
using StardewValley;

namespace JunimoServer.Services.CropSaver
{
    public static class FarmerUtil
    {
        public static Farmer GetClosestFarmer(GameLocation location, Vector2 tileLocation)
        {
            var farmers = Game1.getOnlineFarmers();
            var closestFarmer = Game1.player; // assign ownership of crop to host as fallback (should only be the case if crop planting was automated)
            var closestDistance = float.MaxValue;
            foreach (var farmer in farmers)
            {
                if (!farmer.currentLocation.Equals(location)) continue;
                var farmerDistance = Vector2.Distance(farmer.Tile, tileLocation);
                if (farmerDistance < closestDistance)
                {
                    closestFarmer = farmer;
                    closestDistance = farmerDistance;
                }
            }

            return closestFarmer;
        }
    }
}