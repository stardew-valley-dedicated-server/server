using System;
using System.Linq;
using JunimoServer.Services.CabinManager;
using JunimoServer.Services.PersistentOption;
using JunimoServer.Util;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Locations;

namespace JunimoServer.Services.Commands;

internal static class CabinsConsoleCommand
{
    private static IModHelper _helper;
    private static IMonitor _monitor;
    private static CabinManagerService _cabinManager;
    private static PersistentOptions _options;

    private static readonly CommandDescriptor Descriptor = new()
    {
        Name = "cabins",
        Description =
            "Cabin status and management. Run 'cabins' for list, 'cabins add' to create, "
            + "'cabins stackspot [<x> <y>]' to show or set the CabinStack shared spot, "
            + "'cabins migrate start <strategy>|status|place <x> <y>|commit|abort' for a "
            + "staged strategy migration.",
        Subcommands =
        {
            new SubcommandDescriptor { Name = "add", Description = "Create a new cabin" },
            new SubcommandDescriptor
            {
                Name = "stackspot",
                Description = "Show or set the CabinStack shared spot: cabins stackspot [<x> <y>]",
            },
            new SubcommandDescriptor
            {
                Name = "migrate",
                Description =
                    "Staged strategy migration: cabins migrate "
                    + "start <strategy>|status|place <x> <y>|commit|abort",
            },
        },
    };

    public static void Register(
        IModHelper helper,
        IMonitor monitor,
        CabinManagerService cabinManager,
        PersistentOptions options
    )
    {
        _helper = helper;
        _monitor = monitor;
        _cabinManager = cabinManager;
        _options = options;

        helper.ConsoleCommands.Register(Descriptor, (cmd, args) => HandleCommand(args));
    }

    private static void HandleCommand(string[] args)
    {
        if (args.Length > 0 && args[0].ToLowerInvariant() == "add")
        {
            AddCabin();
            return;
        }

        if (args.Length > 0 && args[0].ToLowerInvariant() == "stackspot")
        {
            HandleStackSpot(args);
            return;
        }

        if (args.Length > 0 && args[0].ToLowerInvariant() == "migrate")
        {
            HandleMigrate(args);
            return;
        }

        RunOnGameThread("cabins", ShowCabins);
    }

    private static void HandleStackSpot(string[] args)
    {
        // Read forms marshal onto the game loop like the mutating ones: they enumerate
        // farm.buildings and run placement validation, and console commands run on a
        // background thread — an unmarshaled read racing a game-thread build tears the
        // enumeration (and SMAPI logs the throw at Error, which is test poison).
        if (args.Length == 1)
        {
            RunOnGameThread(
                "cabins stackspot",
                () =>
                {
                    var status = _cabinManager.GetStackSpotStatus();
                    if (status == null)
                    {
                        _monitor.Log(
                            "The stack spot applies only to the CabinStack strategy.",
                            LogLevel.Warn
                        );
                        return;
                    }

                    var s = status.Value;
                    _monitor.Log(
                        $"Stack spot: ({s.Spot.X},{s.Spot.Y}) "
                            + (s.IsOverride ? "(override)" : "(map default)")
                            + (
                                !s.ObstructionChecked
                                    ? " — obstruction not checked (stack is empty)"
                                : s.IsObstructed ? $" — OBSTRUCTED: {s.ObstructionReason}"
                                : ""
                            ),
                        LogLevel.Info
                    );
                }
            );
            return;
        }

        if (
            args.Length >= 3
            && int.TryParse(args[1], out var x)
            && int.TryParse(args[2], out var y)
        )
        {
            RunOnGameThread(
                "cabins stackspot",
                () => LogResult(_cabinManager.TrySetStackSpot(new Point(x, y), out var m), m)
            );
            return;
        }

        _monitor.Log("Usage: cabins stackspot [<x> <y>]", LogLevel.Warn);
    }

