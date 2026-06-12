using System;
using System.Linq;
using JunimoServer.Services.CabinManager;
using JunimoServer.Services.GameCreator;
using JunimoServer.Services.GameLoader;
using JunimoServer.Services.PersistentOption;
using JunimoServer.Services.Settings;
using JunimoServer.Util;
using StardewModdingAPI;
using StardewValley;
using SmapiLogConfig = JunimoServer.Util.SmapiLogConfig;

namespace JunimoServer.Services.Commands;

internal static class SettingsCommand
{
    private static IMonitor _monitor;
    private static GameLoaderService _gameLoader;
    private static PersistentOptions _options;
    private static ServerSettingsLoader _settings;

    public static void Register(
        IModHelper helper,
        IMonitor monitor,
        GameLoaderService gameLoader,
        PersistentOptions options,
        ServerSettingsLoader settings
    )
    {
        _monitor = monitor;
        _gameLoader = gameLoader;
        _options = options;
        _settings = settings;

        helper.ConsoleCommands.Add(
            "settings",
            "Server settings and game creation. Run 'settings' for subcommands.",
            (cmd, args) => HandleCommand(args)
        );
    }

    private static void HandleCommand(string[] args)
    {
        if (args.Length == 0)
        {
            ShowHelp();
            return;
        }

        switch (args[0].ToLowerInvariant())
        {
            case "show":
                ShowConfig();
                break;
            case "newgame":
                HandleNewGame(args.Skip(1).ToArray());
                break;
            case "validate":
                RunValidation();
                break;
            case "verbose":
                HandleVerbose(args.Skip(1).ToArray());
                break;
            default:
                _monitor.Log(
                    $"Unknown subcommand: {args[0]}. Run 'settings' for help.",
                    LogLevel.Warn
                );
                break;
        }
    }

    private static void ShowHelp()
    {
        _monitor.Log("Available subcommands:", LogLevel.Info);
        _monitor.Log(
            "  settings show       -- Show current configuration from server-settings.json",
            LogLevel.Info
        );
        _monitor.Log("  settings newgame    -- Create a new game", LogLevel.Info);
        _monitor.Log(
            "  settings validate   -- Run configuration and state validation",
            LogLevel.Info
        );
        _monitor.Log(
            "  settings verbose    -- Show or set verbose logging [on|off]",
            LogLevel.Info
        );
    }

    #region Show Config

    /// <summary>
    /// Label for the config display: "N - Name" when selected by a built-in index, else the
    /// name/Id. (e.g. "0 - Standard", "7 - Meadowlands", "FrontierFarm").
    /// </summary>
    private static string FarmTypeLabel(FarmTypeSetting farmType) =>
        farmType.Index is { } index
            ? $"{index} - {farmType.DisplayName()}"
            : farmType.DisplayName();

    private static void ShowConfig()
    {
        _monitor.Log("Current Configuration (server-settings.json)", LogLevel.Info);

        _monitor.Log(
            "  -- Game creation settings (immutable after game created) --",
            LogLevel.Info
        );
        _monitor.Log($"  FarmName             = {_settings.FarmName}", LogLevel.Info);

        _monitor.Log(
            $"  FarmType             = {FarmTypeLabel(_settings.FarmType)}",
            LogLevel.Info
        );
        _monitor.Log($"  ProfitMargin         = {_settings.ProfitMargin}", LogLevel.Info);
        _monitor.Log($"  StartingCabins       = {_settings.StartingCabins}", LogLevel.Info);

        var monstersLabel = _settings.SpawnMonstersAtNight.HasValue
            ? _settings.SpawnMonstersAtNight.Value.ToString()
            : "auto";
        _monitor.Log($"  SpawnMonstersAtNight = {monstersLabel}", LogLevel.Info);
        _monitor.Log($"  RemixBundles = {_settings.RemixBundles}", LogLevel.Info);
        _monitor.Log($"  RemixMines = {_settings.RemixMines}", LogLevel.Info);
        _monitor.Log($"  CommunityCenterYear1 = {_settings.CommunityCenterYear1}", LogLevel.Info);
        _monitor.Log($"  CabinLayoutNearby = {_settings.CabinLayoutNearby}", LogLevel.Info);
        _monitor.Log($"  UseLegacyRandom = {_settings.UseLegacyRandom}", LogLevel.Info);
        _monitor.Log($"  RandomSeed = {_settings.RandomSeed}", LogLevel.Info);
        _monitor.Log($"  PetBreed = {_settings.PetBreed}", LogLevel.Info);
        _monitor.Log($"  PetName = {_settings.PetName}", LogLevel.Info);
        _monitor.Log($"  MushroomCave = {_settings.MushroomCave}", LogLevel.Info);
        _monitor.Log($"  BuyJoja = {_settings.BuyJoja}", LogLevel.Info);

        _monitor.Log("  -- Runtime settings (applied on every startup) --", LogLevel.Info);
        _monitor.Log($"  MaxPlayers           = {_settings.MaxPlayers}", LogLevel.Info);

        var strategyMatch =
            _options.PreviousCabinStrategy == _settings.CabinStrategy
                ? ", matches persisted"
                : $", was {_options.PreviousCabinStrategy}";
        _monitor.Log(
            $"  CabinStrategy        = {_settings.CabinStrategy}{strategyMatch}",
            LogLevel.Info
        );
        _monitor.Log($"  SeparateWallets      = {_settings.SeparateWallets}", LogLevel.Info);
        _monitor.Log($"  ExistingCabinBehavior = {_settings.ExistingCabinBehavior}", LogLevel.Info);
        _monitor.Log($"  VerboseLogging       = {_settings.VerboseLogging}", LogLevel.Info);
    }

