using System;
using System.Linq;
using JunimoServer.Services.CabinManager;
using JunimoServer.Services.GameLoader;
using JunimoServer.Services.PersistentOption;
using JunimoServer.Services.Settings;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.GameData;

namespace JunimoServer.Services.GameCreator;

class GameCreatorService : ModService
{
    private readonly CabinManagerService _cabinManagerService;
    private readonly GameLoaderService _gameLoader;
    private readonly PersistentOptions _options;
    private readonly ServerSettingsLoader _settings;
    private readonly IMonitor _monitor;
    private readonly IModHelper _helper;

    public bool GameIsCreating { get; private set; }

    public GameCreatorService(
        IModHelper helper,
        IMonitor monitor,
        GameLoaderService gameLoader,
        CabinManagerService cabinManagerService,
        PersistentOptions options,
        ServerSettingsLoader settings
    )
    {
        _options = options;
        _settings = settings;
        _gameLoader = gameLoader;
        _monitor = monitor;
        _cabinManagerService = cabinManagerService;
        _helper = helper;
    }

    public bool CreateNewGameFromConfig()
    {
        try
        {
            var config = NewGameConfig.FromSettings(_settings);
            _monitor.Log($"Using config: {config}", LogLevel.Info);

            CreateNewGame(config);
            return true;
        }
        catch (Exception e)
        {
            _monitor.Log(e.ToString(), LogLevel.Error);
            return false;
        }
    }

    /// <summary>
    /// These settings are ONLY applied when creating a new game.
    /// When loading an existing save, the save file contains these values
    /// and they must not be overwritten.
    /// </summary>
    public void CreateNewGame(NewGameConfig config)
    {
        GameIsCreating = true;

        // Both durable stores (persisted options, settings file) are written before the
        // engine builds the world, so a throw anywhere below must restore them: the boot
        // target still points at the OLD save, and because SetPersistentOptions aligns
        // PreviousCabinStrategy, the next boot would otherwise run the old world under the
        // failed game's strategy with no switch even detected — for a stacked→None config
        // that is the unrecoverable hidden-cabins-under-None state.
        var priorOptions = _options.Data;
        var priorApplied = _settings.CaptureAppliedNewGameConfig();
        try
        {
            CreateNewGameCore(config);
        }
        catch
        {
            _options.SetPersistentOptions(priorOptions);
            _settings.RestoreAppliedNewGameConfig(priorApplied);
            throw;
        }
        finally
        {
            GameIsCreating = false;
        }
    }

