using HarmonyLib;
using JunimoServer.Services.AlwaysOn;
using JunimoServer.Services.CabinManager;
using JunimoServer.Services.ChatCommands;
using JunimoServer.Services.Commands;
using JunimoServer.Services.PersistentOption;
using JunimoServer.Services.Roles;
using JunimoServer.Util;
using Microsoft.Extensions.DependencyInjection;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using System;
using System.Linq;
using System.Net;
using System.Reflection;

namespace JunimoServer
{
    internal class ModEntry : Mod
    {
        public static ServiceProvider Services;

        private readonly Type modServiceBaseType = typeof(IModService);

        public override void Entry(IModHelper helper)
        {
            // TODO:
            // a) Create "SDVD Debug Client" mod (features: disable pause when out of focus)
            // b) Create "SDVD Client" mod: POC for enhanced server <-> client functions with custom network message/behavior
            // Disables pause when out of focus, currently useful for testing since clients start up faster this way
            Helper.Events.GameLoop.GameLaunched += OnGameLaunched;
            Helper.Events.GameLoop.SaveLoaded += OnSaveLoaded;

            if (Env.HasServerBypassCommandLineArg)
            {
                Monitor.Log($"Found '--client' command line argument, skipping {ModManifest.Name}", LogLevel.Debug);
                return;
            }

            PrintStartupBanner();

            RegisterServices();
            RegisterChatCommands();
        }

        private void OnGameLaunched(object sender, GameLaunchedEventArgs e)
        {
            Game1.options.pauseWhenOutOfFocus = false;
        }

        private void OnSaveLoaded(object sender, SaveLoadedEventArgs e)
        {
            Game1.options.pauseWhenOutOfFocus = false;
        }

        private void PrintStartupBanner()
        {
            var externalIp = NetworkHelper.GetIpAddressExternal();
            var externalIpValue = externalIp == IPAddress.None ? "n/a" : externalIp.ToString();
            var externalIcon = externalIp == IPAddress.None ? "х" : "✓";

            Monitor.LogBanner(new[] {
                $"JunimoServer {ModManifest.Version}",
                "",
                $"✓ Local:   {NetworkHelper.GetIpAddressLocal()}",
                $"{externalIcon} Network: {externalIpValue}",
            });
        }

        private void RegisterServices()
        {
            Monitor.Log("Loading services...", LogLevel.Trace);

            var services = new ServiceCollection();

            LoadServiceDependencies(services);
            LoadServices(services);
            StartServices(services);

            Monitor.Log("Services loaded and ready!", LogLevel.Trace);
        }

        private void LoadServiceDependencies(ServiceCollection services)
        {
            services.AddSingleton(Helper);
            services.AddSingleton(Monitor);
            services.AddSingleton(new Harmony(ModManifest.UniqueID));
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

            Services = services.BuildServiceProvider();
        }

        private void StartServices(ServiceCollection services)
        {
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
                    var serviceInstance = Services.GetRequiredService(serviceType);

                    // Call service entry point
                    if (HasModServiceBaseType(serviceType))
                    {
                        ((ModService)serviceInstance).Entry();
                    }
                }
                catch (Exception e)
                {
                    Monitor.Log($"   {serviceName} ({serviceType}) FAILED: {e}", LogLevel.Error);
                    continue;
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

        private void RegisterChatCommands()
        {
            var cabinService = Services.GetRequiredService<CabinManagerService>();
            var chatCommandsService = Services.GetRequiredService<ChatCommandsService>();
            var roleService = Services.GetRequiredService<RoleService>();
            var alwaysOnConfig = Services.GetRequiredService<AlwaysOnConfig>();
            var persistentOptions = Services.GetRequiredService<PersistentOptions>();

            CabinCommand.Register(Helper, chatCommandsService, roleService, cabinService, persistentOptions);
            RoleCommands.Register(Helper, chatCommandsService, roleService);
            BanCommand.Register(Helper, chatCommandsService, roleService);
            KickCommand.Register(Helper, chatCommandsService, roleService);
            ListAdminsCommand.Register(Helper, chatCommandsService, roleService);
            ListBansCommand.Register(Helper, chatCommandsService, roleService);
            UnbanCommand.Register(Helper, chatCommandsService, roleService);
            ChangeWalletCommand.Register(Helper, chatCommandsService, roleService);
            JojaCommand.Register(Helper, chatCommandsService, roleService, alwaysOnConfig);
            ConsoleCommand.Register(Helper, chatCommandsService, roleService);
        }
    }
}
