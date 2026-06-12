using System;
using System.Diagnostics;
using System.IO;
using HarmonyLib;
using JunimoServer.Services.AlwaysOn;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.SDKs.GogGalaxy;

namespace JunimoServer.Services.ServerOptim
{
    public class ServerOptimizer : ModService
    {
        private readonly IMonitor _monitor;
        private readonly IModHelper _helper;

        public ServerOptimizer(
            Harmony harmony,
            IMonitor monitor,
            IModHelper helper,
            AlwaysOnConfig alwaysOnConfig
        )
        {
            _monitor = monitor;
            _helper = helper;

            ServerOptimizerOverrides.Initialize(monitor, alwaysOnConfig);

            harmony.Patch(
                original: AccessTools.Method(typeof(Game), "BeginDraw"),
                prefix: new HarmonyMethod(
                    typeof(ServerOptimizerOverrides),
                    nameof(ServerOptimizerOverrides.Draw_Prefix)
                )
            );

            harmony.Patch(
                original: AccessTools.Method(typeof(Game), "Draw", new[] { typeof(GameTime) }),
                prefix: new HarmonyMethod(
                    typeof(ServerOptimizerOverrides),
                    nameof(ServerOptimizerOverrides.GameDraw_Prefix)
                )
            );

            harmony.Patch(
                original: AccessTools.Method("StardewValley.Game1:updateMusic"),
                prefix: new HarmonyMethod(
                    typeof(ServerOptimizerOverrides),
                    nameof(ServerOptimizerOverrides.Disable_Prefix)
                )
            );

            // harmony.Patch(
            //     original: AccessTools.Method("StardewValley.Game1:changeMusicTrack"),
            //     prefix: new HarmonyMethod(typeof(ServerOptimizerOverrides), nameof(ServerOptimizerOverrides.Disable_Prefix))
            // );

            harmony.Patch(
                original: AccessTools.Method("StardewValley.Game1:initializeVolumeLevels"),
                prefix: new HarmonyMethod(
                    typeof(ServerOptimizerOverrides),
                    nameof(ServerOptimizerOverrides.Disable_Prefix)
                )
            );

            harmony.Patch(
                original: AccessTools.Method("StardewValley.Audio.SoundsHelper:PlayLocal"),
                prefix: new HarmonyMethod(
                    typeof(ServerOptimizerOverrides),
                    nameof(ServerOptimizerOverrides.Disable_Prefix)
                )
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
                prefix: new HarmonyMethod(
                    typeof(ServerOptimizerOverrides),
                    nameof(ServerOptimizerOverrides.Disable_Prefix)
                )
            );

            harmony.Patch(
                original: AccessTools.Method(
                    "StardewValley.BellsAndWhistles.AmbientLocationSounds:update"
                ),
                prefix: new HarmonyMethod(
                    typeof(ServerOptimizerOverrides),
                    nameof(ServerOptimizerOverrides.Disable_Prefix)
                )
            );

            harmony.Patch(
                original: AccessTools.Method(typeof(GalaxySocket), "CreateLobby"),
                prefix: new HarmonyMethod(
                    typeof(ServerOptimizerOverrides),
                    nameof(ServerOptimizerOverrides.CreateLobby_Prefix)
                )
            );

            harmony.Patch(
                original: AccessTools.Method(typeof(GalaxySocket), "onGalaxyLobbyCreated"),
                prefix: new HarmonyMethod(
                    typeof(ServerOptimizerOverrides),
                    nameof(ServerOptimizerOverrides.OnGalaxyLobbyCreated_Prefix)
                )
            );

            // Keyboard/Mouse PlatformGetState is the single source feeding every input
            // consumer — the game (control, menus, chat) and SMAPI's ButtonPressed pipeline.
            // The postfixes suppress input there when rendering is off or host automation is
            // active, carving out only the F9/F10 hotkeys so the operator can still drop
            // automation from the VNC display. Read live, so a runtime fps switch or host-auto
            // toggle restores input without a restart. No game-side UpdateControlInput patch
            // is needed — when the device returns empty state, every consumer sees nothing.
            harmony.Patch(
                original: AccessTools.Method(
                    "Microsoft.Xna.Framework.Input.Keyboard:PlatformGetState"
                ),
                postfix: new HarmonyMethod(
                    typeof(ServerOptimizerOverrides),
                    nameof(ServerOptimizerOverrides.KeyboardState_Postfix)
                )
            );

            harmony.Patch(
                original: AccessTools.Method(
                    "Microsoft.Xna.Framework.Input.Mouse:PlatformGetState"
                ),
                postfix: new HarmonyMethod(
                    typeof(ServerOptimizerOverrides),
                    nameof(ServerOptimizerOverrides.MouseState_Postfix)
                )
            );

            // musl/.NET 6 fix: SMAPI's SModHooks.StartTask uses RunSynchronously() which
            // on musl queues to ThreadPool instead of running inline, causing a deadlock
            // when the task needs BlockOnUIThread() (texture loading during _newDayAfterFade).
            // Instead of patching StartTask (which would break other mods' thread-safety),
            // we patch BlockOnUIThread to run inline when called from a background thread.
            // This preserves SMAPI's sync semantics while avoiding the deadlock.
            if (File.Exists("/lib/ld-musl-x86_64.so.1"))
            {
                // Find SMAPI's SModHooks.StartTask. Can't use string-based lookup because
                // the SMAPI assembly may not be loaded yet by that name
                System.Reflection.MethodInfo smodHooksStartTask = null;
                foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
                {
                    var smodHooksType = asm.GetType("StardewModdingAPI.Framework.SModHooks");
                    if (smodHooksType == null)
                        continue;
                    smodHooksStartTask = AccessTools.Method(
                        smodHooksType,
                        "StartTask",
                        new[] { typeof(System.Threading.Tasks.Task), typeof(string) }
                    );
                    if (smodHooksStartTask != null)
                    {
                        monitor.Log(
                            $"[ServerOptimizer] Found SModHooks.StartTask in {asm.GetName().Name}",
                            LogLevel.Debug
                        );
                        break;
                    }
                }

                if (smodHooksStartTask != null)
                {
                    harmony.Patch(
                        original: smodHooksStartTask,
                        prefix: new HarmonyMethod(
                            typeof(ServerOptimizerOverrides),
                            nameof(ServerOptimizerOverrides.StartTask_Prefix)
                        )
                    );
                    monitor.Log(
                        "[ServerOptimizer] musl detected, patched SModHooks.StartTask for content-loading tasks (deadlock prevention)",
                        LogLevel.Info
                    );
                }
                else
                {
                    monitor.Log(
                        "[ServerOptimizer] musl detected but could not find SModHooks.StartTask. Deadlock may occur.",
                        LogLevel.Warn
                    );
                }
            }

            if (Env.EnableModIncompatibleOptimizations)
            {
                harmony.Patch(
                    original: AccessTools.Method(
                        "StardewModdingAPI.Framework.StateTracking.Snapshots.PlayerSnapshot:Update"
                    ),
                    prefix: new HarmonyMethod(
                        typeof(ServerOptimizerOverrides),
                        nameof(ServerOptimizerOverrides.Disable_Prefix)
                    )
                );

                harmony.Patch(
                    original: AccessTools.Method(
                        "StardewModdingAPI.Framework.StateTracking.Snapshots.WorldLocationsSnapshot:Update"
                    ),
                    prefix: new HarmonyMethod(
                        typeof(ServerOptimizerOverrides),
                        nameof(ServerOptimizerOverrides.Disable_Prefix)
                    )
                );

                harmony.Patch(
                    original: AccessTools.Method(
                        "StardewModdingAPI.Framework.StateTracking.PlayerTracker:Update"
                    ),
                    prefix: new HarmonyMethod(
                        typeof(ServerOptimizerOverrides),
                        nameof(ServerOptimizerOverrides.Disable_Prefix)
                    )
                );

                harmony.Patch(
                    original: AccessTools.Method(
                        "StardewModdingAPI.Framework.StateTracking.WorldLocationsTracker:Update"
                    ),
                    prefix: new HarmonyMethod(
                        typeof(ServerOptimizerOverrides),
                        nameof(ServerOptimizerOverrides.Disable_Prefix)
                    )
                );

                harmony.Patch(
                    original: AccessTools.Method(
                        "StardewModdingAPI.Framework.StateTracking.WorldLocationsTracker:Reset"
                    ),
                    prefix: new HarmonyMethod(
                        typeof(ServerOptimizerOverrides),
                        nameof(ServerOptimizerOverrides.Disable_Prefix)
                    )
                );
            }

            helper.Events.GameLoop.GameLaunched += OnGameLaunched;
            helper.Events.GameLoop.DayStarted += OnDayStarted;
            helper.Events.GameLoop.Saving += OnSaving;
            helper.Events.GameLoop.DayEnding += OnDayEnding;

            if (Env.ServerTps != 60)
            {
                helper.Events.GameLoop.UpdateTicked += OnFirstTick;
            }
        }