    private static void HandleMigrate(string[] args)
    {
        // Every subcommand marshals onto the game loop: console commands run on a
        // background thread, building moves / warp rewrites are game-thread-only, and even
        // the read-only 'status' enumerates farm.buildings, which the game thread mutates.
        var subcommand = args.Length > 1 ? args[1].ToLowerInvariant() : "";
        switch (subcommand)
        {
            case "start":
                if (args.Length < 3 || !CabinStrategyParser.TryParse(args[2], out var target))
                {
                    _monitor.Log(
                        "Usage: cabins migrate start <CabinStack|FarmhouseStack|None>",
                        LogLevel.Warn
                    );
                    return;
                }

                RunOnGameThread(
                    "cabins migrate start",
                    () => LogResult(_cabinManager.TryStartMigration(target, out var m), m)
                );
                return;

            case "status":
                RunOnGameThread("cabins migrate status", ShowMigrationStatus);
                return;

            case "place":
                if (
                    args.Length < 4
                    || !int.TryParse(args[2], out var x)
                    || !int.TryParse(args[3], out var y)
                )
                {
                    _monitor.Log("Usage: cabins migrate place <x> <y>", LogLevel.Warn);
                    return;
                }

                RunOnGameThread(
                    "cabins migrate place",
                    () =>
                        LogResult(
                            _cabinManager.TryPlaceMigration(
                                new Point(x, y),
                                manual: true,
                                out var m
                            ),
                            m
                        )
                );
                return;

            case "commit":
                RunOnGameThread(
                    "cabins migrate commit",
                    () => LogResult(_cabinManager.TryCommitMigration(out var m), m)
                );
                return;

            case "abort":
                RunOnGameThread(
                    "cabins migrate abort",
                    () => LogResult(_cabinManager.TryAbortMigration(out var m), m)
                );
                return;

            default:
                _monitor.Log(
                    "Usage: cabins migrate start <strategy>|status|place <x> <y>|commit|abort",
                    LogLevel.Warn
                );
                return;
        }
    }

    private static void RunOnGameThread(string what, Action action)
    {
        GameThreadOneShot.Run(_helper, _monitor, what, action);
    }

    private static void LogResult(bool success, string message)
    {
        // Successes are already logged at Info by the migration methods themselves (the
        // log is shared with the chat-command path); only refusals need surfacing here.
        if (!success)
        {
            _monitor.Log(message, LogLevel.Warn);
        }
    }

    private static void ShowMigrationStatus()
    {
        var status = _cabinManager.GetMigrationStatus();
        if (status == null)
        {
            _monitor.Log(
                "No staged migration. Start one with 'cabins migrate start <strategy>'.",
                LogLevel.Info
            );
            return;
        }

        var s = status.Value;
        _monitor.Log(
            $"Staged migration {s.FromStrategy} → {s.ToStrategy}: {s.PlacedCount} placed, "
                + $"{s.RemainingCount} remaining.",
            LogLevel.Info
        );
        if (s.StackSpot is { } spot)
        {
            _monitor.Log($"  shared stack spot: ({spot.X},{spot.Y})", LogLevel.Info);
        }
        foreach (var tile in _cabinManager.GetMigrationPlacedTiles())
        {
            _monitor.Log($"  placed at ({tile.X},{tile.Y})", LogLevel.Info);
        }

        _monitor.Log(
            s.RemainingCount == 0
                ? "Run 'cabins migrate commit' to finish, or 'cabins migrate abort' to undo."
                : "Place the rest with '!migrate place' / 'cabins migrate place <x> <y>', or "
                    + "'cabins migrate abort' to undo.",
            LogLevel.Info
        );
    }

