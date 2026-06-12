using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;

namespace JunimoServer.Services.CropSaver;

public class CropSaverOverrides
{
    private static IMonitor _monitor;
    private static CropSaverDataLoader _cropSaverDataLoader;

    public static void Initialize(IMonitor monitor, CropSaverDataLoader cropSaverDataLoader)
    {
        _monitor = monitor;
        _cropSaverDataLoader = cropSaverDataLoader;
    }

    public static bool KillCrop_Prefix(ref Crop __instance)
    {
        var dirt = __instance.Dirt;
        if (dirt?.Location == null)
        {
            return true;
        }

        var managed = _cropSaverDataLoader.GetSaverCrop(dirt.Location.Name, dirt.Tile);
        return managed == null;
    }

    public static bool IsManaged(string locationName, Vector2 tile)
    {
        return _cropSaverDataLoader?.GetSaverCrop(locationName, tile) != null;
    }
}
