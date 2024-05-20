using System;
using System.IO;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;

namespace JunimoServer.Services.GameLoader
{
    class GameLoaderService
    {
        private const string SaveKey = "JunimoHost.GameLoader";

        private readonly IModHelper _helper;
        private readonly IMonitor _monitor;
        private readonly GameLoaderSaveData _saveData;
        public GameLoaderService(IModHelper helper, IMonitor monitor)
        {
            _helper = helper;
            _monitor = monitor;
            _saveData = helper.Data.ReadGlobalData<GameLoaderSaveData>(SaveKey) ?? new GameLoaderSaveData();
        }

        public bool HasLoadableSave()
        {
            var saveName = _saveData.SaveNameToLoad;

            if (saveName == null)
            {
                return false;
            }

            var savePath = GetSavePath(saveName);
            var saveExists = Directory.Exists(savePath);

            return saveExists;

        }

        public bool LoadSave()
        {
            var saveName = _saveData.SaveNameToLoad;
            var savePath = GetSavePath(saveName);
            var saveExists = Directory.Exists(savePath);
            if (!saveExists)
            {
                _monitor.Log($"{savePath} does not exist. Aborting load.", LogLevel.Warn);
                return false;
            }

            Game1.multiplayerMode = 2;

            try
            {
                _monitor.Log($"Loading Save: {saveName}", LogLevel.Info);
                SaveGame.Load(saveName);
                if (Game1.activeClickableMenu is IClickableMenu menu && menu != null)
                {
                    menu.exitThisMenu(false);
                }
            }
            catch (Exception ex)
            {
                _monitor.Log($"Load Failed: {ex.Message} ", LogLevel.Error);
                return false;
            }

            return true;

        }

        public void SetSaveNameToLoad(string saveName)
        {
            _saveData.SaveNameToLoad = saveName;
            _helper.Data.WriteGlobalData(SaveKey, _saveData);
            _monitor.Log($"Save set to load: {saveName}", LogLevel.Info);
        }

        public void SetCurrentGameAsSaveToLoad(string FarmName)
        {
            // Save name goes from nothing -> _uuid -> farmName_uuid (once loaded in completely)
            var saveName = Constants.SaveFolderName;
            if (Constants.SaveFolderName.Substring(0, 1) == "_") // savename is in midpoint
            {
                saveName = SaveGame.FilterFileName(FarmName) + saveName;
            }

            SetSaveNameToLoad(saveName);
        }

        private string GetSavePath(string saveName)
        {
            return Path.Combine(Constants.SavesPath, saveName);
        }
    }
}
