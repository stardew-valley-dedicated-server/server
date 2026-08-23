using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.TerrainFeatures;

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

    /// <summary>
    /// Canonical managed-crop lookup: (NameOrUniqueName, pot-canonical tile).
    /// The DayUpdate guard and the /test/crops snapshot resolve through it;
    /// KillCrop_Prefix uses the same addressing but needs the entry itself.
    /// </summary>
    public static bool IsManaged(string locationName, Vector2 tile)
    {
        return _cropSaverDataLoader?.GetSaverCrop(locationName, tile) != null;
    }

    /// <summary>
    /// Captures the crop about to be destroyed by HoeDirt.dayUpdate's winter branch.
    /// That branch calls destroyCrop (nulling dirt.crop) without ever calling Crop.Kill,
    /// so <see cref="KillCrop_Prefix"/> cannot protect managed crops from it. The guard
    /// below mirrors the branch's own condition in full (source of truth:
    /// StardewValley.TerrainFeatures.HoeDirt.dayUpdate) — re-sync if vanilla changes it.
    /// </summary>
    public static void DayUpdate_Prefix(HoeDirt __instance, out Crop __state)
    {
        __state = null;
        var location = __instance.Location;
        var crop = __instance.crop;
        if (location == null || crop == null)
        {
            return;
        }

        if (
            !location.IsOutdoors
            || location.GetSeason() != Season.Winter
            || crop.isWildSeedCrop()
            || crop.IsInSeason(location)
        )
        {
            return;
        }

        // Same entry addressing as KillCrop_Prefix: NameOrUniqueName key,
        // pot-canonical tile.
        if (!IsManaged(location.NameOrUniqueName, __instance.Pot?.TileLocation ?? __instance.Tile))
        {
            return;
        }

        __state = crop;
    }

    /// <summary>
    /// Restores a managed crop the winter-destroy branch removed. Safe: an out-of-season
    /// crop takes Crop.newDay's first branch (Kill — suppressed for managed crops — then
    /// return), so within dayUpdate the winter branch is the only path that nulls
    /// dirt.crop. destroyCrop's other side effect (nearWaterForPaddy reset) is a
    /// recomputed cache and needs no restore.
    /// </summary>
    public static void DayUpdate_Postfix(HoeDirt __instance, Crop __state)
    {
        if (__state != null && __instance.crop == null)
        {
            __instance.crop = __state;
        }
    }
}
