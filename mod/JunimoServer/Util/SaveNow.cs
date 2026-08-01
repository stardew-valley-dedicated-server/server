using StardewModdingAPI;
using StardewValley;

namespace JunimoServer.Util;

/// <summary>
/// Synchronous, day-preserving save. Mirrors the day-transition save's two steps:
/// <c>saveFarmhands()</c> clones every connected farmhand's live root into farmhandData
/// (Multiplayer.cs:1018-1028 — the same call the transition makes, Game1.cs:8238), then
/// <c>SaveGame.getSaveEnumerator()</c> does the file write. <c>SaveGame.Save()</c> normally
/// offloads the enumerator to a background Task and yields across ticks (SaveGame.cs:296-310),
/// but the enumerator itself is fully synchronous (SaveGame.cs:346-546 — the yields are just
/// progress markers), so driving it inline writes the save within one game-thread action,
/// sidestepping the background-task split and SMAPI's UpdateTicked save-suppression. No
/// day/time advance: the enumerator serializes current values as-is.
/// </summary>
public static class SaveNow
{
    /// <summary>
    /// Must run on the game thread. Callers own the when-is-this-safe policy: with connected
    /// farmhands, <c>saveFarmhands</c> runs <c>ResetFarmhandState</c> outside the barriered
    /// sleep sync (NetWorldState.cs:754-773) — acceptable in the controlled test flow, unsafe
    /// as an operator action mid-play. Operator paths should gate on
    /// <see cref="OnlineFarmers.CountOthers"/> == 0 (the empty paused server satisfies every
    /// precondition).
    /// </summary>
    public static bool TrySave(IModHelper helper, out string error)
    {
        if (Game1.gameMode != 3 || !Game1.IsMasterGame)
        {
            error =
                $"Not in a loaded master game (gameMode={Game1.gameMode}, IsMasterGame={Game1.IsMasterGame})";
            return false;
        }

        // Game1.multiplayer is protected; reach it via the established reflective accessor.
        helper.GetMultiplayer().saveFarmhands();

        var save = SaveGame.getSaveEnumerator();
        while (save.MoveNext()) { }

        error = null;
        return true;
    }
}
