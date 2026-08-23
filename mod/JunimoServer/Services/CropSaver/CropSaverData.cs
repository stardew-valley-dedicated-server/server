using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using StardewModdingAPI.Utilities;
using StardewValley;
using StardewValley.Objects;
using StardewValley.TerrainFeatures;

namespace JunimoServer.Services.CropSaver;

public class CropSaverData
{
    public List<SaverCrop> Crops { get; set; } = new List<SaverCrop>();
}

public class SaverCrop
{
    public string cropLocationName;
    public Vector2 cropLocationTile;
    public long ownerId;
    public SDate datePlanted;

    public int extraDays;

    public SaverCrop(
        string cropLocationName,
        Vector2 cropLocationTile,
        long ownerId,
        SDate datePlanted,
        int extraDays = 0
    )
    {
        this.cropLocationName = cropLocationName;
        this.cropLocationTile = cropLocationTile;
        this.ownerId = ownerId;
        this.datePlanted = datePlanted;
        this.extraDays = extraDays;
    }

    public void IncrementExtraDays()
    {
        extraDays++;
    }

    public bool IsLocatedAt(string cropLocation, Vector2 cropPosition)
    {
        return cropLocation.Equals(cropLocationName) && cropLocationTile.Equals(cropPosition);
    }

    public HoeDirt TryGetCoorespondingDirt()
    {
        var location = Game1.getLocationFromName(cropLocationName);
        return location == null ? null : TryGetDirtAt(location, cropLocationTile);
    }

    /// <summary>
    /// CropSaver's canonical dirt resolution for a tile: a Garden Pot's inner dirt,
    /// else a terrain HoeDirt. Pot wins the tile: a pot on an empty tilled tile
    /// shares the key with the crop-less terrain dirt beneath it (see CropWatcher's
    /// terrain-loop skip for the invariant). Test probes of the Crop.Kill seam
    /// (e.g. /test/lightning_strike) resolve through this too, so they can't drift
    /// from the tracker's own lookup.
    /// </summary>
    public static HoeDirt TryGetDirtAt(GameLocation location, Vector2 tile)
    {
        if (location.Objects.TryGetValue(tile, out var obj) && obj is IndoorPot pot)
        {
            return pot.hoeDirt.Value;
        }

        if (location.terrainFeatures.TryGetValue(tile, out var tf) && tf is HoeDirt dirt)
        {
            return dirt;
        }

        return null;
    }

    public Crop TryGetCoorespondingCrop()
    {
        var dirt = TryGetCoorespondingDirt();
        return dirt is { crop: { } } ? dirt.crop : null;
    }

    protected bool Equals(SaverCrop other)
    {
        return cropLocationName == other.cropLocationName
            && cropLocationTile.Equals(other.cropLocationTile)
            && ownerId == other.ownerId
            && Equals(datePlanted, other.datePlanted);
    }

    public override bool Equals(object obj)
    {
        if (ReferenceEquals(null, obj))
        {
            return false;
        }

        if (ReferenceEquals(this, obj))
        {
            return true;
        }

        if (obj.GetType() != this.GetType())
        {
            return false;
        }

        return Equals((SaverCrop)obj);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(cropLocationName, cropLocationTile, ownerId, datePlanted);
    }
}
