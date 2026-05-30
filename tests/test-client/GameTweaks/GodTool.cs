using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.TerrainFeatures;
using StardewValley.Tools;
using SObject = StardewValley.Object;

namespace JunimoTestClient.GameTweaks;

/// <summary>
/// God Tool - Destroys trees, stones, and other obstacles in one hit.
/// The scythe clears everything in a 10x10 area around the player.
///
/// NOTE: This exploits the fact that Stardew Valley has minimal server-side validation.
/// The server trusts client-reported tool actions. This can be used later to test
/// server-side hardening against cheating clients.
/// </summary>
public class GodTool
{
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

    public GodTool(IModHelper helper, IMonitor monitor, Harmony harmony)
    {
        _monitor = monitor;
        _harmony = harmony;
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

            // Patch MeleeWeapon.DoDamage to make scythe clear 10x10 area
            var meleeWeaponDoDamage = AccessTools.Method(typeof(MeleeWeapon), nameof(MeleeWeapon.DoDamage));
            var scythePrefix = new HarmonyMethod(typeof(GodTool), nameof(MeleeWeapon_DoDamage_Prefix));
            _harmony.Patch(meleeWeaponDoDamage, prefix: scythePrefix);

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

    // Cached reflection method for Tree.performTreeFall
    private static System.Reflection.MethodInfo? _performTreeFallMethod;

    /// <summary>
    /// Prefix for Tree.performToolAction - destroys tree AND stump in one hit.
    /// Works with both Axe and Pickaxe for universal destruction.
    /// </summary>
    private static bool Tree_performToolAction_Prefix(Tree __instance, Tool t, int explosion, Vector2 tileLocation, ref bool __result)
    {
        if (!_enabled) return true; // Run original
        if (t is not Axe && t is not Pickaxe) return true; // Run original for other tools

        var location = __instance.Location;
        if (location == null) return true;

        var farmer = t.getLastFarmerToUse();

        // Cache reflection method
        _performTreeFallMethod ??= AccessTools.Method(typeof(Tree), "performTreeFall");

        // If it's a stump, just remove it
        if (__instance.stump.Value)
        {
            location.playSound("axchop", tileLocation);
            _performTreeFallMethod?.Invoke(__instance, new object[] { t, explosion, tileLocation });
            __result = true;
            return false;
        }

        // For full trees: fell the tree, then immediately remove the stump
        __instance.health.Value = 1f;

        // Make the tree fall
        location.playSound("treecrack", tileLocation);
        __instance.stump.Value = true;
        __instance.health.Value = 0f;
        __instance.shakeLeft.Value = farmer != null && farmer.Tile.X > tileLocation.X;
        _performTreeFallMethod?.Invoke(__instance, new object[] { t, explosion, tileLocation });

        __result = true;
        return false; // Skip original
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
    /// Prefix for MeleeWeapon.DoDamage - makes scythe destroy everything in a 10x10 area.
    /// Clears trees, stumps, stones, twigs, and other debris.
    /// </summary>
    private static void MeleeWeapon_DoDamage_Prefix(MeleeWeapon __instance, GameLocation location, int x, int y, int facingDirection, int power, Farmer who)
    {
        if (!_enabled) return;
        if (!__instance.isScythe()) return;

        var playerTile = who.Tile;
        var radius = 5; // 10x10 area = 5 tiles in each direction

        // Collect items to remove (can't modify during iteration)
        var objectsToRemove = new List<Vector2>();
        var terrainToRemove = new List<Vector2>();
        var resourceClumpsToRemove = new List<ResourceClump>();

        // Find all objects in range
        foreach (var kvp in location.Objects.Pairs)
        {
            var tile = kvp.Key;
            if (Math.Abs(tile.X - playerTile.X) <= radius && Math.Abs(tile.Y - playerTile.Y) <= radius)
            {
                var obj = kvp.Value;
                if (obj.IsBreakableStone() || obj.IsTwig() || obj.Name.Contains("Weed") || obj.Name.Contains("Stone"))
                {
                    objectsToRemove.Add(tile);
                }
            }
        }

        // Find all terrain features (trees, grass, etc.)
        foreach (var kvp in location.terrainFeatures.Pairs)
        {
            var tile = kvp.Key;
            if (Math.Abs(tile.X - playerTile.X) <= radius && Math.Abs(tile.Y - playerTile.Y) <= radius)
            {
                var feature = kvp.Value;
                if (feature is Tree || feature is Grass || feature is Bush)
                {
                    terrainToRemove.Add(tile);
                }
            }
        }

        // Find resource clumps (stumps, logs, boulders)
        foreach (var clump in location.resourceClumps)
        {
            var clumpTile = clump.Tile;
            // Resource clumps can be 2x2, check if any part is in range
            for (int dx = 0; dx < clump.width.Value; dx++)
            {
                for (int dy = 0; dy < clump.height.Value; dy++)
                {
                    var checkTile = new Vector2(clumpTile.X + dx, clumpTile.Y + dy);
                    if (Math.Abs(checkTile.X - playerTile.X) <= radius && Math.Abs(checkTile.Y - playerTile.Y) <= radius)
                    {
                        resourceClumpsToRemove.Add(clump);
                        goto nextClump;
                    }
                }
            }
            nextClump:;
        }

        // Remove objects
        foreach (var tile in objectsToRemove)
        {
            if (location.Objects.TryGetValue(tile, out var obj))
            {
                obj.performRemoveAction();
                location.Objects.Remove(tile);
            }
        }

        // Remove terrain features
        foreach (var tile in terrainToRemove)
        {
            location.terrainFeatures.Remove(tile);
        }

        // Remove resource clumps
        foreach (var clump in resourceClumpsToRemove.Distinct())
        {
            location.resourceClumps.Remove(clump);
        }
    }
}
