using JunimoServer.Services.CabinManager;
using JunimoServer.Services.ChatCommands;
using JunimoServer.Services.PersistentOption;
using JunimoServer.Util;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;

namespace JunimoServer.Services.Commands;

public static class CabinCommand
{
    public static void Register(
        IModHelper helper,
        ChatCommandsService chatCommandsService,
        CabinManagerService cabinService,
        PersistentOptions options
    )
    {
        chatCommandsService.RegisterCommand(
            "cabin",
            "Moves your cabin to the right of your player.\nThis will clear basic debris to make space.",
            (args, msg) =>
            {
                if (cabinService.options.IsFarmHouseStack)
                {
                    helper.SendPrivateMessage(
                        msg.SourceFarmer,
                        "Can't move cabin. The host has chosen to keep all cabins in the farmhouse."
                    );
                    return;
                }

                var farmer = Game1.GetPlayer(msg.SourceFarmer);

                if (farmer.currentLocation.Name != "Farm")
                {
                    helper.SendPrivateMessage(
                        msg.SourceFarmer,
                        "Must be on Farm to move your cabin."
                    );
                    return;
                }

                var cabin = Game1.getFarm().GetCabin(msg.SourceFarmer);

                if (cabin == null)
                {
                    helper.SendPrivateMessage(
                        msg.SourceFarmer,
                        "Can't move cabin. (Your cabin was not found, which should not happen.)"
                    );
                    return;
                }

                // TODO:
                // a) Potentially add preview mode consisting of a few commands? (first, check if we can trigger native building-move mode on clients)
                //  - 'cabin move [direction=top|right|bottom|left]': Start "ghost" mode, manipulate LocationIntroduction package to show building as ghost without updating warp targets etc?
                //  - 'cabin cancel': Cancel the move, reset to position from before the ghost mode
                //  - 'cabin confirm': Confirm the move, update warp targets etc

                // Place cabin on the right-hand side of the farmer
                var topLeft = new Point((int)farmer.Tile.X + 1, (int)farmer.Tile.Y);

                if (
                    !CabinPlacementValidator.TryValidate(
                        Game1.getFarm(),
                        cabin,
                        topLeft,
                        out var reason
                    )
                )
                {
                    helper.SendPrivateMessage(msg.SourceFarmer, $"Can't move cabin: {reason}.");
                    return;
                }

                // Record the player's intent BEFORE relocating. The building's
                // tileX/tileY is what persists the position to the save; this map
                // only records that the move was intentional, so the MoveToStack /
                // strategy-switch bulk movers don't sweep it back into the hidden
                // stack on the next load. Guard above precedes this write so a
                // rejected move records no false intent.
                cabinService.Data.PlayerCabinPositions[msg.SourceFarmer] = topLeft.ToVector2();
                cabinService.Data.Write();

                cabin.Relocate(topLeft);
            }
        );
    }
}
