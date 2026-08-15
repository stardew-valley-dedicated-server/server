using System;
using JunimoServer.Services.ServerOptim;
using JunimoServer.Util;
using StardewModdingAPI;

namespace JunimoServer.Services.Commands;

public class RenderingCommand
{
    private static IModHelper _helper;
    private static IMonitor _monitor;

    private static readonly CommandDescriptor Descriptor = new()
    {
        Name = "rendering",
        Description = "Set render rate: 'rendering <fps>' (0 to disable) or 'rendering status'",
        Subcommands =
        {
            new SubcommandDescriptor
            {
                Name = "status",
                Description = "Show the current render rate",
            },
        },
    };

    public static void Register(IModHelper helper, IMonitor monitor)
    {
        _helper = helper;
        _monitor = monitor;

        CommandDescriptorRegistry.Add(Descriptor);
        helper.ConsoleCommands.Add(
            Descriptor.Name,
            Descriptor.Description,
            (cmd, args) => HandleCommand(args)
        );
    }

    private static string Usage =>
        $"Usage: rendering <fps>|{Descriptor.SubcommandNames} (fps is a non-negative integer; 0 disables)";

    private static void HandleCommand(string[] args)
    {
        if (args.Length == 0)
        {
            _monitor.Log(Usage, LogLevel.Warn);
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
            _monitor.Log($"Invalid argument '{args[0]}'. {Usage}", LogLevel.Warn);
            return;
        }

        // SetServerFps writes Game1.mapDisplayDevice — game-thread-only. Works pre-load, so
        // no loaded-save requirement.
        GameThreadOneShot.Run(
            _helper,
            _monitor,
            "rendering command",
            () => ServerOptimizerOverrides.SetServerFps(newFps, _monitor),
            requireLoadedSave: false
        );
    }
}
