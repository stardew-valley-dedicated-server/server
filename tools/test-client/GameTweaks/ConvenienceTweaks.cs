using System.Reflection.Emit;
using HarmonyLib;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.BellsAndWhistles;

namespace JunimoTestClient.GameTweaks;

/// <summary>
/// Applies convenience tweaks for testing:
/// - Removes minimum window size restriction (1280x720 -> 400x300)
/// - Prevents game from pausing when unfocused
/// - Mutes all audio (music, sound, ambient, footsteps)
/// </summary>
public class ConvenienceTweaks
{
    private readonly IModHelper _helper;
    private readonly IMonitor _monitor;
    private readonly Harmony _harmony;

    // Absolute minimum we allow (instead of 1280x720)
    private const int AbsoluteMinWidth = 400;
    private const int AbsoluteMinHeight = 300;

    public ConvenienceTweaks(IModHelper helper, IMonitor monitor)
    {
        _helper = helper;
        _monitor = monitor;
        _harmony = new Harmony("JunimoHost.TestClient.Tweaks");
    }

    public void Apply()
    {
        // Patch SetWindowSize to remove minimum size restriction
        PatchWindowSize();

        // Skip screen fade animations (warps, sleep, day transitions)
        PatchInstantFades();

        // Disable pause when unfocused
        DisablePauseOnUnfocus();

        // Mute all audio
        MuteAudio();

        _monitor.Log("Test tweaks applied (no pause, muted, instant fades)", LogLevel.Trace);
    }

    private void PatchWindowSize()
    {
        try
        {
            var originalMethod = AccessTools.Method(typeof(Game1), "SetWindowSize");
            var transpiler = new HarmonyMethod(typeof(ConvenienceTweaks), nameof(SetWindowSize_Transpiler));

            _harmony.Patch(originalMethod, transpiler: transpiler);
            _monitor.Log("Patched SetWindowSize to remove minimum size restriction", LogLevel.Trace);
        }
        catch (Exception ex)
        {
            _monitor.Log($"Failed to patch SetWindowSize: {ex.Message}", LogLevel.Error);
        }
    }

    /// <summary>
    /// Transpiler that removes the minimum size checks from SetWindowSize.
    /// The original code checks for 1280x720 minimum on Windows.
    /// We replace those constants with our smaller minimums.
    /// </summary>
    private static IEnumerable<CodeInstruction> SetWindowSize_Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var codes = new List<CodeInstruction>(instructions);

        for (int i = 0; i < codes.Count; i++)
        {
            // Replace ldc.i4 1280 with our minimum width
            if (codes[i].opcode == OpCodes.Ldc_I4 && codes[i].operand is int val1 && val1 == 1280)
            {
                codes[i].operand = AbsoluteMinWidth;
            }
            // Replace ldc.i4 720 with our minimum height
            else if (codes[i].opcode == OpCodes.Ldc_I4 && codes[i].operand is int val2 && val2 == 720)
            {
                codes[i].operand = AbsoluteMinHeight;
            }
        }

        return codes;
    }

    private void PatchInstantFades()
    {
        try
        {
            // Patch UpdateFadeAlpha — screen-level fades (warps, transitions)
            // Forces alpha to overshoot value so the next frame's threshold check
            // in UpdateFade fires the completion callbacks immediately.
            var updateFadeAlpha = AccessTools.Method(typeof(ScreenFade), "UpdateFadeAlpha");
            var fadeAlphaPostfix = new HarmonyMethod(typeof(ConvenienceTweaks), nameof(UpdateFadeAlpha_Postfix));
            _harmony.Patch(updateFadeAlpha, postfix: fadeAlphaPostfix);

            // Patch UpdateGlobalFade — global fades (menus, sleep, day end)
            // Forces alpha to the threshold value so the next frame fires the
            // afterFade callback immediately.
            var updateGlobalFade = AccessTools.Method(typeof(ScreenFade), "UpdateGlobalFade");
            var globalFadePostfix = new HarmonyMethod(typeof(ConvenienceTweaks), nameof(UpdateGlobalFade_Postfix));
            _harmony.Patch(updateGlobalFade, postfix: globalFadePostfix);

            _monitor.Log("Patched ScreenFade for instant fades (skip animations)", LogLevel.Trace);
        }
        catch (Exception ex)
        {
            _monitor.Log($"Failed to patch ScreenFade: {ex.Message}", LogLevel.Error);
        }
    }

    /// <summary>
    /// Postfix for ScreenFade.UpdateFadeAlpha — forces instant screen fades.
    /// When fading in (to black): sets alpha to 1.2f so UpdateFade's >1.1f check
    /// fires onFadeToBlackComplete on the next frame.
    /// When fading out (to clear): sets alpha to -0.2f so UpdateFade's less-than -0.1f check
    /// fires onFadedBackInComplete on the next frame.
    /// </summary>
    private static void UpdateFadeAlpha_Postfix(ScreenFade __instance)
    {
        if (__instance.fadeIn)
            __instance.fadeToBlackAlpha = 1.2f;
        else
            __instance.fadeToBlackAlpha = -0.2f;
    }

    /// <summary>
    /// Postfix for ScreenFade.UpdateGlobalFade — forces instant global fades.
    /// When fadeIn (clearing to transparent): sets alpha to 0f so the next frame's
    /// less-than-or-equal 0f check fires the afterFade callback.
    /// When !fadeIn (darkening to black): sets alpha to 1f so the next frame's
    /// greater-than-or-equal 1f check fires the afterFade callback.
    /// </summary>
    private static void UpdateGlobalFade_Postfix(ScreenFade __instance)
    {
        if (__instance.fadeIn)
            __instance.fadeToBlackAlpha = 0f;
        else
            __instance.fadeToBlackAlpha = 1f;
    }

    private void DisablePauseOnUnfocus()
    {
        _helper.Events.GameLoop.GameLaunched += (_, _) =>
        {
            SetPauseOnUnfocus(false);
        };

        _helper.Events.GameLoop.SaveLoaded += (_, _) =>
        {
            SetPauseOnUnfocus(false);
        };

        _helper.Events.GameLoop.UpdateTicked += (_, _) =>
        {
            // Continuously ensure it stays disabled
            if (Game1.options?.pauseWhenOutOfFocus == true)
            {
                Game1.options.pauseWhenOutOfFocus = false;
            }

            // Also ensure game isn't paused
            if (Game1.paused && !Game1.HostPaused)
            {
                Game1.paused = false;
            }
        };
    }

    private void SetPauseOnUnfocus(bool value)
    {
        if (Game1.options != null)
        {
            Game1.options.pauseWhenOutOfFocus = value;
            _monitor.Log($"Set pauseWhenOutOfFocus = {value}", LogLevel.Trace);
        }
    }

    private void MuteAudio()
    {
        // Replace the sound bank with a dummy that plays nothing
        _helper.Events.GameLoop.GameLaunched += (_, _) =>
        {
            DisableAllAudio();
        };
    }

    private void DisableAllAudio()
    {
        // Replace the sound bank with a dummy implementation that does nothing
        // This completely silences all game audio including menu/HUD sounds
        if (Game1.soundBank is not DummySoundBank)
        {
            Game1.soundBank?.Dispose();
            Game1.soundBank = new DummySoundBank();
            _monitor.Log("Replaced soundBank with DummySoundBank - all audio disabled", LogLevel.Trace);
        }
    }

    public void Dispose()
    {
        _harmony.UnpatchAll("JunimoHost.TestClient.Tweaks");
    }
}
