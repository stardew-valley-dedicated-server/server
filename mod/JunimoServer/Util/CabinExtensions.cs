using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Locations;
using System.Collections.Generic;
using System.Linq;

namespace JunimoServer.Util
{
    public static class CabinExtensions
    {
        public static bool IsOwnedBy(this Cabin cabin, long ownerId)
        {
            return cabin?.owner.UniqueMultiplayerID == ownerId;
        }


        public static IEnumerable<Warp> GetWarpsToFarm(this Cabin cabin)
        {
            return cabin.warps.Where(warp => warp.TargetName == "Farm");
        }


        public static void SetWarpsToFarm(this Cabin cabin, Point position)
        {
            foreach (var warp in cabin.GetWarpsToFarm())
            {
                //warp.TargetName = parentLocation?.NameOrUniqueName ?? warp.TargetName;
                warp.TargetX = position.X;
                warp.TargetY = position.Y;
            }
        }

        public static void SetWarpsToFarmCabinDoor(this Cabin cabin)
        {
            cabin.SetWarpsToFarm(cabin.ParentBuilding.getPointForHumanDoor());
        }

        public static void SetWarpsToFarmFarmhouseDoor(this Cabin cabin)
        {
            cabin.SetWarpsToFarm(Game1.getFarm().GetMainFarmHouseEntry());
        }
    }
}
