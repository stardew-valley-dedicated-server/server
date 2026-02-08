using JunimoServer.Services.ChatCommands;
using JunimoServer.Services.PasswordProtection;
using JunimoServer.Util;
using StardewModdingAPI;

namespace JunimoServer.Services.Commands
{
    /// <summary>
    /// Chat command for player authentication.
    /// Usage: !login <password>
    /// </summary>
    public class LoginCommand
    {
        public static void Register(IModHelper helper, IMonitor monitor, ChatCommandsService chatCommandsService, PasswordProtectionService passwordProtectionService)
        {
            chatCommandsService.RegisterCommand("login", "<password> - Authenticate with the server password.", (args, msg) =>
            {
                // Check if password protection is enabled
                if (!passwordProtectionService.IsEnabled)
                {
                    helper.SendPrivateMessage(msg.SourceFarmer, "Password protection is not enabled on this server.");
                    return;
                }

                // Check if already authenticated
                if (passwordProtectionService.IsPlayerAuthenticated(msg.SourceFarmer))
                {
                    helper.SendPrivateMessage(msg.SourceFarmer, "You are already authenticated.");
                    return;
                }

                // Check for password argument
                if (args.Length == 0 || string.IsNullOrWhiteSpace(args[0]))
                {
                    helper.SendPrivateMessage(msg.SourceFarmer, "Usage: !login <password>");
                    return;
                }

                // Combine all args in case password contains spaces
                string password = string.Join(" ", args);

                // Attempt authentication
                var result = passwordProtectionService.TryAuthenticate(msg.SourceFarmer, password);

                // Send result message (if player wasn't kicked)
                if (!result.ShouldKick)
                {
                    helper.SendPrivateMessage(msg.SourceFarmer, result.Message);

                    if (result.Success)
                    {
                        monitor.Log($"[LoginCommand] Player {msg.SourceFarmer} authenticated successfully", LogLevel.Info);
                    }
                }
            });

            monitor.Log("[LoginCommand] Registered !login command", LogLevel.Trace);
        }
    }
}
