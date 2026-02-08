using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.TerrainFeatures;
using StardewValley.Tools;
using SObject = StardewValley.Object;

namespace JunimoTestClient.GameTweaks;

/// <summary>
/// God Tool - Destroys trees, stones, and other obstacles in one hit.
/// The tool glows with a distinct golden/rainbow aura to indicate its power.
///
/// NOTE: This exploits the fact that Stardew Valley has minimal server-side validation.
/// The server trusts client-reported tool actions. This can be used later to test
/// server-side hardening against cheating clients.
/// </summary>
public class GodTool
{
    private readonly IModHelper _helper;
    private readonly IMonitor _monitor;
    private readonly Harmony _harmony;

    private static bool _enabled = true;

    /// <summary>
    /// Whether the god tool effect is enabled.
    /// </summary>
    public static bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    public GodTool(IModHelper helper, IMonitor monitor)
    {
        _helper = helper;
        _monitor = monitor;
        _harmony = new Harmony("JunimoHost.TestClient.GodTool");
    }

    public void Apply()
    {
        try
        {
            // Patch Axe.DoFunction to boost axe power
            var axeDoFunction = AccessTools.Method(typeof(Axe), nameof(Axe.DoFunction));
            var axePrefix = new HarmonyMethod(typeof(GodTool), nameof(Axe_DoFunction_Prefix));
            var axePostfix = new HarmonyMethod(typeof(GodTool), nameof(Axe_DoFunction_Postfix));
            _harmony.Patch(axeDoFunction, prefix: axePrefix, postfix: axePostfix);

            // Patch Pickaxe.DoFunction to boost pickaxe power
            var pickaxeDoFunction = AccessTools.Method(typeof(Pickaxe), nameof(Pickaxe.DoFunction));
            var pickaxePrefix = new HarmonyMethod(typeof(GodTool), nameof(Pickaxe_DoFunction_Prefix));
            var pickaxePostfix = new HarmonyMethod(typeof(GodTool), nameof(Pickaxe_DoFunction_Postfix));
            _harmony.Patch(pickaxeDoFunction, prefix: pickaxePrefix, postfix: pickaxePostfix);

            // Patch Tree.performToolAction to instantly destroy trees
            var treePerformToolAction = AccessTools.Method(typeof(Tree), nameof(Tree.performToolAction));
            var treePrefix = new HarmonyMethod(typeof(GodTool), nameof(Tree_performToolAction_Prefix));
            _harmony.Patch(treePerformToolAction, prefix: treePrefix);

            // Patch ResourceClump.performToolAction to instantly destroy stumps/boulders/logs
            var resourceClumpPerformToolAction = AccessTools.Method(typeof(ResourceClump), nameof(ResourceClump.performToolAction));
            var resourceClumpPrefix = new HarmonyMethod(typeof(GodTool), nameof(ResourceClump_performToolAction_Prefix));
            _harmony.Patch(resourceClumpPerformToolAction, prefix: resourceClumpPrefix);

            // Patch Object.performToolAction to allow cross-tool destruction (axe breaks stones, pickaxe breaks twigs)
            var objectPerformToolAction = AccessTools.Method(typeof(SObject), nameof(SObject.performToolAction), new[] { typeof(Tool) });
            var objectPrefix = new HarmonyMethod(typeof(GodTool), nameof(Object_performToolAction_Prefix));
            _harmony.Patch(objectPerformToolAction, prefix: objectPrefix);

            // Add visual effect for god tools
            _helper.Events.Display.RenderedWorld += OnRenderedWorld;

            _monitor.Log("God Tool patches applied - one-hit destruction enabled!", LogLevel.Info);
        }
        catch (Exception ex)
        {
            _monitor.Log($"Failed to apply God Tool patches: {ex.Message}", LogLevel.Error);
        }
    }

    /// <summary>
    /// Prefix for Axe.DoFunction - temporarily boosts power to maximum.
    /// </summary>
    private static void Axe_DoFunction_Prefix(Axe __instance, ref int __state)
    {
        if (!_enabled) return;

        // Store original value
        __state = __instance.additionalPower.Value;
        // Set to very high value to one-shot everything
        __instance.additionalPower.Value = 100;
    }

    /// <summary>
    /// Postfix for Axe.DoFunction - restores original power.
    /// </summary>
    private static void Axe_DoFunction_Postfix(Axe __instance, int __state)
    {
        if (!_enabled) return;
        __instance.additionalPower.Value = __state;
    }

