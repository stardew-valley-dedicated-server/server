using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using StardewModdingAPI;

namespace JunimoServer.Util;

/// <summary>
/// Best-effort enumeration of every registered SMAPI console command (SMAPI built-ins plus any
/// mod's commands) via reflection over SMAPI's internal <c>SCore.Instance.CommandManager</c> —
/// SMAPI has no public command enumeration (<c>ICommandHelper</c> exposes only <c>Add</c>).
/// Reads a static singleton and a snapshot list, no <c>Game1</c> access, so it is safe off the
/// game thread. Any failure logs a Warn and returns what was collected so far; the command
/// catalog then still contains our own commands.
/// </summary>
public static class SmapiCommandCatalog
{
    /// <summary>Source label for SMAPI's own (mod-less) commands.</summary>
    public const string SmapiSource = "smapi";

    /// <summary>
    /// Returns (name, source) for every registered console command, where source is
    /// <see cref="SmapiSource"/> for SMAPI built-ins or the owning mod's display name.
    /// </summary>
    public static IReadOnlyList<(string Name, string Source)> GetAll(IMonitor monitor)
    {
        var results = new List<(string, string)>();
        const BindingFlags anyStatic =
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        const BindingFlags anyInstance =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        try
        {
            var scoreType = Type.GetType("StardewModdingAPI.Framework.SCore, StardewModdingAPI");
            if (scoreType == null)
            {
                monitor.Log(
                    "Could not find SMAPI SCore type for command enumeration",
                    LogLevel.Warn
                );
                return results;
            }

            var score = scoreType.GetProperty("Instance", anyStatic)?.GetValue(null);
            if (score == null)
            {
                monitor.Log(
                    "SMAPI SCore.Instance is unavailable for command enumeration",
                    LogLevel.Warn
                );
                return results;
            }

            var commandManager =
                scoreType.GetField("CommandManager", anyInstance)?.GetValue(score)
                ?? scoreType.GetProperty("CommandManager", anyInstance)?.GetValue(score);
            if (commandManager == null)
            {
                monitor.Log(
                    "Could not find SMAPI CommandManager for command enumeration",
                    LogLevel.Warn
                );
                return results;
            }

            if (
                commandManager.GetType().GetMethod("GetAll")?.Invoke(commandManager, null)
                is not IEnumerable commands
            )
            {
                monitor.Log("SMAPI CommandManager.GetAll() is unavailable", LogLevel.Warn);
                return results;
            }

            PropertyInfo nameProperty = null;
            PropertyInfo modProperty = null;
            foreach (var command in commands)
            {
                if (command == null)
                {
                    continue;
                }

                nameProperty ??= command.GetType().GetProperty("Name");
                modProperty ??= command.GetType().GetProperty("Mod");
                if (nameProperty?.GetValue(command) is not string name || name.Length == 0)
                {
                    continue;
                }

                var mod = modProperty?.GetValue(command);
                var source =
                    mod?.GetType().GetProperty("DisplayName")?.GetValue(mod) as string
                    ?? SmapiSource;
                results.Add((name, source));
            }
        }
        catch (Exception ex)
        {
            monitor.Log($"Failed to enumerate SMAPI commands: {ex.Message}", LogLevel.Warn);
        }

        return results;
    }
}
