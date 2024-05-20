using System.Linq;
using JunimoServer.Services.AlwaysOnServer;
using JunimoServer.Services.ChatCommands;
using JunimoServer.Services.Roles;
using JunimoServer.Util;
using StardewModdingAPI;
using StardewValley;

namespace JunimoServer.Services.Commands
{
    public static class ConsoleCommand
    {
        public static void Register(IModHelper helper, IChatCommandApi chatCommandApi, RoleService roleService)
        {
            //chatCommandApi.RegisterCommand("console",
            //    "forwards the proceeding command to be run on the server.",
            //    (args, msg) =>
            //    {
            //        if (!roleService.IsPlayerAdmin(msg.SourceFarmer))
            //        {
            //            helper.SendPrivateMessage(msg.SourceFarmer, "Only admins can run console commands.");
            //            return;
            //        }

            //        if (args.Length < 1)
            //        {
            //            helper.SendPrivateMessage(msg.SourceFarmer,
            //                "Invalid use of command. You must provide a console command to run.");
            //            return;
            //        }

            //        var remainingArgs = args.Skip(1).ToArray();
            //        helper.ConsoleCommands.Trigger(args[0], remainingArgs);
            //        helper.SendPublicMessage("Joja run permanently enabled!");
            //    });
        }
    }
}
