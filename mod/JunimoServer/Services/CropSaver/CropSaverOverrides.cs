using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;

namespace JunimoServer.Services.CropSaver;

public class CropSaverOverrides
{
    private static IMonitor _monitor;
    private static CropSaverDataLoader _cropSaverDataLoader;

    /// <summary>
    /// True while Utility.performLightningUpdate is executing. Both lightning paths
    /// (the live storm tick and the overnight batch) run on the game thread, so a
    /// plain field suffices — no interlocking.
    /// </summary>
    private static bool _lightningUpdateInProgress;

    public static void Initialize(IMonitor monitor, CropSaverDataLoader cropSaverDataLoader)
    {
        _monitor = monitor;
        _cropSaverDataLoader = cropSaverDataLoader;
    }

    /// <summary>Harmony prefix on Utility.performLightningUpdate.</summary>
    public static void LightningUpdate_Prefix()
    {
        _lightningUpdateInProgress = true;
    }

    /// <summary>
    /// Harmony finalizer on Utility.performLightningUpdate — clears the flag even when
    /// the strike throws, so an exception can't leave it stuck.
    /// </summary>
    public static void LightningUpdate_Finalizer()
    {
        _lightningUpdateInProgress = false;
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
        if (managed == null)
        {
            return true;
        }

        if (_lightningUpdateInProgress && !Env.CropSaverLightningImmunity)
        {
            // Vanilla lightning kill re-enabled: let the kill through and drop the
            // tracking entry — a dead crop still counts as trackable to the watcher
            // (CropLocation.HasTrackableCrop), so without the removal the corpse
            // would stay managed and OnDayEnd would keep prolonging it.
            _cropSaverDataLoader.RemoveCrop(managed);
            return true;
        }

        return false;
    }

    public static bool IsManaged(string locationName, Vector2 tile)
    {
        return _cropSaverDataLoader?.GetSaverCrop(locationName, tile) != null;
    }
}
