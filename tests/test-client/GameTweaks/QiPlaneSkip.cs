using HarmonyLib;
using Microsoft.Xna.Framework.Input;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Events;

namespace JunimoTestClient.GameTweaks;

/// <summary>
/// Skips the Mr. Qi mystery-box overnight cutscene (<see cref="QiPlaneEvent"/>) the honest way a real
/// player does — by holding Escape, which vanilla <c>QiPlaneEvent.tickUpdate</c> reads to advance the
/// event's own timers (QiPlaneEvent.cs:80-88), draw-independent.
///
/// The test client suppresses its own draws (CLIENT_FPS), so its draw-driven completion would
/// otherwise crawl through the full cutscene — leaving the host blocked on the client at the
/// <c>ready_for_save</c> barrier ("waiting for players"). Reporting Escape held while the event is up
/// completes it promptly through the exact vanilla player path, with no force-skip.
/// </summary>
public class QiPlaneSkip
{
    private readonly IMonitor _monitor;
    private readonly Harmony _harmony;

    private static readonly KeyboardState EscapeHeld = new(Keys.Escape);

    public QiPlaneSkip(IMonitor monitor, Harmony harmony)
    {
        _monitor = monitor;
        _harmony = harmony;
    }

    public void Apply()
    {
        try
        {
            _harmony.Patch(
                AccessTools.Method(typeof(InputState), nameof(InputState.GetKeyboardState)),
                postfix: new HarmonyMethod(typeof(QiPlaneSkip), nameof(GetKeyboardState_Postfix))
            );
            _monitor.Log("QiPlaneSkip applied.", LogLevel.Info);
        }
        catch (System.Exception ex)
        {
            _monitor.Log($"Failed to apply QiPlaneSkip patch: {ex.Message}", LogLevel.Warn);
        }
    }

    // Headless: no human at the keyboard, so reporting only Escape while the event is up is safe.
    // ReSharper disable once InconsistentNaming
    private static void GetKeyboardState_Postfix(ref KeyboardState __result)
    {
        if (Game1.farmEvent is QiPlaneEvent)
        {
            __result = EscapeHeld;
        }
    }
}
