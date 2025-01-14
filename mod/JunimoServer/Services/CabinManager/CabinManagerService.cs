using HarmonyLib;
using JunimoServer.Services.MessageInterceptors;
using JunimoServer.Services.PersistentOption;
using JunimoServer.Services.Roles;
using JunimoServer.Services.ServerOptim;
using JunimoServer.Util;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.Locations;
using StardewValley.Network;
using System;
using System.Collections.Generic;
using System.Linq;

namespace JunimoServer.Services.CabinManager
{
    public class ServerJoinedEventArgs : EventArgs
    {
        private long peerId;

        public long PeerId => peerId;

        public ServerJoinedEventArgs(long peerId)
        {
            this.peerId = peerId;
        }
    }

    public delegate void ServerJoinedHandler(object sender, ServerJoinedEventArgs e);

    public class CabinManagerService : ModService
    {
        private CabinManagerData _data;

        public CabinManagerData Data
        {
            get => _data;
            set
            {
                CabinManagerOverrides.cabinManagerData = _data = value;
            }
        }

        public static readonly Point HiddenCabinLocation = new Point(-20, -20);

        public readonly PersistentOptions options;
        private readonly RoleService roleService;

        private const int minEmptyCabins = 1;

        private readonly HashSet<long> farmersInFarmhouse = new HashSet<long>();

        public CabinManagerService(IModHelper helper, IMonitor monitor, Harmony harmony, RoleService roleService, MessageInterceptorsService messageInterceptorsService, PersistentOptions options) : base(helper, monitor)
        {
            this.roleService = roleService;
            this.options = options;

            Data = new CabinManagerData(helper, monitor);
            CabinManagerOverrides.Initialize(options, OnServerJoined);

            Helper.Events.GameLoop.SaveLoaded += OnSaveLoaded;
            Helper.Events.GameLoop.UpdateTicked += OnTicked;

            harmony.Patch(
                original: AccessTools.Method(typeof(GameLocation), nameof(GameLocation.BuildStartingCabins)),
                prefix: new HarmonyMethod(typeof(ServerOptimizerOverrides), nameof(ServerOptimizerOverrides.Disable_Prefix))
            );

            harmony.Patch(
                original: AccessTools.Method(typeof(GameServer), nameof(GameServer.sendServerIntroduction)),
                postfix: new HarmonyMethod(typeof(CabinManagerOverrides), nameof(CabinManagerOverrides.sendServerIntroduction_Postfix))
            );

            messageInterceptorsService
                .Add(Multiplayer.locationIntroduction, CabinManagerOverrides.OnLocationIntroductionMessage)
                .Add(Multiplayer.locationDelta, CabinManagerOverrides.OnLocationDeltaMessage);
        }

        private void OnSaveLoaded(object sender, SaveLoadedEventArgs e)
        {
            Data.Read();
            EnsureAtLeastXCabins();
        }

        private void OnServerJoined(object sender, ServerJoinedEventArgs e)
        {
            AddPeer(e.PeerId);
            EnsureAtLeastXCabins();
        }

        private void OnTicked(object sender, UpdateTickedEventArgs e)
        {
            MonitorFarmhouse();
        }

        private void AddPeer(long peerId)
        {
            Monitor.Log($"Adding peer '{peerId}'", LogLevel.Debug);
            Data.AllPlayerIdsEverJoined.Add(peerId);
            Data.Write();
        }

        private void MonitorFarmhouse()
        {
            if (!Game1.hasLoadedGame)
            {
                return;
            }

            var farmersInFarmHouseCurrent = new HashSet<long>();
            var farmers = Game1.getLocationFromName("Farmhouse").farmers;

            foreach (var farmer in farmers)
            {
                farmersInFarmHouseCurrent.Add(farmer.UniqueMultiplayerID);
            }

            foreach (var farmer in farmers)
            {
                if (!farmersInFarmhouse.Contains(farmer.UniqueMultiplayerID))
                {
                    farmersInFarmhouse.Add(farmer.UniqueMultiplayerID);

                    // TODO: a) Prevent entering instead of porting player out after entering the farmhouse
                    // TODO: b) Send request to allow entering to farmhouse owner, could this be useful?
                    if (!roleService.IsPlayerOwner(farmer))
                    {
                        Game1.Multiplayer.sendChatMessage(
                            LocalizedContentManager.CurrentLanguageCode,
                            "Can't enter main building, porting to your own cabin",
                            farmer.UniqueMultiplayerID
                        );

                        farmer.WarpHome();
                    }
                }
            }

            farmersInFarmhouse.RemoveWhere(farmerId => !farmersInFarmHouseCurrent.Contains(farmerId));
        }

        public void EnsureAtLeastXCabins()
        {
            var farm = Game1.getFarm();
            var emptyCabinCount = GetCabinHiddenCount(farm);
            var cabinsMissingCount = minEmptyCabins - emptyCabinCount;

            // TODO: Currently using hardcoded minEmptyCabins = 1, essentially creates a new empty cabin as soon as the "current" free cabin got picked... lets see if that works with concurrency.
            Monitor.Log($"Cabin check: {{ min: '{minEmptyCabins}', empty: '{emptyCabinCount}', missing: '{cabinsMissingCount}' }}", LogLevel.Debug);

            for (var i = 0; i < cabinsMissingCount; i++)
            {
                Monitor.Log($"Cabin check: building cabin {i + 1}/{cabinsMissingCount}", LogLevel.Debug);

                if (!BuildNewCabin(farm))
                {
                    Monitor.Log($"Cabin check: failed building cabin {i + 1}/{cabinsMissingCount}'", LogLevel.Error);
                }
            }
        }

        private int GetCabinHiddenCount(GameLocation farm)
        {
            return farm.buildings
                .Where(building => building.isCabin)
                .Count(cabin => !Data.AllPlayerIdsEverJoined.Contains(cabin.GetIndoors<Cabin>().owner.UniqueMultiplayerID));
        }

        public bool BuildNewCabin(GameLocation location)
        {
            var cabinTilePosition = HiddenCabinLocation.ToVector2();

            var cabin = new Building("Cabin", cabinTilePosition);
            cabin.skinId.Value = "Log Cabin";
            cabin.magical.Value = true;
            cabin.daysOfConstructionLeft.Value = 0;
            cabin.load();

            // Cabin now exists within the game data, but still needs to be placed in its desired location
            if (location.buildStructure(cabin, cabinTilePosition, Game1.player, true))
            {
                // Usually cabins are placed out-of-bounds, so this is just for good measure
                cabin.ClearTerrainBelow();
                return true;
            }

            return false;
        }
    }
}
