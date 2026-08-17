using JunimoServer.Services.ChatCommands;
using JunimoServer.Services.Roles;
using JunimoServer.Util;
using StardewModdingAPI;

namespace JunimoServer.Services.Commands;

public class ListAdminsCommand
{
    public static void Register(
        IModHelper helper,
        ChatCommandsService chatCommandsService,
        RoleService roleService
    )
    {
        chatCommandsService.RegisterCommand(
            "listadmins",
            "Lists server admins.",
            (args, msg) =>
            {
                helper.SendPrivateMessage(msg.SourceFarmer, "Admins:");

                foreach (var farmerId in roleService.GetAdmins())
                {
                    var farmerName = helper.GetFarmerNameById(farmerId);
                    var userName = helper.GetFarmerUserNameById(farmerId);
                    helper.SendPrivateMessage(msg.SourceFarmer, $"{farmerName} | {userName}");
                }
            },
            requiresAdmin: true
        );
    }
}
