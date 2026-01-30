using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Buildings;

namespace JunimoServer.Services.CabinManager
{
    /// <summary>
    /// Determines the visible position where stacked cabins appear client-side.
    /// This is the position the owning player sees their cabin at — NOT the hidden
    /// out-of-bounds location where cabins are stored server-side.
    ///
    /// Resolution order:
    ///   1. DefaultCabinLocation from CabinManagerData (persisted per-save override)
    ///   2. First map-designated cabin position from FarmCabinPositions (Paths layer tiles 29/30)
    ///   3. Hardcoded fallback (50, 14) if the farm map has no designated positions
    /// </summary>
    public struct StackLocation
    {
        public Vector2 Location;

        public Building CabinChosen;

        private StackLocation(Vector2 location, Building cabinChosen)
        {
            Location = location;
            CabinChosen = cabinChosen;
        }

        public static StackLocation Create(CabinManagerData cabinManagerData)
        {
            // 1. Per-save override (set by admin or imported from save data)
            if (cabinManagerData.DefaultCabinLocation.HasValue)
            {
                return new StackLocation(cabinManagerData.DefaultCabinLocation.Value, null);
            }

            // 2. Map-designated position → 3. hardcoded fallback (50, 14)
            var position = FarmCabinPositions.GetDefaultStackPosition(Game1.getFarm());
            return new StackLocation(position, null);
        }

        public Point ToPoint()
        {
            return Location.ToPoint();
        }
    }
}
