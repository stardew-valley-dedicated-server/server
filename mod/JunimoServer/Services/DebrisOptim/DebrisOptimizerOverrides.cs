using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using System.Collections.Generic;
using Netcode;

namespace JunimoServer.Services.DebrisOptim
{
    // TODO: Low prio, but needs to be understood and fixed up
    public class DebrisOptimizerOverrides
    {
        //private static IMonitor _monitor;
        //private static IModHelper _helper;
        //private static Queue<DebrisLocation> _debrisQueue = new Queue<DebrisLocation>();
        //private static HashSet<Debris> _debrisSet = new HashSet<Debris>();
        //private static int _maxUpdatesPerTick = 1;
        //private static int _updatesThisTick = 0;
        //private static Multiplayer _multiplayer;

        public class DebrisLocation
        {
            public Debris Debris { get; set; }
            public GameLocation Location { get; set; }

            public DebrisLocation(Debris debris, GameLocation location)
            {
                Debris = debris;
                Location = location;
            }
        }


        public static void Initialize(IMonitor monitor, IModHelper helper)
        {
            //_monitor = monitor;
            //_helper = helper;
        }

        // ReSharper disable twice InconsistentNaming
        //public static bool updateChunks_Prefix(Debris __instance, GameTime time, GameLocation location,
        //    ref bool __result)
        //{
        //    if (__instance.debrisType.Value != Debris.DebrisType.RESOURCE)
        //    {
        //        return true;
        //    }

        //    if (_updatesThisTick >= _maxUpdatesPerTick)
        //    {
        //        __result = false;
        //        return false;
        //    }


        //    Vector2 debrisPos = new Vector2();
        //    foreach (Chunk chunk in __instance.Chunks)
        //    {
        //        debrisPos += chunk.position.Value;
        //    }

        //    debrisPos /= __instance.Chunks.Count;

        //    Farmer closestFarmer = null;
        //    var closestDistance = float.MaxValue;
        //    foreach (Farmer farmer in location.farmers)
        //    {
        //        var distance = (farmer.Position - debrisPos).LengthSquared();
        //        if (distance < closestDistance)
        //        {
        //            closestFarmer = farmer;
        //            closestDistance = distance;
        //        }
        //    }

        //    if (closestFarmer == null)
        //    {
        //        __result = false;
        //        return false;
        //    }

        //    var added = false;
        //    _multiplayer ??= _helper.Reflection.GetField<Multiplayer>(typeof(Game1), "multiplayer").GetValue();

        //    for (int index = __instance.Chunks.Count - 1; index >= 0; --index)
        //    {
        //        var chunk = __instance.Chunks[index];

        //        string itemId =
        //            __instance.debrisType.Value is Debris.DebrisType.ARCHAEOLOGY or Debris.DebrisType.OBJECT
        //                ? chunk.randomOffset.ToString()
        //                : (chunk.randomOffset - chunk.randomOffset % 2).ToString();

        //        Item item;
        //        if (chunk.randomOffset != 93 && chunk.randomOffset != 94)

        //            item = new Object(Vector2.Zero, itemId, 1)
        //            {
        //                Quality = __instance.itemQuality,
        //            };
        //        else
        //            item = new Torch(Vector2.Zero, 1, itemId);

        //        var farmerCanAccept = closestFarmer.couldInventoryAcceptThisItem(item);
        //        if (farmerCanAccept)
        //        {
        //            closestFarmer.addItemToInventory(item);
        //            __instance.Chunks.RemoveAt(index);
        //            added = true;
        //            //TODO: keep track of whos inventory was changed and batch it so farmer delta broadcasting is done in Update()
        //        }
        //    }

        //    _updatesThisTick++;
        //    __result = __instance.Chunks.Count == 0;

        //    if (added)
        //    {
        //        var farmerRoot = _multiplayer.farmerRoot(closestFarmer.UniqueMultiplayerID);
        //        var delta = _multiplayer.writeObjectDeltaBytes(farmerRoot);
        //        foreach (var otherFarmer in Game1.otherFarmers)
        //        {
        //            if (otherFarmer.Value.UniqueMultiplayerID != Game1.player.UniqueMultiplayerID)
        //                otherFarmer.Value.queueMessage(0, closestFarmer,
        //                    closestFarmer.UniqueMultiplayerID, delta);
        //        }
        //    }

        //    return false;
        //}

        public static void Update()
        {
            //_updatesThisTick = 0;
        }
    }
}