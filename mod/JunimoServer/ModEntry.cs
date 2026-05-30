using HarmonyLib;
using JunimoServer.Shared;
using JunimoServer.Services.AlwaysOn;
using JunimoServer.Services.CabinManager;
using JunimoServer.Services.ChatCommands;
using JunimoServer.Services.Commands;
using JunimoServer.Services.GameLoader;
using JunimoServer.Services.Lobby;
using JunimoServer.Services.PasswordProtection;
using JunimoServer.Services.PersistentOption;
using JunimoServer.Services.Roles;
using JunimoServer.Services.Settings;
using JunimoServer.Util;
using Microsoft.Extensions.DependencyInjection;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using System;
using System.Linq;
using System.Reflection;

namespace JunimoServer
{
    internal class ModEntry : Mod
    {
        private ServiceProvider _services;

        private readonly Type modServiceBaseType = typeof(IModService);
        private bool _failFastEnabled;

        // Cold-start phase timing. Captured once at Entry; used to emit
        // mod_phase events with phaseMs (delta from previous phase) and
        // bootMs (cumulative since Entry started). Read by the harness's
        // SimpleContainerLogStreamer.TryForwardSdvdEvent → infrastructure.jsonl.
        private System.Diagnostics.Stopwatch _bootStopwatch;
        private long _previousPhaseMs;

        private void EmitModPhase(string phase)
        {
            if (_bootStopwatch == null) return;
            var bootMs = _bootStopwatch.ElapsedMilliseconds;
            var phaseMs = bootMs - _previousPhaseMs;
            _previousPhaseMs = bootMs;
            Services.Diagnostics.ModEventLog.Emit("mod_phase", new
            {
                phase,
                phaseMs,
                bootMs,
            });
        }

        public override void Entry(IModHelper helper)
        {
            _bootStopwatch = System.Diagnostics.Stopwatch.StartNew();

            // Check for fail-fast mode (used in E2E testing)
            _failFastEnabled = string.Equals(
                Environment.GetEnvironmentVariable("TEST_FAIL_FAST"),
                "true",
                StringComparison.OrdinalIgnoreCase);

            if (_failFastEnabled)
            {
                Monitor.Log("TEST_FAIL_FAST mode enabled - will exit on unhandled exceptions", LogLevel.Warn);
                AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
            }

            EmitModPhase("mod_load_started");

            Helper.Events.GameLoop.GameLaunched += OnGameLaunched;
            Helper.Events.GameLoop.SaveLoaded += OnSaveLoaded;
            Helper.Events.GameLoop.DayStarted += OnFirstDayStarted;

            // Clear invite code file on startup
            JunimoServer.Util.InviteCodeFile.Clear(Monitor);

            RegisterServices();
            EmitModPhase("services_started");

            RegisterChatCommands();
            RegisterConsoleCommands();
            EmitModPhase("api_listener_ready");
        }

        private bool _firstDayStartedEmitted;
        private void OnFirstDayStarted(object sender, DayStartedEventArgs e)
        {
            if (_firstDayStartedEmitted) return;
            _firstDayStartedEmitted = true;
            EmitModPhase("world_ready");
        }

        private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            Monitor.Log($"FAIL_FAST: Unhandled exception detected - {e.ExceptionObject}", LogLevel.Error);
            Monitor.Log("Exiting due to TEST_FAIL_FAST mode", LogLevel.Error);
            Environment.Exit(1);
        }

        private void OnGameLaunched(object sender, GameLaunchedEventArgs e)
        {
            Game1.options.pauseWhenOutOfFocus = false;

            // Resize the window to fill the X display so the server renders at the
            // configured resolution (not the 1280x720 default) — lower resolution
            // saves CPU per frame and the recorder captures a full, uncropped frame.
            DisplaySizing.ApplyFromEnv(Monitor);
        }

        private void OnSaveLoaded(object sender, SaveLoadedEventArgs e)
        {
            Game1.options.pauseWhenOutOfFocus = false;
            EmitModPhase("save_loaded");
        }

