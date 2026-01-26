using JunimoServer.Services.ChatCommands;
using JunimoServer.Services.PasswordProtection;
using JunimoServer.Services.Roles;
using JunimoServer.Util;
using StardewModdingAPI;
using StardewValley;
using System.Linq;

namespace JunimoServer.Services.Commands
{
    /// <summary>
    /// Admin command to view authentication status of connected players.
    /// Usage: !authstatus
    /// </summary>
    public class AuthStatusCommand
    {
        public static void Register(IModHelper helper, IMonitor monitor, ChatCommandsService chatCommandsService, RoleService roleService, PasswordProtectionService passwordProtectionService)
        {
            chatCommandsService.RegisterCommand("authstatus", "(Admin) View authentication status of all players.", (args, msg) =>
            {
                // Check if player is admin
                if (!roleService.IsPlayerAdmin(msg.SourceFarmer))
                {
                    helper.SendPrivateMessage(msg.SourceFarmer, "You must be an admin to use this command.");
                    return;
                }

                // Check if password protection is enabled
                if (!passwordProtectionService.IsEnabled)
                {
                    helper.SendPrivateMessage(msg.SourceFarmer, "Password protection is not enabled.");
                    return;
                }

                // Get all connected players (excluding host)
                var players = Game1.otherFarmers.Values.ToList();

                if (!players.Any())
                {
                    helper.SendPrivateMessage(msg.SourceFarmer, "No other players connected.");
                    return;
                }

                helper.SendPrivateMessage(msg.SourceFarmer, "=== Player Authentication Status ===");

                foreach (var farmer in players)
                {
                    var isAuth = passwordProtectionService.IsPlayerAuthenticated(farmer.UniqueMultiplayerID);
                    var status = isAuth ? "[OK]" : "[PENDING]";
                    var userName = helper.GetFarmerUserNameById(farmer.UniqueMultiplayerID) ?? "Unknown";
                    helper.SendPrivateMessage(msg.SourceFarmer, $"{status} {farmer.Name} ({userName})");
                }
            });

            monitor.Log("[AuthStatusCommand] Registered !authstatus command", LogLevel.Trace);
        }
    }
}
