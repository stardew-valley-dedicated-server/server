using HarmonyLib;
using Microsoft.VisualBasic;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.SDKs.GogGalaxy;
using System;
using System.Diagnostics;

namespace JunimoServer.Services.ServerOptim
{
    public class ServerOptimizer : ModService
    {
        private readonly bool _disableRendering;
        private readonly IMonitor _monitor;

        public ServerOptimizer(
            Harmony harmony,
            IMonitor monitor,
            IModHelper helper
        )
        {
            _monitor = monitor;
            _disableRendering = Env.DisableRendering;

            ServerOptimizerOverrides.Initialize(monitor);

            harmony.Patch(
                original: AccessTools.Method(typeof(Game), "BeginDraw"),
                prefix: new HarmonyMethod(typeof(ServerOptimizerOverrides), nameof(ServerOptimizerOverrides.Draw_Prefix))
            );

            harmony.Patch(
                original: AccessTools.Method(typeof(Game), "Draw", new[] { typeof(GameTime) }),
                prefix: new HarmonyMethod(typeof(ServerOptimizerOverrides), nameof(ServerOptimizerOverrides.GameDraw_Prefix))
            );

            harmony.Patch(
                original: AccessTools.Method("StardewValley.Game1:updateMusic"),
                prefix: new HarmonyMethod(typeof(ServerOptimizerOverrides), nameof(ServerOptimizerOverrides.Disable_Prefix))
            );

            // harmony.Patch(
            //     original: AccessTools.Method("StardewValley.Game1:changeMusicTrack"),
            //     prefix: new HarmonyMethod(typeof(ServerOptimizerOverrides), nameof(ServerOptimizerOverrides.Disable_Prefix))
            // );

            harmony.Patch(
                original: AccessTools.Method("StardewValley.Game1:initializeVolumeLevels"),
                prefix: new HarmonyMethod(typeof(ServerOptimizerOverrides), nameof(ServerOptimizerOverrides.Disable_Prefix))
            );

            harmony.Patch(
                original: AccessTools.Method("StardewValley.Audio.SoundsHelper:PlayLocal"),
                prefix: new HarmonyMethod(typeof(ServerOptimizerOverrides), nameof(ServerOptimizerOverrides.Disable_Prefix))
            );

            // harmony.Patch(
            //     original: AccessTools.Method("StardewValley.Game1:CheckGamepadMode"),
            //     prefix: new HarmonyMethod(typeof(ServerOptimizerOverrides), nameof(ServerOptimizerOverrides.Disable_Prefix))
            // );

            // harmony.Patch(
            //     original: AccessTools.Method("StardewValley.Game1:updateRaindropPosition"),
            //     prefix: new HarmonyMethod(typeof(ServerOptimizerOverrides), nameof(ServerOptimizerOverrides.Disable_Prefix))
            // );

            // harmony.Patch(
            //     original: AccessTools.Method("StardewValley.Game1:updateRainDropPositionForPlayerMovement"),
            //     prefix: new HarmonyMethod(typeof(ServerOptimizerOverrides), nameof(ServerOptimizerOverrides.Disable_Prefix))
            // );

            harmony.Patch(
                original: AccessTools.Method("StardewValley.BellsAndWhistles.Butterfly:update"),
                prefix: new HarmonyMethod(typeof(ServerOptimizerOverrides), nameof(ServerOptimizerOverrides.Disable_Prefix))
            );

            harmony.Patch(
                original: AccessTools.Method("StardewValley.BellsAndWhistles.AmbientLocationSounds:update"),
                prefix: new HarmonyMethod(typeof(ServerOptimizerOverrides), nameof(ServerOptimizerOverrides.Disable_Prefix))
            );

            harmony.Patch(
                original: AccessTools.Method(typeof(GalaxySocket), "CreateLobby"),
                prefix: new HarmonyMethod(typeof(ServerOptimizerOverrides), nameof(ServerOptimizerOverrides.CreateLobby_Prefix))
            );

            // Always patch UpdateControlInput — blocks input when rendering is disabled
            // or host automation is active (e.g. toggled via F9 on VNC).
            harmony.Patch(
                original: AccessTools.Method("StardewValley.Game1:UpdateControlInput"),
                prefix: new HarmonyMethod(typeof(ServerOptimizerOverrides), nameof(ServerOptimizerOverrides.UpdateControlInput_Prefix))
            );

            if (_disableRendering)
            {
                harmony.Patch(
                    original: AccessTools.Method("Microsoft.Xna.Framework.Input.Keyboard:PlatformGetState"),
                    prefix: new HarmonyMethod(typeof(ServerOptimizerOverrides), nameof(ServerOptimizerOverrides.Disable_Prefix))
                );

                harmony.Patch(
                    original: AccessTools.Method("Microsoft.Xna.Framework.Input.Mouse:PlatformGetState"),
                    prefix: new HarmonyMethod(typeof(ServerOptimizerOverrides), nameof(ServerOptimizerOverrides.Disable_Prefix))
                );
            }



            if (Env.EnableModIncompatibleOptimizations)
            {
                harmony.Patch(
                    original: AccessTools.Method("StardewModdingAPI.Framework.StateTracking.Snapshots.PlayerSnapshot:Update"),
                    prefix: new HarmonyMethod(typeof(ServerOptimizerOverrides),
                        nameof(ServerOptimizerOverrides.Disable_Prefix)));

                harmony.Patch(
                    original: AccessTools.Method("StardewModdingAPI.Framework.StateTracking.Snapshots.WorldLocationsSnapshot:Update"),
                    prefix: new HarmonyMethod(typeof(ServerOptimizerOverrides),
                        nameof(ServerOptimizerOverrides.Disable_Prefix)));

                harmony.Patch(
                    original: AccessTools.Method("StardewModdingAPI.Framework.StateTracking.PlayerTracker:Update"),
                    prefix: new HarmonyMethod(typeof(ServerOptimizerOverrides),
                        nameof(ServerOptimizerOverrides.Disable_Prefix)));

                harmony.Patch(
                    original: AccessTools.Method("StardewModdingAPI.Framework.StateTracking.WorldLocationsTracker:Update"),
                    prefix: new HarmonyMethod(typeof(ServerOptimizerOverrides),
                        nameof(ServerOptimizerOverrides.Disable_Prefix)));

                harmony.Patch(
                    original: AccessTools.Method("StardewModdingAPI.Framework.StateTracking.WorldLocationsTracker:Reset"),
                    prefix: new HarmonyMethod(typeof(ServerOptimizerOverrides),
                        nameof(ServerOptimizerOverrides.Disable_Prefix)));
            }

            helper.Events.GameLoop.GameLaunched += OnGameLaunched;
            helper.Events.GameLoop.DayStarted += OnDayStarted;
            helper.Events.GameLoop.Saving += OnSaving;
            helper.Events.GameLoop.DayEnding += OnDayEnding;
        }

