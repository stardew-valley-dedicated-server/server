using JunimoServer.Services.ChatCommands;
using JunimoServer.Services.Roles;
using StardewModdingAPI;

namespace JunimoServer.Services.Commands
{
    public static class ConsoleCommand
    {
        public static void Register(IModHelper helper, ChatCommandsService chatCommandsService, RoleService roleService)
        {
            //chatCommandsService.RegisterCommand("console",
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
