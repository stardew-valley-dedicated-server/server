using System.Linq;
using JunimoServer.Services.ChatCommands;
using JunimoServer.Services.Roles;
using JunimoServer.Util;
using StardewModdingAPI;
using StardewValley;

namespace JunimoServer.Services.Commands
{
    public class BanCommand
    {
         public static void Register(IModHelper helper, IChatCommandApi chatCommandApi, RoleService roleService)
        {
            chatCommandApi.RegisterCommand("ban", "\"farmerName|userName\" to ban the player.", (args, msg) =>
            {
                if (!roleService.IsPlayerAdmin(msg.SourceFarmer))
                {
                    helper.SendPrivateMessage(msg.SourceFarmer, "You are not an admin.");
                    return;
                }
                if (args.Length != 1 || (args.Length == 1 && args[0] == ""))
                {
                    helper.SendPrivateMessage(msg.SourceFarmer, "Invalid use of command. Correct format is !ban name");
                    return;
                }

                var nameFromCommand = args[0];
                var targetFarmer = helper.FindPlayerIdByFarmerNameOrUserName(nameFromCommand);

                if (targetFarmer == null)
                {
                    helper.SendPrivateMessage(msg.SourceFarmer, "Player not found: " + nameFromCommand);
                    return;
                }
                
                if (roleService.IsPlayerOwner(targetFarmer.UniqueMultiplayerID))
                {
                    helper.SendPrivateMessage(msg.SourceFarmer, "You can't ban the owner of the server.");
                    return;
                }

                Game1.server.ban(targetFarmer.UniqueMultiplayerID);
                helper.SendPrivateMessage(msg.SourceFarmer, "Banned: " + targetFarmer.Name);
            });
        }
    
    }
}