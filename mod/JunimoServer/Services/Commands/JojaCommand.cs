using JunimoServer.Services.AlwaysOnServer;
using JunimoServer.Services.ChatCommands;
using JunimoServer.Services.Roles;
using JunimoServer.Util;
using StardewModdingAPI;
using StardewValley;

namespace JunimoServer.Services.Commands
{
    public static class JojaCommand
    {
        public static void Register(IModHelper helper, IChatCommandApi chatCommandApi, RoleService roleService,
            AlwaysOnConfig alwaysOnConfig)
        {
            chatCommandApi.RegisterCommand("joja",
                "Type \"!joja IRREVERSIBLY_ENABLE_JOJA_RUN\" to enable joja and disable the standard community center forever.",
                (args, msg) =>
                {
                    if (!roleService.IsPlayerOwner(msg.SourceFarmer))
                    {
                        helper.SendPrivateMessage(msg.SourceFarmer, "Only the owner of the server can enable Joja.");
                        return;
                    }

                    if (args.Length != 1 || (args.Length == 1 && args[0] != "IRREVERSIBLY_ENABLE_JOJA_RUN"))
                    {
                        helper.SendPrivateMessage(msg.SourceFarmer,
                            "Invalid use of command. You must type \"!joja IRREVERSIBLY_ENABLE_JOJA_RUN\" exactly.");
                        return;
                    }

                    alwaysOnConfig.IsCommunityCenterRun = false;
                    helper.SendPublicMessage("Joja run permanently enabled!");
                });
        }
    }
}