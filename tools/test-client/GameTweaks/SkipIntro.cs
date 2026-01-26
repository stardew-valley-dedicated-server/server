using HarmonyLib;
using StardewModdingAPI;
using StardewValley.Menus;

namespace JunimoTestClient.GameTweaks;

/// <summary>
/// Automatically skips the intro/splash screen on game start.
/// </summary>
public class SkipIntro
{
    private readonly IMonitor _monitor;
    private readonly Harmony _harmony;

    public SkipIntro(IMonitor monitor)
    {
        _monitor = monitor;
        _harmony = new Harmony("JunimoHost.TestClient.SkipIntro");
    }

    public void Apply()
    {
        try
        {
            // Patch TitleMenu constructor to skip intro
            var constructor = AccessTools.Constructor(typeof(TitleMenu), new Type[] { });
            var postfix = new HarmonyMethod(typeof(SkipIntro), nameof(TitleMenu_Postfix));
            _harmony.Patch(constructor, postfix: postfix);

            _monitor.Log("Skip intro patch applied", LogLevel.Debug);
        }
        catch (Exception ex)
        {
            _monitor.Log($"Failed to apply skip intro patch: {ex.Message}", LogLevel.Error);
        }
    }

    private static void TitleMenu_Postfix(TitleMenu __instance)
    {
        // Skip directly to title buttons, bypassing logo animation
        __instance.skipToTitleButtons();
    }

    public void Dispose()
    {
        _harmony.UnpatchAll("JunimoHost.TestClient.SkipIntro");
    }
}
