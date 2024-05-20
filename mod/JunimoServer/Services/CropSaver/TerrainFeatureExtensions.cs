#nullable enable
using StardewValley;
using StardewValley.TerrainFeatures;

namespace JunimoServer.Services.CropSaver
{
    public static class TerrainFeatureExtensions
    {
        public static bool ContainsCrop(this TerrainFeature feature)
        {
            return feature is HoeDirt dirt && dirt.crop != null;
        }
        
        public static Crop? TryGetCrop(this TerrainFeature feature)
        {
            if (feature is HoeDirt dirt && dirt.crop != null)
            {
                return dirt.crop;
            }
            else
            {
                return null;
            }
        }
    }
}