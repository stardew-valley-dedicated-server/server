using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;

namespace JunimoServer.Services.CropSaver
{
    public class CropSaverOverrides
    {
        private static IModHelper _helper;
        private static IMonitor _monitor;
        private static CropSaverDataLoader _cropSaverDataLoader;

        public static void Initialize(IModHelper helper, IMonitor monitor, CropSaverDataLoader cropSaverDataLoader)
        {
            _helper = helper;
            _monitor = monitor;
            _cropSaverDataLoader = cropSaverDataLoader;
        }

        public static bool KillCrop_Prefix(ref Crop __instance)
        {
            var cropLocation = _helper.Reflection.GetField<Vector2>(__instance, "tilePosition").GetValue();

            var townieCrop = _cropSaverDataLoader.GetSaverCrop("Farm", cropLocation);

            if (townieCrop != null) return false;

            return true;
        }
    }
}