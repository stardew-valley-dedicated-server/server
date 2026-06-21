using System;
using System.Collections.Generic;
using System.IO;
using System.Xml;
using JunimoServer.Services.GameCreator;
using JunimoServer.Services.SaveImport;
using StardewModdingAPI;

namespace JunimoServer.Services.Commands;

internal static class SavesCommand
{
    private static IMonitor _monitor;
    private static SaveImportService _saveImport;

    public static void Register(IModHelper helper, IMonitor monitor, SaveImportService saveImport)
    {
        _monitor = monitor;
        _saveImport = saveImport;

        helper.ConsoleCommands.Add(
            "saves",
            "Save management. Run 'saves' for list, 'saves info <name>', "
                + "'saves import <name> [--swap-host-to <id>]'.",
            (cmd, args) => HandleCommand(args)
        );
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
            case "import":
                ImportSave(args);
                break;
            default:
                _monitor.Log(
                    $"Unknown saves subcommand: {args[0]}. Use: saves [info|import]",
                    LogLevel.Warn
                );
                break;
        }
    }

    /// <summary>
    /// Imports a save in one shot. <c>saves import &lt;name&gt;</c> imports as-is (the save's owner
    /// becomes the headless host); <c>saves import &lt;name&gt; --swap-host-to &lt;id&gt;</c> demotes
    /// the owner into a cabin farmhand bound to the platform id and installs a fresh blank "Server"
    /// host. Presence of <c>--swap-host-to</c> selects swap+bind; absence selects as-is. Executes
    /// directly (no preview/confirm gate) — the safety net is the fault-tolerant in-place transform.
    /// </summary>
    private static void ImportSave(string[] args)
    {
        if (args.Length < 2)
        {
            _monitor.Log("Usage: saves import <saveName> [--swap-host-to <id>]", LogLevel.Warn);
            return;
        }

        var saveName = args[1];

        // Parse the optional --swap-host-to <id>. Platform-neutral on purpose (Steam64 OR
        // GOG Galaxy-uint64). Absence = as-is import.
        string userId = null;
        var flagIndex = Array.FindIndex(
            args,
            a => string.Equals(a, "--swap-host-to", StringComparison.OrdinalIgnoreCase)
        );
        if (flagIndex >= 0)
        {
            if (flagIndex + 1 >= args.Length)
            {
                _monitor.Log(
                    "--swap-host-to requires a platform id (e.g. a Steam64 or GOG Galaxy id).",
                    LogLevel.Warn
                );
                return;
            }
            userId = args[flagIndex + 1];
        }

        var result = _saveImport.ExecuteImport(saveName, userId);
        if (!result.Success)
        {
            // ExecuteImport already logged the specific Warn; add the headline.
            _monitor.Log(
                $"Import of '{saveName}' did not complete (see warning above).",
                LogLevel.Warn
            );
            return;
        }

        if (result.RepointedBind)
        {
            _monitor.Log(
                $"Re-pointed the pending host-swap bind for '{saveName}'. Restart to finalize.",
                LogLevel.Info
            );
        }
        else if (result.Swapped)
        {
            _monitor.Log(
                $"Imported '{saveName}' with host swap (former owner "
                    + $"'{result.FormerOwnerName ?? result.FormerOwnerUid.ToString()}' → cabin farmhand "
                    + "bound to the provided id). Restart the server to finalize.",
                LogLevel.Info
            );
        }
        else
        {
            _monitor.Log(
                $"Imported '{saveName}' as-is. Restart the server to load it.",
                LogLevel.Info
            );
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
            // Warn, not Error: console commands run in the server process, where LogLevel.Error trips
            // ServerContainer's ERROR/FATAL test-poison scan. A bad save name is operator error, not
            // a server fault.
            _monitor.Log($"Save '{saveName}' not found.", LogLevel.Warn);
            return;
        }

        var info = ReadSaveGameInfo(savePath);
        if (info == null)
        {
            _monitor.Log($"Could not read save info for '{saveName}'.", LogLevel.Warn);
            return;
        }

        _monitor.Log($"Save: {saveName}", LogLevel.Info);
        _monitor.Log($"  Farm Type:  {info.FarmTypeDisplay}", LogLevel.Info);
        _monitor.Log($"  Farm Name:  {info.FarmName}", LogLevel.Info);
        _monitor.Log($"  Cabins:     {info.CabinCount}", LogLevel.Info);

        if (info.PlayerNames.Count > 0)
        {
            _monitor.Log($"  Players:    {string.Join(", ", info.PlayerNames)}", LogLevel.Info);
        }
    }

    #region Save Info Parsing

    private class SaveInfo
    {
        public string FarmName = "Unknown";

        /// <summary>Raw &lt;whichFarm&gt; token: a vanilla index "0"-"6" or a modded farm Id.</summary>
        public string FarmTypeRaw = "Unknown";
        public string FarmTypeName = "Unknown";
        public int CabinCount = 0;
        public List<string> PlayerNames = new List<string>();

        /// <summary>Detail label: "Name (raw)" for vanilla, just the Id for a modded farm.</summary>
        public string FarmTypeDisplay =>
            FarmTypeName == FarmTypeRaw ? FarmTypeName : $"{FarmTypeName} ({FarmTypeRaw})";
    }

    private static SaveInfo ReadSaveGameInfo(string saveDirectory)
    {
        var saveGameInfoPath = Path.Combine(saveDirectory, "SaveGameInfo");
        if (!File.Exists(saveGameInfoPath))
        {
            return null;
        }

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

                // whichFarm is serialized as a string (GetFarmTypeID): "0"-"6" for vanilla
                // farms, or the Data/AdditionalFarms Id for a modded farm. An int.TryParse
                // here would return false on a modded Id and silently drop the farm type.
                var whichFarmNode = mainDoc.SelectSingleNode("//whichFarm");
                if (whichFarmNode != null && !string.IsNullOrWhiteSpace(whichFarmNode.InnerText))
                {
                    info.FarmTypeRaw = whichFarmNode.InnerText;
                    // The token is a vanilla index ("0"-"6") or a Data/AdditionalFarms Id;
                    // DisplayName() turns either into the same friendly label as elsewhere.
                    info.FarmTypeName = (
                        int.TryParse(info.FarmTypeRaw, out var index)
                            ? FarmTypeSetting.FromIndex(index)
                            : FarmTypeSetting.FromId(info.FarmTypeRaw)
                    ).DisplayName();
                }

                var buildingNodes = mainDoc.SelectNodes("//Building[buildingType='Cabin']");
                info.CabinCount = buildingNodes?.Count ?? 0;

                var farmhandNodes = mainDoc.SelectNodes(
                    "//Building[buildingType='Cabin']/indoors/farmhand/name"
                );
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
