using JunimoServer.Util;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using System;

namespace JunimoServer.Services.ServerStatus
{
    /// <summary>
    /// Periodically writes server status to a JSON file for external tools
    /// like Discord bots to read.
    /// </summary>
    public class ServerStatusService : ModService
    {
        private const int UpdateIntervalSeconds = 10;
        private int _tickCounter = 0;

        public ServerStatusService(IModHelper helper, IMonitor monitor) : base(helper, monitor)
        {
        }

        public override void Entry()
        {
            // Clear status file on startup
            ServerStatusFile.Clear();

            Helper.Events.GameLoop.OneSecondUpdateTicked += OnOneSecondUpdateTicked;
            Helper.Events.GameLoop.ReturnedToTitle += OnReturnedToTitle;

            Monitor.Log("ServerStatusService started - writing status to /tmp/server-status.json", LogLevel.Info);
        }

        private void OnOneSecondUpdateTicked(object sender, OneSecondUpdateTickedEventArgs e)
        {
            _tickCounter++;

            if (_tickCounter < UpdateIntervalSeconds)
            {
                return;
            }

            _tickCounter = 0;
            UpdateStatus();
        }

        private void OnReturnedToTitle(object sender, ReturnedToTitleEventArgs e)
        {
            // Write offline status when returning to title
            WriteOfflineStatus();
        }

        private void UpdateStatus()
        {
            try
            {
                if (!Context.IsWorldReady || !Game1.IsServer)
                {
                    WriteOfflineStatus();
                    return;
                }

                var modInfo = Helper.ModRegistry.Get("JunimoHost.Server");
                var version = modInfo?.Manifest?.Version?.ToString() ?? "unknown";

                var inviteCode = InviteCodeFile.Read(Monitor);
                var playerCount = Game1.server?.connectionsCount ?? 0;

                // Get max players from world state (set by NetworkTweaker from PersistentOptions)
                var maxPlayers = Game1.netWorldState.Value?.CurrentPlayerLimit ?? 4;

                var status = new ServerStatusFile.ServerStatus
                {
                    PlayerCount = playerCount,
                    MaxPlayers = maxPlayers,
                    InviteCode = inviteCode ?? "",
                    ServerVersion = version,
                    IsOnline = true
                };

                ServerStatusFile.Write(status);
            }
            catch (Exception ex)
            {
                Monitor.Log($"Failed to update server status: {ex.Message}", LogLevel.Warn);
            }
        }

        private void WriteOfflineStatus()
        {
            try
            {
                var modInfo = Helper.ModRegistry.Get("JunimoHost.Server");
                var version = modInfo?.Manifest?.Version?.ToString() ?? "unknown";

                var status = new ServerStatusFile.ServerStatus
                {
                    PlayerCount = 0,
                    MaxPlayers = 4,
                    InviteCode = "",
                    ServerVersion = version,
                    IsOnline = false
                };

                ServerStatusFile.Write(status);
            }
            catch (Exception ex)
            {
                Monitor.Log($"Failed to write offline status: {ex.Message}", LogLevel.Warn);
            }
        }
    }
}
