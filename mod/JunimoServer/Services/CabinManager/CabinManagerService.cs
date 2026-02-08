using HarmonyLib;
using JunimoServer.Services.Lobby;
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
            if (_instance != null)
                throw new InvalidOperationException("CabinManagerService already initialized - only one instance allowed");

            _instance = this;

            this.roleService = roleService;
            this.options = options;

            Data = new CabinManagerData(helper, monitor);

            Helper.Events.GameLoop.SaveLoaded += OnSaveLoaded;

            // For None strategy, let vanilla handle starting cabins and skip message
            // interception and farmhouse monitoring — cabins are real and visible.
            if (!options.IsNone)
            {
                // Disable default starting cabin logic, we handle it
                harmony.Patch(
                    original: AccessTools.Method(typeof(GameLocation), nameof(GameLocation.BuildStartingCabins)),
                    prefix: new HarmonyMethod(typeof(ServerOptimizerOverrides), nameof(ServerOptimizerOverrides.Disable_Prefix))
                );

                // Hijack outgoing messages for cabin warp manipulation
                messageInterceptorsService
                    .Add(Multiplayer.locationIntroduction, OnLocationIntroductionMessage)
                    .Add(Multiplayer.locationDelta, OnLocationDeltaMessage);

                // Monitor farmhouse access — only the server host can enter (no human players)
                Helper.Events.GameLoop.UpdateTicked += OnTicked;
            }

            // Always hook player join — needed for peer tracking and auto-cabin creation
            harmony.Patch(
                original: AccessTools.Method(typeof(GameServer), nameof(GameServer.sendServerIntroduction)),
                postfix: new HarmonyMethod(typeof(CabinManagerService), nameof(OnServerJoined_Postfix))
            );
        }

        private void OnSaveLoaded(object sender, SaveLoadedEventArgs e)
        {
            Data.Read();

            // Detect and handle strategy changes between runs
            DetectAndMigrateStrategyChange();

            // Register existing cabin owners from imported saves
            SyncExistingCabins();

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

        #region Strategy Change Migration

        private void DetectAndMigrateStrategyChange()
        {
            var previousStrategy = options.PreviousCabinStrategy;
            var currentStrategy = options.Data.CabinStrategy;

            if (previousStrategy == currentStrategy)
            {
                return;
            }

            Monitor.Log($"CabinStrategy changed from {previousStrategy} to {currentStrategy}, migrating cabins...", LogLevel.Warn);
            MigrateCabins(previousStrategy, currentStrategy);
        }

        private void MigrateCabins(CabinStrategy from, CabinStrategy to)
        {
            var farm = Game1.getFarm();
            bool fromUsesHidden = (from == CabinStrategy.CabinStack || from == CabinStrategy.FarmhouseStack);
            bool toUsesHidden = (to == CabinStrategy.CabinStack || to == CabinStrategy.FarmhouseStack);

            if (fromUsesHidden && !toUsesHidden)
            {
                // Stacked → None: move hidden cabins to visible farm positions
                var hiddenCabins = farm.buildings
                    .Where(b => b.isCabin && b.IsInHiddenStack())
                    .ToList();

                foreach (var cabin in hiddenCabins)
                {
                    var nextPos = FarmCabinPositions.GetNextAvailablePosition(farm);
                    if (nextPos.HasValue)
                    {
                        cabin.Relocate(nextPos.Value);
                        Monitor.Log($"  Migrated cabin to ({nextPos.Value.X}, {nextPos.Value.Y})", LogLevel.Info);
                    }
                    else
                    {
                        Monitor.Log("  No available farm position for cabin migration", LogLevel.Error);
                    }
                }
            }
            else if (!fromUsesHidden && toUsesHidden)
            {
                // None → Stacked: move visible cabins to hidden stack
                var visibleCabins = farm.buildings
                    .Where(b => b.isCabin && !b.IsInHiddenStack())
                    .ToList();

                foreach (var cabin in visibleCabins)
                {
                    cabin.SetPosition(HiddenCabinLocation);
                    Monitor.Log($"  Migrated cabin to hidden stack", LogLevel.Info);
                }
            }
            // Stacked ↔ Stacked: no relocation needed, only warp behavior changes
        }

        #endregion

        #region Existing Cabin Import Handling

        private void SyncExistingCabins()
        {
            var farm = Game1.getFarm();
            var allCabins = farm.buildings.Where(b => b.isCabin).ToList();
            var syncedCount = 0;

            foreach (var cabin in allCabins)
            {
                var indoors = cabin.GetIndoors<Cabin>();
                if (indoors?.owner == null)
                {
                    continue;
                }

                // Only sync cabins that are actually claimed by a real player.
                // Unassigned cabins have auto-generated UniqueMultiplayerIDs but empty userIDs.
                var owner = indoors.owner;
                var ownerId = owner.UniqueMultiplayerID;
                if (ownerId != 0 && !string.IsNullOrEmpty(owner.userID.Value) && Data.AllPlayerIdsEverJoined.Add(ownerId))
                {
                    syncedCount++;
                }
            }

            if (syncedCount > 0)
            {
                Monitor.Log($"Synced {syncedCount} existing cabin owner(s) from save", LogLevel.Info);
                Data.Write();
            }

            // Handle ExistingCabinBehavior for stacked strategies
            if (options.UsesHiddenCabins && options.Data.ExistingCabinBehavior == ExistingCabinBehavior.MoveToStack)
            {
                var visibleCabins = allCabins.Where(b => !b.IsInHiddenStack()).ToList();
                if (visibleCabins.Count > 0)
                {
                    Monitor.Log($"MoveToStack: relocating {visibleCabins.Count} visible cabin(s) to hidden stack", LogLevel.Info);
                    foreach (var cabin in visibleCabins)
                    {
                        cabin.SetPosition(HiddenCabinLocation);
                    }
                }
            }
        }

        #endregion

        #region Message Interception

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
                // Only relocate cabins that are in the hidden stack — cabins at real
                // positions (e.g. from imported saves with KeepExisting) stay put.
                farm = netRootFarm;
                var cabin = farm.GetCabin(context.PeerId);
                if (cabin != null && cabin.IsInHiddenStack())
                {
                    cabin.Relocate(StackLocation.Create(_cabinManagerData).ToPoint());
                }
            }

            // Update the outgoing message
            context.ModifiedMessage = NetworkHelper.CreateMessageLocationIntroduction(context.PeerId, farm.Root, forceCurrentLocation);
        }

        private void OnLocationDeltaMessage(MessageContext context)
        {
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

        #endregion

        #region Peer Management

        private void AddPeer(long peerId)
        {
            Monitor.Log($"Adding peer '{peerId}'", LogLevel.Debug);
            Data.AllPlayerIdsEverJoined.Add(peerId);
            Data.Write();
        }

        #endregion

        #region Farmhouse Access Control

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

                    // Block all human players from the farmhouse - it's reserved for the server host
                    if (!roleService.IsServerHost(farmer))
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

        #endregion

        #region Cabin Creation

        public void EnsureAtLeastXCabins()
        {
            var farm = Game1.getFarm();
            var availableCount = GetAvailableCabinCount(farm);
            var cabinsMissingCount = minEmptyCabins - availableCount;

            Monitor.Log($"Cabin check: {availableCount}/{minEmptyCabins} available, building {Math.Max(0, cabinsMissingCount)}", LogLevel.Debug);

            for (var i = 0; i < cabinsMissingCount; i++)
            {
                Monitor.Log($"Cabin check: building cabin {i + 1}/{cabinsMissingCount}", LogLevel.Trace);

                bool success = options.IsNone
                    ? BuildNewCabinVisible(farm)
                    : BuildNewCabin(farm);

                if (!success)
                {
                    Monitor.Log($"Cabin check: failed building cabin {i + 1}/{cabinsMissingCount}", LogLevel.Error);
                }
            }
        }

        /// <summary>
        /// Count available (unassigned) cabins, strategy-aware.
        /// A cabin is available if its owner has NOT been customized (isCustomized = false)
        /// and has no userID assigned. This matches how SyncExistingCabins determines claimed cabins.
        /// Excludes lobby cabins which are managed separately by the password protection system.
        /// </summary>
        private int GetAvailableCabinCount(GameLocation farm)
        {
            return farm.buildings
                .Where(b => b.isCabin && !LobbyService.IsLobbyCabin(b))
                .Count(b => IsCabinAvailable(b));
        }

        /// <summary>
        /// Determines if a cabin is available for a new player to claim.
        /// A cabin is available if it has NOT been customized by a player yet.
        /// </summary>
        private static bool IsCabinAvailable(Building cabinBuilding)
        {
            var cabin = cabinBuilding.GetIndoors<Cabin>();
            var owner = cabin?.owner;

            if (owner == null)
            {
                // No owner object = definitely available
                return true;
            }

            // A cabin is "taken" if the owner has been customized OR has a userID assigned
            // (userID is set when a player claims the farmhand slot via Steam/GOG)
            if (owner.isCustomized.Value)
            {
                return false;
            }

            if (!string.IsNullOrEmpty(owner.userID.Value))
            {
                return false;
            }

            // Owner exists but is not customized and has no userID = available slot
            return true;
        }

        /// <summary>
        /// Cleans up an abandoned cabin claim when a player disconnects before completing character customization.
        /// If a cabin has userID set (player claimed it) but isCustomized is false (didn't finish customization),
        /// clear the userID to release the slot for other players.
        /// </summary>
        /// <param name="odId">The platform ID (Steam/GOG) of the disconnecting player as a string</param>
        public static void CleanupAbandonedCabinClaim(string odId)
        {
            if (string.IsNullOrEmpty(odId) || _instance == null)
                return;

            var farm = Game1.getFarm();
            if (farm == null)
                return;

            foreach (var building in farm.buildings)
            {
                if (!building.isCabin)
                    continue;

                var cabin = building.GetIndoors<Cabin>();
                var owner = cabin?.owner;
                if (owner == null)
                    continue;

                // Check if this cabin was claimed by the disconnecting player but not customized
                if (owner.userID.Value == odId && !owner.isCustomized.Value)
                {
                    _instance.Monitor.Log($"Cleaning up abandoned cabin claim for user '{odId}' (slot was claimed but not customized)", LogLevel.Info);
                    owner.userID.Value = "";
                    // No need to clear other fields - they should already be in default state
                    return; // A player can only claim one cabin at a time
                }
            }
        }

        /// <summary>
        /// Build a cabin at the hidden out-of-bounds location (for CabinStack/FarmhouseStack).
        /// </summary>
        public bool BuildNewCabin(GameLocation location)
        {
            var cabinTilePosition = HiddenCabinLocation.ToVector2();

            var cabin = new Building("Cabin", cabinTilePosition);
            cabin.skinId.Value = "Log Cabin";
            cabin.magical.Value = true;
            cabin.daysOfConstructionLeft.Value = 0;
            cabin.load();

            if (location.buildStructure(cabin, cabinTilePosition, Game1.player, true))
            {
                cabin.ClearTerrainBelow();

                // Create the farmhand entry in farmhandData - this is normally done by
                // performActionOnConstruction but we're bypassing that for hidden cabins
                var indoors = cabin.GetIndoors<Cabin>();
                if (indoors != null && !indoors.HasOwner)
                {
                    indoors.CreateFarmhand();
                    Monitor.Log($"Created farmhand for new cabin", LogLevel.Debug);
                }

                return true;
            }

            return false;
        }

        /// <summary>
        /// Build a cabin at a real, visible farm position (for None strategy).
        /// Uses map-designated positions from the Paths layer.
        /// </summary>
        public bool BuildNewCabinVisible(GameLocation location)
        {
            var farm = location as Farm ?? Game1.getFarm();
            var position = FarmCabinPositions.GetNextAvailablePosition(farm);

            if (!position.HasValue)
            {
                Monitor.Log("No available designated cabin position on farm map", LogLevel.Warn);
                return false;
            }

            var cabin = new Building("Cabin", position.Value);
            cabin.skinId.Value = "Log Cabin";
            cabin.magical.Value = true;
            cabin.daysOfConstructionLeft.Value = 0;
            cabin.load();

            if (location.buildStructure(cabin, position.Value, Game1.player, true))
            {
                cabin.ClearTerrainBelow();

                // Create the farmhand entry in farmhandData - this is normally done by
                // performActionOnConstruction but we're bypassing that for programmatic builds
                var indoors = cabin.GetIndoors<Cabin>();
                if (indoors != null && !indoors.HasOwner)
                {
                    indoors.CreateFarmhand();
                    Monitor.Log($"Created farmhand for new cabin", LogLevel.Debug);
                }

                Monitor.Log($"Built visible cabin at ({position.Value.X}, {position.Value.Y})", LogLevel.Info);
                return true;
            }

            return false;
        }

        #endregion
    }
}
