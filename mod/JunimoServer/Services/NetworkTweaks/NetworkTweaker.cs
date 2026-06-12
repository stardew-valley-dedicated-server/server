using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using JunimoServer.Services.PersistentOption;
using JunimoServer.Services.Settings;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.Network;

namespace JunimoServer.Services.NetworkTweaks
{
    public class NetworkTweaker : ModService
    {
        private readonly PersistentOptions _options;
        private readonly ServerSettingsLoader _settings;
        private bool _networkSettingsApplied;
        private static IMonitor _monitor = null!;
        private static readonly MethodInfo RejectFarmhandRequestMethod = AccessTools.Method(
            typeof(GameServer),
            "rejectFarmhandRequest"
        );

        public NetworkTweaker(
            IModHelper helper,
            IMonitor monitor,
            PersistentOptions options,
            ServerSettingsLoader settings,
            Harmony harmony
        )
        {
            _options = options;
            _settings = settings;
            _monitor = monitor;

            // Vanilla bug fix: Game1.getAllFarmhands() (Game1.cs:10967) throws
            // KeyNotFoundException during player disconnect.
            //
            // Root cause: getAllFarmhands() iterates farmhandData.Values and for active
            // farmers does an unchecked otherFarmers[value.UniqueMultiplayerID]. But
            // farmhandData and otherFarmers are independent data structures with different
            // lifecycles: Multiplayer.removeDisconnectedFarmers() (Multiplayer.cs:999)
            // removes the key from otherFarmers, but farmhandData still reports
            // isActive() == true until the network state catches up. The unchecked
            // indexer throws during this window.
            //
            // getAllFarmhands() is called from many places during _update() (via
            // getAllFarmers()), so this race is triggered frequently under load with
            // rapid connect/disconnect cycles. SMAPI catches the exception in SCore.cs
            // and resumes the next frame (non-fatal), but it produces ERROR-level log
            // lines that trip our test harness error detection.
            //
            // Fix: replace with TryGetValue, silently skip mid-disconnect farmers.
            // Ref: decompiled/sdv-1.6.15-24356/StardewValley/Game1.cs:10967-10980
            harmony.Patch(
                original: AccessTools.Method(typeof(Game1), nameof(Game1.getAllFarmhands)),
                prefix: new HarmonyMethod(typeof(NetworkTweaker), nameof(GetAllFarmhands_Prefix))
            );

            // Vanilla bug fix: GameServer.warpFarmer() (GameServer.cs:682) throws
            // KeyNotFoundException when a client sends a warp request with isStructure=false
            // for a cabin interior (which is a structure).
            //
            // Root cause: performPassoutWarp (Farmer.cs:5849) calls getLocationRequest()
            // without isStructure=true. The client encodes isStructure=false in its warp
            // message (type 5). The server's warpFarmer() passes this flag to
            // Game1.RequireLocation(), which calls getLocationFromName(name, isStructure: false)
            // but cabin interiors only exist as structure locations, so it returns null
            // and RequireLocation throws KeyNotFoundException.
            //
            // This is triggered by our post-auth passout warp when the client doesn't have
            // the location pre-cached (e.g., first connection or cache miss).
            //
            // Fix: prefix that catches the isStructure=false case and retries with true.
            // Ref: decompiled/sdv-1.6.15-24356/StardewValley/Network/GameServer.cs:682-692
            harmony.Patch(
                original: AccessTools.Method(
                    typeof(GameServer),
                    "warpFarmer",
                    new[]
                    {
                        typeof(Farmer),
                        typeof(short),
                        typeof(short),
                        typeof(string),
                        typeof(bool),
                    }
                ),
                prefix: new HarmonyMethod(typeof(NetworkTweaker), nameof(WarpFarmer_Prefix))
            );

            // Vanilla bug fix: GameServer.checkFarmhandRequest() inner Check() (GameServer.cs:542)
            // does farmhandData[id] with an unchecked indexer. This throws KeyNotFoundException
            // when two clients connect concurrently after a day transition and the first client's
            // TryAssignFarmhandHome reassigns a cabin, and AssignFarmhand deletes the previous
            // unclaimed owner from farmhandData. The second client, which received that farmhand
            // in its available-farmhands list moments earlier, then crashes.
            //
            // Same class of bug as getAllFarmhands() (see GetAllFarmhands_Prefix).
            // Fix: reject the request gracefully if the farmhand ID no longer exists.
            harmony.Patch(
                original: AccessTools.Method(
                    typeof(GameServer),
                    nameof(GameServer.checkFarmhandRequest)
                ),
                prefix: new HarmonyMethod(
                    typeof(NetworkTweaker),
                    nameof(CheckFarmhandRequest_SafeLookup_Prefix)
                )
                {
                    priority = Priority.High,
                }
            );

            // Fix: ensure isCustomized is correct before saveFarmhand persists data.
            //
            // Root cause: character customization (name, appearance, isCustomized flag) is
            // set on the client and synced to the server via netcode tick updates. If the
            // client disconnects before the server processes the full sync, the server-side
            // farmer can have a non-empty Name but isCustomized=false. saveFarmhand then
            // persists this stale state, making the slot appear "unclaimed"
            // (isUnclaimedFarmhand = !isCustomized). Other clients can then claim the slot,
            // and cleanup deletes the original farmer's data.
            //
            // Fix: prefix on SaveFarmhand that sets isCustomized=true when the farmer has
            // a non-empty name. Character creation always sets both name and isCustomized
            // together (CharacterCustomization.cs:1696-1699), so a named farmer with
            // isCustomized=false is always a netcode sync race, never a legitimate state.
            harmony.Patch(
                original: AccessTools.Method(
                    typeof(NetWorldState),
                    nameof(NetWorldState.SaveFarmhand)
                ),
                prefix: new HarmonyMethod(
                    typeof(NetworkTweaker),
                    nameof(SaveFarmhand_FixCustomizedFlag_Prefix)
                )
            );

            // Replace network propagation of buildingConstructedEvent with a direct
            // local invocation. Vanilla Building.performActionOnConstruction
            // (Building.cs:1220-1222) runs unconditionally on every receiving peer;
            // when master builds a cabin, peers allocate phantom farmhand uids via
            // Utility.RandomLong() and replicate them back, corrupting state.
            // Master-side side-effects (LoadFromBuildingData, CreateFarmhand for
            // ownerless cabins, mail dispatch) are preserved by the direct call.
            // Peers see the building itself via farm.buildings (NetCollection<Building>).
            harmony.Patch(
                original: AccessTools.Method(
                    typeof(FarmerTeam),
                    nameof(FarmerTeam.SendBuildingConstructedEvent)
                ),
                prefix: new HarmonyMethod(
                    typeof(NetworkTweaker),
                    nameof(SendBuildingConstructedEvent_Prefix)
                )
            );

            // Fix: vanilla IsFarmhandAvailable checks Cabin.isInventoryOpen() which
            // guards a host-side feature (depositing items into a farmhand's backpack
            // via interactive tiles inside the cabin). This feature is unreachable on a
            // dedicated server in all cabin strategies (hidden cabins off-map, host never
            // triggers tile actions). But the inventory mutex can get stuck during network
            // disconnect races, rejecting valid farmhand slots. Replace with a prefix
            // that performs only the cabin-assignment check (one TryAssignFarmhandHome
            // call), skipping the mutex check entirely. The postfix variant accidentally
            // double-invoked TryAssignFarmhandHome because vanilla already calls it too,
            // causing cascading AssignFarmhand-driven cabin steals across every iteration
            // of sendAvailableFarmhands.
            harmony.Patch(
                original: AccessTools.Method(
                    typeof(GameServer),
                    nameof(GameServer.IsFarmhandAvailable)
                ),
                prefix: new HarmonyMethod(
                    typeof(NetworkTweaker),
                    nameof(IsFarmhandAvailable_SkipInventoryLock_Prefix)
                )
            );

            helper.Events.GameLoop.UpdateTicked += OnTick;
        }

