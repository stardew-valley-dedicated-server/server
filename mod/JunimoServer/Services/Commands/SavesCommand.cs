using JunimoServer.Services.GameLoader;
using JunimoServer.Services.Settings;
using StardewModdingAPI;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;

namespace JunimoServer.Services.Commands
{
    internal static class SavesCommand
    {
        private static IMonitor _monitor;
        private static GameLoaderService _gameLoader;
        private static ServerSettingsLoader _settings;

        public static void Register(
            IModHelper helper,
            IMonitor monitor,
            GameLoaderService gameLoader,
            ServerSettingsLoader settings)
        {
            _monitor = monitor;
            _gameLoader = gameLoader;
            _settings = settings;

            helper.ConsoleCommands.Add("saves",
                "Save management. Run 'saves' for list, 'saves info <name>', 'saves select <name> [--confirm]'.",
                (cmd, args) => HandleCommand(args));
        }

        private static void HandleCommand(string[] args)
        {
            if (args.Length == 0)
            {
                ListSaves();
                return;
            }

            switch (args[0].ToLowerInvariant())
            {
                case "info":
                    if (args.Length < 2)
                    {
                        _monitor.Log("Usage: saves info <saveName>", LogLevel.Warn);
                        return;
                    }
                    ShowSaveInfo(args[1]);
                    break;
                case "select":
                    if (args.Length < 2)
                    {
                        _monitor.Log("Usage: saves select <saveName> [--confirm]", LogLevel.Warn);
                        return;
                    }
                    var confirm = args.Any(a => a == "--confirm");
                    SelectSave(args[1], confirm);
                    break;
                default:
                    _monitor.Log($"Unknown saves subcommand: {args[0]}. Use: saves [info|select]", LogLevel.Warn);
                    break;
            }
        }

        private static void ListSaves()
        {
            var savesPath = Constants.SavesPath;
            if (!Directory.Exists(savesPath))
            {
                _monitor.Log("No saves directory found.", LogLevel.Warn);
                return;
            }

            var activeSave = Constants.SaveFolderName;
            var saveDirs = Directory.GetDirectories(savesPath);

            _monitor.Log("Available Saves:", LogLevel.Info);

            foreach (var dir in saveDirs)
            {
                var saveName = Path.GetFileName(dir);
                var isActive = saveName == activeSave;
                var prefix = isActive ? "  * " : "    ";
                var suffix = isActive ? "  (active - currently loaded)" : "";

                var info = ReadSaveGameInfo(dir);
                var details = info != null ? $"  {info.FarmTypeName}, {info.CabinCount} cabins" : "";

                _monitor.Log($"{prefix}{saveName}{suffix}{details}", LogLevel.Info);
            }
        }

        private static void ShowSaveInfo(string saveName)
        {
            var savePath = Path.Combine(Constants.SavesPath, saveName);
            if (!Directory.Exists(savePath))
            {
                _monitor.Log($"Save '{saveName}' not found.", LogLevel.Error);
                return;
            }

            var info = ReadSaveGameInfo(savePath);
            if (info == null)
            {
                _monitor.Log($"Could not read save info for '{saveName}'.", LogLevel.Error);
                return;
            }

            _monitor.Log($"Save: {saveName}", LogLevel.Info);
            _monitor.Log($"  Farm Type:  {info.FarmTypeName} ({info.FarmType})", LogLevel.Info);
            _monitor.Log($"  Farm Name:  {info.FarmName}", LogLevel.Info);
            _monitor.Log($"  Cabins:     {info.CabinCount}", LogLevel.Info);

            if (info.PlayerNames.Count > 0)
            {
                _monitor.Log($"  Players:    {string.Join(", ", info.PlayerNames)}", LogLevel.Info);
            }
        }

