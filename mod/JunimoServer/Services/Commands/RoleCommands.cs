using System.Linq;
using JunimoServer.Services.ChatCommands;
using JunimoServer.Services.Roles;
using JunimoServer.Util;
using StardewModdingAPI;
using StardewValley;

namespace JunimoServer.Services.Commands
{
    public static class RoleCommands
    {
        public static void Register(IModHelper helper, IChatCommandApi chatCommandApi, RoleService roleService)
        {
            chatCommandApi.RegisterCommand("admin", "\"farmerName|userName\" to assign admin status to the player.", (args, msg) =>
            {
                if (!roleService.IsPlayerAdmin(msg.SourceFarmer))
                {
                    helper.SendPrivateMessage(msg.SourceFarmer, "You are not an admin.");
                    return;
                }
                if (args.Length != 1 || (args.Length == 1 && args[0] == ""))
                {
                    helper.SendPrivateMessage(msg.SourceFarmer, "Invalid use of command. Correct format is !admin name");
                    return;
                }

                var nameFromCommand = args[0];
                var farmerToAdmin = helper.FindPlayerIdByFarmerNameOrUserName(nameFromCommand);

                if (farmerToAdmin == null)
                {
                    helper.SendPrivateMessage(msg.SourceFarmer, "Player not found: " + nameFromCommand);
                    return;
                }

                roleService.AssignAdmin(farmerToAdmin.UniqueMultiplayerID);
                helper.SendPrivateMessage(msg.SourceFarmer, "Assigned Admin to: " + farmerToAdmin.Name);
            });
            chatCommandApi.RegisterCommand("unadmin", "\"farmerName|userName\" to take away admin status from the player.", (args, msg) =>
            {
                if (!roleService.IsPlayerAdmin(msg.SourceFarmer))
                {
                    helper.SendPrivateMessage(msg.SourceFarmer, "You are not an admin.");
                    return;
                }

                if (args.Length != 1 || (args.Length == 1 && args[0] == ""))
                {
                    helper.SendPrivateMessage(msg.SourceFarmer, "Invalid use of command. Correct format is !unadmin name");
                    return;
                }

                var nameFromCommand = args[0];
                var farmerToUnadmin = helper.FindPlayerIdByFarmerNameOrUserName(nameFromCommand);

                if (farmerToUnadmin == null)
                {
                    helper.SendPrivateMessage(msg.SourceFarmer, "Player not found: " + nameFromCommand);
                    return;
                }

                if (farmerToUnadmin.UniqueMultiplayerID == helper.GetOwnerPlayerId())
                {
                    helper.SendPrivateMessage(msg.SourceFarmer, "You can't unadmin the owner of the server.");
                    return;
                }

                roleService.UnassignAdmin(farmerToUnadmin.UniqueMultiplayerID);
                helper.SendPrivateMessage(msg.SourceFarmer, "Took away admin from: " + farmerToUnadmin.Name);
            });
        }
    }
}