        /// <summary>
        /// PREFIX: replaces GameServer.warpFarmer() to handle isStructure mismatch.
        /// When the client sends isStructure=false for a structure location (e.g., cabin
        /// interior), the vanilla code throws KeyNotFoundException. We catch this and
        /// retry with isStructure=true before falling back to the original behavior.
        /// </summary>
        public static bool WarpFarmer_Prefix(
            GameServer __instance,
            Farmer farmer,
            short x,
            short y,
            string name,
            bool isStructure
        )
        {
            // Only intercept when isStructure is false. If true, vanilla handles it fine.
            if (isStructure)
                return true;

            // Try the normal lookup first
            var location = Game1.getLocationFromName(name, isStructure: false);
            if (location != null)
                return true; // Normal path works, let vanilla handle it

            // Vanilla would throw here. Try as structure instead.
            location = Game1.getLocationFromName(name, isStructure: true);
            if (location == null)
                return true; // Neither works, let vanilla throw its normal error

            // Found as structure. Execute the warp ourselves.
            _monitor.Log(
                $"[WarpFix] Location '{name}' found as structure (client sent isStructure=false)",
                LogLevel.Debug
            );
            if (Game1.IsMasterGame)
                location.hostSetup();
            farmer.currentLocation = location;
            farmer.Position = new Microsoft.Xna.Framework.Vector2(
                x * 64,
                y * 64 - (farmer.Sprite.getHeight() - 32) + 16
            );

            // Call private sendLocation via reflection
            var sendLocationMethod = AccessTools.Method(
                typeof(GameServer),
                "sendLocation",
                new[] { typeof(long), typeof(GameLocation), typeof(bool) }
            );
            sendLocationMethod.Invoke(
                __instance,
                new object[] { farmer.UniqueMultiplayerID, location, false }
            );

            return false; // Skip original
        }