    /// <summary>
    /// Prefix for Pickaxe.DoFunction - temporarily boosts power to maximum.
    /// </summary>
    private static void Pickaxe_DoFunction_Prefix(Pickaxe __instance, ref int __state)
    {
        if (!_enabled) return;

        // Store original value
        __state = __instance.additionalPower.Value;
        // Set to very high value to one-shot everything
        __instance.additionalPower.Value = 100;
    }

    /// <summary>
    /// Postfix for Pickaxe.DoFunction - restores original power.
    /// </summary>
    private static void Pickaxe_DoFunction_Postfix(Pickaxe __instance, int __state)
    {
        if (!_enabled) return;
        __instance.additionalPower.Value = __state;
    }

    /// <summary>
    /// Prefix for Tree.performToolAction - sets health to minimum before tool action.
    /// Works with both Axe and Pickaxe for universal destruction.
    /// </summary>
    private static void Tree_performToolAction_Prefix(Tree __instance, Tool t)
    {
        if (!_enabled) return;
        if (t is not Axe && t is not Pickaxe) return;

        // Set health to 1 so the next hit destroys it
        __instance.health.Value = 1f;
    }

    /// <summary>
    /// Prefix for ResourceClump.performToolAction - sets health to minimum.
    /// ResourceClumps include large stumps, hollow logs, boulders, and meteorites.
    /// </summary>
    private static void ResourceClump_performToolAction_Prefix(ResourceClump __instance, Tool t, int damage)
    {
        if (!_enabled) return;

        // ResourceClumps (stumps, logs, boulders) have varying health
        // Set to minimum so one hit destroys them
        __instance.health.Value = 1f;
    }

    /// <summary>
    /// Prefix for Object.performToolAction - handles cross-tool destruction.
    /// Allows axes to break stones and pickaxes to break twigs.
    /// </summary>
    private static bool Object_performToolAction_Prefix(SObject __instance, Tool t, ref bool __result)
    {
        if (!_enabled) return true; // Run original
        if (t is not Axe && t is not Pickaxe) return true; // Run original for other tools

        var location = __instance.Location;
        if (location == null) return true;

        // Handle stones with axe (normally only pickaxe works)
        if (__instance.IsBreakableStone() && t is Axe)
        {
            // Instantly destroy the stone
            __instance.MinutesUntilReady = 0;
            location.playSound("stoneCrack", __instance.TileLocation);
            Game1.createRadialDebris(location, 14, (int)__instance.TileLocation.X, (int)__instance.TileLocation.Y, Game1.random.Next(2, 5), resource: false);

            // Trigger stone destruction logic
            location.OnStoneDestroyed(__instance.ItemId, (int)__instance.TileLocation.X, (int)__instance.TileLocation.Y, t.getLastFarmerToUse());
            __instance.performRemoveAction();
            location.Objects.Remove(__instance.TileLocation);
            Game1.stats.RocksCrushed++;

            __result = true;
            return false; // Skip original
        }

        // Handle twigs with pickaxe (normally only axe works)
        if (__instance.IsTwig() && t is Pickaxe)
        {
            __instance.Fragility = 2;
            location.playSound("axchop", __instance.TileLocation);
            location.debris.Add(new Debris(ItemRegistry.Create("(O)388"), __instance.TileLocation * 64f));
            Game1.createRadialDebris(location, 12, (int)__instance.TileLocation.X, (int)__instance.TileLocation.Y, Game1.random.Next(4, 10), resource: false);
            t.getLastFarmerToUse()?.gainExperience(2, 1);

            __instance.performRemoveAction();
            location.Objects.Remove(__instance.TileLocation);

            __result = true;
            return false; // Skip original
        }

        return true; // Run original for everything else
    }

