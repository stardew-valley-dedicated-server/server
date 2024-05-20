using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using StardewModdingAPI.Utilities;
using StardewValley;
using StardewValley.TerrainFeatures;

namespace JunimoServer.Services.CropSaver
{
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


        public SaverCrop(string cropLocationName, Vector2 cropLocationTile, long ownerId, SDate datePlanted,
            int extraDays = 0)
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
            if (location.terrainFeatures.TryGetValue(cropLocationTile, out TerrainFeature terrainFeature))
            {
                if (terrainFeature is HoeDirt dirt)
                {
                    return dirt;
                }
            }

            return null;
        }

        public Crop TryGetCoorespondingCrop()
        {
            var dirt = TryGetCoorespondingDirt();
            return dirt is {crop: { }} ? dirt.crop : null;
        }

        protected bool Equals(SaverCrop other)
        {
            return cropLocationName == other.cropLocationName && cropLocationTile.Equals(other.cropLocationTile) &&
                   ownerId == other.ownerId && Equals(datePlanted, other.datePlanted);
        }

        public override bool Equals(object obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            if (obj.GetType() != this.GetType()) return false;
            return Equals((SaverCrop) obj);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(cropLocationName, cropLocationTile, ownerId, datePlanted);
        }
    }
}