        /// <summary>
        /// PREFIX: replaces Game1.getAllFarmhands() entirely (returns false).
        /// Safe reimplementation that tolerates the farmhandData/otherFarmers desync
        /// during player disconnect. Mid-disconnect farmers (active in farmhandData
        /// but already removed from otherFarmers) are silently skipped. They will
        /// appear as offline on the next call once isActive() catches up.
        /// </summary>
        public static bool GetAllFarmhands_Prefix(ref IEnumerable<Farmer> __result)
        {
            __result = GetAllFarmhandsSafe();
            return false;
        }

        /// <summary>
        /// PREFIX: guards against KeyNotFoundException in checkFarmhandRequest's inner Check()
        /// and restores server-side homeLocation before the vanilla availability check.
        ///
        /// Guard: When the farmhand ID is no longer in farmhandData (e.g., deleted by a
        /// concurrent TryAssignFarmhandHome), reject the request gracefully instead of crashing.
        ///
        /// homeLocation fix: The client-sent farmer has homeLocation set to the lobby cabin
        /// by our FarmhandSenderService lobby redirect. Vanilla IsFarmhandAvailable (line 562)
        /// calls TryAssignFarmhandHome + getHomeOfFarmer with this data, evaluating the
        /// shared lobby cabin's isInventoryOpen() instead of the farmhand's real cabin.
        /// Under concurrent connections, the shared lobby cabin's mutex state can cause
        /// the check to fail, creating a tight reject-reconnect loop. Restoring the
        /// server-side homeLocation ensures the availability check evaluates the real cabin.
        /// Note: vanilla lobby spawn routing (lines 586-600) skips sendLocation for the lobby
        /// cabin because isAlwaysActiveLocation() returns true for Farm-nested interiors.
        /// PasswordProtectionService patches sendServerIntroduction to send it explicitly.
        /// </summary>
        public static bool CheckFarmhandRequest_SafeLookup_Prefix(
            GameServer __instance,
            string userId,
            string connectionId,
            NetFarmerRoot farmer,
            Action<OutgoingMessage> sendMessage
        )
        {
            if (farmer.Value == null)
                return true;

            if (!__instance.isGameAvailable())
                return true;

            var id = farmer.Value.UniqueMultiplayerID;
            if (!Game1.netWorldState.Value.farmhandData.ContainsKey(id))
            {
                _monitor.Log(
                    $"[FarmhandFix] Rejected farmhand {id}: not in farmhandData (likely reassigned by concurrent connection)",
                    LogLevel.Debug
                );
                RejectFarmhandRequestMethod.Invoke(
                    __instance,
                    new object[] { userId, connectionId, farmer, sendMessage }
                );
                return false;
            }

            // Restore server-side homeLocation so IsFarmhandAvailable checks the
            // farmhand's real cabin, not the lobby cabin from our redirect.
            var serverFarmhand = Game1.netWorldState.Value.farmhandData[id];
            var clientHome = farmer.Value.homeLocation.Value;
            var serverHome = serverFarmhand.homeLocation.Value;
            if (clientHome != serverHome)
            {
                _monitor.Log(
                    $"[FarmhandFix] Restored homeLocation for {id}: "
                        + $"client={clientHome ?? "(null)"}, server={serverHome ?? "(null)"}",
                    LogLevel.Debug
                );
                farmer.Value.homeLocation.Value = serverHome;
            }

            return true;
        }