    private void CreateNewGameCore(NewGameConfig config)
    {
        _options.SetPersistentOptions(
            new PersistentOptionsSaveData
            {
                MaxPlayers = config.MaxPlayers,
                CabinStrategy = config.CabinStrategy,
                ExistingCabinBehavior = _settings.ExistingCabinBehavior,
                AllowCabinRelocation = config.AllowCabinRelocation,
            }
        );

        // Persist the config into server-settings.json before the world loads: the file is
        // the durable source of truth (SyncFromSettings reapplies it on every boot/reload),
        // so any /newgame override must land in it or it silently reverts at the next
        // reload. Must precede setGameMode(3) — SaveLoaded consumers that read the file
        // (IpConnectionService's AllowIpConnections door) apply the created game's values.
        _settings.ApplyNewGameConfig(config);

        Game1.player.team.useSeparateWallets.Value = config.UseSeparateWallets;

        Game1.cabinsSeparate = !config.CabinLayoutNearby;
        Game1.bundleType = config.BundlesRemix
            ? Game1.BundleType.Remixed
            : Game1.BundleType.Default;
        Game1.game1.SetNewGameOption(
            "MineChests",
            config.MinesRemix ? Game1.MineChestType.Remixed : Game1.MineChestType.Default
        );
        Game1.game1.SetNewGameOption("YearOneCompletable", config.CommunityCenterYear1);
        Game1.startingGameSeed = config.RandomSeed;
        Game1.UseLegacyRandom = config.UseLegacyRandom;

        const int dogIndex = 5;
        if (config.PetBreed is >= 0 and <= 9)
        {
            if (config.PetBreed < dogIndex)
            {
                Game1.player.whichPetType = "Cat";
                Game1.player.whichPetBreed = config.PetBreed.ToString();
            }
            else
            {
                Game1.player.whichPetType = "Dog";
                Game1.player.whichPetBreed = (config.PetBreed - dogIndex).ToString();
            }
        }

        // BuildStartingCabins is patched out for every strategy (CabinManagerService) and
        // the mod places all cabins itself below, so the engine never consumes this value
        // headless. Kept in sync with the config for menu/diagnostic reads only.
        Game1.startingCabins = config.StartingCabins;

        var isUltimateFarmModLoaded = _helper
            .ModRegistry.GetAll()
            .Any(mod => mod.Manifest.Name == "Ultimate Farm CP");

        // Resolve the user's requested farm to (whichFarm bucket, modFarm data). The
        // monster-spawn default keys off this requested farm, matching the prior behavior
        // even when Ultimate Farm CP overrides the map below.
        var (whichFarm, modFarm) = ResolveFarmType(config.WhichFarm);

        // Monster spawning: explicit override, else the requested farm's default — vanilla
        // Wilderness (4) or the modded farm's SpawnMonstersByDefault (matches the new-game
        // UI at CharacterCustomization.optionButtonClick).
        Game1.spawnMonstersAtNight =
            config.SpawnMonstersAtNight ?? (modFarm?.SpawnMonstersByDefault ?? whichFarm == 4);

        // Ultimate Farm CP compat: the mod expects whichFarm = Riverland (1) to apply its
        // custom farm map, overriding the requested map (but not the monster default above).
        if (isUltimateFarmModLoaded)
        {
            whichFarm = 1;
            modFarm = null;
        }

        // whichModFarm MUST be set BEFORE loadForNewGame(): the Farm map is created
        // during loadForNewGame() via Farm.getMapNameFromTypeInt(), which consults
        // whichModFarm when whichFarm is 7.
        Game1.whichFarm = whichFarm;
        Game1.whichModFarm = modFarm;

        // Name/favoriteThing/isCustomized are the shared host identity, sourced from
        // ServerFarmerIdentity so the new-game host and the save-import clone-blank host
        // (SaveImportXmlTransform) cannot drift. displayName is a runtime field derived
        // from Name (no serialized display-name field), so it is assigned here, not shared.
        Game1.player.Name = ServerFarmerIdentity.Name;
        Game1.player.displayName = Game1.player.Name;
        Game1.player.favoriteThing.Value = ServerFarmerIdentity.FavoriteThing;
        Game1.player.farmName.Value = config.FarmName;

        Game1.player.isCustomized.Value = ServerFarmerIdentity.IsCustomized;
        Game1.player.ConvertClothingOverrideToClothesItems();

        Game1.multiplayerMode = 2; // Server mode (Game1.IsServer)

        // Restore the id re-roll vanilla's title-screen new-game does in
        // ResetGameStateOnTitleScreen (which we skip), so a /newgame after a /reload
        // doesn't inherit the reloaded save's id and reuse its folder. RandomSeed pins
        // the id via loadForNewGame, so only re-roll when unset. Fresh Random avoids
        // Game1.random (deterministic here). Redraw guards the negligible folder collision.
        if (config.RandomSeed is null)
        {
            var rng = new Random();
            do
            {
                Game1.uniqueIDForThisGame = (ulong)Utility.RandomLong(rng);
            } while (SaveGame.IsNewGameSaveNameCollision(SaveGame.FilterFileName(config.FarmName)));
        }

        // From TitleMenu.createdNewCharacter
        Game1.game1.loadForNewGame();

        // Must be set AFTER loadForNewGame() because it resets difficultyModifier to 1.0
        Game1.player.difficultyModifier = config.ProfitMargin;

        // NOTE: The game has a built-in dedicated host mode (hasDedicatedHost)
        // that activates instant fades, skips end-of-night UI, etc.
        // We deliberately do NOT use it. Our mod handles automation independently.

        Game1.saveOnNewDay = true;
        Game1.player.eventsSeen.Add("60367");
        Game1.player.currentLocation = Utility.getHomeOfFarmer(Game1.player);
        Game1.player.Position = new Vector2(9f, 9f) * 64f;
        Game1.player.isInBed.Value = true;
        _monitor.Log(
            $"[GameCreator] Before NewDay(0f): otherFarmers={Game1.otherFarmers.Count}, gameMode={Game1.gameMode}, newDay={Game1.newDay}",
            LogLevel.Debug
        );
        Game1.NewDay(0f);
        _monitor.Log(
            $"[GameCreator] After NewDay(0f): newDay={Game1.newDay}, fadeToBlack={Game1.fadeToBlack}, showingEndOfNightStuff={Game1.showingEndOfNightStuff}",
            LogLevel.Debug
        );
        Game1.exitActiveMenu();
        Game1.setGameMode(3);

        _gameLoader.SetCurrentGameAsSaveToLoad(config.FarmName);

        // Place the starting cabins through the mod's own path. Vanilla BuildStartingCabins
        // (run during loadForNewGame) does not leave its cabins on the realized farm on the
        // headless path, so for None we place visible cabins here via EnsureAtLeastXCabins →
        // BuildNewCabinVisible (which runs after the farm is fully realized and verifies each
        // interior). Stacked strategies don't use map positions, so they keep the default
        // minimum of 1 hidden cabin.
        //
        // Under None, StartingCabins is ignored: the real player ceiling is
        // min(designated map positions, MaxPlayers), and the farm is empty at creation, so
        // placing the full set now is the only moment it can never bulldoze player content.
        // The count is frozen into NoneCabinCount — a later MaxPlayers raise must not
        // trigger on-demand placement onto a developed farm.
        var minCabins = 1;
        if (_options.IsNone)
        {
            var designatedCount = FarmCabinPositions.GetDesignatedPositions(Game1.getFarm()).Count;
            if (designatedCount == 0)
            {
                // Custom maps only — every vanilla farm map authors both marker sets. With
                // zero positions the cap clamps every build request to 0 with only a Debug
                // line, so without this Warn the operator sees a joinable-looking server
                // that can never place a cabin.
                _monitor.Log(
                    "None strategy on a map with zero designated cabin positions: no cabin "
                        + "can ever be placed, so no player can join. Author cabin markers "
                        + "(Paths layer tiles 29/30 with an Order property) on the map, or "
                        + "use a stacked cabin strategy.",
                    LogLevel.Warn
                );
            }
            minCabins = Math.Min(designatedCount, config.MaxPlayers);
            _options.Data.NoneCabinCount = minCabins;
            _options.Save();
        }
        _cabinManagerService.EnsureAtLeastXCabins(minCabins);
    }

