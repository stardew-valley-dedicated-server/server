using JunimoServer.Services.CabinManager;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.Locations;
using System;
using System.Collections.Generic;
using System.Linq;

namespace JunimoServer.Util
{
    public static class GameLocationExtensions
    {

        public static IEnumerable<Building> GetBuildings(this GameLocation location, Func<Building, bool> predicate)
        {
            return location.buildings.Where(predicate);
        }

        public static Building GetBuilding(this GameLocation location, Func<Building, bool> predicate)
        {
            return location.buildings.FirstOrDefault(predicate);
        }

        public static Building GetCabin(this GameLocation location, long playerId)
        {
            return location.GetBuilding(building =>
                building.isCabin && building.IsOwnedBy(playerId)
            );
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

        public static IEnumerable<Building> GetCabins(this GameLocation location)
        {
            return location.GetBuildings(building => building.isCabin);
        }

        public static int GetCabinHiddenCount(this GameLocation location)
        {
            // TODO: Replace static `AllPlayerIdsEverJoined`, maybe with cabin.modData?
            return location.buildings
                .Where(building => building.isCabin)
                .Count(cabin => !CabinManagerService.Data.AllPlayerIdsEverJoined.Contains(cabin.GetCabinIndoors().owner.UniqueMultiplayerID));
        }

        public static bool BuildNewCabin(this GameLocation location)
        {
            // TODO: Replace static `HiddenCabinLocation`, maybe with location.modData?
            var cabinTilePosition = CabinManagerService.HiddenCabinLocation.ToVector2();

            var cabin = new Building("Cabin", cabinTilePosition);
            cabin.skinId.Value = "Log Cabin";
            cabin.magical.Value = true;
            cabin.daysOfConstructionLeft.Value = 0;
            cabin.load();
            if (location.buildStructure(cabin, cabinTilePosition, Game1.player, skipSafetyChecks: true))
            {
                // Terrain is also cleared when cabin is moved out of the stack location
                cabin.ClearTerrainBelow();
                return true;
            }

            return false;
        }
    }

    public static class CabinExtensions
    {
        public static IEnumerable<Warp> GetWarpsToFarm(this Cabin cabin)
        {
            return cabin.warps.Where(warp => warp.TargetName == "Farm");
        }
    }
}
