using Grpc.Net.Client;
using HarmonyLib;
using Junimohost.Stardewgame.V1;
using JunimoServer.Services.AlwaysOnServer;
using JunimoServer.Services.CabinManager;
using JunimoServer.Services.ChatCommands;
using JunimoServer.Services.Commands;
using JunimoServer.Services.Daemon;
using JunimoServer.Services.GameCreator;
using JunimoServer.Services.GameLoader;
using JunimoServer.Services.HostAutomation;
using JunimoServer.Services.Map;
using JunimoServer.Services.PersistentOption;
using JunimoServer.Services.Roles;
using JunimoServer.Services.ServerOptim;
using JunimoServer.Util;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Menus;
using System;
using System.Net.Http;

namespace JunimoServer
{
    internal class ModEntry : Mod
    {
        private static readonly bool SteamAuthEnabled =
            bool.Parse(Environment.GetEnvironmentVariable("STEAM_AUTH_ENABLED") ?? "false");

        private static readonly bool EnableModIncompatibleOptimizations =
            bool.Parse(Environment.GetEnvironmentVariable("ENABLE_MOD_INCOMPATIBLE_OPTIMIZATIONS") ?? "true");

        private static readonly string SteamAuthServerAddress =
            Environment.GetEnvironmentVariable("STEAM_AUTH_IP_PORT") ?? "localhost:50053";

        private static readonly string JunimoBootServerAddress =
            Environment.GetEnvironmentVariable("BACKEND_HOSTPORT") ?? "";

        private static readonly int HealthCheckSeconds =
            Int32.Parse(Environment.GetEnvironmentVariable("HEALTH_CHECK_SECONDS") ?? "15");

        private static readonly string ServerId =
            Environment.GetEnvironmentVariable("SERVER_ID") ?? "test";

        private static readonly string DaemonPort = Environment.GetEnvironmentVariable("DAEMON_HTTP_PORT") ?? "8080";

        private static readonly bool DisableRendering =
            bool.Parse(Environment.GetEnvironmentVariable("DISABLE_RENDERING") ?? "true");

        private static readonly bool ForceNewDebugGame =
            bool.Parse(Environment.GetEnvironmentVariable("FORCE_NEW_DEBUG_GAME") ?? "false");

        private GameCreatorService _gameCreatorService;
        private GameLoaderService _gameLoaderService;
        private ServerOptimizer _serverOptimizer;
        private DaemonService _daemonService;
        private StardewGameService.StardewGameServiceClient _stardewGameServiceClient;

        private bool _titleLaunched = false;
        private bool _gameStarted = false;
        private int _healthCheckTimer = 0;
        private DateTime? _lastNullCodeTime = null;

        private WebSocketClient _webSocketClient;

