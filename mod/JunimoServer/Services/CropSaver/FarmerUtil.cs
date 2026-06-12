using Microsoft.Xna.Framework;
using StardewValley;

namespace JunimoServer.Services.CropSaver
{
    public static class FarmerUtil
    {
        public static Farmer GetClosestFarmer(
            GameLocation location,
            Vector2 tileLocation,
            long serverHostId
        )
        {
            Farmer closestFarmer = null;
            var closestDistance = float.MaxValue;
            foreach (var farmer in Game1.getOnlineFarmers())
            {
                if (farmer.UniqueMultiplayerID == serverHostId)
                    continue;
                if (!farmer.currentLocation.Equals(location))
                    continue;
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
