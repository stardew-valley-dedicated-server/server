using System;

namespace JunimoServer.Shared
{
    /// <summary>
    /// Frame rate throttle for limiting draw calls independently of the game's TPS.
    /// Used from BeginDraw Harmony prefixes in both the server mod and test client.
    /// </summary>
    public static class FpsThrottle
    {
        private static DateTime _lastDrawTime = DateTime.UtcNow;

        /// <summary>
        /// Returns true if enough time has elapsed since the last draw to render
        /// a new frame at the requested FPS. When this returns false, the caller
        /// should call SuppressDraw() and skip the frame.
        ///
        /// <para>
        /// The threshold has a one-frame-worth-of-jitter tolerance baked in
        /// (<see cref="JitterToleranceFactor"/>). MonoGame's <c>Game.Tick</c>
        /// scheduler targets <c>TargetElapsedTime</c> intervals but OS scheduling
        /// jitter means actual gaps vary slightly around the target. Without a
        /// tolerance, a Tick arriving 60ms after the previous accepted draw
        /// (when target = 66.67ms) is rejected — and because rejected draws
        /// don't update <see cref="_lastDrawTime"/>, the next Tick is measured
        /// from a longer baseline and tends to be accepted, alternating
        /// reject/accept and producing an effective draw rate well below
        /// target. Frame captures (x11grab at the same target rate) then
        /// produce stretches of duplicate frames. The 5% slack covers normal
        /// jitter; pathological scheduler stalls still produce honest
        /// suppressions.
        /// </para>
        /// </summary>
        /// <param name="targetFps">
        /// Target frames per second; must be &gt;= 1. Values &lt; 1 return true without
        /// throttling — a runtime guard in front of the <c>1000.0 / targetFps</c> divide,
        /// not a "no cap" signal. Callers gate the 0-fps (disabled) case upstream.
        /// </param>
        public static bool ShouldDraw(int targetFps)
        {
            if (targetFps < 1)
                return true;

            var now = DateTime.UtcNow;
            var thresholdMs = (1000.0 / targetFps) * JitterToleranceFactor;
            if ((now - _lastDrawTime).TotalMilliseconds < thresholdMs)
                return false;

            _lastDrawTime = now;
            return true;
        }

        /// <summary>
        /// Multiplier on the strict <c>1/targetFps</c> threshold that absorbs
        /// MonoGame Tick jitter. 0.95 means a draw is accepted when the gap is
        /// &gt;= 95% of the target interval — covering ±5% scheduling slop
        /// without admitting actual double-pumped draws.
        /// </summary>
        private const double JitterToleranceFactor = 0.95;
    }
}
