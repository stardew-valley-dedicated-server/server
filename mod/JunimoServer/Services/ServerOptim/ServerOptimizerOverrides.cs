using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using JunimoServer.Services.AlwaysOn;
using JunimoServer.Shared;
using Galaxy.Api;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using StardewModdingAPI;
using StardewValley;
using xTile.Display;

namespace JunimoServer.Services.ServerOptim
{
    public class ServerOptimizerOverrides
    {
        private static IMonitor _monitor;

        // Single source of truth for render-rate control, shared with the test client.
        // Owns the fps state, the draw-gate decision, the disabled-notice paint, and
        // the NullDisplayDevice swap. Constructed in Initialize.
        private static RenderingController _rendering;

        private static bool _automationSuppressesInput;

        // The host-automation hotkeys (F9/F10 by default) that stay live while automation
        // suppresses input, so the operator can drop automation from the VNC display.
        // Resolved from AlwaysOnConfig at Initialize; a binding with no keyboard equivalent
        // (e.g. controller-only) is dropped.
        private static Keys[] _allowedHotkeys = Array.Empty<Keys>();

        public static void Initialize(IMonitor monitor, AlwaysOnConfig config)
        {
            _monitor = monitor;
            _rendering = new RenderingController(monitor, "Server");

            var hotkeys = new List<Keys>();
            foreach (var button in new[] { config.HotKeyToggleAutoMode, config.HotKeyToggleVisibility })
                if (button.TryGetKeyboard(out var key))
                    hotkeys.Add(key);
            _allowedHotkeys = hotkeys.ToArray();
        }

        /// <summary>
        /// Saves the original display device so it can be restored when re-enabling rendering.
        /// Must be called after the game has fully initialized its display device.
        /// </summary>
        public static void SaveOriginalDisplayDevice(IDisplayDevice device)
            => _rendering.SaveOriginalDisplayDevice(device);

        /// <summary>
        /// Sets the server render rate. 0 disables rendering (NullDisplayDevice installed,
        /// draws suppressed, "Rendering Disabled" notice queued); N &gt; 0 restores the real
        /// display device and caps draws at N fps. The monitor is already held by the
        /// controller, so the parameter is accepted for call-site symmetry and ignored.
        /// </summary>
        public static void SetServerFps(int fps, IMonitor _) => _rendering.SetFps(fps);

        /// <summary>
        /// Returns the current server render rate: 0 = disabled, N &gt; 0 = enabled at N fps.
        /// </summary>
        public static int GetCurrentServerFps() => _rendering.CurrentFps;

        /// <summary>
        /// Sets whether host automation should suppress player input.
        /// Called by AlwaysOnServer when toggling automation mode.
        /// </summary>
        public static void SetAutomationInputSuppression(bool suppress)
        {
            _automationSuppressesInput = suppress;
        }

        // Input is suppressed while rendering is off (no operator can see anything to
        // drive) or host automation is driving the host. Both inputs are read live, so a
        // runtime fps switch (the VNC button, `rendering <fps>`, POST /rendering?fps=N) or
        // a host-auto/F9 toggle flips input behavior without a restart.
        private static bool InputSuppressed
            => _rendering.CurrentFps == 0 || _automationSuppressesInput;

        // The hotkeys stay live only while rendering is on: they exist so an operator
        // watching VNC can drop automation. With rendering off there's nothing to see, so
        // input is suppressed fully.
        private static bool HotkeysStayLive
            => _rendering.CurrentFps > 0 && _automationSuppressesInput;

        /// <summary>
        /// Harmony postfix on Keyboard.PlatformGetState — the single source feeding the game
        /// (control, menus, chat, minigames) AND SMAPI's ButtonPressed pipeline. Three cases:
        /// not suppressed → pass through; suppressed with rendering on → strip to the
        /// host-automation hotkeys so all other input is blocked but those toggles still reach
        /// SMAPI (the operator's only way to drop automation from VNC); suppressed with
        /// rendering off → blank fully.
        /// </summary>
        public static void KeyboardState_Postfix(ref KeyboardState __result)
        {
            if (!InputSuppressed) return;
            if (HotkeysStayLive)
            {
                var down = Array.FindAll(_allowedHotkeys, __result.IsKeyDown);
                __result = down.Length == 0 ? default : new KeyboardState(down);
                return;
            }
            __result = default;
        }

