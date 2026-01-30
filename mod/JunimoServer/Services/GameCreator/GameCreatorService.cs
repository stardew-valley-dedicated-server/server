using JunimoServer.Services.CabinManager;
using JunimoServer.Services.GameLoader;
using JunimoServer.Services.PersistentOption;
using JunimoServer.Services.Settings;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using System;
using System.Linq;
using System.Threading;

namespace JunimoServer.Services.GameCreator
{
    class GameCreatorService : ModService
    {
        private readonly CabinManagerService _cabinManagerService;
        private readonly GameLoaderService _gameLoader;
        private static readonly Mutex CreateGameMutex = new Mutex();
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
            CreateGameMutex.WaitOne();
            GameIsCreating = true;

            _options.SetPersistentOptions(new PersistentOptionsSaveData
            {
                MaxPlayers = config.MaxPlayers,
                CabinStrategy = config.CabinStrategy,
                ExistingCabinBehavior = _settings.ExistingCabinBehavior,
            });

            Game1.player.team.useSeparateWallets.Value = config.UseSeparateWallets;

            // For CabinStack/FarmhouseStack: BuildStartingCabins is patched out, so this value
            // is unused — we create cabins ourselves at the hidden location.
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
            }
            else
            {
                Game1.whichFarm = config.WhichFarm;
            }

            // Monster spawning: explicit override or auto-detect from farm type
            Game1.spawnMonstersAtNight = config.SpawnMonstersAtNight ?? (config.WhichFarm == 4);

            // Dedicated server always uses cat as the farm pet
            Game1.player.whichPetType = "Cat";

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
            // We deliberately do NOT use it — our mod handles automation independently.

            Game1.saveOnNewDay = true;
            Game1.player.eventsSeen.Add("60367");
            Game1.player.currentLocation = Utility.getHomeOfFarmer(Game1.player);
            Game1.player.Position = new Vector2(9f, 9f) * 64f;
            Game1.player.isInBed.Value = true;
            Game1.NewDay(0f);
            Game1.exitActiveMenu();
            Game1.setGameMode(3);

            _gameLoader.SetCurrentGameAsSaveToLoad(config.FarmName);

            _cabinManagerService.EnsureAtLeastXCabins();

            GameIsCreating = false;
            CreateGameMutex.ReleaseMutex();
        }
    }
}
