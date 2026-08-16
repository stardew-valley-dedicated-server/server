using System;
using JunimoServer.Services.CabinManager;
using JunimoServer.Services.ChatCommands;
using JunimoServer.Services.Roles;
using JunimoServer.Util;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;

namespace JunimoServer.Services.Commands;

/// <summary>
/// Admin chat command for the CabinStack shared stack spot: '!stackspot' reports the
/// effective spot (override vs map default, obstruction), '!stackspot place' sets it to
/// the admin's position + (1,0) — the same ergonomics as '!cabin' and '!migrate place'.
/// Console equivalent: 'cabins stackspot [&lt;x&gt; &lt;y&gt;]'.
/// </summary>
public static class StackSpotCommand
{
    public static void Register(
        IModHelper helper,
        ChatCommandsService chatCommandsService,
        RoleService roleService,
        CabinManagerService cabinService
    )
    {
        chatCommandsService.RegisterCommand(
            "stackspot",
            "Admin: '!stackspot' shows the shared stack spot, '!stackspot place' moves it "
                + "to the right of your player.",
            (args, msg) =>
            {
                if (!roleService.IsPlayerAdmin(msg.SourceFarmer))
                {
                    helper.SendPrivateMessage(msg.SourceFarmer, "You are not an admin.");
                    return;
                }

                if (args.Length == 0)
                {
                    var status = cabinService.GetStackSpotStatus();
                    if (status == null)
                    {
                        helper.SendPrivateMessage(
                            msg.SourceFarmer,
                            "The stack spot applies only to the CabinStack strategy."
                        );
                        return;
                    }

                    var s = status.Value;
                    helper.SendPrivateMessage(
                        msg.SourceFarmer,
                        $"Stack spot: ({s.Spot.X},{s.Spot.Y}) "
                            + (s.IsOverride ? "(override)" : "(map default)")
                            + (s.IsObstructed ? $" — obstructed: {s.ObstructionReason}" : "")
                    );
                    return;
                }

                if (!args[0].Equals("place", StringComparison.OrdinalIgnoreCase))
                {
                    helper.SendPrivateMessage(
                        msg.SourceFarmer,
                        "Usage: !stackspot (show) or !stackspot place (set to your position)."
                    );
                    return;
                }

                var farmer = Game1.GetPlayer(msg.SourceFarmer);
                if (farmer.currentLocation.Name != "Farm")
                {
                    helper.SendPrivateMessage(
                        msg.SourceFarmer,
                        "Must be on Farm to place the stack spot."
                    );
                    return;
                }

                // Same placement anchor as !cabin: top-left lands one tile to the right.
                var topLeft = new Point((int)farmer.Tile.X + 1, (int)farmer.Tile.Y);
                cabinService.TrySetStackSpot(topLeft, out var message);
                helper.SendPrivateMessage(msg.SourceFarmer, message);
            }
        );
    }
}