    private static void ShowCabins()
    {
        var strategy = _options.Data.CabinStrategy;
        var farm = Game1.getFarm();
        var cabins = farm.buildings.Where(b => b.isCabin).ToList();

        _monitor.Log($"Cabin Status (Strategy: {strategy})", LogLevel.Info);

        int index = 1;
        int assignedCount = 0;
        int availableCount = 0;

        foreach (var building in cabins)
        {
            var cabin = building.GetIndoors<Cabin>();
            var role = building.GetCabinRole();
            var posLabel = role switch
            {
                CabinRole.SharedLobby => "SharedLobby",
                CabinRole.IndividualLobby =>
                    $"IndividualLobby ({building.tileX.Value},{building.tileY.Value})",
                CabinRole.Editing => "Editing (temp)",
                _ when building.IsInHiddenStack() => "Hidden (player stack)",
                _ => $"Visible ({building.tileX.Value},{building.tileY.Value})",
            };

            var ownerId = cabin?.owner?.UniqueMultiplayerID ?? 0;
            string ownerLabel;
            if (ownerId == 0)
            {
                ownerLabel = "Unassigned (available)";
                availableCount++;
            }
            else
            {
                var ownerName = cabin?.owner?.Name ?? "Unknown";
                ownerLabel = $"{ownerName} (ID: {ownerId})";
                assignedCount++;
            }

            _monitor.Log($"  #{index, -3} {posLabel, -30} {ownerLabel}", LogLevel.Info);
            index++;
        }

        if (strategy != CabinStrategy.None)
        {
            var stackPos = StackLocation.Create(_cabinManager.Data);
            _monitor.Log($"", LogLevel.Info);
            _monitor.Log(
                $"  Stack position: ({stackPos.Location.X}, {stackPos.Location.Y})",
                LogLevel.Info
            );
        }

        _monitor.Log($"", LogLevel.Info);
        _monitor.Log(
            $"  Total: {cabins.Count} | Assigned: {assignedCount} | Available: {availableCount}",
            LogLevel.Info
        );
    }

    private static void AddCabin()
    {
        // Marshaled: buildStructure mutates game state, which is game-thread-only (same
        // treatment as the migrate subcommands). GameThreadOneShot Warns when no game is
        // loaded, replacing an inline hasLoadedGame check.
        RunOnGameThread(
            "cabins add",
            () =>
            {
                var farm = Game1.getFarm();

                // Under None the cap IS the player ceiling: refuse instead of letting the
                // build path place onto (or fail against) a developed farm.
                if (_options.IsNone)
                {
                    // Mirror EnsureAtLeastXCabins' None-growth guard (the runtime enforcer this
                    // manual add proxies) exactly — every non-lobby cabin, hidden included. Do
                    // NOT exclude hidden to match TryCommitMigration's freeze snapshot: the two
                    // are equal at commit (no hidden cabins then), but excluding hidden here would
                    // let 'cabins add' over-permit past the frozen cap if a hidden cabin
                    // transiently coexists with None.
                    var totalCount = farm.buildings.Count(b => b.isCabin && !b.IsLobbyOrEditing());
                    var cap = _cabinManager.GetNoneCabinCap(farm);
                    if (totalCount >= cap)
                    {
                        _monitor.Log(
                            $"Refused: the None cabin cap is reached ({totalCount}/{cap} "
                                + "cabins). The cap is frozen at min(designated map positions, "
                                + "MaxPlayers) so cabin growth can never bulldoze a developed "
                                + "farm spot.",
                            LogLevel.Warn
                        );
                        return;
                    }
                }

                bool success = _options.IsNone
                    ? _cabinManager.BuildNewCabinVisible(farm)
                    : _cabinManager.BuildNewCabin(farm);

                if (success)
                {
                    var totalCabins = farm.buildings.Count(b => b.isCabin);
                    var available = farm
                        .buildings.Where(b => b.isCabin)
                        .Count(b =>
                        {
                            var cabin = b.GetIndoors<Cabin>();
                            return cabin?.owner == null || cabin.owner.UniqueMultiplayerID == 0;
                        });
                    _monitor.Log(
                        $"Cabin created. Total: {totalCabins} | Available: {available}",
                        LogLevel.Info
                    );
                }
                else
                {
                    // Warn, not Error: Error is server-side test poison (debugging.md), and
                    // a failed build is recoverable (surfaced via cabin_build_failed too).
                    _monitor.Log("Failed to create cabin.", LogLevel.Warn);
                }
            }
        );
    }
}
