using StardewValley;
using StardewValley.Buildings;
using System;
using System.Collections.Generic;
using System.Linq;

namespace JunimoServer.Util
{
    public static class GameLocationExtensions
    {
        public static Building GetBuilding(this GameLocation location, Func<Building, bool> predicate)
        {
            return location.buildings.FirstOrDefault(predicate);
        }

        public static IEnumerable<Building> GetBuildings(this GameLocation location, Func<Building, bool> predicate)
        {
            return location.buildings.Where(predicate);
        }


        public static Building GetCabin(this GameLocation location, long playerId)
        {
            return location.GetBuilding(building =>
                building.isCabin && building.IsOwnedBy(playerId)
            );
        }

        public static bool GetCabin(this GameLocation location, long peerId, out Building building)
        {
            building = location.GetCabin(peerId);
            return building != null;
        }


        public static Building GetCabinHidden(this GameLocation location, long playerId)
        {
            return location.GetBuilding(building =>
                building.isCabin && building.IsOwnedBy(playerId) && building.IsInHiddenStack()
            );
        }

        public static bool GetCabinHidden(this GameLocation location, long peerId, out Building building)
        {
            building = location.GetCabinHidden(peerId);
            return building != null;
        }
    }
}
