using JunimoServer.Services.ChatCommands;
using JunimoServer.Services.Lobby;
using JunimoServer.Services.Roles;
using JunimoServer.Util;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Locations;
using System.Linq;

namespace JunimoServer.Services.Commands
{
    /// <summary>
    /// Admin commands for managing lobby layouts.
    /// </summary>
    public static class LobbyCommands
    {
        public static void Register(IModHelper helper, IMonitor monitor, ChatCommandsService chatCommandsService, RoleService roleService, LobbyService lobbyService)
        {
            // !lobby - Show help
            chatCommandsService.RegisterCommand("lobby", "(Admin) Manage lobby layouts. Use !lobby help for details.", (args, msg) =>
            {
                if (!roleService.IsPlayerAdmin(msg.SourceFarmer))
                {
                    helper.SendPrivateMessage(msg.SourceFarmer, "You must be an admin to use lobby commands.");
                    return;
                }

                if (args.Length == 0 || args[0].ToLower() == "help")
                {
                    ShowHelp(helper, msg.SourceFarmer);
                    return;
                }

                var subCommand = args[0].ToLower();
                var subArgs = args.Skip(1).ToArray();

                switch (subCommand)
                {
                    case "create":
                        HandleCreate(helper, monitor, lobbyService, msg.SourceFarmer, subArgs);
                        break;
                    case "edit":
                        HandleEdit(helper, monitor, lobbyService, msg.SourceFarmer, subArgs);
                        break;
                    case "save":
                        HandleSave(helper, monitor, lobbyService, msg.SourceFarmer);
                        break;
                    case "cancel":
                        HandleCancel(helper, monitor, lobbyService, msg.SourceFarmer);
                        break;
                    case "list":
                        HandleList(helper, lobbyService, msg.SourceFarmer);
                        break;
                    case "info":
                        HandleInfo(helper, lobbyService, msg.SourceFarmer, subArgs);
                        break;
                    case "set":
                        HandleSet(helper, monitor, lobbyService, msg.SourceFarmer, subArgs);
                        break;
                    case "rename":
                        HandleRename(helper, monitor, lobbyService, msg.SourceFarmer, subArgs);
                        break;
                    case "copy":
                        HandleCopy(helper, monitor, lobbyService, msg.SourceFarmer, subArgs);
                        break;
                    case "delete":
                        HandleDelete(helper, monitor, lobbyService, msg.SourceFarmer, subArgs);
                        break;
                    case "export":
                        HandleExport(helper, monitor, lobbyService, msg.SourceFarmer, subArgs);
                        break;
                    case "import":
                        HandleImport(helper, monitor, lobbyService, msg.SourceFarmer, subArgs);
                        break;
                    case "spawn":
                        HandleSpawn(helper, monitor, lobbyService, msg.SourceFarmer);
                        break;
                    case "reset":
                        HandleReset(helper, monitor, lobbyService, msg.SourceFarmer);
                        break;
                    default:
                        helper.SendPrivateMessage(msg.SourceFarmer, $"Unknown subcommand: {subCommand}. Use !lobby help.");
                        break;
                }
            });

            monitor.Log("[LobbyCommands] Registered !lobby command", LogLevel.Trace);
        }

        private static void ShowHelp(IModHelper helper, long playerId)
        {
            var lines = new[]
            {
                "=== Lobby Commands ===",
                "!lobby create <name> - Create new layout",
                "!lobby edit <name> - Edit existing layout",
                "!lobby save - Save and exit editing mode",
                "!lobby cancel - Discard changes and exit editing",
                "!lobby spawn - Set spawn point at your position",
                "!lobby reset - Clear all furniture while editing",
                "!lobby list - List all layouts",
                "!lobby info <name> - Show layout details",
                "!lobby set <name> - Set active layout",
                "!lobby rename <old> <new> - Rename a layout",
                "!lobby copy <src> <dest> - Duplicate a layout",
                "!lobby delete <name> - Delete a layout",
                "!lobby export <name> - Export as string",
                "!lobby import <name> <str> - Import from string",
                "",
                "Workflow: create/edit -> decorate -> spawn -> save -> set"
            };

            foreach (var line in lines)
                helper.SendPrivateMessage(playerId, line);
        }

