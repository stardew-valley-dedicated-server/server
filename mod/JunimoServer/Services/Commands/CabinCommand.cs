using System;
using System.Linq;
using JunimoServer.Services.CabinManager;
using JunimoServer.Services.ChatCommands;
using JunimoServer.Services.PersistentOption;
using JunimoServer.Util;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.Locations;

namespace JunimoServer.Services.Commands
{
    public static class CabinCommand
    {
        public static void Register(IModHelper helper, IChatCommandApi chatCommandApi,
            PersistentOptions options, IMonitor monitor)
        {
            chatCommandApi.RegisterCommand("cabin",
                "Moves your cabin to the right of your player.\nThis will clear basic debris to make space.",
                (args, msg) =>
                {
                    if (options.Data.CabinStrategy == CabinStrategy.FarmhouseStack)
                    {
                        helper.SendPrivateMessage(msg.SourceFarmer, "Can't move cabin. The host has chosen to keep all cabins in the farmhouse.");
                        return;
                    }
                    var fromPlayer = Game1.getOnlineFarmers()
                        .First(farmer => farmer.UniqueMultiplayerID == msg.SourceFarmer);

                    if (fromPlayer.currentLocation.Name != "Farm")
                    {
                        helper.SendPrivateMessage(msg.SourceFarmer, "Must be on Farm to move your cabin.");
                        return;
                    }

                    var isOwnersCabin = helper.GetOwnerPlayerId() == fromPlayer.UniqueMultiplayerID;
                    if (isOwnersCabin)
                    {
                        helper.SendPrivateMessage(msg.SourceFarmer, "Can't move cabin as primary admin. (Your cabin is the farmhouse)");
                        return;
                    }


                    var cabinBlueprint = Building.CreateInstanceFromId("Log Cabin", Vector2.Zero);
                    var cabinLocation = new Vector2(fromPlayer.Tile.X + 1, fromPlayer.Tile.Y);

                    for (var x = 0; x < cabinBlueprint.tilesWide.Value; x++)
                    {
                        for (var y = 0; y < cabinBlueprint.tilesHigh.Value; y++)
                        {
                            var currentTileLocation = new Vector2(cabinLocation.X + x, cabinLocation.Y + y);

                            fromPlayer.currentLocation.terrainFeatures.Remove(currentTileLocation);
                            fromPlayer.currentLocation.objects.Remove(currentTileLocation);

                        }
                    }

                    var farmersCabin = Game1.getFarm().buildings.First(building => building.isCabin && ((Cabin)(building.indoors.Value)).owner.UniqueMultiplayerID == msg.SourceFarmer);
                    farmersCabin.tileX.Value = (int)cabinLocation.X;
                    farmersCabin.tileY.Value = (int)cabinLocation.Y;

                    var indoor = (Cabin)farmersCabin.indoors.Value;
                    foreach (var warp in indoor.warps.Where(warp => warp.TargetName == "Farm"))
                    {
                        warp.TargetX = (int)cabinLocation.X + 2;
                        warp.TargetY = (int)cabinLocation.Y + 2;
                    }
                }
            );
        }
    }
}