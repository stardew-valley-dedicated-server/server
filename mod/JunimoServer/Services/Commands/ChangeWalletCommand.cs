using JunimoServer.Services.ChatCommands;
using JunimoServer.Services.Roles;
using JunimoServer.Util;
using StardewModdingAPI;
using StardewValley;
using System;

namespace JunimoServer.Services.Commands
{
    public class ChangeWalletCommand
    {
        public static void Register(IModHelper helper, ChatCommandsService chatCommandsService, RoleService roleService)
        {
            chatCommandsService.RegisterCommand("changewallet",
                "Type \"!changewallet shared\" or \"!changewallet separate\" to switch wallet mode at the end of the day.",
                (args, msg) =>
                {
                    if (!roleService.IsPlayerAdmin(msg.SourceFarmer))
                    {
                        helper.SendPrivateMessage(msg.SourceFarmer, "You are not an admin.");
                        return;
                    }

                    bool wantSeparate;
                    if (args.Length == 1 && args[0].Equals("separate", StringComparison.OrdinalIgnoreCase))
                    {
                        wantSeparate = true;
                    }
                    else if (args.Length == 1 && args[0].Equals("shared", StringComparison.OrdinalIgnoreCase))
                    {
                        wantSeparate = false;
                    }
                    else
                    {
                        helper.SendPrivateMessage(msg.SourceFarmer,
                            "Usage: !changewallet shared or !changewallet separate");
                        return;
                    }

                    // Mid-transition, tonight's flip may already have run and the wallet mode
                    // may be about to change under us.
                    var dayTransitionActive =
                        Game1.newDaySync != null && Game1.newDaySync.hasInstance() && !Game1.newDaySync.hasFinished();
                    if (dayTransitionActive)
                    {
                        helper.SendPrivateMessage(msg.SourceFarmer,
                            "Cannot change wallet mode during a day transition. Try again shortly.");
                        return;
                    }

                    // Schedule via vanilla's overnight flip (Game1.cs:8203), which runs between
                    // newDaySync barriers where no client has menus or transactions open.
                    var isSeparate = Game1.player.team.useSeparateWallets.Value;
                    var flipScheduled = Game1.player.changeWalletTypeTonight.Value;
                    var actorName = helper.GetFarmerNameById(msg.SourceFarmer) ?? Game1.player.Name;

                    if (wantSeparate == isSeparate)
                    {
                        if (flipScheduled)
                        {
                            Game1.player.changeWalletTypeTonight.Value = false;
                            helper.GetMultiplayer().globalChatInfoMessage(
                                isSeparate ? "MergeWalletsCancel" : "SeparateWalletsCancel", actorName);
                        }
                        else
                        {
                            helper.SendPrivateMessage(msg.SourceFarmer,
                                wantSeparate ? "Wallets are already separate." : "Wallets are already shared.");
                        }
                        return;
                    }

                    if (flipScheduled)
                    {
                        helper.SendPrivateMessage(msg.SourceFarmer,
                            wantSeparate
                                ? "Wallets are already scheduled to be separated tonight."
                                : "Wallets are already scheduled to be merged tonight.");
                        return;
                    }

                    Game1.player.changeWalletTypeTonight.Value = true;
                    helper.GetMultiplayer().globalChatInfoMessage(
                        wantSeparate ? "SeparateWallets" : "MergeWallets", actorName);
                });
        }
    }
}
