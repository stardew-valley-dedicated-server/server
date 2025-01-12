using JunimoServer.Services.CabinManager;
using JunimoServer.Services.GameLoader;
using JunimoServer.Services.PersistentOption;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace JunimoServer.Services.GameCreator
{
    class GameCreatorService : ModService
    {
        private readonly CabinManagerService _cabinManagerService;
        private readonly GameLoaderService _gameLoader;
        private static readonly Mutex CreateGameMutex = new Mutex();
        private readonly PersistentOptions _options;
        private readonly IMonitor _monitor;
        private readonly IModHelper _helper;

        public bool GameIsCreating { get; private set; }

        public GameCreatorService(IModHelper helper, IMonitor monitor, GameLoaderService gameLoader, CabinManagerService cabinManagerService, PersistentOptions options)
        {
            _options = options;
            _gameLoader = gameLoader;
            _monitor = monitor;
            _cabinManagerService = cabinManagerService;
            _helper = helper;
        }

        public async Task<NewGameConfig> GetConfig()
        {
            // TODO: Make this configurable again
            NewGameConfig config = new NewGameConfig()
            {
                WhichFarm = 0,
                UseSeparateWallets = false,
                StartingCabins = 1,
                CatPerson = false,
                FarmName = "Test",
                MaxPlayers = 10,
                CabinStrategy = 0,
            };

            _monitor.Log($"Using config: {config}", LogLevel.Info);

            return await Task.Run(() => { return config; });
        }

        public bool CreateNewGameFromConfig()
        {
            try
            {
                var configTask = GetConfig();
                configTask.Wait();
                var config = configTask.Result;

                CreateNewGame(config);
                return true;
            }
            catch (Exception e)
            {
                _monitor.Log(e.ToString(), LogLevel.Error);
                return false;
            }
        }

        public void CreateNewGame(NewGameConfig config)
        {
            CreateGameMutex.WaitOne(); //prevent trying to start new game while in the middle of creating a game
            GameIsCreating = true;

            _options.SetPersistentOptions(new PersistentOptionsSaveData
            {
                MaxPlayers = config.MaxPlayers,
                CabinStrategy = (CabinStrategy)config.CabinStrategy
            });


            Game1.player.team.useSeparateWallets.Value = config.UseSeparateWallets;
            // TODO: Should be fine as hardcoded value, cabins are supposed to be built on the fly when players join
            Game1.startingCabins = 1;

            // Ultimate Farm CP compat
            var isUltimateFarmModLoaded = _helper.ModRegistry.GetAll().Any(mod => mod.Manifest.Name == "Ultimate Farm CP");
            if (isUltimateFarmModLoaded)
            {
                // Ultimate Farm CP expects riverland == 1
                Game1.whichFarm = 1;
            }
            else
            {
                Game1.whichFarm = config.WhichFarm;
            }

            var isWildernessFarm = config.WhichFarm == 4;
            Game1.spawnMonstersAtNight = isWildernessFarm;

            if (config.CatPerson)
            {
                Game1.player.whichPetType = "Cat";
            }

            Game1.player.Name = "Server";
            Game1.player.displayName = Game1.player.Name;
            Game1.player.favoriteThing.Value = "Junimos";
            Game1.player.farmName.Value = config.FarmName;

            Game1.player.isCustomized.Value = true;
            Game1.player.ConvertClothingOverrideToClothesItems();

            Game1.multiplayerMode = 2; // multiplayer enabled

            // From TitleMenu.createdNewCharacter
            Game1.game1.loadForNewGame();
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
