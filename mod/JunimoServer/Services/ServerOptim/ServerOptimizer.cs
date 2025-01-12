using HarmonyLib;
using Microsoft.VisualBasic;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Events;
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
                original: AccessTools.Method("StardewValley.Game1:updateMusic"),
                prefix: new HarmonyMethod(typeof(ServerOptimizerOverrides), nameof(ServerOptimizerOverrides.Disable_Prefix))
            );

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

            if (_disableRendering)
            {
                harmony.Patch(
                    original: AccessTools.Method("StardewModdingAPI.Framework.SCore:OnInstanceContentLoaded"),
                    prefix: new HarmonyMethod(typeof(ServerOptimizerOverrides), nameof(ServerOptimizerOverrides.AssignNullDisplay_Prefix))
                );

                // TODO: This seem to have moved to Game1.mapDisplayDevice
                //harmony.Patch(
                //    original: AccessTools.Method("StardewModdingAPI.Framework.SCore:GetMapDisplayDevice"),
                //    prefix: new HarmonyMethod(typeof(ServerOptimizerOverrides), nameof(ServerOptimizerOverrides.ReturnNullDisplay_Prefix))
                //);

                harmony.Patch(
                    original: AccessTools.Method("Microsoft.Xna.Framework.Input.Keyboard:PlatformGetState"),
                    prefix: new HarmonyMethod(typeof(ServerOptimizerOverrides), nameof(ServerOptimizerOverrides.Disable_Prefix))
                );

                harmony.Patch(
                    original: AccessTools.Method("Microsoft.Xna.Framework.Input.Mouse:PlatformGetState"),
                    prefix: new HarmonyMethod(typeof(ServerOptimizerOverrides), nameof(ServerOptimizerOverrides.Disable_Prefix))
                );

                harmony.Patch(
                    original: AccessTools.Method("StardewValley.Game1:UpdateControlInput"),
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

            helper.Events.GameLoop.DayStarted += OnDayStarted;
            helper.Events.GameLoop.Saving += OnSaving;
            helper.Events.GameLoop.DayEnding += OnDayEnding;
        }

        private void OnDayEnding(object sender, DayEndingEventArgs e)
        {
            var before = checked((long)Math.Round(Process.GetCurrentProcess().PrivateMemorySize64 / 1024.0 / 1024.0));
            _monitor.Log($"Running GC", LogLevel.Info);
            GC.Collect(generation: 0, GCCollectionMode.Forced, blocking: true);
            GC.Collect(generation: 1, GCCollectionMode.Forced, blocking: true);
            GC.Collect(generation: 2, GCCollectionMode.Forced, blocking: true);
            var after = checked((long)Math.Round(Process.GetCurrentProcess().PrivateMemorySize64 / 1024.0 / 1024.0));
            var beforeFormatted = Strings.Format(before / 1024.0, "0.00") + " GB";
            var afterFormatted = Strings.Format(after / 1024.0, "0.00") + " GB";
            _monitor.Log($"Ran GC {beforeFormatted} -> {afterFormatted}", LogLevel.Info);
        }

        private void OnDayStarted(object sender, DayStartedEventArgs e)
        {
            if (_disableRendering)
            {
                ServerOptimizerOverrides.DisableDrawing();
            }
        }

        private void OnSaving(object sender, SavingEventArgs e)
        {
            if (_disableRendering)
            {
                ServerOptimizerOverrides.EnableDrawing();
            }
        }

    }
}
