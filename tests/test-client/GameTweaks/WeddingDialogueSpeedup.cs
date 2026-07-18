using HarmonyLib;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;

namespace JunimoTestClient.GameTweaks;

/// <summary>
/// Zeroes <see cref="DialogueBox.safetyTimer"/> while a wedding plays, so the auto-clicker
/// (<see cref="WeddingCutscenePlayer"/>) advances each box without waiting out the timer's per-box input
/// guard. Vanilla itself zeroes it for automated participants — <c>(!Game1.IsDedicatedHost) ? 750 : 0</c>
/// (DialogueBox.cs:462) — and the headless test client is exactly one; we mirror that, scoped to
/// weddings, rather than invent new timing. A prefix on <see cref="DialogueBox.update"/> re-zeroes it
/// every tick, covering every path that sets it. <c>Event.Speak</c>'s separate 500ms throttle is left
/// alone (event-machinery pacing with no vanilla automation exemption to mirror).
///
/// <para>
/// Accepted cost: boxes then live ~2 ticks, so at CLIENT_FPS=1 the dialogue text mostly won't land in a
/// recorded frame — the beats (WeddingCutscenePlayer's visible pauses), not the text, are the anchor.
/// </para>
/// </summary>
public class WeddingDialogueSpeedup
{
    private readonly IMonitor _monitor;
    private readonly Harmony _harmony;

    public WeddingDialogueSpeedup(IMonitor monitor, Harmony harmony)
    {
        _monitor = monitor;
        _harmony = harmony;
    }

    public void Apply()
    {
        try
        {
            _harmony.Patch(
                AccessTools.Method(typeof(DialogueBox), nameof(DialogueBox.update)),
                prefix: new HarmonyMethod(typeof(WeddingDialogueSpeedup), nameof(Update_Prefix))
            );
            _monitor.Log("WeddingDialogueSpeedup applied.", LogLevel.Info);
        }
        catch (System.Exception ex)
        {
            _monitor.Log(
                $"Failed to apply WeddingDialogueSpeedup patch: {ex.Message}",
                LogLevel.Error
            );
        }
    }

    private static void Update_Prefix(DialogueBox __instance)
    {
        if (Game1.CurrentEvent?.isWedding == true)
        {
            __instance.safetyTimer = 0;
        }
    }
}