        private void OnGameLaunched(object sender, GameLaunchedEventArgs e)
        {
            // Capture the original display device now that the game has initialized it.
            // This allows runtime switching between the real device and NullDisplayDevice.
            ServerOptimizerOverrides.SaveOriginalDisplayDevice(Game1.mapDisplayDevice);

            // Apply the boot fps unconditionally. SetServerFps does its own fps == 0 check:
            // 0 installs NullDisplayDevice and suppresses draws, N > 0 throttles at N.
            ServerOptimizerOverrides.SetServerFps(Env.ServerFps, _monitor);
        }

        private void OnFirstTick(object sender, UpdateTickedEventArgs e)
        {
            // Set TargetElapsedTime on the first tick, after MonoGame's Initialize() has
            // finished. Setting it in GameLaunched is too early; base.Initialize() resets
            // TargetElapsedTime to the 60 FPS default after our handler runs.
            _helper.Events.GameLoop.UpdateTicked -= OnFirstTick;
            Game1.game1.TargetElapsedTime = TimeSpan.FromMilliseconds(1000.0 / Env.ServerTps);
            _monitor.Log(
                $"Server TPS set to {Env.ServerTps} (tick interval: {Game1.game1.TargetElapsedTime.TotalMilliseconds:F1}ms)",
                LogLevel.Info
            );

            // Test mode: cap MaxElapsedTime to one tick. MonoGame's default is 500ms,
            // which at low test TPS (15 → 66.7ms/tick) allows up to ~7 catch-up
            // Updates back-to-back when the game thread falls behind. The catch-up
            // burst runs Updates without Draws, so the framebuffer stays stale for
            // the burst's wall-clock duration and then jumps forward when Draw
            // resumes — producing per-test video frames whose content lags real
            // wall-clock and de-syncs server vs client video. Capping the accumulator
            // to one tick prevents the burst: dropped Updates stay dropped (game-time
            // drifts behind real-time during slow stretches), but Draw stays paced
            // with wall-clock so video frames represent what's happening when.
            //
            // Gated to test mode: in production the default 500ms catch-up budget
            // keeps in-game time accurate to wall-clock during transient slowdowns,
            // which matters for 24/7 hosts with festival/save cadences.
            if (Env.IsTest)
            {
                // MaxElapsedTime lives on the MonoGame Game base — accessed through
                // StardewValley.GameRunner.instance, since Game1 itself derives from
                // InstanceGame which only proxies TargetElapsedTime / IsFixedTimeStep.
                StardewValley.GameRunner.instance.MaxElapsedTime = Game1.game1.TargetElapsedTime;
                _monitor.Log(
                    $"Test mode: MaxElapsedTime capped to TargetElapsedTime "
                        + $"({StardewValley.GameRunner.instance.MaxElapsedTime.TotalMilliseconds:F1}ms) — no MonoGame catch-up bursts.",
                    LogLevel.Info
                );
            }
        }

