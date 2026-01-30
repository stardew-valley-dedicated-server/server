using System.Reflection.Emit;
using HarmonyLib;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

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

        // Disable pause when unfocused
        DisablePauseOnUnfocus();

        // Mute all audio
        MuteAudio();

        _monitor.Log("Test tweaks applied (no pause, muted)", LogLevel.Trace);
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
