using Microsoft.Xna.Framework;
using StardewValley;

namespace JunimoServer.Services.CropSaver;

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
            {
                continue;
            }

            // currentLocation can be null for a just-approved farmhand: the server adds it to
            // otherFarmers (GameServer.checkFarmhandRequest -> Multiplayer.addPlayer) before the
            // client's first farmer delta binds a location.
            if (farmer.currentLocation?.Equals(location) != true)
            {
                continue;
            }

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
