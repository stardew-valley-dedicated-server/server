using Microsoft.Xna.Framework;
using StardewValley.Buildings;

namespace JunimoServer.Services.CabinManager
{
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
            if (cabinManagerData.DefaultCabinLocation.HasValue)
            {
                return new StackLocation(cabinManagerData.DefaultCabinLocation.Value, null);
            }

            // Hardcoded fallback
            return new StackLocation(new Vector2(50, 14), null);
        }

        public Point ToPoint()
        {
            return Location.ToPoint();
        }
    }
}
