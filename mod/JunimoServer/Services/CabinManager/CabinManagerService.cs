using HarmonyLib;
using JunimoServer.Services.MessageInterceptors;
using JunimoServer.Services.PersistentOption;
using JunimoServer.Services.Roles;
using JunimoServer.Services.ServerOptim;
using JunimoServer.Util;
using Microsoft.Xna.Framework;
using Netcode;
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
        public CabinManagerData Data
        {
            get => _cabinManagerData;
            set
            {
                _cabinManagerData = value;
            }
        }

        public static readonly Point HiddenCabinLocation = new Point(-20, -20);

        public readonly PersistentOptions options;

        private readonly RoleService roleService;

        private static readonly int minEmptyCabins = 1;

        private readonly HashSet<long> farmersInFarmhouse = new HashSet<long>();


        // Static reference ONLY for Harmony patches (unavoidable)
        private static CabinManagerService _instance;

        // Instance data - NOT static
        private CabinManagerData _cabinManagerData;

        public CabinManagerService(IModHelper helper, IMonitor monitor, Harmony harmony, RoleService roleService, MessageInterceptorsService messageInterceptorsService, PersistentOptions options) : base(helper, monitor)
        {
            _instance = this;

            this.roleService = roleService;
            this.options = options;

            Data = new CabinManagerData(helper, monitor);

            Helper.Events.GameLoop.SaveLoaded += OnSaveLoaded;
            Helper.Events.GameLoop.UpdateTicked += OnTicked;

            // Disable default starting cabin logic, we handle it
            harmony.Patch(
                original: AccessTools.Method(typeof(GameLocation), nameof(GameLocation.BuildStartingCabins)),
                prefix: new HarmonyMethod(typeof(ServerOptimizerOverrides), nameof(ServerOptimizerOverrides.Disable_Prefix))
            );

            harmony.Patch(
                original: AccessTools.Method(typeof(GameServer), nameof(GameServer.sendServerIntroduction)),
                postfix: new HarmonyMethod(typeof(CabinManagerService), nameof(OnServerJoined_Postfix))
            );

            // Hijack outgoing messages
            messageInterceptorsService
                .Add(Multiplayer.locationIntroduction, OnLocationIntroductionMessage)
                .Add(Multiplayer.locationDelta, OnLocationDeltaMessage);
        }

        private void OnSaveLoaded(object sender, SaveLoadedEventArgs e)
        {
            // Load saved cabin data
            // TODO: Check for proper persistence, see https://github.com/stardew-valley-dedicated-server/server/issues/64
            Data.Read();
            EnsureAtLeastXCabins();
        }

        private void OnTicked(object sender, UpdateTickedEventArgs e)
        {
            MonitorFarmhouse();
        }

        private static void OnServerJoined_Postfix(long peer)
        {
            _instance?.OnServerJoined(peer);
        }

        private void OnServerJoined(long peer)
        {
            AddPeer(peer);
            EnsureAtLeastXCabins();
        }

        //private void OnServerJoined(object sender, ServerJoinedEventArgs e)
        //{
        //    AddPeer(e.PeerId);
        //    EnsureAtLeastXCabins();
        //}

        private void OnLocationIntroductionMessage(MessageContext context)
        {
            // Parse message
            var forceCurrentLocation = context.Reader.ReadBoolean();
            var netRootLocation = NetRoot<GameLocation>.Connect(context.Reader);

            // Check location
            if (netRootLocation.Value is not Farm netRootFarm)
            {
                return;
            }

            GameLocation farm;

            if (this.options.IsFarmHouseStack)
            {
                // Farmhouse stacking strategy:
                // Update warp coordinates on the server. Since there is only a single
                // farmhouse building, we adjust its warps while leaving all cabins in
                // `HiddenCabinLocation`.
                farm = Game1.getFarm();
                farm.GetCabin(context.PeerId).SetWarpsToFarmFarmhouseDoor();
            }
            else
            {
                // Cabin stacking strategy:
                // Relocate the player's cabin client-side so only the owner sees it.
                // The cabin is moved from `HiddenCabinLocation` to `DefaultCabinLocation`,
                // making it visible and interactable for the owner only.
                // This allows individual cabin repainting even while stacked, unlike the
                // farmhouse strategy.
                farm = netRootFarm;
                farm.GetCabin(context.PeerId).Relocate(StackLocation.Create(_cabinManagerData).ToPoint());
            }

            // Update the outgoing message
            context.ModifiedMessage = NetworkHelper.CreateMessageLocationIntroduction(context.PeerId, farm.Root, forceCurrentLocation);
        }

        private void OnLocationDeltaMessage(MessageContext context)
        {
            // TODO: Do we have to do it on each delta, or can it be done once on assignment?
            // Either way, document reasons/whatabouts here, e.g. "this is the only message we know about the location"
            if (NetworkHelper.IsLocationDeltaMessageForLocation(context, out Cabin cabin))
            {
                if (this.options.IsFarmHouseStack)
                {
                    cabin.SetWarpsToFarmFarmhouseDoor();
                }
                else
                {
                    cabin.SetWarpsToFarmCabinDoor();
                }
            }
        }

        private void AddPeer(long peerId)
        {
            Monitor.Log($"Adding peer '{peerId}'", LogLevel.Debug);
            Data.AllPlayerIdsEverJoined.Add(peerId);

            // Save/persist/store cabin data
            // TODO: Should happen after adding cabin, so that we can store the moved cabin location alongside.
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

                    // TODO: This handling is rather awkward and inefficient, we should act and not react!
                    // a) Prevent entering instead of porting player out after entering the farmhouse
                    // b) Send request to allow entering to farmhouse owner, could this be useful?
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

            // TODO: Currently using hardcoded minEmptyCabins = 1, essentially creates
            // a new empty cabin as soon as the "current" free cabin got picked...
            // lets see if that works with concurrency.
            Monitor.Log($"Cabin check:", LogLevel.Debug);
            Monitor.Log($"\tRequired: {minEmptyCabins}", LogLevel.Debug);
            Monitor.Log($"\tCurrent: {emptyCabinCount}", LogLevel.Debug);
            Monitor.Log($"\tMissing: {cabinsMissingCount}", LogLevel.Debug);

            for (var i = 0; i < cabinsMissingCount; i++)
            {
                Monitor.Log($"Cabin check: building cabin {i + 1}/{cabinsMissingCount}", LogLevel.Trace);

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