    #endregion

    #region New Game

    private static void HandleNewGame(string[] args)
    {
        var confirm = args.Any(a => a == "--confirm");

        if (!confirm)
        {
            var config = NewGameConfig.FromSettings(_settings);

            _monitor.Log("New Game Preview (from server-settings.json):", LogLevel.Info);
            _monitor.Log($"  Farm Name:        {config.FarmName}", LogLevel.Info);
            _monitor.Log($"  Farm Type:        {FarmTypeLabel(config.WhichFarm)}", LogLevel.Info);
            _monitor.Log($"  Max Players:      {config.MaxPlayers}", LogLevel.Info);
            _monitor.Log($"  Cabin Strategy:   {config.CabinStrategy}", LogLevel.Info);
            _monitor.Log($"  Separate Wallets: {config.UseSeparateWallets}", LogLevel.Info);
            _monitor.Log($"  Profit Margin:    {config.ProfitMargin}", LogLevel.Info);
            _monitor.Log($"  Starting Cabins:  {config.StartingCabins}", LogLevel.Info);
            _monitor.Log($"", LogLevel.Info);
            _monitor.Log(
                "  WARNING: This will replace the current active save reference!",
                LogLevel.Warn
            );
            _monitor.Log("  Run 'settings newgame --confirm' to proceed.", LogLevel.Info);
            return;
        }

        // Clear active save so next restart creates a new game
        _gameLoader.SetSaveNameToLoad(null);
        _monitor.Log(
            "Active save cleared. A new game will be created on next restart.",
            LogLevel.Info
        );
    }

    #endregion

    #region Validate

