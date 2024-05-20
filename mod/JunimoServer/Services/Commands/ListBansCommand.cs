using System.Linq;
using JunimoServer.Services.ChatCommands;
using JunimoServer.Services.Roles;
using JunimoServer.Util;
using StardewModdingAPI;
using StardewValley;

namespace JunimoServer.Services.Commands
{
    public class ListBansCommand
    {
        public static void Register(IModHelper helper, IChatCommandApi chatCommandApi, RoleService roleService)
        {
            chatCommandApi.RegisterCommand("listbans", "list bans", (args, msg) =>
            {
                if (!roleService.IsPlayerAdmin(msg.SourceFarmer))
                {
                    helper.SendPrivateMessage(msg.SourceFarmer, "You are not an admin.");
                    return;
                }

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
            });
        }

    }
}