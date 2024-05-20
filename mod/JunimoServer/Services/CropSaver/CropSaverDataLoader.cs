using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewModdingAPI;

namespace JunimoServer.Services.CropSaver
{
    public class CropSaverDataLoader
    {
        private const string CropSaverDataKey = "JunimoHost.CropSaver.data";
        private readonly IModHelper _helper;
        
        private CropSaverData _data = new CropSaverData();

        public CropSaverDataLoader(IModHelper helper)
        {
            _helper = helper;
        }


        public void AddCrop(SaverCrop crop)
        {
            _data.Crops.Add(crop);
        }

        public void RemoveCrop(SaverCrop crop)
        {
            _data.Crops.Remove(crop);
        }
        
        public void RemoveCrop(string locationName, Vector2 tileLocation)
        {
            var crop = GetSaverCrop(locationName, tileLocation);
            if (crop != null)
            {
                RemoveCrop(crop);
            }
        }

        public SaverCrop GetSaverCrop(string locationName, Vector2 tileLocation)
        {
            var i = _data.Crops.FindIndex((crop) => crop.IsLocatedAt(locationName, tileLocation));
            return i != -1 ? _data.Crops.ElementAt(i) : null;
        }

        public List<SaverCrop> GetSaverCrops()
        {
            return _data.Crops;
        }

        public void LoadDataFromDisk()
        {
            _data = _helper.Data.ReadSaveData<CropSaverData>(CropSaverDataKey) ?? new CropSaverData();
        }

        public void SaveDataToDisk()
        {
            _helper.Data.WriteSaveData(CropSaverDataKey, _data);
        }
    }
}