        private static void HandleCreate(IModHelper helper, IMonitor monitor, LobbyService lobbyService, long playerId, string[] args)
        {
            if (args.Length == 0)
            {
                helper.SendPrivateMessage(playerId, "Usage: !lobby create <name>");
                return;
            }

            var layoutName = string.Join("-", args); // Join with dash instead of space

            // Validate layout name
            var validationError = LobbyService.ValidateLayoutName(layoutName);
            if (validationError != null)
            {
                helper.SendPrivateMessage(playerId, validationError);
                helper.SendPrivateMessage(playerId, "Use letters, numbers, dash (-), underscore (_) only.");
                return;
            }

            // Check if already in an editing session
            if (lobbyService.IsEditingLayout(playerId))
            {
                var currentLayout = lobbyService.GetEditingLayoutName(playerId);
                helper.SendPrivateMessage(playerId, $"You are already editing layout '{currentLayout}'.");
                helper.SendPrivateMessage(playerId, "Use !lobby save to finish, or !lobby cancel to discard.");
                return;
            }

            // Create the layout and get the cabin
            var cabin = lobbyService.CreateLayoutForEditing(layoutName, playerId);
            if (cabin == null)
            {
                helper.SendPrivateMessage(playerId, $"Layout '{layoutName}' already exists or could not be created.");
                return;
            }

            // Warp the admin into the cabin at a safe position (center of room)
            var indoors = cabin.GetIndoors<Cabin>();
            if (indoors != null)
            {
                var entry = lobbyService.GetSafeEntryPoint(indoors);
                Game1.server.sendMessage(playerId, Multiplayer.passout, Game1.player, new object[]
                {
                    indoors.NameOrUniqueName, entry.X, entry.Y, false
                });

                helper.SendPrivateMessage(playerId, $"Created layout '{layoutName}'!");
                helper.SendPrivateMessage(playerId, "Editing mode: Permanent daylight, immune to exhaustion/sleep.");
                helper.SendPrivateMessage(playerId, "Other players can sleep - you'll keep editing!");
                helper.SendPrivateMessage(playerId, "Commands: !lobby spawn, !lobby reset, !lobby save, !lobby cancel");
            }
            else
            {
                helper.SendPrivateMessage(playerId, "Error: Could not access cabin interior.");
            }
        }

        private static void HandleEdit(IModHelper helper, IMonitor monitor, LobbyService lobbyService, long playerId, string[] args)
        {
            if (args.Length == 0)
            {
                helper.SendPrivateMessage(playerId, "Usage: !lobby edit <name>");
                return;
            }

            var layoutName = string.Join("-", args);

            // Check if already in an editing session
            if (lobbyService.IsEditingLayout(playerId))
            {
                var currentLayout = lobbyService.GetEditingLayoutName(playerId);
                helper.SendPrivateMessage(playerId, $"You are already editing layout '{currentLayout}'.");
                helper.SendPrivateMessage(playerId, "Use !lobby save to finish, or !lobby cancel to discard.");
                return;
            }

            // Open the layout for editing
            var cabin = lobbyService.EditLayoutForEditing(layoutName, playerId);
            if (cabin == null)
            {
                helper.SendPrivateMessage(playerId, $"Layout '{layoutName}' not found.");
                helper.SendPrivateMessage(playerId, "Use !lobby list to see available layouts.");
                return;
            }

            // Warp the admin into the cabin at a safe position
            var indoors = cabin.GetIndoors<Cabin>();
            if (indoors != null)
            {
                var entry = lobbyService.GetSafeEntryPoint(indoors);
                Game1.server.sendMessage(playerId, Multiplayer.passout, Game1.player, new object[]
                {
                    indoors.NameOrUniqueName, entry.X, entry.Y, false
                });

                helper.SendPrivateMessage(playerId, $"Editing layout '{layoutName}'");
                helper.SendPrivateMessage(playerId, "Editing mode: Permanent daylight, immune to exhaustion/sleep.");
                helper.SendPrivateMessage(playerId, "Other players can sleep - you'll keep editing!");
                helper.SendPrivateMessage(playerId, "Commands: !lobby spawn, !lobby reset, !lobby save, !lobby cancel");
            }
            else
            {
                helper.SendPrivateMessage(playerId, "Error: Could not access cabin interior.");
            }
        }

