using System;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace JunimoServer.Util;

/// <summary>
/// The one home for marshalling console-command work onto the game loop. SMAPI console
/// commands run on a background thread; game state is game-thread-only, so the action runs
/// from a one-shot <c>UpdateTicked</c> handler. A throw from an UpdateTicked handler logs at
/// Error — test poison per debugging.md — so the action is always caught to Warn.
/// </summary>
public static class GameThreadOneShot
{
    /// <param name="what">Names the work in the no-save / failure Warn logs (e.g. "farmhand command").</param>
    /// <param name="requireLoadedSave">When true, no-ops with a Warn if no world is loaded.</param>
    public static void Run(
        IModHelper helper,
        IMonitor monitor,
        string what,
        Action action,
        bool requireLoadedSave = true
    )
    {
        void Apply(object sender, UpdateTickedEventArgs e)
        {
            helper.Events.GameLoop.UpdateTicked -= Apply;
            try
            {
                if (requireLoadedSave && !Game1.hasLoadedGame)
                {
                    monitor.Log($"No world loaded — {what} needs a loaded save.", LogLevel.Warn);
                    return;
                }

                action();
            }
            catch (Exception ex)
            {
                monitor.Log($"{what} failed: {ex.Message}", LogLevel.Warn);
            }
        }

        helper.Events.GameLoop.UpdateTicked += Apply;
    }
}
