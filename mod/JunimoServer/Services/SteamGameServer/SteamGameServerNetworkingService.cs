using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Network;

namespace JunimoServer.Services.SteamGameServer
{
    /// <summary>
    /// Service that adds SteamGameServerNetServer to the game's server list when GameServer mode is enabled.
    /// This allows Steam clients to connect via SDR without requiring the Steam client on the server.
    /// </summary>
    public class SteamGameServerNetworkingService : ModService
    {
        private static IMonitor _monitor;
        private static IModHelper _helper;
        private static bool _serverAdded = false;

        public SteamGameServerNetworkingService(
            Harmony harmony,
            IMonitor monitor,
            IModHelper helper,
            SteamGameServerService steamGameServerService)  // Dependency ensures correct init order
        {
            _monitor = monitor;
            _helper = helper;

            // Listen for save loaded to add the server
            helper.Events.GameLoop.SaveLoaded += OnSaveLoaded;
            helper.Events.GameLoop.ReturnedToTitle += OnReturnedToTitle;

            _monitor.Log("Steam GameServer networking service initialized", LogLevel.Debug);
        }

        private void OnSaveLoaded(object sender, SaveLoadedEventArgs e)
        {
            // Try to add the server after save is loaded (when hosting starts)
            TryAddSteamGameServer();
        }

        private void OnReturnedToTitle(object sender, ReturnedToTitleEventArgs e)
        {
            // Reset flag when returning to title so we can add again
            _serverAdded = false;
        }

        /// <summary>
        /// Attempts to add the SteamGameServerNetServer to the game's server list.
        /// </summary>
        public static void TryAddSteamGameServer()
        {
            if (_serverAdded)
                return;

            if (!SteamGameServerService.IsInitialized)
            {
                _monitor.Log("GameServer not yet initialized, deferring network server addition", LogLevel.Debug);
                return;
            }

            try
            {
                // Check if game server exists
                if (Game1.server == null)
                {
                    _monitor.Log("Game1.server is null, waiting for server creation", LogLevel.Debug);
                    return;
                }

                // Access the internal 'servers' list via reflection
                var serversField = _helper.Reflection.GetField<List<Server>>(Game1.server, "servers");
                var servers = serversField.GetValue();

                // Check if SteamGameServerNetServer already exists
                if (servers.Any(s => s is SteamGameServerNetServer))
                {
                    _monitor.Log("SteamGameServerNetServer already exists", LogLevel.Debug);
                    _serverAdded = true;
                    return;
                }

                // Remove any existing SteamNetServer - it's not properly initialized in GameServer mode
                // and will crash when trying to check connection IDs
                var existingSteamServer = servers.FirstOrDefault(s => s.GetType().Name == "SteamNetServer");
                if (existingSteamServer != null)
                {
                    servers.Remove(existingSteamServer);
                    _monitor.Log("Removed vanilla SteamNetServer (not compatible with GameServer mode)", LogLevel.Info);
                }

                // Create and add our GameServer-based network server
                _monitor.Log("Adding SteamGameServerNetServer for SDR connections", LogLevel.Info);
                var gameServerNet = new SteamGameServerNetServer(Game1.server, _monitor, _helper);
                servers.Add(gameServerNet);
                gameServerNet.initialize();

                _serverAdded = true;
                _monitor.Log("SteamGameServerNetServer added successfully!", LogLevel.Info);
                _monitor.Log($"Server Steam ID: {SteamGameServerService.ServerSteamId.m_SteamID}", LogLevel.Info);
                _monitor.Log("Steam clients can now connect via SDR using this Steam ID", LogLevel.Info);
            }
            catch (Exception ex)
            {
                _monitor.Log($"Failed to add SteamGameServerNetServer: {ex.Message}", LogLevel.Error);
                _monitor.Log(ex.ToString(), LogLevel.Debug);
            }
        }
    }
}
