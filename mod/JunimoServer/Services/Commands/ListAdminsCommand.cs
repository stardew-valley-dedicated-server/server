using System.Linq;
using JunimoServer.Services.ChatCommands;
using JunimoServer.Services.Roles;
using JunimoServer.Util;
using StardewModdingAPI;
using StardewValley;

namespace JunimoServer.Services.Commands
{
    public class ListAdminsCommand
    {
        public static void Register(IModHelper helper, IChatCommandApi chatCommandApi, RoleService roleService)
        {
            chatCommandApi.RegisterCommand("listadmins", "list bans", (args, msg) =>
            {
                if (!roleService.IsPlayerAdmin(msg.SourceFarmer))
                {
                    helper.SendPrivateMessage(msg.SourceFarmer, "You are not an admin.");
                    return;
                }

                helper.SendPrivateMessage(msg.SourceFarmer, "Admins:");

                foreach (var farmerId in roleService.GetAdmins())

                {
                    var farmerName = helper.GetFarmerNameById(farmerId);
                    var userName = helper.GetFarmerUserNameById(farmerId);
                    helper.SendPrivateMessage(msg.SourceFarmer, $"{farmerName} | {userName}");
                }
            });
        }

    }
}