        private static void HandleSave(IModHelper helper, IMonitor monitor, LobbyService lobbyService, long playerId)
        {
            if (!lobbyService.IsEditingLayout(playerId))
            {
                helper.SendPrivateMessage(playerId, "You are not editing any layout.");
                helper.SendPrivateMessage(playerId, "Use !lobby create <name> or !lobby edit <name> to start.");
                return;
            }

            var layoutName = lobbyService.GetEditingLayoutName(playerId);

            if (lobbyService.SaveCurrentLayout(playerId))
            {
                helper.SendPrivateMessage(playerId, $"Saved layout '{layoutName}' successfully!");
                helper.SendPrivateMessage(playerId, $"Use '!lobby set {layoutName}' to make it active.");
            }
            else
            {
                helper.SendPrivateMessage(playerId, "Failed to save layout. Make sure you are inside the editing cabin.");
            }
        }

        private static void HandleCancel(IModHelper helper, IMonitor monitor, LobbyService lobbyService, long playerId)
        {
            if (!lobbyService.IsEditingLayout(playerId))
            {
                helper.SendPrivateMessage(playerId, "You are not editing any layout.");
                return;
            }

            var layoutName = lobbyService.GetEditingLayoutName(playerId);
            var isNew = lobbyService.IsEditingNewLayout(playerId);

            if (lobbyService.CancelEditing(playerId))
            {
                if (isNew)
                {
                    helper.SendPrivateMessage(playerId, $"Cancelled. New layout '{layoutName}' was discarded.");
                }
                else
                {
                    helper.SendPrivateMessage(playerId, $"Cancelled. Changes to '{layoutName}' were discarded.");
                }
            }
            else
            {
                helper.SendPrivateMessage(playerId, "Failed to cancel editing session.");
            }
        }

        private static void HandleList(IModHelper helper, LobbyService lobbyService, long playerId)
        {
            var layouts = lobbyService.GetLayoutNames().ToList();
            var activeLayoutName = lobbyService.GetActiveLayout()?.Name ?? "default";

            helper.SendPrivateMessage(playerId, "=== Lobby Layouts ===");

            if (!layouts.Any())
            {
                helper.SendPrivateMessage(playerId, "(no layouts)");
                return;
            }

            foreach (var name in layouts)
            {
                var layout = lobbyService.GetLayout(name);
                var marker = name == activeLayoutName ? " [ACTIVE]" : "";
                var itemCount = (layout?.Furniture.Count ?? 0) + (layout?.Objects.Count ?? 0);
                helper.SendPrivateMessage(playerId, $"  - {name}{marker} ({itemCount} items)");
            }
        }

        private static void HandleSet(IModHelper helper, IMonitor monitor, LobbyService lobbyService, long playerId, string[] args)
        {
            if (args.Length == 0)
            {
                helper.SendPrivateMessage(playerId, "Usage: !lobby set <name>");
                return;
            }

            var layoutName = string.Join("-", args);

            if (lobbyService.SetActiveLayout(layoutName))
            {
                helper.SendPrivateMessage(playerId, $"Active layout set to '{layoutName}'.");
                helper.SendPrivateMessage(playerId, "New players will now see this layout in the lobby.");
            }
            else
            {
                helper.SendPrivateMessage(playerId, $"Layout '{layoutName}' not found.");
                helper.SendPrivateMessage(playerId, "Use !lobby list to see available layouts.");
            }
        }

