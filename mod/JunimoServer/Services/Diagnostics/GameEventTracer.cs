using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Network;

namespace JunimoServer.Services.Diagnostics
{
    /// <summary>
    /// Harmony-based tracer for game engine events that the HTTP API cannot expose
    /// without polling. Emits structured events via <see cref="ModEventLog"/>; each
    /// patch is best-effort — if the target method can't be found the tracer logs
    /// once and continues without that specific signal.
    ///
    /// <para>
    /// Targets (all postfix — no behavior change):
    /// <list type="bullet">
    ///   <item><c>Multiplayer.addPlayer</c> → <c>farmhand_request { approved:true }</c>.</item>
    ///   <item><c>GameServer.rejectFarmhandRequest</c> → <c>farmhand_request { approved:false }</c>.</item>
    ///   <item><c>Multiplayer.playerDisconnected</c> → <c>peer_disconnected_engine</c>.</item>
    ///   <item><c>ReadySynchronizer.Reset</c> → clears the ready-check dedup dict (memory hygiene; no event).</item>
    ///   <item><c>ServerReadyCheck.Update</c> → <c>ready_check_transition</c> (state-diff only, instance-identity aware).</item>
    /// </list>
    /// </para>
    /// </summary>
    public class GameEventTracer : ModService
    {
        private readonly Harmony _harmony;

        // Per-check dedup: last-seen (numberReady, numberRequired, isReady) tuple
        // paired with the runtime identity of the instance we observed. Keyed by
        // the check's Id string.
        //
        // Thread safety: ServerReadyCheck.Update runs on the game thread
        // (FarmerTeam.Update → netReady.Update). ReadySynchronizer.Reset() runs
        // on the _newDayTask threadpool thread (called from _newDayAfterFade's
        // state machine at Game1.cs:7879). Dictionary<> is not safe for
        // concurrent read/write, so every access takes _lastReadyStateLock.
        //
        // Identity matters: Reset() clears the dict and a later GetOrCreate
        // allocates a fresh ServerReadyCheck with
        // (NumberReady=0, NumberRequired=0, IsReady=false). Keying dedup only
        // on Id would suppress the first emit on the recreated check whenever
        // the previous day's final tuple happened to match (0,0,false).
        // Tracking RuntimeHelpers.GetHashCode(__instance) lets us force-emit
        // when the instance identity changes, even if the tuple is unchanged —
        // and it's also the only correctness guarantee if the Reset patch fails
        // to apply.
        private static readonly object _lastReadyStateLock = new();
        private static readonly Dictionary<string, (int InstanceId, int Ready, int Required, bool IsReady)> _lastReadyState = new();

        public GameEventTracer(Harmony harmony, IModHelper helper, IMonitor monitor) : base(helper, monitor)
        {
            _harmony = harmony;
        }

        public override void Entry()
        {
            TryPatch(
                AccessTools.Method(typeof(Multiplayer), nameof(Multiplayer.addPlayer)),
                postfix: nameof(AddPlayer_Postfix),
                label: "Multiplayer.addPlayer");

            TryPatch(
                AccessTools.Method(typeof(GameServer), "rejectFarmhandRequest"),
                postfix: nameof(RejectFarmhandRequest_Postfix),
                label: "GameServer.rejectFarmhandRequest");

            TryPatch(
                AccessTools.Method(typeof(Multiplayer), nameof(Multiplayer.playerDisconnected)),
                postfix: nameof(PlayerDisconnected_Postfix),
                label: "Multiplayer.playerDisconnected");

            // ReadySynchronizer.Reset — clear our dedup dict so it doesn't grow
            // across days / session resets. The identity-hash guard in the Update
            // postfix handles correctness if this patch fails for some reason;
            // this is purely a memory-hygiene hook.
            TryPatch(
                AccessTools.Method(typeof(StardewValley.Network.NetReady.ReadySynchronizer), "Reset"),
                postfix: nameof(ReadySynchronizer_Reset_Postfix),
                label: "ReadySynchronizer.Reset");

            // ServerReadyCheck is internal sealed — look up by name.
            var serverReadyCheckType = AccessTools.TypeByName(
                "StardewValley.Network.NetReady.Internal.ServerReadyCheck");
            if (serverReadyCheckType != null)
            {
                TryPatch(
                    AccessTools.Method(serverReadyCheckType, "Update"),
                    postfix: nameof(ServerReadyCheck_Update_Postfix),
                    label: "ServerReadyCheck.Update");
            }
            else
            {
                Monitor.Log("[GameEventTracer] ServerReadyCheck type not found; ready_check_transition events disabled", LogLevel.Warn);
            }
        }

