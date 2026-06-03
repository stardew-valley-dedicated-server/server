using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Events;

namespace JunimoServer.Services.AlwaysOn
{
    /// <summary>
    /// Force-completes the Mr. Qi mystery-box overnight cutscene (<see cref="QiPlaneEvent"/>) on the
    /// host. Unlike every other overnight <c>FarmEvent</c>, QiPlaneEvent's completion gate
    /// (<c>finalFadeTimer &gt; 4000</c>) is advanced inside its <c>draw()</c>, not its
    /// <c>tickUpdate()</c> — so when the headless host's draw cadence is gated/throttled/desynced from
    /// the per-tick update pump, the gate never converges and <c>tickUpdate</c> returns false forever.
    /// The new day never starts and clients hang on "Waiting for the host's event to finish."
    ///
    /// The postfix accumulates <see cref="GameTime.ElapsedGameTime"/> (game-time, not wall-clock and not
    /// draw-coupled) per event instance and forces <c>tickUpdate</c> to return true once the event has
    /// run longer than it ever would with healthy draws. This is framerate-independent: with draws
    /// running normally the event self-completes first and the postfix is a no-op; otherwise the
    /// fallback fires and the vanilla completion block (warp-to-bed + save) runs unchanged.
    /// </summary>
    public static class QiPlaneEventOverrides
    {
        private static IMonitor _monitor;

        private static QiPlaneEvent _trackedInstance;
        private static double _accumulatedMs;
        private static bool _forcedThisInstance;

        /// <summary>
        /// Game-time the event may run before we force completion. Natural draw-driven completion is
        /// ~15-25s of game-time; 30s sits above that so a host with a healthy draw cadence always
        /// self-completes first (postfix no-op), while a host where draws never advance the gate still
        /// escapes after 30s of accumulated game-time. tickUpdate runs every update tick regardless of
        /// draws, so this elapses quickly in wall-clock on a stuck host. The stuck-farm-event watchdog
        /// (<c>AlwaysOnServer.HandleStuckFarmEvent</c>) keys its own threshold off this so the two stay
        /// ordered (watchdog fires only well after this).
        /// </summary>
        public const double FallbackThresholdMs = 30000.0;

        public static void Initialize(IMonitor monitor) => _monitor = monitor;

        /// <summary>
        /// Harmony postfix for <see cref="QiPlaneEvent.tickUpdate"/>. Forces the event to complete on
        /// the host after the game-time fallback threshold if its draw-driven gate never converged.
        /// </summary>
        // ReSharper disable once InconsistentNaming
        public static void TickUpdate_Postfix(QiPlaneEvent __instance, GameTime time, ref bool __result)
        {
            if (!Game1.IsMasterGame) return;   // only the host runs the completion block + newDaySync
            if (__result) return;              // event self-completed (draws were healthy) — nothing to do

            // Reset the accumulator when a new event instance starts ticking.
            if (!ReferenceEquals(__instance, _trackedInstance))
            {
                _trackedInstance = __instance;
                _accumulatedMs = 0.0;
                _forcedThisInstance = false;
            }
            if (_forcedThisInstance) return;

            _accumulatedMs += time.ElapsedGameTime.TotalMilliseconds;
            if (_accumulatedMs < FallbackThresholdMs) return;

            _forcedThisInstance = true;
            __result = true;
            _monitor?.Log(
                "QiPlaneEvent (Mr. Qi mystery box) did not self-complete — forcing overnight completion " +
                "so the new day can start.", LogLevel.Info);
        }
    }
}
