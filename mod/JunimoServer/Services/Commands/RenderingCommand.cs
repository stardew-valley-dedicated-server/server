using System;
using JunimoServer.Services.ServerOptim;
using StardewModdingAPI;
using StardewModdingAPI.Events;

namespace JunimoServer.Services.Commands;

public class RenderingCommand
{
    private static IModHelper _helper;
    private static IMonitor _monitor;

    public static void Register(IModHelper helper, IMonitor monitor)
    {
        _helper = helper;
        _monitor = monitor;

        helper.ConsoleCommands.Add(
            "rendering",
            "Set render rate: 'rendering <fps>' (0 to disable) or 'rendering status'",
            (cmd, args) => HandleCommand(args)
        );
    }

    private static void HandleCommand(string[] args)
    {
        if (args.Length == 0)
        {
            _monitor.Log(
                "Usage: rendering <fps>|status (fps is a non-negative integer; 0 disables)",
                LogLevel.Warn
            );
            return;
        }

        if (string.Equals(args[0], "status", StringComparison.OrdinalIgnoreCase))
        {
            var fps = ServerOptimizerOverrides.GetCurrentServerFps();
            _monitor.Log(
                fps == 0 ? "Rendering is disabled (fps 0)" : $"Rendering is at {fps} fps",
                LogLevel.Info
            );
            return;
        }

        if (!int.TryParse(args[0], out var newFps) || newFps < 0)
        {
            _monitor.Log(
                $"Invalid argument '{args[0]}'. Usage: rendering <fps>|status (fps is a non-negative integer; 0 disables)",
                LogLevel.Warn
            );
            return;
        }

        // Marshal the mutation onto the game loop. SetServerFps writes
        // Game1.mapDisplayDevice, which must run on the game thread; SMAPI console
        // commands run on a background thread. Mirrors ApiService.RunOnGameThreadAsync.
        // A one-shot UpdateTicked handler runs once on the next tick, then unsubscribes.
        void Apply(object sender, UpdateTickedEventArgs e)
        {
            _helper.Events.GameLoop.UpdateTicked -= Apply;
            ServerOptimizerOverrides.SetServerFps(newFps, _monitor);
        }

        _helper.Events.GameLoop.UpdateTicked += Apply;
    }
}