        private void OnGameLaunched(object sender, GameLaunchedEventArgs e)
        {
            // Capture the original display device now that the game has initialized it.
            // This allows runtime toggling between the real device and NullDisplayDevice.
            ServerOptimizerOverrides.SaveOriginalDisplayDevice(Game1.mapDisplayDevice);

            if (_disableRendering)
            {
                ServerOptimizerOverrides.ToggleRendering(false, _monitor);
            }
        }

        private void OnDayEnding(object sender, DayEndingEventArgs e)
        {
            // Enable drawing for the end-of-night phase.
            // SaveGameMenu.update() requires hasDrawn (set in draw()) before it starts
            // saving. If drawing is suppressed, draw() never runs and the save deadlocks.
            // DayEnding fires before the SaveGameMenu is created, so enabling here
            // ensures draw() can run. OnDayStarted re-disables drawing afterward.
            if (!ServerOptimizerOverrides.IsRenderingEnabled())
            {
                ServerOptimizerOverrides.EnableDrawing();
            }

            _monitor.Log($"[ServerOptimizer] Running garbage collection...", LogLevel.Debug);
            var before = checked((long)Math.Round(Process.GetCurrentProcess().PrivateMemorySize64 / 1024.0 / 1024.0));
            GC.Collect(generation: 0, GCCollectionMode.Forced, blocking: true);
            GC.Collect(generation: 1, GCCollectionMode.Forced, blocking: true);
            GC.Collect(generation: 2, GCCollectionMode.Forced, blocking: true);
            var after = checked((long)Math.Round(Process.GetCurrentProcess().PrivateMemorySize64 / 1024.0 / 1024.0));
            var beforeFormatted = Strings.Format(before / 1024.0, "0.00") + " GB";
            var afterFormatted = Strings.Format(after / 1024.0, "0.00") + " GB";
            _monitor.Log($"[ServerOptimizer] Garbage collection complete. Before: {beforeFormatted} After: {afterFormatted}", LogLevel.Debug);
        }

        private void OnDayStarted(object sender, DayStartedEventArgs e)
        {
            // Re-apply the current rendering state after each day transition
            if (!ServerOptimizerOverrides.IsRenderingEnabled())
            {
                ServerOptimizerOverrides.DisableDrawing();
            }
        }

        private void OnSaving(object sender, SavingEventArgs e)
        {
            // Safety net: ensure drawing is enabled during save.
            // OnDayEnding already enables it, but this covers edge cases where
            // a save is triggered outside the normal day-end flow.
            if (!ServerOptimizerOverrides.IsRenderingEnabled())
            {
                ServerOptimizerOverrides.EnableDrawing();
            }
        }

    }
}
