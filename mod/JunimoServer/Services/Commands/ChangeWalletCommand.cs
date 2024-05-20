using JunimoServer.Services.ChatCommands;
using JunimoServer.Services.Roles;
using JunimoServer.Util;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Locations;

namespace JunimoServer.Services.Commands
{
    public class ChangeWalletCommand
    {
         public static void Register(IModHelper helper, IChatCommandApi chatCommandApi, RoleService roleService)
        {
            chatCommandApi.RegisterCommand("changewallet", "Immediate toggle between shared or split money.", (args, msg) =>
            {
                if (!roleService.IsPlayerAdmin(msg.SourceFarmer))
                {
                    helper.SendPrivateMessage(msg.SourceFarmer, "You are not an admin.");
                    return;
                }

                var isSeparateWallets = Game1.player.team.useSeparateWallets.Value;
                if (isSeparateWallets)
                {
                    ManorHouse.MergeWallets();
                    helper.SendPublicMessage("Now using shared money.");
                }
                else
                {
                    ManorHouse.SeparateWallets();
                    helper.SendPublicMessage("Now using separate money.");
                }
            });
        }
    
    }
}