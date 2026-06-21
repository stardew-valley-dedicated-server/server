using System;
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
            "Moves your cabin to the right of your player.\nThis will clear basic debris to make space.\nUse '!cabin reset' to send your cabin back to the shared stack.",
            (args, msg) =>
            {
                if (cabinService.options.IsFarmHouseStack)
                {
                    // FarmhouseStack rejects both move and reset before any subcommand
                    // parsing: cabins stay in the farmhouse, so there is nothing to move
                    // or to send back to a stack.
                    helper.SendPrivateMessage(
                        msg.SourceFarmer,
                        "Can't move cabin. The host has chosen to keep all cabins in the farmhouse."
                    );
                    return;
                }

                if (args.Length > 0 && args[0].Equals("reset", StringComparison.OrdinalIgnoreCase))
                {
                    ResetCabin(helper, cabinService, msg);
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

    /// <summary>
    /// Sends the player's cabin back to the hidden stack and clears their saved-position
    /// intent, undoing a prior !cabin placement. Keyed on the cabin's visibility, not on
    /// whether an intent entry exists: a half-applied reset (intent cleared but cabin still
    /// visible) is recoverable by re-running the command, with no dependence on a later
    /// sweep (which only runs under ExistingCabinBehavior=MoveToStack). The FarmhouseStack
    /// gate is handled by the caller before this runs.
    /// </summary>
    private static void ResetCabin(
        IModHelper helper,
        CabinManagerService cabinService,
        ReceivedMessage msg
    )
    {
        var cabin = Game1.getFarm().GetCabin(msg.SourceFarmer);

        if (cabin == null)
        {
            helper.SendPrivateMessage(
                msg.SourceFarmer,
                "Can't reset cabin. (Your cabin was not found, which should not happen.)"
            );
            return;
        }

        // Clear the intent BEFORE moving the cabin. A throw mid-way then leaves
        // "no intent + still visible", which a re-run of reset fixes (it's keyed on
        // visibility); the reverse order could leave "intent + hidden", pinning a hidden
        // cabin out of every future sweep with no user-visible way to clear it. TryRemove
        // ignores a miss — the dict is a ConcurrentDictionary and may have no entry under
        // None (intent is written but never read) or after a half-applied reset.
        cabinService.Data.PlayerCabinPositions.TryRemove(msg.SourceFarmer, out _);
        cabinService.Data.Write();

        if (cabinService.options.IsNone)
        {
            // None has no hidden stack — cabins live at real visible positions. Clear the
            // (unread) intent entry, but leave the cabin where it is.
            helper.SendPrivateMessage(
                msg.SourceFarmer,
                "Cabin placement reset. (Under this server's strategy your cabin stays where it is.)"
            );
            return;
        }

        if (cabin.IsInHiddenStack())
        {
            helper.SendPrivateMessage(
                msg.SourceFarmer,
                "Nothing to reset — your cabin is already in the shared stack."
            );
            return;
        }

        // SetPosition (not Relocate): same call the bulk movers use to send a cabin to the
        // hidden stack. OnLocationDeltaMessage re-points the cabin's door on the next
        // location introduction, so warps follow the established path.
        cabin.SetPosition(CabinManagerService.HiddenCabinLocation);
        helper.SendPrivateMessage(msg.SourceFarmer, "Cabin reset — it's back in the shared stack.");
    }
}
