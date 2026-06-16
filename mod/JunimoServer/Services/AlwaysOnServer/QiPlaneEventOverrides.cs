using HarmonyLib;
using StardewValley;
using StardewValley.Events;

namespace JunimoServer.Services.AlwaysOn;

/// <summary>
/// Completes the Mr. Qi mystery-box overnight cutscene (<see cref="QiPlaneEvent"/>) on the host the
/// way a player holding Escape would. QiPlaneEvent's completion gate advances only in <c>draw()</c>,
/// so on a headless host (draws gated) it never converges and the new day never starts (issue #242;
/// see <c>host-automation.md</c> #4). Vanilla's Escape branch advances the same gate from
/// <c>tickUpdate</c> instead (QiPlaneEvent.cs:80-88); the host has no keyboard, so this postfix
/// drives those private fields past their gates. The cutscene is never drawn here, so rather than
/// ramp the timers tick-by-tick we jump straight past both thresholds in one tick — the event then
/// completes through its own gate and runs the vanilla completion block (warp-to-bed + save)
/// unchanged, starting the new day a few seconds sooner. With healthy draws the event self-completes
/// first and the postfix is a no-op.
/// </summary>
public static class QiPlaneEventOverrides
{
    private static AccessTools.FieldRef<QiPlaneEvent, float> _finalFadeTimerRef;
    private static AccessTools.FieldRef<QiPlaneEvent, float> _textTimerRef;
    private static AccessTools.FieldRef<QiPlaneEvent, string> _strRef;

    /// <summary>Resolves the private fields up front; throws loudly if a future SDV renames them.</summary>
    public static void Initialize()
    {
        _finalFadeTimerRef = AccessTools.FieldRefAccess<QiPlaneEvent, float>("finalFadeTimer");
        _textTimerRef = AccessTools.FieldRefAccess<QiPlaneEvent, float>("textTimer");
        _strRef = AccessTools.FieldRefAccess<QiPlaneEvent, string>("str");
    }

    // ReSharper disable once InconsistentNaming
    public static void TickUpdate_Postfix(QiPlaneEvent __instance, ref bool __result)
    {
        if (!Game1.IsMasterGame || __result)
        {
            return; // only the host completes the event; __result already true means draws handled it
        }

        // The host doesn't draw the cutscene, so jump both private fields past the gates vanilla
        // checks (text-scrolled: textTimer/100 > str.Length + 27, then finalFadeTimer > 4000;
        // QiPlaneEvent.cs:57,120) in one tick. tickUpdate then returns true through the event's own
        // gate, running the vanilla completion block unchanged.
        _textTimerRef(__instance) = ((_strRef(__instance)?.Length ?? 0) + 28) * 100f;
        _finalFadeTimerRef(__instance) = 4001f;
        __result = true;
    }
}