        private void RegisterServices()
        {
            Monitor.Log("Loading services...", LogLevel.Info);

            var services = new ServiceCollection();

            LoadServiceDependencies(services);
            LoadServices(services);
            StartServices(services);

            Monitor.Log("Services loaded and ready!", LogLevel.Info);
        }

        private void LoadServiceDependencies(ServiceCollection services)
        {
            services.AddSingleton(Helper);
            services.AddSingleton(Monitor);

            var harmony = new Harmony(ModManifest.UniqueID);
            services.AddSingleton(harmony);

            // Fix .NET 6 HttpListener race condition (dotnet/runtime#28658) before
            // any service can call HttpListener.Start(). Must be patched before
            // GameLaunched fires (where ApiService starts the HTTP listener).
            HttpListenerFix.Apply(harmony, Monitor);

            // Fix vanilla NRE: GameLocation.UpdateWhenCurrentLocation accesses
            // Game1.player.currentLocation without null check during warps/day transitions.
            harmony.Patch(
                original: AccessTools.Method(typeof(GameLocation), nameof(GameLocation.UpdateWhenCurrentLocation)),
                prefix: new HarmonyMethod(typeof(ModEntry), nameof(UpdateWhenCurrentLocation_Prefix)));
            Monitor.Log("Patched UpdateWhenCurrentLocation with null-safety guard", LogLevel.Trace);

            // Keep the display zoom/UI scale pinned in every game mode (the actual resize
            // happens on GameLaunched via DisplaySizing.ApplyFromEnv).
            DisplaySizing.Install(harmony);

            // Test overlay for E2E test debugging (only active when SDVD_ENV=test)
            if (Env.IsTest)
            {
                TestOverlay.Apply(harmony);
                ButtonTutorialSuppressor.Apply(harmony);
            }

            // Load settings first so we can apply verbose logging before other services start
            var settingsLoader = new ServerSettingsLoader(Helper, Monitor);
            services.AddSingleton(settingsLoader);

            // Apply verbose logging setting (env var overrides config file)
            var verboseLogging = Env.VerboseLogging ?? settingsLoader.VerboseLogging;
            Monitor.Log($"Applying VerboseLogging={verboseLogging} (env={Env.VerboseLogging}, config={settingsLoader.VerboseLogging})", LogLevel.Info);
            SmapiLogConfig.SetVerboseLogging(ModManifest.UniqueID, verboseLogging, Monitor);

            services.AddSingleton<PersistentOptions>();
            services.AddSingleton<AlwaysOnConfig>();
        }

        private void LoadServices(ServiceCollection services)
        {
            var serviceTypes = Assembly.GetExecutingAssembly().GetTypesWithInterface(modServiceBaseType);

            foreach (var serviceType in serviceTypes)
            {
                services.AddSingleton(serviceType);
            }
        }

        private void StartServices(ServiceCollection services)
        {
            _services = services.BuildServiceProvider();

            // Filter and sort services
            var servicesAll = services
                .Where(d => d.ImplementationType != null)
                .OrderBy(d => d.ImplementationType.Name);

            // Start services
            foreach (var serviceDescriptor in servicesAll)
            {
                var serviceType = serviceDescriptor.ImplementationType;
                var serviceName = serviceType.Name;

                try
                {
                    // Retrieve service at least once to run constructors and possible side-effects
                    var serviceInstance = _services.GetRequiredService(serviceType);

                    // Call service entry point
                    if (HasModServiceBaseType(serviceType))
                    {
                        ((ModService)serviceInstance).Entry();
                    }
                }
                catch (Exception e)
                {
                    // Fail-fast: a failed service init can leave Harmony patches half-installed
                    // (e.g. PasswordProtectionService applies 5 patches in its constructor — a
                    // throw mid-sequence would silently disable the auth gate while accepting
                    // connections). Terminate the process so the operator/container restarts
                    // into a coherent state instead of running with partial enforcement.
                    // To run without password protection, leave SERVER_PASSWORD unset; the
                    // service short-circuits before installing any patches.
                    Monitor.Log($"   {serviceName} ({serviceType}) FAILED: {e}", LogLevel.Error);
                    Monitor.Log("Service initialization failed — exiting to avoid partial-enforcement state", LogLevel.Error);
                    Environment.Exit(1);
                }

                Monitor.Log($"   {serviceName} ({serviceType})", LogLevel.Trace);
            }

            // Filter out services which don't use ModService yet
            var servicesWithBaseType = servicesAll.Where(s => HasModServiceBaseType(s.ImplementationType));

            // Print loaded services
            Monitor.Log($"Loaded {servicesWithBaseType.Count()} services:", LogLevel.Info);
            foreach (var serviceDescriptor in servicesWithBaseType)
            {
                Monitor.Log($"   {serviceDescriptor.ImplementationType.Name}", LogLevel.Info);
            }
        }

