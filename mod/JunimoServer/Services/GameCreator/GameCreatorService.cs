using JunimoServer.Services.CabinManager;
using JunimoServer.Services.GameLoader;
using JunimoServer.Services.PersistentOption;
using JunimoServer.Services.Settings;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using System;
using System.Linq;

namespace JunimoServer.Services.GameCreator
{
    class GameCreatorService : ModService
    {
        private readonly CabinManagerService _cabinManagerService;
        private readonly GameLoaderService _gameLoader;
        private readonly PersistentOptions _options;
        private readonly ServerSettingsLoader _settings;
        private readonly IMonitor _monitor;
        private readonly IModHelper _helper;

        public bool GameIsCreating { get; private set; }

        public GameCreatorService(IModHelper helper, IMonitor monitor, GameLoaderService gameLoader, CabinManagerService cabinManagerService, PersistentOptions options, ServerSettingsLoader settings)
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

            _options.SetPersistentOptions(new PersistentOptionsSaveData
            {
                MaxPlayers = config.MaxPlayers,
                CabinStrategy = config.CabinStrategy,
                ExistingCabinBehavior = _settings.ExistingCabinBehavior,
            });

            Game1.player.team.useSeparateWallets.Value = config.UseSeparateWallets;

            Game1.cabinsSeparate = !config.CabinLayoutNearby;
            Game1.bundleType = config.BundlesRemix ? Game1.BundleType.Remixed : Game1.BundleType.Default;
            Game1.game1.SetNewGameOption("MineChests",  config.MinesRemix ? Game1.MineChestType.Remixed : Game1.MineChestType.Default);
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

            // For CabinStack/FarmhouseStack: BuildStartingCabins is patched out, so this value
            // is unused; we create cabins ourselves at the hidden location.
            // For None strategy: this controls how many vanilla cabins are placed at
            // map-designated positions by the unpatched BuildStartingCabins.
            Game1.startingCabins = config.StartingCabins;

            // Ultimate Farm CP compat: the mod expects whichFarm to be set to Riverland (1)
            // to correctly apply its custom farm map. Override the user's farm type setting.
            var isUltimateFarmModLoaded = _helper.ModRegistry.GetAll()
                .Any(mod => mod.Manifest.Name == "Ultimate Farm CP");
            if (isUltimateFarmModLoaded)
            {
                Game1.whichFarm = 1;
                Game1.whichModFarm = null;
            }
            else
            {
                Game1.whichFarm = config.WhichFarm;

                // Farm type 7 (Meadowlands) uses the AdditionalFarms system.
                // whichModFarm MUST be set BEFORE loadForNewGame() because the Farm map
                // is created during loadForNewGame() using Farm.getMapNameFromTypeInt()
                // which checks whichModFarm when whichFarm is 7.
                if (config.WhichFarm == 7)
                {
                    var additionalFarms = DataLoader.AdditionalFarms(Game1.content);
                    Game1.whichModFarm = additionalFarms?.FirstOrDefault(f => f.Id == "MeadowlandsFarm");

                    if (Game1.whichModFarm == null)
                    {
                        _monitor.Log("Could not find MeadowlandsFarm data, falling back to Standard farm", LogLevel.Warn);
                        Game1.whichFarm = 0;
                    }
                }
                else
                {
                    Game1.whichModFarm = null;
                }
            }

            // Monster spawning: explicit override or auto-detect from farm type
            Game1.spawnMonstersAtNight = config.SpawnMonstersAtNight ?? (config.WhichFarm == 4);

            Game1.player.Name = "Server";
            Game1.player.displayName = Game1.player.Name;
            Game1.player.favoriteThing.Value = "Junimos";
            Game1.player.farmName.Value = config.FarmName;

            Game1.player.isCustomized.Value = true;
            Game1.player.ConvertClothingOverrideToClothesItems();

            Game1.multiplayerMode = 2; // Server mode (Game1.IsServer)

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
            _monitor.Log($"[GameCreator] Before NewDay(0f): otherFarmers={Game1.otherFarmers.Count}, gameMode={Game1.gameMode}, newDay={Game1.newDay}", LogLevel.Debug);
            Game1.NewDay(0f);
            _monitor.Log($"[GameCreator] After NewDay(0f): newDay={Game1.newDay}, fadeToBlack={Game1.fadeToBlack}, showingEndOfNightStuff={Game1.showingEndOfNightStuff}", LogLevel.Debug);
            Game1.exitActiveMenu();
            Game1.setGameMode(3);

            _gameLoader.SetCurrentGameAsSaveToLoad(config.FarmName);

            // Place the starting cabins through the mod's own path. Vanilla BuildStartingCabins
            // (run during loadForNewGame) does not leave its cabins on the realized farm on the
            // headless path, so for None we place config.StartingCabins visible cabins here via
            // EnsureAtLeastXCabins → BuildNewCabinVisible (which runs after the farm is fully
            // realized and verifies each interior). Stacked strategies don't use map positions,
            // so they keep the default minimum of 1 hidden cabin.
            var minCabins = _options.IsNone ? Math.Max(1, config.StartingCabins) : 1;
            _cabinManagerService.EnsureAtLeastXCabins(minCabins);

            GameIsCreating = false;
        }
    }
}