    /// <summary>
    /// Renders a elegant glowing aura around the player when holding a god-powered tool.
    /// Uses the game's built-in light glow texture for a smooth, professional look.
    /// </summary>
    private void OnRenderedWorld(object? sender, RenderedWorldEventArgs e)
    {
        if (!_enabled) return;
        if (Game1.player?.CurrentTool == null) return;

        var tool = Game1.player.CurrentTool;
        if (tool is not Axe && tool is not Pickaxe) return;

        var time = Game1.currentGameTime.TotalGameTime.TotalMilliseconds;

        // Smooth pulsing
        var pulse = (float)(Math.Sin(time / 400.0) * 0.15 + 0.85);

        // Slow, elegant golden hue shift (gold -> amber -> gold)
        var hue = (float)(Math.Sin(time / 2000.0) * 15 + 45); // Oscillates 30-60 (gold/amber range)

        // Get player feet position on screen
        var playerPos = Game1.player.getLocalPosition(Game1.viewport);
        var centerX = playerPos.X + 32;
        var centerY = playerPos.Y + 60; // At feet level

        // Use the game's light glow texture for smooth circular glow
        var glowTexture = Game1.mouseCursors;
        var glowSourceRect = new Rectangle(88, 1779, 30, 30); // Soft circular glow from cursors

        // Main golden aura - large soft glow at feet
        var mainColor = ColorFromHsv(hue, 0.7f, 1f) * 0.35f * pulse;
        var mainSize = 140;
        e.SpriteBatch.Draw(
            glowTexture,
            new Rectangle((int)(centerX - mainSize / 2), (int)(centerY - mainSize / 2), mainSize, mainSize),
            glowSourceRect,
            mainColor);

        // Secondary inner glow - brighter core
        var coreColor = ColorFromHsv(hue - 10, 0.5f, 1f) * 0.5f * pulse;
        var coreSize = 80;
        e.SpriteBatch.Draw(
            glowTexture,
            new Rectangle((int)(centerX - coreSize / 2), (int)(centerY - coreSize / 2), coreSize, coreSize),
            glowSourceRect,
            coreColor);

        // Orbiting star particles using the game's star/sparkle sprite
        var starSourceRect = new Rectangle(294, 1432, 16, 16); // Small star from cursors
        var particleCount = 4;

        for (int i = 0; i < particleCount; i++)
        {
            // Each particle orbits at different speed and phase
            var orbitSpeed = 1500.0 + i * 200;
            var angle = (time / orbitSpeed + i * (Math.PI * 2 / particleCount)) % (Math.PI * 2);

            // Elliptical orbit (wider than tall)
            var radiusX = 45 + (float)Math.Sin(time / 800.0 + i) * 5;
            var radiusY = 25 + (float)Math.Sin(time / 800.0 + i) * 3;

            var particleX = centerX + (float)Math.Cos(angle) * radiusX;
            var particleY = centerY + (float)Math.Sin(angle) * radiusY * 0.6f; // Flatten for perspective

            // Particle fades based on position (dimmer when "behind")
            var depthFade = (float)(Math.Sin(angle) * 0.3 + 0.7);

            // Slight color variation per particle
            var particleHue = (hue + i * 8) % 360;
            var particleColor = ColorFromHsv(particleHue, 0.4f, 1f) * pulse * depthFade;

            // Scale particles - smaller when "behind"
            var particleScale = 1.5f + depthFade * 0.5f;
            var particleSize = (int)(12 * particleScale);

            e.SpriteBatch.Draw(
                glowTexture,
                new Rectangle((int)(particleX - particleSize / 2), (int)(particleY - particleSize / 2), particleSize, particleSize),
                starSourceRect,
                particleColor);
        }

        // Occasional rising sparkle effect
        var sparklePhase = (time / 100.0) % 60;
        if (sparklePhase < 40)
        {
            var sparkleY = centerY - (float)(sparklePhase * 1.5);
            var sparkleAlpha = sparklePhase < 20 ? (float)(sparklePhase / 20.0) : (float)((40 - sparklePhase) / 20.0);
            var sparkleX = centerX + (float)Math.Sin(sparklePhase * 0.3) * 15;

            var sparkleColor = Color.White * sparkleAlpha * 0.8f * pulse;
            e.SpriteBatch.Draw(
                glowTexture,
                new Rectangle((int)(sparkleX - 6), (int)(sparkleY - 6), 12, 12),
                starSourceRect,
                sparkleColor);
        }
    }

    /// <summary>
    /// Converts HSV color to XNA Color.
    /// </summary>
    private static Color ColorFromHsv(float hue, float saturation, float value)
    {
        var hi = (int)(hue / 60) % 6;
        var f = hue / 60 - (int)(hue / 60);
        var p = value * (1 - saturation);
        var q = value * (1 - f * saturation);
        var t = value * (1 - (1 - f) * saturation);

        return hi switch
        {
            0 => new Color(value, t, p),
            1 => new Color(q, value, p),
            2 => new Color(p, value, t),
            3 => new Color(p, q, value),
            4 => new Color(t, p, value),
            _ => new Color(value, p, q)
        };
    }

    public void Dispose()
    {
        _helper.Events.Display.RenderedWorld -= OnRenderedWorld;
        _harmony.UnpatchAll("JunimoHost.TestClient.GodTool");
    }
}
