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
/// Admin chat command for the staged strategy migration: '!migrate place' stages the next
/// placement at the admin's position + (1,0) — the same ergonomics as '!cabin'. The rest of
/// the migration lifecycle (start/status/commit/abort) lives on the 'cabins migrate' console
/// command; this in-game command exists so an admin can walk the farm and pick spots visually.
/// </summary>
public static class MigrateCommand
{
    public static void Register(
        IModHelper helper,
        ChatCommandsService chatCommandsService,
        RoleService roleService,
        CabinManagerService cabinService
    )
    {
        chatCommandsService.RegisterCommand(
            "migrate",
            "Admin: '!migrate place' stages the next migration cabin to the right of your player.",
            (args, msg) =>
            {
                if (!roleService.IsPlayerAdmin(msg.SourceFarmer))
                {
                    helper.SendPrivateMessage(msg.SourceFarmer, "You are not an admin.");
                    return;
                }

                if (
                    args.Length == 0
                    || !args[0].Equals("place", StringComparison.OrdinalIgnoreCase)
                )
                {
                    helper.SendPrivateMessage(
                        msg.SourceFarmer,
                        "Usage: !migrate place (stages the next migration cabin at your position)."
                    );
                    return;
                }

                var farmer = Game1.GetPlayer(msg.SourceFarmer);
                if (farmer?.currentLocation?.Name != "Farm")
                {
                    helper.SendPrivateMessage(
                        msg.SourceFarmer,
                        "Must be on Farm to place a migration cabin."
                    );
                    return;
                }

                // Same placement anchor as !cabin: top-left lands one tile to the right.
                var topLeft = new Point((int)farmer.Tile.X + 1, (int)farmer.Tile.Y);
                cabinService.TryPlaceMigration(topLeft, manual: true, out var message);
                helper.SendPrivateMessage(msg.SourceFarmer, message);
            }
        );
    }
}
