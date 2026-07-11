using StardewModdingAPI;
using StardewValley;

namespace JunimoServer.Services.AlwaysOn;

/// <summary>
/// Guards <see cref="Game1.getAvailableWeddingEvent"/> against a vanilla null-dereference that crashes
/// the host's update loop. The method pulls the next id from <c>Game1.weddingsToday</c> and, when the
/// farmer has no NPC <c>spouse</c>, assumes a player↔player marriage and dereferences
/// <c>farmer.team.GetSpouse(...).Value</c> with no null check (Game1.cs:6111-6112). If such a farmer
/// is queued but has neither an NPC spouse nor a player spouse at fire time, that <c>.Value</c> throws
/// <c>InvalidOperationException: Nullable object must have a value</c>, which on a headless host
/// aborts the day-start update and can wedge the server.
///
/// <para>
/// This prefix drops any queued wedding whose farmer can't produce a valid ceremony — no resolvable
/// farmer, no NPC spouse AND no player spouse — before vanilla reads it, so a single bad entry can't
/// crash the day transition. A healthy NPC or player wedding is left untouched and fires normally. It
/// also logs each drop so a stuck/queued-but-unmarried farmhand is visible rather than silent.
/// </para>
/// </summary>
public static class WeddingEventGuard
{
    private static IMonitor _monitor;

    public static void Initialize(IMonitor monitor)
    {
        _monitor = monitor;
    }

    // ReSharper disable once InconsistentNaming
    public static void GetAvailableWeddingEvent_Prefix()
    {
        if (!Game1.IsMasterGame || Game1.weddingsToday == null || Game1.weddingsToday.Count == 0)
        {
            return;
        }

        // Walk the queue and drop entries vanilla would crash on. A valid wedding needs either an NPC
        // spouse (farmer.spouse) or a resolvable player spouse (team.GetSpouse). Anything else makes
        // getAvailableWeddingEvent's unguarded team.GetSpouse(...).Value throw.
        Game1.weddingsToday.RemoveAll(id =>
        {
            var farmer = Game1.GetPlayer(id);
            if (farmer == null)
            {
                _monitor?.Log(
                    $"[Wedding] Dropping queued wedding for id {id}: no farmer resolves for it.",
                    LogLevel.Warn
                );
                return true;
            }

            if (!string.IsNullOrEmpty(farmer.spouse))
            {
                return false; // NPC marriage — valid, vanilla returns getWeddingEvent(farmer)
            }

            var playerSpouse = farmer.team.GetSpouse(farmer.UniqueMultiplayerID);
            if (!playerSpouse.HasValue)
            {
                _monitor?.Log(
                    $"[Wedding] Dropping queued wedding for {farmer.Name} (id {id}): no NPC spouse and "
                        + "no player spouse — vanilla getAvailableWeddingEvent would null-deref here. "
                        + $"isEngaged={farmer.isEngaged()}, online={Game1.getOnlineFarmers().Contains(farmer)}.",
                    LogLevel.Warn
                );
                return true;
            }

            return false; // player↔player marriage — valid
        });
    }
}