        /// <summary>
        /// PREFIX: replaces IsFarmhandAvailable entirely to skip the inventory-open
        /// mutex check while performing the cabin-assignment check exactly once.
        /// Vanilla calls TryAssignFarmhandHome internally; the earlier postfix
        /// implementation accidentally called it a second time, which mutated
        /// farmhand state on every filter pass and created cascading cabin steals.
        /// </summary>
        public static bool IsFarmhandAvailable_SkipInventoryLock_Prefix(
            Farmer farmhand,
            ref bool __result
        )
        {
            __result =
                farmhand != null && Game1.netWorldState.Value.TryAssignFarmhandHome(farmhand);
            return false; // Skip original
        }

        /// <summary>
        /// PREFIX on NetWorldState.SaveFarmhand. Fixes isCustomized when the client's
        /// netcode update hasn't been processed yet. Character creation sets Name and
        /// isCustomized together on the client, but the server may not have received the
        /// isCustomized update before the player disconnects.
        /// </summary>
        public static void SaveFarmhand_FixCustomizedFlag_Prefix(NetFarmerRoot farmhand)
        {
            if (farmhand?.Value == null)
                return;

            if (!string.IsNullOrEmpty(farmhand.Value.Name) && !farmhand.Value.isCustomized.Value)
            {
                _monitor.Log(
                    $"[SaveFarmhand] Fixing isCustomized for '{farmhand.Value.Name}' ({farmhand.Value.UniqueMultiplayerID}): name was synced but isCustomized was not",
                    LogLevel.Info
                );
                farmhand.Value.isCustomized.Value = true;
            }
        }

        public static bool SendBuildingConstructedEvent_Prefix(
            GameLocation location,
            Building building,
            Farmer who
        )
        {
            location.OnBuildingConstructed(building, who);
            return false;
        }

        private static IEnumerable<Farmer> GetAllFarmhandsSafe()
        {
            foreach (var value in Game1.netWorldState.Value.farmhandData.Values)
            {
                if (value.isActive())
                {
                    // Vanilla uses otherFarmers[id] here, which throws if key was
                    // already removed by removeDisconnectedFarmers().
                    if (Game1.otherFarmers.TryGetValue(value.UniqueMultiplayerID, out var farmer))
                        yield return farmer;
                }
                else
                {
                    yield return value;
                }
            }
        }

        private void OnTick(object sender, UpdateTickedEventArgs e)
        {
            if (Game1.netWorldState.Value == null || !Game1.hasLoadedGame)
            {
                return;
            }

            HandleNetworkSettings();
            HandlePlayerLimit();
        }

        private void HandleNetworkSettings()
        {
            if (_networkSettingsApplied)
                return;

            var period = _settings.NetworkBroadcastPeriod;
            Game1.Multiplayer.defaultInterpolationTicks = 7; // Default: 15
            Game1.Multiplayer.farmerDeltaBroadcastPeriod = period;
            Game1.Multiplayer.locationDeltaBroadcastPeriod = period;
            Game1.Multiplayer.worldStateDeltaBroadcastPeriod = period;

            _monitor.Log(
                $"Applied NetworkBroadcastPeriod={period} (vanilla default: 3)",
                LogLevel.Info
            );
            _networkSettingsApplied = true;
        }

        private void HandlePlayerLimit()
        {
            var maxPlayers = _options.Data.MaxPlayers;
            Game1.Multiplayer.playerLimit = maxPlayers;

            if (Game1.netWorldState.Value.CurrentPlayerLimit != maxPlayers)
            {
                Game1.netWorldState.Value.CurrentPlayerLimit = maxPlayers;
            }

            if (Game1.netWorldState.Value.HighestPlayerLimit != maxPlayers)
            {
                Game1.netWorldState.Value.HighestPlayerLimit = maxPlayers;
            }
        }
    }
}