    /// <summary>
    /// Resolves a <see cref="FarmTypeSetting"/> into the game's two-part farm identity:
    /// the <c>whichFarm</c> bucket and the <c>whichModFarm</c> data (null for vanilla).
    /// Accepts, interchangeably: a vanilla index 0-6 or its name ("Standard", "FourCorners",
    /// case/space-insensitive); the index 7 or the Id "MeadowlandsFarm" (both always select
    /// the base-game Meadowlands farm); the keyword "modded" (the first installed mod farm);
    /// or any other Data/AdditionalFarms Id for a specific mod farm. Anything invalid — an
    /// out-of-range index, an unknown Id, or "modded" with no mod farm installed — falls back
    /// to Standard with a warning (a config mistake shouldn't abort game creation).
    /// </summary>
    private (int whichFarm, ModFarmType? modFarm) ResolveFarmType(FarmTypeSetting setting)
    {
        // Normalize the selector to either a vanilla index (0-6) or an AdditionalFarms Id
        // to look up. Index 7 is a permanent alias for Meadowlands' Id (it ships with the
        // game, so it's a built-in 0-7 farm regardless of which mods are installed).
        string? lookupId = null;
        if (setting.IsModded)
        {
            if (setting.IsFirstModFarmKeyword)
            {
                return ResolveFirstModFarm();
            }
            if (FarmTypeSetting.TryGetVanillaIndex(setting.Id!, out var namedIndex))
            {
                return (namedIndex, null);
            }
            lookupId = setting.Id;
        }
        else
        {
            var index = setting.Index ?? 0;
            if (index >= 0 && index < FarmTypeSetting.FirstModdedIndex)
            {
                return (index, null);
            }
            if (index == FarmTypeSetting.MeadowlandsIndex)
            {
                lookupId = FarmTypeSetting.MeadowlandsFarmId;
            }
            else
            {
                _monitor.Log(
                    $"Farm type index {index} is not a vanilla farm (0-{FarmTypeSetting.MeadowlandsIndex}); "
                        + "falling back to Standard. Use a Data/AdditionalFarms Id string to select a mod farm.",
                    LogLevel.Warn
                );
                return (0, null);
            }
        }

        // Resolve the Id against Data/AdditionalFarms (matched exactly, as the game does).
        var additionalFarms = DataLoader.AdditionalFarms(Game1.content);
        var match = additionalFarms?.FirstOrDefault(f => f.Id == lookupId);
        if (match != null)
        {
            return (FarmTypeSetting.FirstModdedIndex, match);
        }

        _monitor.Log(
            $"Farm type '{lookupId}' not found in Data/AdditionalFarms; falling back to Standard farm. "
                + "Check the Id matches the mod's AdditionalFarms entry.",
            LogLevel.Warn
        );
        return (0, null);
    }

    /// <summary>
    /// Resolves the "modded" keyword to the first installed mod farm — the first
    /// Data/AdditionalFarms entry that isn't base-game Meadowlands. Falls back to Standard
    /// with a warning when no mod farm is present.
    /// </summary>
    private (int whichFarm, ModFarmType? modFarm) ResolveFirstModFarm()
    {
        var additionalFarms = DataLoader.AdditionalFarms(Game1.content);
        var modFarm = additionalFarms?.FirstOrDefault(f =>
            f.Id != FarmTypeSetting.MeadowlandsFarmId
        );
        if (modFarm != null)
        {
            _monitor.Log(
                $"Farm type '{FarmTypeSetting.FirstModFarmKeyword}' resolved to mod farm '{modFarm.Id}'.",
                LogLevel.Info
            );
            return (FarmTypeSetting.FirstModdedIndex, modFarm);
        }

        _monitor.Log(
            $"Farm type '{FarmTypeSetting.FirstModFarmKeyword}' was requested but no mod farm is installed "
                + "(Data/AdditionalFarms has only the base-game Meadowlands); falling back to Standard farm.",
            LogLevel.Warn
        );
        return (0, null);
    }
}