        private void OnDayEnding(object sender, DayEndingEventArgs e)
        {
            // Enable drawing for the end-of-night phase.
            // SaveGameMenu.update() requires hasDrawn (set in draw()) before it starts
            // saving. If drawing is suppressed, draw() never runs and the save deadlocks.
            // DayEnding fires before the SaveGameMenu is created, so enabling here
            // ensures draw() can run. OnDayStarted re-disables drawing afterward.
            if (ServerOptimizerOverrides.GetCurrentServerFps() == 0)
            {
                _monitor.Log(
                    $"[ServerOptimizer] OnDayEnding: enabling drawing for save phase",
                    LogLevel.Debug
                );
                ServerOptimizerOverrides.EnableDrawing();
            }
            else
            {
                _monitor.Log(
                    $"[ServerOptimizer] OnDayEnding: drawing already enabled",
                    LogLevel.Debug
                );
            }

            _monitor.Log($"[ServerOptimizer] Running garbage collection...", LogLevel.Debug);
            var before = checked(
                (long)Math.Round(Process.GetCurrentProcess().PrivateMemorySize64 / 1024.0 / 1024.0)
            );
            GC.Collect(generation: 2, GCCollectionMode.Optimized, blocking: true);
            var after = checked(
                (long)Math.Round(Process.GetCurrentProcess().PrivateMemorySize64 / 1024.0 / 1024.0)
            );
            var beforeFormatted = (before / 1024.0).ToString("F2") + " GB";
            var afterFormatted = (after / 1024.0).ToString("F2") + " GB";
            _monitor.Log(
                $"[ServerOptimizer] Garbage collection complete. Before: {beforeFormatted} After: {afterFormatted}",
                LogLevel.Debug
            );
        }

        private void OnDayStarted(object sender, DayStartedEventArgs e)
        {
            // Re-apply the current rendering state after each day transition
            if (ServerOptimizerOverrides.GetCurrentServerFps() == 0)
            {
                ServerOptimizerOverrides.DisableDrawing();
            }
        }

        private void OnSaving(object sender, SavingEventArgs e)
        {
            // Safety net: ensure drawing is enabled during save.
            // OnDayEnding already enables it, but this covers edge cases where
            // a save is triggered outside the normal day-end flow.
            if (ServerOptimizerOverrides.GetCurrentServerFps() == 0)
            {
                ServerOptimizerOverrides.EnableDrawing();
            }
        }
    }
}
