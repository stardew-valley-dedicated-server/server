using System.Threading.Tasks;
using JunimoServer.Shared;
using Galaxy.Api;
using Microsoft.Xna.Framework;
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

        public static void Initialize(IMonitor monitor)
        {
            _monitor = monitor;
            _rendering = new RenderingController(monitor, "Server");
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

        /// <summary>
        /// Shared Harmony prefix for Game1.UpdateControlInput and the raw Keyboard/Mouse
        /// PlatformGetState reads. Returning false skips the original, blocking input both
        /// where the game processes it and at the device source. Suppresses input while
        /// rendering is off (the server isn't drawing, so a watching operator can't
        /// meaningfully drive the game) or host automation is driving the host. Both inputs
        /// are live, so a runtime fps switch (the VNC button, `rendering &lt;fps&gt;`,
        /// POST /rendering?fps=N) restores input without a restart.
        /// </summary>
        public static bool SuppressInput_Prefix()
            => _rendering.CurrentFps != 0 && !_automationSuppressesInput;

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
