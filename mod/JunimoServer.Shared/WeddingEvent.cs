using System;
using StardewValley;

namespace JunimoServer.Shared;

/// <summary>
/// Helpers for inspecting a live wedding <see cref="Event"/>, shared by the server mod (host
/// participation + the test-only wedding_state endpoint) and the test-client mod.
/// </summary>
public static class WeddingEvent
{
    /// <summary>
    /// Parse the <c>waitForOtherPlayers &lt;gateId&gt;</c> gate from a wedding event's command list,
    /// or null if none. Each <see cref="Event.eventCommands"/> element is one space-delimited command
    /// line; the gate ID is the token after the command name. The vanilla wedding script (Data/Weddings)
    /// has exactly one such gate, dynamically named <c>weddingEnd&lt;farmerId&gt;</c> — reading it from
    /// the event itself is the only reliable source, since there is no static name to hard-code.
    /// </summary>
    public static string? ExtractWaitGate(Event ev)
    {
        var commands = ev?.eventCommands;
        if (commands == null)
        {
            return null;
        }

        foreach (var line in commands)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var tokens = ArgUtility.SplitBySpace(line);
            if (
                tokens.Length >= 2
                && string.Equals(
                    tokens[0],
                    "waitForOtherPlayers",
                    StringComparison.OrdinalIgnoreCase
                )
                && !string.IsNullOrWhiteSpace(tokens[1])
            )
            {
                return tokens[1];
            }
        }

        return null;
    }

    /// <summary>
    /// True once the event has advanced to (or past) its <c>waitForOtherPlayers</c> command — i.e. the
    /// cutscene has played through and this instance has reached the wait gate. This is the reliable
    /// "the ceremony finished playing here" signal: unlike <c>Game1.CurrentEvent == null</c>, it does NOT
    /// transiently flip during the Town↔Farm exit/entry warps of a back-to-back same-day wedding (where
    /// <c>CurrentEvent</c> momentarily reads null mid-warp while the event is in fact still running and
    /// about to resume). Returns false if there is no wait gate or the command index can't be resolved.
    /// </summary>
    public static bool HasReachedWaitGate(Event ev)
    {
        if (ev?.eventCommands is not { } commands)
        {
            return false;
        }

        for (int i = 0; i < commands.Length; i++)
        {
            var line = commands[i];
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var tokens = ArgUtility.SplitBySpace(line);
            if (
                tokens.Length >= 1
                && string.Equals(
                    tokens[0],
                    "waitForOtherPlayers",
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                return ev.CurrentCommand >= i;
            }
        }

        return false;
    }
}
