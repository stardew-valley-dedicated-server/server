using JunimoServer.Services.CabinManager;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.Locations;

namespace JunimoServer.Util
{
    public static class BuildingExtensions
    {
        /// <summary>
        /// Remove all objects, bushes, resource clumps, and terrain features below this building. 
        /// </summary>
        public static void ClearTerrainBelow(this Building building)
        {
            var location = building.GetParentLocation();

            // Clear tiles at cabin collision rect
            location.removeObjectsAndSpawned(building.tileX.Value, building.tileY.Value, 5, 3);

            // Clear single tile one south of cabin door
            location.removeObjectsAndSpawned(building.tileX.Value + 2, building.tileY.Value + 3, 1, 1);
        }

        public static T GetIndoors<T>(this Building building) where T : GameLocation
        {
            return (T)building.GetIndoors();
        }

        public static Cabin GetCabinIndoors(this Building building)
        {
            return building.GetIndoors<Cabin>();
        }

        public static void Relocate(this Building cabin, float x, float y)
        {
            cabin.Relocate(new Vector2(x, y));
        }

        public static void Relocate(this Building cabin, Vector2 position)
        {
            cabin.Relocate(position.ToPoint());
        }

        public static void Relocate(this Building cabin, Point position)
        {
            cabin.SetPosition(position);

            // Update the warp targets so players don't exit at the hidden stack location
            cabin.RefreshCabinToFarmWarps();

            // Clear terrain below building to prevent blocking debris on the porch
            // TODO: Use `cabin.GetParentLocation().OnBuildingMoved(cabin)`?
            cabin.ClearTerrainBelow();
        }

        // TODO: BELONGS TO CABIN
        public static bool IsOwnedBy(this Building building, long ownerId)
        {
            if (building == null || !building.isCabin)
            {
                return false;
            }

            var indoors = building.GetCabinIndoors();
            if (indoors == null)
            {
                return false;
            }

            return indoors.owner.UniqueMultiplayerID == ownerId;
        }

        public static bool IsInHiddenStack(this Building building)
        {
            return building.tileX.Value == CabinManagerService.HiddenCabinLocation.X && building.tileY.Value == CabinManagerService.HiddenCabinLocation.Y;
        }

        public static void SetPosition(this Building building, Vector2 position)
        {
            building.SetPosition(position.ToPoint());
        }

        public static void SetPosition(this Building building, Point position)
        {
            building.tileX.Value = position.X;
            building.tileY.Value = position.Y;
        }

        // TODO: BELONGS TO CABIN
        public static void SetCabinToFarmWarps(this Building building, Point position)
        {
            var cabin = building.GetIndoors<Cabin>();

            foreach (var warp in cabin.GetWarpsToFarm())
            {
                warp.TargetX = position.X;
                warp.TargetY = position.Y;
            }
        }

        // TODO: BELONGS TO CABIN
        public static void RefreshCabinToFarmWarps(this Building building)
        {
            building.SetCabinToFarmWarps(new Point(building.tileX.Value + 2, building.tileY.Value + 2));
        }
    }
}
