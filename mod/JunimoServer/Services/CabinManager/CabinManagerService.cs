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
using StardewValley.Network;
using System;
using System.Collections.Generic;

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
        private static CabinManagerData _data;

        public static CabinManagerData Data
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
                        farmer.WarpToHomeCabin(options.Data.CabinStrategy);
                    }
                }
            }

            farmersInFarmhouse.RemoveWhere(farmerId => !farmersInFarmHouseCurrent.Contains(farmerId));
        }

        public void EnsureAtLeastXCabins()
        {
            var farm = Game1.getFarm();
            var emptyCabinCount = farm.GetCabinHiddenCount();
            var cabinsMissingCount = minEmptyCabins - emptyCabinCount;

            // For now we have hardcoded minEmptyCabins = 1, basically create a new empty cabin whenever the current one was picked
            Monitor.Log($"Cabin check: {{ min: '{minEmptyCabins}', empty: '{emptyCabinCount}', missing: '{cabinsMissingCount}' }}", LogLevel.Debug);

            for (var i = 0; i < cabinsMissingCount; i++)
            {
                Monitor.Log($"Cabin check: building cabin {i + 1}/{cabinsMissingCount}", LogLevel.Debug);
                if (!farm.BuildNewCabin())
                {
                    Monitor.Log($"Cabin check: failed building cabin {i + 1}/{cabinsMissingCount}'", LogLevel.Error);
                }
            }
        }
    }
}