    private static void RunValidation()
    {
        _monitor.Log("Running validation...", LogLevel.Info);
        int passed = 0;
        int total = 0;

        total++;
        var farmType = _settings.FarmType;
        // A numeric index outside the built-in 0-7 range can't name a farm (mod farms use a
        // string Id); it resolves to Standard at creation. Everything else — a built-in
        // index/name or a mod Id — is accepted (a mod Id is verified against
        // Data/AdditionalFarms at game creation, with a Standard fallback if missing).
        if (farmType.Index is { } idx && (idx < 0 || idx > FarmTypeSetting.MeadowlandsIndex))
        {
            _monitor.Log(
                $"  [WARN] FarmType={idx} is not a built-in farm (0-{FarmTypeSetting.MeadowlandsIndex}); will use Standard. Use a mod farm's Id string instead.",
                LogLevel.Warn
            );
            passed++;
        }
        else
        {
            _monitor.Log($"  [PASS] FarmType={FarmTypeLabel(farmType)}", LogLevel.Info);
            passed++;
        }

        // MaxPlayers range
        total++;
        if (_settings.MaxPlayers >= 1 && _settings.MaxPlayers <= 100)
        {
            _monitor.Log(
                $"  [PASS] MaxPlayers={_settings.MaxPlayers} in range [1..100]",
                LogLevel.Info
            );
            passed++;
        }
        else
        {
            _monitor.Log(
                $"  [FAIL] MaxPlayers={_settings.MaxPlayers} out of range [1..100]",
                LogLevel.Error
            );
        }

        // CabinStrategy valid
        total++;
        if (Enum.IsDefined(typeof(CabinStrategy), _settings.CabinStrategy))
        {
            _monitor.Log(
                $"  [PASS] CabinStrategy={_settings.CabinStrategy} is valid",
                LogLevel.Info
            );
            passed++;
        }
        else
        {
            _monitor.Log(
                $"  [FAIL] CabinStrategy={_settings.CabinStrategy} is invalid",
                LogLevel.Error
            );
        }

        // ExistingCabinBehavior valid
        total++;
        if (Enum.IsDefined(typeof(ExistingCabinBehavior), _settings.ExistingCabinBehavior))
        {
            _monitor.Log(
                $"  [PASS] ExistingCabinBehavior={_settings.ExistingCabinBehavior} is valid",
                LogLevel.Info
            );
            passed++;
        }
        else
        {
            _monitor.Log(
                $"  [FAIL] ExistingCabinBehavior={_settings.ExistingCabinBehavior} is invalid",
                LogLevel.Error
            );
        }

        // ProfitMargin range
        total++;
        if (_settings.ProfitMargin >= 0.25f && _settings.ProfitMargin <= 1.0f)
        {
            _monitor.Log(
                $"  [PASS] ProfitMargin={_settings.ProfitMargin} in range [0.25..1.0]",
                LogLevel.Info
            );
            passed++;
        }
        else
        {
            _monitor.Log(
                $"  [FAIL] ProfitMargin={_settings.ProfitMargin} out of range [0.25..1.0]",
                LogLevel.Error
            );
        }

        // Active save exists
        total++;
        if (Game1.hasLoadedGame)
        {
            var saveName = Constants.SaveFolderName;
            _monitor.Log($"  [PASS] Active save '{saveName}' is loaded", LogLevel.Info);
            passed++;

            // Cabin consistency
            total++;
            var farm = Game1.getFarm();
            if (farm != null)
            {
                var cabinCount = farm.buildings.Count(b => b.isCabin);
                var hiddenCount = farm.buildings.Count(b => b.isCabin && b.IsInHiddenStack());
                var lobbyCount = farm.buildings.Count(b => b.isCabin && b.IsLobbyOrEditing());
                var visibleCount = cabinCount - hiddenCount - lobbyCount;

                if (_options.IsNone && hiddenCount == 0)
                {
                    var lobbyNote = lobbyCount > 0 ? $", {lobbyCount} lobby/editing" : "";
                    _monitor.Log(
                        $"  [PASS] {cabinCount} cabins, {visibleCount} visible{lobbyNote} (consistent with None strategy)",
                        LogLevel.Info
                    );
                    passed++;
                }
                else if (_options.UsesHiddenCabins)
                {
                    var lobbyNote = lobbyCount > 0 ? $", {lobbyCount} lobby/editing" : "";
                    _monitor.Log(
                        $"  [PASS] {cabinCount} cabins ({hiddenCount} hidden, {visibleCount} visible{lobbyNote}, strategy: {_options.Data.CabinStrategy})",
                        LogLevel.Info
                    );
                    passed++;
                }
                else
                {
                    _monitor.Log(
                        $"  [WARN] {cabinCount} cabins ({hiddenCount} hidden, {visibleCount} visible), may need migration",
                        LogLevel.Warn
                    );
                    passed++;
                }
            }
            else
            {
                _monitor.Log($"  [FAIL] Could not access farm data", LogLevel.Error);
            }
        }
        else
        {
            _monitor.Log($"  [WARN] No game currently loaded", LogLevel.Warn);
        }

        _monitor.Log($"  {passed}/{total} checks passed.", LogLevel.Info);
    }

    #endregion

    #region Verbose Logging

    private static void HandleVerbose(string[] args)
    {
        if (args.Length == 0)
        {
            _monitor.Log($"VerboseLogging = {_settings.VerboseLogging}", LogLevel.Info);
            _monitor.Log("Usage: settings verbose [on|off]", LogLevel.Info);
            return;
        }

        var value = args[0].ToLowerInvariant();
        bool? newValue = value switch
        {
            "on" or "true" or "1" => true,
            "off" or "false" or "0" => false,
            _ => null,
        };

        if (newValue == null)
        {
            _monitor.Log($"Invalid value: {args[0]}. Use 'on' or 'off'.", LogLevel.Warn);
            return;
        }

        // Update settings and persist
        _settings.SetVerboseLogging(newValue.Value);

        // Apply to SMAPI immediately
        SmapiLogConfig.SetVerboseLogging("JunimoHost.Server", newValue.Value, _monitor);

        _monitor.Log($"VerboseLogging set to {newValue.Value} (saved to config)", LogLevel.Info);
    }

    #endregion
}
