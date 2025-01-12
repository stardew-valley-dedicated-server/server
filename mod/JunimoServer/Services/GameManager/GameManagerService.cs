using JunimoServer.Services.GameCreator;
using JunimoServer.Services.GameLoader;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Menus;
using System;

namespace JunimoServer.Services.GameManager
{
    class GameManagerService : ModService
    {
        private readonly GameCreatorService _gameCreatorService;
        private readonly GameLoaderService _gameLoaderService;

        private bool _titleLaunched = false;
        private bool _gameStarted = false;
        private int _healthCheckTimer = 0;
        private DateTime? _lastNullCodeTime = null;

        public GameManagerService(GameCreatorService gameCreator, GameLoaderService gameLoader, IModHelper helper, IMonitor monitor) : base(helper, monitor)
        {
            _gameCreatorService = gameCreator;
            _gameLoaderService = gameLoader;
        }

        public override void Entry()
        {
            // Unsubscribe first to avoid duplicate bindings
            Helper.Events.Display.RenderedActiveMenu -= OnRenderedActiveMenu;
            Helper.Events.GameLoop.OneSecondUpdateTicked -= OnOneSecondTicked;

            // Subscribe to the events
            Helper.Events.Display.RenderedActiveMenu += OnRenderedActiveMenu;
            Helper.Events.GameLoop.OneSecondUpdateTicked += OnOneSecondTicked;
        }

        private void OnOneSecondTicked(object sender, OneSecondUpdateTickedEventArgs e)
        {
            RunHealthCheck();
            ConditionallyStartGame();
        }

        private void OnRenderedActiveMenu(object sender, RenderedActiveMenuEventArgs e)
        {
            if (Game1.activeClickableMenu is TitleMenu && !_titleLaunched)
            {
                _titleLaunched = true;
            }
        }

        private void ConditionallyStartGame()
        {
            if (_gameStarted)
            {
                return;
            }

            if (!_titleLaunched)
            {
                return;
            }

            _gameStarted = true;

            if (Env.ForceNewDebugGame)
            {
                var config = new NewGameConfig
                {
                    WhichFarm = 0,
                    MaxPlayers = 4,
                };
                _gameCreatorService.CreateNewGame(config);
                return;
            }

            var successfullyStarted = true;
            if (_gameLoaderService.HasLoadableSave())
            {
                successfullyStarted = _gameLoaderService.LoadSave();
            }
            else
            {
                successfullyStarted = _gameCreatorService.CreateNewGameFromConfig();
            }

            try
            {
                // TODO: There is no backend to update anymore; add again once web UI is available
                //if (successfullyStarted)
                //{
                //    var updateTask = _daemonService.UpdateConnectableStatus();
                //    updateTask.Wait();
                //}
                //else
                //{
                //    var updateTask = _daemonService.UpdateNotConnectableStatus();
                //    updateTask.Wait();
                //}
            }
            catch (Exception e)
            {
                Monitor.Log(e.ToString(), LogLevel.Error);
            }
        }

        private bool HasDurationPassedSinceLastNullCode(TimeSpan duration)
        {
            return _lastNullCodeTime.HasValue && (DateTime.Now - _lastNullCodeTime.Value) >= duration;
        }

        private void RunHealthCheck()
        {
            if (_healthCheckTimer > 0)
            {
                _healthCheckTimer--;
                return;
            }

            _healthCheckTimer = Env.HealthCheckSeconds;

            if (Game1.server != null)
            {
                if (Game1.server.canAcceptIPConnections())
                {
                    Monitor.Log("Healthcheck ✓", LogLevel.Info);
                }
                else
                {
                    Monitor.Log("Healthcheck ✗", LogLevel.Info);
                    _lastNullCodeTime ??= DateTime.Now;

                    if (HasDurationPassedSinceLastNullCode(TimeSpan.FromMinutes(2)))
                    {
                        Environment.Exit(0);
                    }
                }
            }
            else
            {
                Monitor.Log("Healthcheck ✗", LogLevel.Info);
            }
        }

        private void SendHealthCheck(string inviteCode)
        {
            // TODO: There is no backend to update anymore; add again once web UI is available
            //if (Env.JunimoBootServerAddress == "") return;

            //try
            //{
            //    await _stardewGameServiceClient.GameHealthCheckAsync(new GameHealthCheckRequest
            //    {
            //        InviteCode = inviteCode,
            //        IsConnectable = true,
            //        ServerId = ServerId,
            //    });
            //}
            //catch (Exception e)
            //{
            //    Monitor.Log("Failed to send health check: " + e.Message, LogLevel.Error);

            //    // Manually retry connection
            //    var junimoBootGenChannel = GrpcChannel.ForAddress($"http://{Env.JunimoBootServerAddress}");
            //    _stardewGameServiceClient = new StardewGameService.StardewGameServiceClient(junimoBootGenChannel);
            //}
        }
    }
}