        private static void SelectSave(string saveName, bool confirm)
        {
            var savePath = Path.Combine(Constants.SavesPath, saveName);
            if (!Directory.Exists(savePath))
            {
                _monitor.Log($"Save '{saveName}' not found.", LogLevel.Error);
                return;
            }

            if (!confirm)
            {
                var info = ReadSaveGameInfo(savePath);
                _monitor.Log("Import Preview:", LogLevel.Info);
                _monitor.Log($"  Save:                    {saveName}", LogLevel.Info);

                if (info != null)
                {
                    _monitor.Log($"  Farm Type:               {info.FarmTypeName} ({info.FarmType})", LogLevel.Info);
                    _monitor.Log($"  Existing Cabins:         {info.CabinCount}", LogLevel.Info);
                }

                _monitor.Log($"  -- Settings to apply --", LogLevel.Info);
                _monitor.Log($"  Cabin Strategy:          {_settings.CabinStrategy}", LogLevel.Info);
                _monitor.Log($"  Existing Cabin Behavior: {_settings.ExistingCabinBehavior}", LogLevel.Info);
                _monitor.Log($"", LogLevel.Info);
                _monitor.Log($"Run 'saves select {saveName} --confirm' to activate.", LogLevel.Info);
                _monitor.Log($"The server will load this save on next restart.", LogLevel.Info);
                return;
            }

            _gameLoader.SetSaveNameToLoad(saveName);
            _monitor.Log($"Save '{saveName}' set as active. Restart the server to load it.", LogLevel.Info);
        }

        #region Save Info Parsing

        private class SaveInfo
        {
            public string FarmName = "Unknown";
            public int FarmType = -1;
            public string FarmTypeName = "Unknown";
            public int CabinCount = 0;
            public List<string> PlayerNames = new List<string>();
        }

        private static SaveInfo ReadSaveGameInfo(string saveDirectory)
        {
            var saveGameInfoPath = Path.Combine(saveDirectory, "SaveGameInfo");
            if (!File.Exists(saveGameInfoPath))
            {
                return null;
            }

            var farmTypeNames = new Dictionary<int, string>
            {
                { 0, "Standard" }, { 1, "Riverland" }, { 2, "Forest" },
                { 3, "Hilltop" }, { 4, "Wilderness" }, { 5, "Four Corners" }, { 6, "Beach" }
            };

            try
            {
                var info = new SaveInfo();
                var doc = new XmlDocument();
                doc.Load(saveGameInfoPath);

                var farmNameNode = doc.SelectSingleNode("//farmName");
                if (farmNameNode != null)
                {
                    info.FarmName = farmNameNode.InnerText;
                }

                var saveName = Path.GetFileName(saveDirectory);
                var mainSavePath = Path.Combine(saveDirectory, saveName);
                if (File.Exists(mainSavePath))
                {
                    var mainDoc = new XmlDocument();
                    mainDoc.Load(mainSavePath);

                    var whichFarmNode = mainDoc.SelectSingleNode("//whichFarm");
                    if (whichFarmNode != null && int.TryParse(whichFarmNode.InnerText, out var farmType))
                    {
                        info.FarmType = farmType;
                        info.FarmTypeName = farmTypeNames.TryGetValue(farmType, out var n) ? n : $"Custom ({farmType})";
                    }

                    var buildingNodes = mainDoc.SelectNodes("//Building[buildingType='Cabin']");
                    info.CabinCount = buildingNodes?.Count ?? 0;

                    var farmhandNodes = mainDoc.SelectNodes("//Building[buildingType='Cabin']/indoors/farmhand/name");
                    if (farmhandNodes != null)
                    {
                        foreach (XmlNode node in farmhandNodes)
                        {
                            var name = node.InnerText;
                            if (!string.IsNullOrEmpty(name))
                            {
                                info.PlayerNames.Add(name);
                            }
                        }
                    }
                }

                return info;
            }
            catch (Exception ex)
            {
                _monitor.Log($"Error reading save info: {ex.Message}", LogLevel.Debug);
                return null;
            }
        }

        #endregion
    }
}
