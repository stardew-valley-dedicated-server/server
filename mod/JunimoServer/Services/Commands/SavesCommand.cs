using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using JunimoServer.Services.GameCreator;
using JunimoServer.Services.GameManager;
using JunimoServer.Services.SaveImport;
using JunimoServer.Util;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace JunimoServer.Services.Commands;

internal static class SavesCommand
{
    private static IModHelper _helper;
    private static IMonitor _monitor;
    private static SaveImportService _saveImport;

    public static void Register(IModHelper helper, IMonitor monitor, SaveImportService saveImport)
    {
        _helper = helper;
        _monitor = monitor;
        _saveImport = saveImport;

        helper.ConsoleCommands.Add(
            "saves",
            "Save management. Run 'saves' for list, 'saves info <name>', "
                + "'saves import <name> [--swap-host-to <id>] [--reload | --force-reload]', "
                + "'saves reload [--force]'.",
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
            case "reload":
                ReloadCommand(args);
                break;
            default:
                _monitor.Log(
                    $"Unknown saves subcommand: {args[0]}. Use: saves [info|import|reload]",
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
            _monitor.Log(
                "Usage: saves import <saveName> [--swap-host-to <id>] [--reload | --force-reload]",
                LogLevel.Warn
            );
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

        // --force-reload implies --reload. Both are matched case-insensitively, same idiom as
        // --swap-host-to above.
        var forceReload = HasFlag(args, "--force-reload");
        var reload = forceReload || HasFlag(args, "--reload");

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

        // "apply" verb: a reload finalizes/loads in-process; otherwise it's a restart away.
        var applyHint = reload ? "Reloading to apply…" : "Restart the server to load it.";
        var finalizeHint = reload ? "Reloading to finalize…" : "Restart the server to finalize.";

        if (result.RepointedBind)
        {
            _monitor.Log(
                $"Re-pointed the pending host-swap bind for '{saveName}'. {finalizeHint}",
                LogLevel.Info
            );
        }
        else if (result.Swapped)
        {
            _monitor.Log(
                $"Imported '{saveName}' with host swap (former owner "
                    + $"'{result.FormerOwnerName ?? result.FormerOwnerUid.ToString()}' → cabin farmhand "
                    + $"bound to the provided id). {finalizeHint}",
                LogLevel.Info
            );
        }
        else
        {
            _monitor.Log($"Imported '{saveName}' as-is. {applyHint}", LogLevel.Info);
        }

        if (reload)
        {
            // The import already succeeded and is queued for next restart regardless; the reload
            // is a layered, opt-in step on top. A refusal (clients connected, no --force) leaves
            // the queued import intact — it is NOT an import failure.
            TryReloadActiveWorld(
                force: forceReload,
                contextLine: $"applying imported save '{saveName}'"
            );
        }
    }

    /// <summary>
    /// Reloads the active world in-process (no container restart) — for applying a manual save
    /// change without a bounce. <c>--force</c> broadcasts a warning and kicks non-host players first.
    /// </summary>
    private static void ReloadCommand(string[] args)
    {
        var force = HasFlag(args, "--force");
        TryReloadActiveWorld(force, contextLine: "reloading the active world");
    }

    private static bool HasFlag(string[] args, string flag) =>
        Array.Exists(args, a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The single home for the guard + kick + in-process reload orchestration shared by the import
    /// <c>--reload</c>/<c>--force-reload</c> flags and the standalone <c>saves reload</c> subcommand.
    /// Console commands run on a background thread, so the count/kick/reload are marshalled onto the
    /// game thread via a one-shot <see cref="UpdateTickedEventArgs"/> handler (the RenderingCommand
    /// pattern). With clients connected and <paramref name="force"/> false, it refuses and returns —
    /// any queued import stays queued for the next restart.
    /// </summary>
    private static void TryReloadActiveWorld(bool force, string contextLine)
    {
        void Apply(object sender, UpdateTickedEventArgs e)
        {
            _helper.Events.GameLoop.UpdateTicked -= Apply;

            // Wrap the whole body: this runs inside SMAPI's UpdateTicked dispatch on the game thread,
            // and an unhandled throw here would be logged by SMAPI at Error — which trips
            // ServerContainer's ERROR/FATAL test-poison scan (debugging.md). kick / SendPublicMessage /
            // RequestReloadSave's synchronous ExitToTitle could all throw on an edge case. Catch and
            // log at Warn, mirroring GameManagerService.ConditionallyStartGame's reload guard.
            try
            {
                // No world loaded → nothing to reload (e.g. server still at title). Reuse-on-restart
                // path handles a queued import; a standalone reload here would have nothing to act on.
                if (!Game1.hasLoadedGame || Game1.player == null)
                {
                    _monitor.Log(
                        $"No active world loaded — skipping reload ({contextLine}). "
                            + "It will load on the next restart.",
                        LogLevel.Warn
                    );
                    return;
                }

                // Host-excluded, event-safe count (mirrors AlwaysOnFestivals.CountOnlineOtherPlayers):
                // the bare otherFarmers.Count is unreliable during an active event (host-automation.md
                // invariant 7), so count online non-disconnecting non-host farmers.
                var others = Game1
                    .getOnlineFarmers()
                    .Where(f =>
                        f.UniqueMultiplayerID != Game1.player.UniqueMultiplayerID
                        && !Game1.Multiplayer.isDisconnecting(f)
                    )
                    .ToList();

                if (others.Count > 0 && !force)
                {
                    var names = string.Join(", ", others.Select(f => f.Name));
                    _monitor.Log(
                        $"Refusing to reload while {others.Count} player(s) are connected: {names}. "
                            + "Re-run with --force-reload to kick them, or restart when they leave.",
                        LogLevel.Warn
                    );
                    return;
                }

                if (others.Count > 0)
                {
                    _helper.SendPublicMessage(
                        "Server is reloading to apply a save change — reconnect in a moment."
                    );
                    foreach (var f in others)
                    {
                        Game1.server.kick(f.UniqueMultiplayerID);
                    }
                    _monitor.Log(
                        $"Kicked {others.Count} player(s) before reload: "
                            + string.Join(", ", others.Select(f => f.Name)),
                        LogLevel.Info
                    );
                }

                var manager = GameManagerService.Instance;
                if (manager == null)
                {
                    _monitor.Log(
                        "Game manager not ready; cannot reload in-process. Restart to load.",
                        LogLevel.Warn
                    );
                    return;
                }

                _monitor.Log($"Reloading the active world ({contextLine})…", LogLevel.Info);

                // Console commands return void and can't await across the tick boundary, so
                // fire-and-forget with a logged continuation. RequestReloadSave faults its own TCS on a
                // failed LoadSave (GameManagerService.cs:261-264), so no extra timeout is needed. The
                // continuation runs on a thread-pool thread and only touches _monitor.Log (thread-safe).
                manager
                    .RequestReloadSave()
                    .ContinueWith(t =>
                    {
                        if (t.IsFaulted)
                        {
                            _monitor.Log(
                                $"World reload failed: {t.Exception?.GetBaseException().Message}",
                                LogLevel.Warn
                            );
                        }
                        else
                        {
                            _monitor.Log($"World reloaded ({contextLine}).", LogLevel.Info);
                        }
                    });
            }
            catch (Exception ex)
            {
                // Warn, not Error (test-poison). The import (if any) stays queued for next restart.
                _monitor.Log(
                    $"In-process reload failed ({contextLine}): {ex.Message}",
                    LogLevel.Warn
                );
            }
        }

        _helper.Events.GameLoop.UpdateTicked += Apply;
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
