using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewValley;
using xTile.Display;

namespace JunimoServer.Services.ServerOptim
{
    public class ServerOptimizerOverrides
    {
        private static IMonitor _monitor;
        private static bool _shouldDrawFrame = true;

        private static IDisplayDevice _originalDisplayDevice;
        private static IDisplayDevice _nullDisplayDevice;
        private static bool _renderingEnabled = true;
        private static bool _automationSuppressesInput;

        // When true, the next frame clears the screen and draws a "Rendering Disabled" notice,
        // then suppresses all subsequent frames — leaving the notice visible on the VNC display.
        private static bool _renderDisabledNoticeNeeded;

        public static void Initialize(IMonitor monitor)
        {
            _monitor = monitor;
        }

        /// <summary>
        /// Saves the original display device so it can be restored when toggling rendering on.
        /// Must be called after the game has fully initialized its display device.
        /// </summary>
        public static void SaveOriginalDisplayDevice(IDisplayDevice device)
        {
            _originalDisplayDevice = device;
            _nullDisplayDevice = new NullDisplayDevice();
        }

        /// <summary>
        /// Toggles rendering on or off at runtime.
        /// When disabling: assigns NullDisplayDevice, queues a "disabled" notice frame, then suppresses drawing.
        /// When enabling: restores the original display device and resumes frame drawing.
        /// </summary>
        public static void ToggleRendering(bool enable, IMonitor monitor)
        {
            if (enable)
            {
                if (_originalDisplayDevice != null)
                {
                    Game1.mapDisplayDevice = _originalDisplayDevice;
                }
                _renderDisabledNoticeNeeded = false;
                EnableDrawing();
                _renderingEnabled = true;
                monitor.Log("Rendering enabled", LogLevel.Info);
            }
            else
            {
                if (_nullDisplayDevice == null)
                {
                    _nullDisplayDevice = new NullDisplayDevice();
                }
                Game1.mapDisplayDevice = _nullDisplayDevice;
                // Queue one notice frame before suppressing draws
                _renderDisabledNoticeNeeded = true;
                _shouldDrawFrame = false;
                _renderingEnabled = false;
                monitor.Log("Rendering disabled", LogLevel.Info);
            }
        }

        /// <summary>
        /// Returns whether rendering is currently enabled.
        /// </summary>
        public static bool IsRenderingEnabled()
        {
            return _renderingEnabled;
        }

        /// <summary>
        /// Sets whether host automation should suppress player input.
        /// Called by AlwaysOnServer when toggling automation mode.
        /// </summary>
        public static void SetAutomationInputSuppression(bool suppress)
        {
            _automationSuppressesInput = suppress;
        }

        /// <summary>
        /// Harmony prefix for Game1.UpdateControlInput.
        /// Blocks input processing when rendering is disabled or automation is active.
        /// </summary>
        public static bool UpdateControlInput_Prefix()
        {
            if (!_renderingEnabled || _automationSuppressesInput)
                return false;
            return true;
        }

        public static bool AssignNullDisplay_Prefix()
        {
            Game1.mapDisplayDevice = new NullDisplayDevice();
            return false;
        }

        public static bool ReturnNullDisplay_Prefix(IDisplayDevice __result)
        {
            __result = new NullDisplayDevice();
            return false;
        }

        public static void CreateLobby_Prefix(ref ServerPrivacy privacy, ref uint memberLimit)
        {
            // Used by GoG
            privacy = ServerPrivacy.Public;
            memberLimit = 150;
        }

        public static bool Disable_Prefix()
        {
            return false;
        }

        /// <summary>
        /// Harmony prefix on Game.BeginDraw().
        /// When drawing is suppressed but a disabled-notice frame is needed,
        /// allows one frame through so GameDraw_Prefix can render the notice.
        /// </summary>
        // ReSharper disable once InconsistentNaming
        public static bool Draw_Prefix(GameRunner __instance)
        {
            if (!_shouldDrawFrame)
            {
                if (_renderDisabledNoticeNeeded)
                {
                    // Let this frame through — GameDraw_Prefix will handle it
                    return true;
                }

                __instance.SuppressDraw();
                return false;
            }

            return true;
        }

        /// <summary>
        /// Harmony prefix on Game.Draw(GameTime).
        /// When a disabled-notice is needed, clears the screen to black and draws
        /// "Rendering Disabled" centered text, then skips the game's normal Draw.
        /// EndDraw still runs and calls Present(), so the notice appears on the VNC display.
        /// </summary>
        // ReSharper disable once InconsistentNaming
        public static bool GameDraw_Prefix(Game __instance, GameTime gameTime)
        {
            if (!_renderDisabledNoticeNeeded)
                return true;

            _renderDisabledNoticeNeeded = false;

            try
            {
                var gd = __instance.GraphicsDevice;
                gd.Clear(Color.Black);

                var spriteBatch = Game1.spriteBatch;
                var font = Game1.smallFont;

                if (spriteBatch != null && font != null)
                {
                    spriteBatch.Begin();

                    var text = "Rendering Disabled";
                    var textSize = font.MeasureString(text);
                    var position = new Vector2(
                        (gd.Viewport.Width - textSize.X) / 2f,
                        (gd.Viewport.Height - textSize.Y) / 2f
                    );

                    spriteBatch.DrawString(font, text, position, Color.Gray);
                    spriteBatch.End();
                }
            }
            catch
            {
                // If anything fails during the notice draw, just skip it silently
            }

            // Skip the game's normal Draw — EndDraw will present our frame
            return false;
        }

        public static void DisableDrawing()
        {
            _shouldDrawFrame = false;
        }

        public static void EnableDrawing()
        {
            _shouldDrawFrame = true;
        }
    }
}
