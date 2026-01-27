using JunimoServer.Services.ServerOptim;
using StardewModdingAPI;

namespace JunimoServer.Services.Commands
{
    public class RenderingCommand
    {
        public static void Register(IModHelper helper, IMonitor monitor)
        {
            helper.ConsoleCommands.Add("rendering",
                "Toggle rendering: 'rendering on', 'rendering off', 'rendering toggle', or 'rendering status'",
                (cmd, args) => HandleCommand(args, monitor));
        }

        private static void HandleCommand(string[] args, IMonitor monitor)
        {
            if (args.Length == 0)
            {
                monitor.Log("Usage: rendering on|off|toggle|status", LogLevel.Warn);
                return;
            }

            switch (args[0].ToLowerInvariant())
            {
                case "on":
                    ServerOptimizerOverrides.ToggleRendering(true, monitor);
                    break;
                case "off":
                    ServerOptimizerOverrides.ToggleRendering(false, monitor);
                    break;
                case "toggle":
                    ServerOptimizerOverrides.ToggleRendering(!ServerOptimizerOverrides.IsRenderingEnabled(), monitor);
                    break;
                case "status":
                    var state = ServerOptimizerOverrides.IsRenderingEnabled() ? "enabled" : "disabled";
                    monitor.Log($"Rendering is {state}", LogLevel.Info);
                    break;
                default:
                    monitor.Log("Usage: rendering on|off|toggle|status", LogLevel.Warn);
                    break;
            }
        }
    }
}