        private bool HasModServiceBaseType(Type service)
        {
            return modServiceBaseType.IsAssignableFrom(service);
        }

        private void RegisterConsoleCommands()
        {
            var gameLoader = _services.GetRequiredService<GameLoaderService>();
            var cabinManager = _services.GetRequiredService<CabinManagerService>();
            var persistentOptions = _services.GetRequiredService<PersistentOptions>();
            var settings = _services.GetRequiredService<ServerSettingsLoader>();

            RenderingCommand.Register(Helper, Monitor);
            SettingsCommand.Register(Helper, Monitor, gameLoader, persistentOptions, settings);
            CabinsConsoleCommand.Register(Helper, Monitor, cabinManager, persistentOptions);
            SavesCommand.Register(Helper, Monitor, gameLoader, settings);
        }

        private void RegisterChatCommands()
        {
            var cabinService = _services.GetRequiredService<CabinManagerService>();
            var chatCommandsService = _services.GetRequiredService<ChatCommandsService>();
            var roleService = _services.GetRequiredService<RoleService>();
            var alwaysOnConfig = _services.GetRequiredService<AlwaysOnConfig>();
            var persistentOptions = _services.GetRequiredService<PersistentOptions>();
            var passwordProtectionService = _services.GetRequiredService<PasswordProtectionService>();
            var lobbyService = _services.GetRequiredService<LobbyService>();

            CabinCommand.Register(Helper, chatCommandsService, cabinService, persistentOptions);
            RoleCommands.Register(Helper, chatCommandsService, roleService);
            BanCommand.Register(Helper, chatCommandsService, roleService);
            KickCommand.Register(Helper, chatCommandsService, roleService);
            ListAdminsCommand.Register(Helper, chatCommandsService, roleService);
            ListBansCommand.Register(Helper, chatCommandsService, roleService);
            UnbanCommand.Register(Helper, chatCommandsService, roleService);
            ChangeWalletCommand.Register(Helper, chatCommandsService, roleService);
            JojaCommand.Register(Helper, chatCommandsService, roleService, alwaysOnConfig);
            ConsoleCommand.Register(Helper, chatCommandsService, roleService);
            InviteCodeCommand.Register(Helper, Monitor, chatCommandsService);
            ServerCommand.Register(Helper, Monitor, chatCommandsService);

            // Password protection commands
            LoginCommand.Register(Helper, Monitor, chatCommandsService, passwordProtectionService);
            AuthStatusCommand.Register(Helper, Monitor, chatCommandsService, roleService, passwordProtectionService);
            LobbyCommands.Register(Helper, Monitor, chatCommandsService, roleService, lobbyService);
        }

        /// <summary>
        /// Skips GameLocation.UpdateWhenCurrentLocation when Game1.player.currentLocation
        /// is null. Prevents a vanilla NRE during warp/day-transition frames.
        /// </summary>
        private static bool UpdateWhenCurrentLocation_Prefix()
        {
            return Game1.player?.currentLocation != null;
        }

    }
}
