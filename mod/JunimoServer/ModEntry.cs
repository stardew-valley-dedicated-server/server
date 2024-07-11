using Grpc.Net.Client;
using HarmonyLib;
using Junimohost.Stardewgame.V1;
using JunimoServer.Services.AlwaysOnServer;
using JunimoServer.Services.CabinManager;
using JunimoServer.Services.ChatCommands;
using JunimoServer.Services.Commands;
using JunimoServer.Services.CropSaver;
using JunimoServer.Services.GameCreator;
using JunimoServer.Services.GameLoader;
using JunimoServer.Services.GameTweaks;
using JunimoServer.Services.HostAutomation;
using JunimoServer.Services.Map;
using JunimoServer.Services.NetworkTweaks;
using JunimoServer.Services.PersistentOption;
using JunimoServer.Services.Roles;
using JunimoServer.Services.ServerOptim;
using JunimoServer.Util;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Menus;
using Steamworks;
using System;
using System.Net.Http;

namespace JunimoServer
{
    internal class ModEntry : Mod
    {
        private static readonly bool EnableModIncompatibleOptimizations =
            bool.Parse(Environment.GetEnvironmentVariable("ENABLE_MOD_INCOMPATIBLE_OPTIMIZATIONS") ?? "true");

        private static readonly string JunimoBootServerAddress =
            Environment.GetEnvironmentVariable("BACKEND_HOSTPORT") ?? "";

        private static readonly int HealthCheckSeconds =
            Int32.Parse(Environment.GetEnvironmentVariable("HEALTH_CHECK_SECONDS") ?? "300");

        private static readonly bool DisableRendering =
            bool.Parse(Environment.GetEnvironmentVariable("DISABLE_RENDERING") ?? "true");

        private static readonly bool ForceNewDebugGame =
            bool.Parse(Environment.GetEnvironmentVariable("FORCE_NEW_DEBUG_GAME") ?? "false");

        // TODO: Once web UI is available, add info to docs about how "host.docker.internal:3000" can be used to connect to local web UI (dev mode)
        private static readonly string WebSocketServerAddress =
            Environment.GetEnvironmentVariable("WEB_SOCKET_SERVER_ADDRESS") ?? "stardew-dedicated-web:3000";

        private GameCreatorService _gameCreatorService;
        private GameLoaderService _gameLoaderService;
        private ServerOptimizer _serverOptimizer;

        private bool _titleLaunched = false;
        private bool _gameStarted = false;
        private int _healthCheckTimer = 0;
        private DateTime? _lastNullCodeTime = null;

        // private WebSocketClient _webSocketClient;

        public override void Entry(IModHelper helper)
        {
            // TODO: What *exactly* does this do, and should it really be hardcoded?
            Program.enableCheats = true;
            Game1.options.pauseWhenOutOfFocus = false;

            var harmony = new Harmony(this.ModManifest.UniqueID);
            var options = new PersistentOptions(helper);

            // Register services
            var chatCommands = new ChatCommands(Monitor, harmony, helper);
            var alwaysOnConfig = new AlwaysOnConfig();
            var alwaysOnServer = new AlwaysOnServer(helper, Monitor, chatCommands, alwaysOnConfig);
            _gameLoaderService = new GameLoaderService(helper, Monitor);
            var cabinManager = new CabinManagerService(helper, Monitor, harmony, options);
            _gameCreatorService = new GameCreatorService(_gameLoaderService, options, Monitor, cabinManager, helper);
            
            var cropSaver = new CropSaver(helper, harmony, Monitor);
            var gameTweaker = new GameTweaker(helper);
            var networkTweaker = new NetworkTweaker(helper, Monitor, options);
            var desyncKicker = new DesyncKicker(helper, Monitor);
            _serverOptimizer = new ServerOptimizer(harmony, Monitor, helper, DisableRendering, EnableModIncompatibleOptimizations);

            // TODO: Add once web UI is available
            // _webSocketClient = new WebSocketClient($"ws://{WebSocketServerAddress}/api/websocket");
            // var mapService = new MapService(helper, Monitor, _webSocketClient);

            // TODO: Backup service needs to be refactored, removed due to gRPC and hardcoded google cloud storage under the hood
            //var backupService = new BackupService(Monitor);
            //var backupScheduler = new BackupScheduler(helper, backupService, Monitor);

            // Host automation (hiding/teleporting host player)
            var hostBot = new HostBot(helper, Monitor);

            // Register custom chat commands
            var roleService = new RoleService(helper);
            CabinCommand.Register(helper, chatCommands, options, Monitor);
            RoleCommands.Register(helper, chatCommands, roleService);
            BanCommand.Register(helper, chatCommands, roleService);
            KickCommand.Register(helper, chatCommands, roleService);
            ListAdminsCommand.Register(helper, chatCommands, roleService);
            ListBansCommand.Register(helper, chatCommands, roleService);
            UnbanCommand.Register(helper, chatCommands, roleService);
            ChangeWalletCommand.Register(helper, chatCommands, roleService);
            JojaCommand.Register(helper, chatCommands, roleService, alwaysOnConfig);
            ConsoleCommand.Register(helper, chatCommands, roleService);

            helper.Events.Display.RenderedActiveMenu += OnRenderedActiveMenu;
            helper.Events.GameLoop.OneSecondUpdateTicked += OnOneSecondTicked;
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

            if (ForceNewDebugGame)
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

            _healthCheckTimer = HealthCheckSeconds;

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
                Monitor.Log("Waiting for server to not be null", LogLevel.Info);
            }
        }

        private void SendHealthCheck(string inviteCode)
        {
            // TODO: There is no backend to update anymore; add again once web UI is available
            //if (JunimoBootServerAddress == "") return;

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
            //    var junimoBootGenChannel = GrpcChannel.ForAddress($"http://{JunimoBootServerAddress}");
            //    _stardewGameServiceClient = new StardewGameService.StardewGameServiceClient(junimoBootGenChannel);
            //}
        }
    }
}