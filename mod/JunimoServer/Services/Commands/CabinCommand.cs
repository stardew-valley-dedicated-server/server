using JunimoServer.Services.CabinManager;
using JunimoServer.Services.ChatCommands;
using JunimoServer.Services.PersistentOption;
using JunimoServer.Services.Roles;
using JunimoServer.Util;
using StardewModdingAPI;
using StardewValley;
using System;

namespace JunimoServer.Services.Commands
{
    public static class CabinCommand
    {
        public static void Register(IModHelper helper, ChatCommandsService chatCommandsService, RoleService roleSerivce, CabinManagerService cabinService, PersistentOptions options)
        {
            Console.WriteLine("Registering cabin command");
            chatCommandsService.RegisterCommand("cabin",
                "Moves your cabin to the right of your player.\nThis will clear basic debris to make space.",
                (args, msg) => {
                    if (cabinService.options.IsFarmHouseStack)
                    {
                        helper.SendPrivateMessage(msg.SourceFarmer, "Can't move cabin. The host has chosen to keep all cabins in the farmhouse.");
                        return;
                    }

                    var farmer = Game1.GetPlayer(msg.SourceFarmer);

                    if (farmer.currentLocation.Name != "Farm")
                    {
                        helper.SendPrivateMessage(msg.SourceFarmer, "Must be on Farm to move your cabin.");
                        return;
                    }

                    if (roleSerivce.IsPlayerOwner(farmer))
                    {
                        helper.SendPrivateMessage(msg.SourceFarmer, "Can't move cabin as primary admin. (Your cabin is the farmhouse)");
                        return;
                    }

                    var cabin = Game1.getFarm().GetCabin(msg.SourceFarmer);

                    if (cabin == null)
                    {
                        helper.SendPrivateMessage(msg.SourceFarmer, "Can't move cabin. (Your cabin was not found, which should not happen.)");
                        return;
                    }


                    // TODO:
                    // a) When cabin was relocated out of the stack, move a dummy/placeholder/other cabin in place of the stack location
                    // b) Add checks to prevent placing cabin out-of-bounds, over trees, buildings etc.
                    // c) Potentially add preview mode consisting of a few commands? (first, check if we can trigger native building-move mode on clients)
                    //  - 'cabin move [direction=top|right|bottom|left]': Start "ghost" mode, manipulate LocationIntroduction package to show building as ghost without updating warp targets etc?
                    //  - 'cabin cancel': Cancel the move, reset to position from before the ghost mode
                    //  - 'cabin confirm': Confirm the move, update warp targets etc

                    // Place cabin on the right-hand side the farmer
                    cabin.Relocate(farmer.Tile.X + 1, farmer.Tile.Y);
                }
            );
        }
    }
}
