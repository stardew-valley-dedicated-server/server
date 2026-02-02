using JunimoServer.Services.CabinManager;
using JunimoServer.Services.ChatCommands;
using JunimoServer.Services.PersistentOption;
using JunimoServer.Services.Roles;
using JunimoServer.Util;
using StardewModdingAPI;
using StardewValley;
using System;
using System.Linq;

namespace JunimoServer.Services.Commands
{
    public static class CabinCommand
    {
        private static readonly string[] CabinStyles = new[]
        {
            "Stone Cabin",
            "Log Cabin",
            "Plank Cabin",
            "Rustic Cabin",
            "Trailer Cabin",
            "Neighbor Cabin",
            "Beach Cabin"
        };

        public static void Register(IModHelper helper, ChatCommandsService chatCommandsService, RoleService roleSerivce, CabinManagerService cabinService, PersistentOptions options)
        {
            chatCommandsService.RegisterCommand("cabin",
                "Manage your cabin. Usage:\n  !cabin here - Move cabin to your location\n  !cabin hide - Move cabin back to hidden stack\n  !cabin style [0-6] - Change cabin style (0=Stone, 1=Log, 2=Plank, 3=Rustic, 4=Trailer, 5=Neighbor, 6=Beach)",
                (args, msg) =>
                {
                    if (cabinService.options.IsFarmHouseStack)
                    {
                        helper.SendPrivateMessage(msg.SourceFarmer, "Can't modify cabin. The host has chosen to keep all cabins in the farmhouse.");
                        return;
                    }

                    var farmer = Game1.GetPlayer(msg.SourceFarmer);

                    if (roleSerivce.IsPlayerOwner(farmer))
                    {
                        helper.SendPrivateMessage(msg.SourceFarmer, "Can't modify cabin as primary admin. (Your cabin is the farmhouse)");
                        return;
                    }

                    var cabin = Game1.getFarm().GetCabin(msg.SourceFarmer);

                    if (cabin == null)
                    {
                        helper.SendPrivateMessage(msg.SourceFarmer, "Can't modify cabin. (Your cabin was not found, which should not happen.)");
                        return;
                    }

                    if (args.Length == 0)
                    {
                        helper.SendPrivateMessage(msg.SourceFarmer, "Usage: !cabin here | !cabin hide | !cabin style [0-6]");
                        return;
                    }

                    var action = args[0].ToLowerInvariant();

                    // Handle "!cabin hide" - move cabin back to hidden stack
                    if (action == "hide")
                    {
                        if (cabin.IsInHiddenStack())
                        {
                            helper.SendPrivateMessage(msg.SourceFarmer, "Your cabin is already hidden.");
                            return;
                        }

                        var hiddenPos = CabinManagerService.HiddenCabinLocation;
                        cabin.Relocate(hiddenPos.X, hiddenPos.Y);
                        helper.SendPrivateMessage(msg.SourceFarmer, "Your cabin has been moved back to the hidden stack.");
                        return;
                    }

                    // Handle "!cabin here" - move cabin to player's location
                    if (action == "here")
                    {
                        if (farmer.currentLocation.Name != "Farm")
                        {
                            helper.SendPrivateMessage(msg.SourceFarmer, "Must be on Farm to move your cabin here.");
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
                        helper.SendPrivateMessage(msg.SourceFarmer, "Your cabin has been moved to your location.");
                        return;
                    }

                    // Handle "!cabin style [0-6]" - change cabin style
                    if (action == "style")
                    {
                        if (args.Length < 2)
                        {
                            var currentStyle = cabin.skinId.Value ?? CabinStyles[0];
                            var styleList = string.Join("\n", CabinStyles.Select((name, i) =>
                            {
                                var suffix = "";
                                if (name == currentStyle) suffix += " (current)";
                                if (i == 0) suffix += " (default)";
                                return $"  {i} = {name}{suffix}";
                            }));
                            helper.SendPrivateMessage(msg.SourceFarmer, $"Usage: !cabin style [0-6]\n{styleList}");
                            return;
                        }

                        if (!int.TryParse(args[1], out var styleIndex) || styleIndex < 0 || styleIndex >= CabinStyles.Length)
                        {
                            helper.SendPrivateMessage(msg.SourceFarmer, $"Invalid style '{args[1]}'. Use 0-6.");
                            return;
                        }

                        var styleName = CabinStyles[styleIndex];
                        cabin.skinId.Value = styleName;
                        helper.SendPrivateMessage(msg.SourceFarmer, $"Your cabin style has been changed to {styleName}.");
                        return;
                    }

                    helper.SendPrivateMessage(msg.SourceFarmer, $"Unknown action '{args[0]}'. Usage: !cabin here | !cabin hide | !cabin style [0-6]");
                }
            );
        }
    }
}