        /// <summary>
        /// Harmony postfix on Mouse.PlatformGetState. While suppressed the mouse is fully
        /// blanked — there is no mouse equivalent of the keyboard hotkeys.
        /// </summary>
        public static void MouseState_Postfix(ref MouseState __result)
        {
            if (InputSuppressed) __result = default;
        }

        private static int _galaxyLobbyFailureCount;
        private const int MaxGalaxyLobbyRetries = 3;

        public static void CreateLobby_Prefix(ref ServerPrivacy privacy, ref uint memberLimit)
        {
            // Used by GoG
            privacy = ServerPrivacy.Public;
            memberLimit = 150;
            _galaxyLobbyFailureCount = 0;
        }

        /// <summary>
        /// Harmony prefix for GalaxySocket.onGalaxyLobbyCreated.
        /// Limits the infinite retry loop when Galaxy matchmaking is unavailable.
        /// First 3 failures: original behavior (Error log + 20s retry).
        /// After that: log at Warn, skip OnLobbyCreateFailed (no more retries).
        /// Recovery handled by TryLateAddGalaxyServer on Galaxy reconnect.
        /// </summary>
        public static bool OnGalaxyLobbyCreated_Prefix(GalaxyID lobbyID, LobbyCreateResult result)
        {
            if (result != LobbyCreateResult.LOBBY_CREATE_RESULT_ERROR)
                return true; // Success path: let original handle it

            _galaxyLobbyFailureCount++;

            if (_galaxyLobbyFailureCount <= MaxGalaxyLobbyRetries)
                return true; // Let original run (Error log + retry timer)

            // Max retries exceeded. Stop the retry loop.
            _monitor?.Log(
                $"Galaxy lobby creation failed {_galaxyLobbyFailureCount} times. Stopping retries. " +
                "Recovery via TryLateAddGalaxyServer if Galaxy reconnects.", LogLevel.Warn);
            return false; // Skip original: don't set recreateTimer
        }

        public static bool Disable_Prefix()
        {
            return false;
        }

        /// <summary>
        /// Harmony prefix for SMAPI's SModHooks.StartTask.
        /// On musl/.NET 6 (Alpine), SMAPI's RunSynchronously() queues the task to ThreadPool
        /// instead of running inline, causing a deadlock when the task calls BlockOnUIThread()
        /// (e.g., texture loading during _newDayAfterFade).
        /// Only overrides tasks that are known to trigger BlockOnUIThread calls (NewDay, Save,
        /// Load_*). All other tasks use SMAPI's original RunSynchronously, preserving mod
        /// compatibility for event handlers that assume game-thread context.
        /// </summary>
        public static bool StartTask_Prefix(Task task, string id, ref Task __result)
        {
            // Only override tasks that load content / textures and would deadlock
            if (id == "NewDay" || id == "Save" || id.StartsWith("Load_"))
            {
                _monitor?.Log($"[MuslFix] StartTask: using task.Start() for '{id}' (BlockOnUIThread deadlock prevention)", LogLevel.Debug);
                task.Start();
                __result = task;
                return false;
            }

            // All other tasks: let SMAPI's RunSynchronously handle them (mod compatibility)
            return true;
        }

        /// <summary>
        /// Harmony prefix on Game.BeginDraw(). Forwards to the shared controller,
        /// which decides whether to draw, paint the disabled-notice frame, or allow
        /// the day-end save window through. Suppresses the frame when it returns false.
        /// </summary>
        // ReSharper disable once InconsistentNaming
        public static bool Draw_Prefix(GameRunner __instance)
        {
            if (_rendering.ShouldBeginDraw())
                return true;
            __instance.SuppressDraw();
            return false;
        }

        /// <summary>
        /// Harmony prefix on Game.Draw(GameTime). Forwards to the shared controller,
        /// which paints the "Rendering Disabled" notice once when queued (skipping the
        /// game's normal Draw for that frame) and lets normal draws through otherwise.
        /// </summary>
        // ReSharper disable once InconsistentNaming
        public static bool GameDraw_Prefix(Game __instance, GameTime gameTime)
            => _rendering.ShouldGameDraw(__instance);

        public static void DisableDrawing() => _rendering.DisableDrawing();

        public static void EnableDrawing() => _rendering.EnableDrawing();
    }
}