        private static void HandleInfo(IModHelper helper, LobbyService lobbyService, long playerId, string[] args)
        {
            if (args.Length == 0)
            {
                helper.SendPrivateMessage(playerId, "Usage: !lobby info <name>");
                return;
            }

            var layoutName = string.Join("-", args);
            var layout = lobbyService.GetLayout(layoutName);

            if (layout == null)
            {
                helper.SendPrivateMessage(playerId, $"Layout '{layoutName}' not found.");
                return;
            }

            var isActive = lobbyService.GetActiveLayout()?.Name == layoutName;

            helper.SendPrivateMessage(playerId, $"=== Layout: {layoutName} ===");
            helper.SendPrivateMessage(playerId, $"Status: {(isActive ? "ACTIVE" : "inactive")}");
            helper.SendPrivateMessage(playerId, $"Cabin: {layout.CabinSkin} (level {layout.UpgradeLevel})");
            helper.SendPrivateMessage(playerId, $"Furniture: {layout.Furniture.Count}");
            helper.SendPrivateMessage(playerId, $"Objects: {layout.Objects.Count}");
            helper.SendPrivateMessage(playerId, $"Wallpapers: {layout.Wallpapers.Count}");
            helper.SendPrivateMessage(playerId, $"Floors: {layout.Floors.Count}");

            if (layout.SpawnX.HasValue && layout.SpawnY.HasValue)
            {
                helper.SendPrivateMessage(playerId, $"Spawn: ({layout.SpawnX}, {layout.SpawnY})");
            }
            else
            {
                helper.SendPrivateMessage(playerId, "Spawn: (default entry)");
            }
        }

        private static void HandleRename(IModHelper helper, IMonitor monitor, LobbyService lobbyService, long playerId, string[] args)
        {
            if (args.Length < 2)
            {
                helper.SendPrivateMessage(playerId, "Usage: !lobby rename <old_name> <new_name>");
                return;
            }

            var oldName = args[0];
            var newName = string.Join("-", args.Skip(1)); // Join with dash

            if (oldName == "default")
            {
                helper.SendPrivateMessage(playerId, "Cannot rename the default layout.");
                return;
            }

            // Validate new name
            var validationError = LobbyService.ValidateLayoutName(newName);
            if (validationError != null)
            {
                helper.SendPrivateMessage(playerId, validationError);
                return;
            }

            if (lobbyService.IsLayoutBeingEdited(oldName))
            {
                helper.SendPrivateMessage(playerId, $"Cannot rename '{oldName}' - it is currently being edited.");
                return;
            }

            if (lobbyService.RenameLayout(oldName, newName))
            {
                helper.SendPrivateMessage(playerId, $"Renamed '{oldName}' to '{newName}'.");
            }
            else
            {
                helper.SendPrivateMessage(playerId, $"Cannot rename '{oldName}'.");
                helper.SendPrivateMessage(playerId, "Check that it exists and the new name is available.");
            }
        }

        private static void HandleCopy(IModHelper helper, IMonitor monitor, LobbyService lobbyService, long playerId, string[] args)
        {
            if (args.Length < 2)
            {
                helper.SendPrivateMessage(playerId, "Usage: !lobby copy <source> <destination>");
                return;
            }

            var sourceName = args[0];
            var destName = string.Join("-", args.Skip(1)); // Join with dash

            // Validate destination name
            var validationError = LobbyService.ValidateLayoutName(destName);
            if (validationError != null)
            {
                helper.SendPrivateMessage(playerId, validationError);
                return;
            }

            if (lobbyService.CopyLayout(sourceName, destName))
            {
                helper.SendPrivateMessage(playerId, $"Copied '{sourceName}' to '{destName}'.");
                helper.SendPrivateMessage(playerId, $"Use '!lobby edit {destName}' to modify the copy.");
            }
            else
            {
                helper.SendPrivateMessage(playerId, $"Cannot copy '{sourceName}' to '{destName}'.");
                helper.SendPrivateMessage(playerId, "Check that source exists and destination is available.");
            }
        }

