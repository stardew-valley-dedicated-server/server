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

        // Pot crops are keyed under pot.TileLocation (the watcher can't rely on
        // dirt.Tile at pot creation), so canonicalize via HoeDirt.Pot — the
        // vanilla back-reference to the containing pot (see CropWatcher's
        // terrain-loop skip for the shared pot-wins-the-tile rule).
        var tile = dirt.Pot?.TileLocation ?? dirt.Tile;
        var managed = _cropSaverDataLoader.GetSaverCrop(dirt.Location.NameOrUniqueName, tile);
        return managed == null;
    }

    public static bool IsManaged(string locationName, Vector2 tile)
    {
        return _cropSaverDataLoader?.GetSaverCrop(locationName, tile) != null;
    }
}