        private void TryPatch(MethodInfo target, string postfix, string label)
        {
            if (target == null)
            {
                Monitor.Log($"[GameEventTracer] Target method not found: {label}", LogLevel.Warn);
                return;
            }
            try
            {
                _harmony.Patch(
                    original: target,
                    postfix: new HarmonyMethod(typeof(GameEventTracer), postfix));
            }
            catch (Exception ex)
            {
                Monitor.Log($"[GameEventTracer] Failed to patch {label}: {ex.GetType().Name}: {ex.Message}", LogLevel.Warn);
            }
        }

        public static void AddPlayer_Postfix(NetFarmerRoot f)
        {
            if (f?.Value == null) return;
            ModEventLog.Emit("farmhand_request", new
            {
                approved = true,
                sourceFarmerId = f.Value.UniqueMultiplayerID
            });
        }

        public static void RejectFarmhandRequest_Postfix(string userId, string connectionId, NetFarmerRoot farmer)
        {
            long? sourceFarmerId = farmer?.Value?.UniqueMultiplayerID;
            ModEventLog.Emit("farmhand_request", new
            {
                approved = false,
                userId,
                connectionId,
                sourceFarmerId
            });
        }

        public static void PlayerDisconnected_Postfix(long id)
        {
            ModEventLog.Emit("peer_disconnected_engine", new { id });
        }

        public static void ReadySynchronizer_Reset_Postfix()
        {
            lock (_lastReadyStateLock)
            {
                _lastReadyState.Clear();
            }
        }

        /// <summary>
        /// Postfix on ServerReadyCheck.Update. Reads the instance's public BaseReadyCheck
        /// state and emits a ready_check_transition event only when any of
        /// (numberReady, numberRequired, isReady) changed since the last emit for this
        /// check id. Id-keyed dedup prevents per-tick spam.
        /// </summary>
        public static void ServerReadyCheck_Update_Postfix(object __instance)
        {
            if (__instance == null) return;
            try
            {
                // All of Id/NumberReady/NumberRequired/IsReady live on the abstract
                // BaseReadyCheck. Walk up the type chain once — BindingFlags.Instance
                // alone would miss properties declared on the base for some reflection
                // runtimes when the subclass is internal sealed.
                string id = GetBaseMember(__instance, "Id") as string ?? "?";
                int numberReady = (int)(GetBaseMember(__instance, "NumberReady") ?? 0);
                int numberRequired = (int)(GetBaseMember(__instance, "NumberRequired") ?? 0);
                bool isReady = (bool)(GetBaseMember(__instance, "IsReady") ?? false);
                int instanceId = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(__instance);

                lock (_lastReadyStateLock)
                {
                    if (_lastReadyState.TryGetValue(id, out var prev)
                        && prev.InstanceId == instanceId
                        && prev.Ready == numberReady
                        && prev.Required == numberRequired
                        && prev.IsReady == isReady)
                    {
                        return;
                    }
                    _lastReadyState[id] = (instanceId, numberReady, numberRequired, isReady);
                }

                ModEventLog.Emit("ready_check_transition", new
                {
                    checkId = id,
                    numberReady,
                    numberRequired,
                    isReady
                });
            }
            catch
            {
                // Never let instrumentation fail the game tick.
            }
        }

        private static object GetBaseMember(object instance, string name)
        {
            var t = instance.GetType();
            while (t != null)
            {
                var prop = t.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (prop != null) return prop.GetValue(instance);
                var field = t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (field != null) return field.GetValue(instance);
                t = t.BaseType;
            }
            return null;
        }
    }
}
