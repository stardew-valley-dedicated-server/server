using JunimoServer.Services.ChatCommands;
using JunimoServer.Util;
using StardewModdingAPI;
using StardewValley;

namespace JunimoServer.Services.Commands;

public class ListBansCommand
{
    public static void Register(IModHelper helper, ChatCommandsService chatCommandsService)
    {
        chatCommandsService.RegisterCommand(
            "listbans",
            "Lists banned players.",
            (args, msg) =>
            {
                if (Game1.bannedUsers.Count == 0)
                {
                    helper.SendPrivateMessage(msg.SourceFarmer, "There are 0 banned users.");
                    return;
                }

                helper.SendPrivateMessage(msg.SourceFarmer, "Banned users:");

                foreach (var (k, v) in Game1.bannedUsers)
                {
                    helper.SendPrivateMessage(msg.SourceFarmer, $"{k} | {v} ");
                }
            },
            requiresAdmin: true
        );
    }
}
