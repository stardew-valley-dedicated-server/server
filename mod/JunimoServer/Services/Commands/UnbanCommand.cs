using JunimoServer.Services.ChatCommands;
using JunimoServer.Util;
using StardewModdingAPI;
using StardewValley;

namespace JunimoServer.Services.Commands;

public class UnbanCommand
{
    public static void Register(IModHelper helper, ChatCommandsService chatCommandsService)
    {
        chatCommandsService.RegisterCommand(
            "unban",
            "Unban <player> (see !listbans).",
            (args, msg) =>
            {
                if (args.Length != 1 || (args.Length == 1 && args[0] == ""))
                {
                    helper.SendPrivateMessage(
                        msg.SourceFarmer,
                        "Invalid use of command. Correct format is !unban name"
                    );
                    return;
                }

                var target = args[0];

                var unbanKey = "";
                var unbanVal = "";
                foreach (var (k, v) in Game1.bannedUsers)
                {
                    if (k == target || v == target)
                    {
                        unbanKey = k;
                        unbanVal = v;
                        break;
                    }
                }

                if (unbanKey == "")
                {
                    helper.SendPrivateMessage(msg.SourceFarmer, "Player not found: " + target);
                    return;
                }
                Game1.bannedUsers.Remove(unbanKey);
                helper.SendPrivateMessage(msg.SourceFarmer, $"Unbanned: {unbanKey} | {unbanVal}");
            },
            requiresAdmin: true
        );
    }
}