        private static void HandleDelete(IModHelper helper, IMonitor monitor, LobbyService lobbyService, long playerId, string[] args)
        {
            if (args.Length == 0)
            {
                helper.SendPrivateMessage(playerId, "Usage: !lobby delete <name>");
                return;
            }

            var layoutName = string.Join("-", args);

            if (layoutName == "default")
            {
                helper.SendPrivateMessage(playerId, "Cannot delete the default layout.");
                return;
            }

            if (lobbyService.IsLayoutBeingEdited(layoutName))
            {
                helper.SendPrivateMessage(playerId, $"Cannot delete '{layoutName}' - it is currently being edited.");
                return;
            }

            if (lobbyService.DeleteLayout(layoutName))
            {
                helper.SendPrivateMessage(playerId, $"Deleted layout '{layoutName}'.");
            }
            else
            {
                helper.SendPrivateMessage(playerId, $"Cannot delete '{layoutName}'.");
                helper.SendPrivateMessage(playerId, "Make sure it exists and is not the active layout.");
            }
        }

        private static void HandleExport(IModHelper helper, IMonitor monitor, LobbyService lobbyService, long playerId, string[] args)
        {
            if (args.Length == 0)
            {
                helper.SendPrivateMessage(playerId, "Usage: !lobby export <name>");
                return;
            }

            var layoutName = string.Join("-", args);
            var exportString = lobbyService.ExportLayout(layoutName);

            if (exportString == null)
            {
                helper.SendPrivateMessage(playerId, $"Layout '{layoutName}' not found.");
                return;
            }

            helper.SendPrivateMessage(playerId, $"Exported layout '{layoutName}' ({exportString.Length} chars).");
            helper.SendPrivateMessage(playerId, "Check the server logs for the export string.");

            // Log to server console for copying
            monitor.Log($"[Lobby] Export string for '{layoutName}':", LogLevel.Info);
            monitor.Log(exportString, LogLevel.Info);
        }

        private static void HandleImport(IModHelper helper, IMonitor monitor, LobbyService lobbyService, long playerId, string[] args)
        {
            if (args.Length < 2)
            {
                helper.SendPrivateMessage(playerId, "Usage: !lobby import <name> <string>");
                helper.SendPrivateMessage(playerId, "The string should start with 'SDVL0'");
                return;
            }

            var layoutName = args[0];

            // Validate layout name
            var validationError = LobbyService.ValidateLayoutName(layoutName);
            if (validationError != null)
            {
                helper.SendPrivateMessage(playerId, validationError);
                return;
            }

            var exportString = string.Join("", args.Skip(1)); // Join remaining args (no spaces in base64)

            var (success, message) = lobbyService.ImportLayout(layoutName, exportString);

            if (success)
            {
                helper.SendPrivateMessage(playerId, $"Successfully imported layout '{layoutName}'!");
                helper.SendPrivateMessage(playerId, message);
                helper.SendPrivateMessage(playerId, $"Use '!lobby set {layoutName}' to make it active.");
            }
            else
            {
                helper.SendPrivateMessage(playerId, $"Import failed: {message}");
            }
        }

        private static void HandleSpawn(IModHelper helper, IMonitor monitor, LobbyService lobbyService, long playerId)
        {
            if (!lobbyService.IsEditingLayout(playerId))
            {
                helper.SendPrivateMessage(playerId, "You must be editing a layout to set spawn point.");
                helper.SendPrivateMessage(playerId, "Use !lobby create <name> to start editing.");
                return;
            }

            if (lobbyService.SetLayoutSpawnPoint(playerId))
            {
                helper.SendPrivateMessage(playerId, "Spawn point set to your current position!");
                helper.SendPrivateMessage(playerId, "New players will spawn here when they join.");
            }
            else
            {
                helper.SendPrivateMessage(playerId, "Failed to set spawn point.");
            }
        }

        private static void HandleReset(IModHelper helper, IMonitor monitor, LobbyService lobbyService, long playerId)
        {
            if (!lobbyService.IsEditingLayout(playerId))
            {
                helper.SendPrivateMessage(playerId, "You must be editing a layout to reset it.");
                helper.SendPrivateMessage(playerId, "Use !lobby create <name> to start editing.");
                return;
            }

            if (lobbyService.ResetEditingCabin(playerId))
            {
                helper.SendPrivateMessage(playerId, "Cabin reset! All furniture and decorations cleared.");
            }
            else
            {
                helper.SendPrivateMessage(playerId, "Failed to reset cabin. Make sure you are inside it.");
            }
        }
    }
}
