using JunimoServer.Services.ChatCommands;
using JunimoServer.Services.ServerOptim;
using JunimoServer.Util;
using StardewModdingAPI;
using StardewValley;
using System;
using System.Globalization;

namespace JunimoServer.Services.Commands
{
    public class ServerCommand
    {
        private static IModHelper _helper;
        private static IMonitor _monitor;
        private static DateTime _startTimeUtc;

        public static void Register(IModHelper helper, IMonitor monitor, ChatCommandsService chatCommandsService)
        {
            _helper = helper;
            _monitor = monitor;
            _startTimeUtc = DateTime.UtcNow;

            chatCommandsService.RegisterCommand("info", "Displays server information.", (args, msg) =>
            {
                if (Game1.server == null)
                {
                    helper.SendPrivateMessage(msg.SourceFarmer, "Server is not running.");
                    return;
                }

                var modInfo = helper.ModRegistry.Get("JunimoHost.Server");
                var version = modInfo?.Manifest?.Version?.ToString() ?? "unknown";
                var farmName = Game1.player?.farmName.Value ?? "Unknown";
                var playerCount = Game1.server?.connectionsCount ?? 0;
                var maxPlayers = Game1.netWorldState.Value?.CurrentPlayerLimit ?? 4;
                var inviteCode = Game1.server.getInviteCode() ?? "N/A";
                var uptime = DateTime.UtcNow - _startTimeUtc;
                var season = Game1.currentSeason ?? "unknown";

                var ping = (int)Game1.server.getPingToClient(msg.SourceFarmer);
                var isReady = Game1.server.isGameAvailable();
                var renderingEnabled = ServerOptimizerOverrides.IsRenderingEnabled();

                helper.SendPrivateMessage(msg.SourceFarmer, $"--- Server Info ---");
                helper.SendPrivateMessage(msg.SourceFarmer, $"Name: {farmName} Farm");
                helper.SendPrivateMessage(msg.SourceFarmer, $"Version: {version}");
                helper.SendPrivateMessage(msg.SourceFarmer, $"Uptime: {FormatUptime(uptime)}");
                helper.SendPrivateMessage(msg.SourceFarmer, $"In-Game: {CultureInfo.InvariantCulture.TextInfo.ToTitleCase(season)} {Game1.dayOfMonth}, Year {Game1.year} - {FormatGameTime(Game1.timeOfDay)}");
                helper.SendPrivateMessage(msg.SourceFarmer, $"Players: {playerCount}/{maxPlayers}");
                helper.SendPrivateMessage(msg.SourceFarmer, $"Ping: {ping}ms");
                helper.SendPrivateMessage(msg.SourceFarmer, $"Status: {(isReady ? "Ready" : "Busy")}");
                helper.SendPrivateMessage(msg.SourceFarmer, $"Rendering: {(renderingEnabled ? "On" : "Off")}");
                helper.SendPrivateMessage(msg.SourceFarmer, $"Invite Code: {inviteCode}");
            });

            helper.ConsoleCommands.Add("info", "Displays server information.", ServerConsoleCommand);
        }

        private static string FormatUptime(TimeSpan uptime)
        {
            if (uptime.TotalDays >= 1)
                return $"{(int)uptime.TotalDays}d {uptime.Hours}h {uptime.Minutes}m";
            if (uptime.TotalHours >= 1)
                return $"{(int)uptime.TotalHours}h {uptime.Minutes}m";
            return $"{(int)uptime.TotalMinutes}m";
        }

        private static string FormatGameTime(int timeOfDay)
        {
            var hours = timeOfDay / 100;
            var minutes = timeOfDay % 100;
            var period = hours >= 12 && hours < 24 ? "PM" : "AM";
            var displayHours = hours % 12;
            if (displayHours == 0) displayHours = 12;
            return $"{displayHours}:{minutes:D2} {period}";
        }

        private static void ServerConsoleCommand(string command, string[] args)
        {
            if (Game1.server == null)
            {
                _monitor.Log("Server is not running.", LogLevel.Error);
                return;
            }

            var modInfo = _helper.ModRegistry.Get("JunimoHost.Server");
            var version = modInfo?.Manifest?.Version?.ToString() ?? "unknown";
            var farmName = Game1.player?.farmName.Value ?? "Unknown";
            var playerCount = Game1.server?.connectionsCount ?? 0;
            var maxPlayers = Game1.netWorldState.Value?.CurrentPlayerLimit ?? 4;
            var inviteCode = Game1.server.getInviteCode() ?? "N/A";
            var uptime = DateTime.UtcNow - _startTimeUtc;
            var season = Game1.currentSeason ?? "unknown";

            var isReady = Game1.server.isGameAvailable();
            var renderingEnabled = ServerOptimizerOverrides.IsRenderingEnabled();

            _monitor.Log("--- Server Info ---", LogLevel.Info);
            _monitor.Log($"  Name: {farmName} Farm", LogLevel.Info);
            _monitor.Log($"  Version: {version}", LogLevel.Info);
            _monitor.Log($"  Uptime: {FormatUptime(uptime)}", LogLevel.Info);
            _monitor.Log($"  In-Game: {CultureInfo.InvariantCulture.TextInfo.ToTitleCase(season)} {Game1.dayOfMonth}, Year {Game1.year} - {FormatGameTime(Game1.timeOfDay)}", LogLevel.Info);
            _monitor.Log($"  Players: {playerCount}/{maxPlayers}", LogLevel.Info);
            _monitor.Log($"  Status: {(isReady ? "Ready" : "Busy")}", LogLevel.Info);
            _monitor.Log($"  Rendering: {(renderingEnabled ? "On" : "Off")}", LogLevel.Info);
            _monitor.Log($"  Invite Code: {inviteCode}", LogLevel.Info);
        }
    }
}
