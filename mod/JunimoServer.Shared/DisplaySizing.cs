using System;
using HarmonyLib;
using StardewValley;
using StardewModdingAPI;

namespace JunimoServer.Shared
{
    /// <summary>
    /// Sizes the game to the X display dimensions (DISPLAY_WIDTH / DISPLAY_HEIGHT) and zooms it
    /// out to fit, so the rendered game fills the framebuffer exactly — no black borders or
    /// clipping in VNC captures and recordings, and the game rasterizes only as many pixels as
    /// the display has (lower resolution = less CPU per frame).
    ///
    /// Stardew's UI and world are laid out against a fixed logical baseline of
    /// <see cref="Game1.defaultResolutionX"/>×<see cref="Game1.defaultResolutionY"/>
    /// (1280×720); a smaller window just shows a cropped center of that, so content looks
    /// "too big". To make everything fit, we zoom out by <c>height / 720</c> (the world's
    /// render target scales up by 1/zoom then downscales into the small backbuffer) and apply
    /// the same factor to the UI scale so menus/HUD shrink too. At 640×360 the factor is 0.5;
    /// at 1280×720 it is 1.0 (a no-op, preserving default behavior).
    ///
    /// <para>
    /// <b>Why the zoom is enforced via Harmony, not a one-time field write:</b> the game fights
    /// any direct write to the scale fields. <c>singlePlayerBaseZoomLevel</c> is a persisted
    /// option (<c>[XmlElement("zoomLevel")]</c>) that a save load resets to 1.0, and
    /// <c>Options.desiredUIScale</c>'s getter hard-returns 1.0 outside gameMode 3 — so on menus
    /// (title, farmhand-select, "connecting") <c>Game1.Update</c> reverts our UI scale every
    /// tick, leaving menus oversized and flickering. We instead postfix the two
    /// <c>desired*</c> getters to return the target scale in every game mode. Game1.Update's own
    /// per-tick reconciliation then reads our value, drives <c>baseZoomLevel</c>/<c>baseUIScale</c>
    /// to it, and refreshes the window when needed — revert-proof by construction, no per-tick
    /// re-pinning from us.
    /// </para>
    ///
    /// Both the server mod and the test client opt in: call <see cref="Install"/> once at mod
    /// entry (to install the patches) and <see cref="ApplyFromEnv"/> on GameLaunched (to size
    /// the window and apply the initial scale). The vanilla 1280×720 window minimum in
    /// <c>Game1.SetWindowSize</c> is Windows-only, so on the Linux containers the window takes
    /// any size requested.
    /// </summary>
    public static class DisplaySizing
    {
        private const int DefaultWidth = 1280;
        private const int DefaultHeight = 720;

        // The scale resolved from DISPLAY_HEIGHT on the first ApplyFromEnv. 1.0 until then and
        // at native resolution, in which case the getter postfixes pass through unchanged.
        private static float _targetScale = 1f;

        /// <summary>
        /// Installs the Harmony postfixes that keep the game's zoom and UI scale pinned to the
        /// target in every game mode. Idempotent per Harmony instance; call once at mod entry.
        /// </summary>
        public static void Install(Harmony harmony)
        {
            harmony.Patch(
                AccessTools.PropertyGetter(typeof(Options), nameof(Options.desiredBaseZoomLevel)),
                postfix: new HarmonyMethod(typeof(DisplaySizing), nameof(ScalePostfix)));
            harmony.Patch(
                AccessTools.PropertyGetter(typeof(Options), nameof(Options.desiredUIScale)),
                postfix: new HarmonyMethod(typeof(DisplaySizing), nameof(ScalePostfix)));
        }

        /// <summary>
        /// Reads DISPLAY_WIDTH / DISPLAY_HEIGHT from the environment (defaulting to 1280×720),
        /// resolves the zoom/UI scale that fits the standard 1280×720 layout into that
        /// framebuffer, applies it, and resizes the window. Best-effort: a failure is logged at
        /// Warn and swallowed so it can never poison a test (an Error-level log would trip the
        /// container's error cancellation).
        /// </summary>
        public static void ApplyFromEnv(IMonitor monitor)
        {
            try
            {
                var w = int.TryParse(Environment.GetEnvironmentVariable("DISPLAY_WIDTH"), out var dw) ? dw : DefaultWidth;
                var h = int.TryParse(Environment.GetEnvironmentVariable("DISPLAY_HEIGHT"), out var dh) ? dh : DefaultHeight;

                // Derived from height because Stardew's logical baseline is height-driven
                // (Game1.defaultResolutionY = 720); this keeps the full vertical extent on screen.
                _targetScale = (float)h / Game1.defaultResolutionY;

                // Apply to the base* mirrors the renderer reads directly so the very first frame
                // is correct; the patched getters keep them there on every later tick.
                Game1.options.baseZoomLevel = _targetScale;
                Game1.options.baseUIScale = _targetScale;

                Game1.game1.SetWindowSize(w, h);
                monitor.Log($"Resized game window to {w}x{h} (zoom/UI scale {_targetScale:0.###}, matching X display)", LogLevel.Trace);
            }
            catch (Exception ex)
            {
                monitor.Log($"Failed to resize game window: {ex.Message}", LogLevel.Warn);
            }
        }

        // Postfix for Options.desiredBaseZoomLevel and Options.desiredUIScale getters: override
        // the returned value with the target scale once a non-native resolution is active. At
        // native resolution (_targetScale == 1) it leaves the original value untouched, so the
        // patch is inert outside the lowered-resolution case.
        private static void ScalePostfix(ref float __result)
        {
            if (_targetScale != 1f)
                __result = _targetScale;
        }
    }
}