        public override void Entry(IModHelper helper)
        {
            Program.enableCheats = true;
            Game1.options.pauseWhenOutOfFocus = false;

            var harmony = new Harmony(this.ModManifest.UniqueID);
            var daemonHttpClient = new HttpClient();
            daemonHttpClient.BaseAddress = new Uri($"http://localhost:{DaemonPort}");

            var options = new PersistentOptions(helper);

            // Core Services: Necessary for core dedicated server functionality
            var chatCommands = new ChatCommands(Monitor, harmony, helper);
            var alwaysOnConfig = new AlwaysOnConfig();
            var alwaysOnServer = new AlwaysOnServer(helper, Monitor, chatCommands, alwaysOnConfig);
            _gameLoaderService = new GameLoaderService(helper, Monitor);
            _daemonService = new DaemonService(daemonHttpClient, Monitor);

            var cabinManager = new CabinManagerService(helper, Monitor, harmony, options);
            _gameCreatorService = new GameCreatorService(_gameLoaderService, options, Monitor, _daemonService, cabinManager, helper);
            //var cropSaver = new CropSaver(helper, harmony, Monitor);
            //var gameTweaker = new GameTweaker(helper);
            //var networkTweaker = new NetworkTweaker(helper, options);
            //var desyncKicker = new DesyncKicker(helper, Monitor);
            //_serverOptimizer = new ServerOptimizer(harmony, Monitor, helper, DisableRendering, EnableModIncompatibleOptimizations);


            _webSocketClient = new WebSocketClient("ws://host.docker.internal:3000/api/websocket");
            //foo = new Foo("ws://stardew-dedicated-web:3000/api/websocket");
            var mapService = new MapService(helper, Monitor, _webSocketClient);

            //foo = new Foo("ws://stardew-dedicated-server:3000");
            // TODO: Backup service needs to be refactored, currently hardcoded to use google cloud storage??
            //var backupService = new BackupService(daemonHttpClient, Monitor);
            //var backupScheduler = new BackupScheduler(helper, backupService, Monitor);

            // TODO: Low prio, but needs to be understood and fixed up
            //var debrisOptimizer = new DebrisOptimizer(harmony, Monitor, helper);

            // TODO: No idea what this SteamAuthServer is, maybe needed to get invite codes working
            //if (SteamAuthEnabled)
            //{
            //    var steamTicketGenChannel = GrpcChannel.ForAddress($"http://{SteamAuthServerAddress}");
            //    var steamTicketGenClient =
            //        new StardewSteamAuthService.StardewSteamAuthServiceClient(steamTicketGenChannel);
            //    var galaxyAuthService = new GalaxyAuthService(Monitor, helper, harmony, steamTicketGenClient);
            //}

            // TODO: We don't have the backend src yet, see what to do with this
            //if (JunimoBootServerAddress != "")
            //{
            //    var junimoBootGenChannel = GrpcChannel.ForAddress($"http://{JunimoBootServerAddress}");
            //    _stardewGameServiceClient = new StardewGameService.StardewGameServiceClient(junimoBootGenChannel);
            //}

            // Host automation (hiding/teleporting host player)
            var hostBot = new HostBot(helper, Monitor);

            // Register custom commands
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

        //private void SavePng(string name, Texture2D texture)
        //{
        //    string filename = $"region_{textureIndex}.png";
        //    Monitor.Log($"Exporting region base texture: {filename} ({texture.Name})", LogLevel.Info);
        //    Helper.Data.WritePngFile(filename, texture);
        //}

        private void ConditionallyStartGame()

        {
            if (_gameStarted) return;

            var isNetworkingReady = Helper.IsNetworkingReady();
            var networkingIsNeededButNotReady = SteamAuthEnabled && !isNetworkingReady;
            if (!_titleLaunched || networkingIsNeededButNotReady) return;
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
                successfullyStarted = _gameCreatorService.CreateNewGameFromDaemonConfig();
            }

            try
            {
                if (successfullyStarted)
                {
                    var updateTask = _daemonService.UpdateConnectableStatus();
                    updateTask.Wait();
                }
                else
                {
                    var updateTask = _daemonService.UpdateNotConnectableStatus();
                    updateTask.Wait();
                }
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
                // TODO: Figure out how to get GOG/Galaxy/Steamauth working properly, galaxy auth fails and we have no lobby/invite code - only works via IP:PORT
                //if (Game1.server.getInviteCode() != null)
                //{
                //    // Monitor.Log(Game1.server.getInviteCode(), LogLevel.Info);
                //    Task.Run(() => { SendHealthCheck(Game1.server.getInviteCode()); });
                //}
                //else
                //{
                //    Monitor.Log("Invite code is null", LogLevel.Info);
                //    Monitor.Log("canOfferInvite" + (Game1.server.canOfferInvite() ? "Yes" : "No"), LogLevel.Info);
                //    Monitor.Log("canAcceptIPConnections" + (Game1.server.canAcceptIPConnections() ? "Yes" : "No"), LogLevel.Info);
                //    Monitor.Log("_gameStarted" + (_gameStarted ? "Yes" : "No"), LogLevel.Info);
                //    Monitor.Log("_titleLaunched" + (_titleLaunched ? "Yes" : "No"), LogLevel.Info);
                //    _lastNullCodeTime ??= DateTime.Now;

                //    if (HasDurationPassedSinceLastNullCode(TimeSpan.FromMinutes(2)))
                //    {
                //        Environment.Exit(0); //let kubernetes restart the pod.
                //    }
                //}

                if (Game1.server.canOfferInvite() && Game1.server.canAcceptIPConnections())
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

        private async void SendHealthCheck(string inviteCode)
        {
            if (JunimoBootServerAddress == "") return;

            try
            {
                await _stardewGameServiceClient.GameHealthCheckAsync(new GameHealthCheckRequest
                {
                    InviteCode = inviteCode,
                    IsConnectable = true,
                    ServerId = ServerId,
                });
            }
            catch (Exception e)
            {
                Monitor.Log("Failed to send health check: " + e.Message, LogLevel.Error);

                // manually retry connection
                var junimoBootGenChannel = GrpcChannel.ForAddress($"http://{JunimoBootServerAddress}");
                _stardewGameServiceClient = new StardewGameService.StardewGameServiceClient(junimoBootGenChannel);
            }
        }
    }
}