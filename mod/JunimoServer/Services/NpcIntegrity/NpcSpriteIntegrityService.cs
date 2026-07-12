using System;
using System.Collections.Generic;
using JunimoServer.Services.Diagnostics;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace JunimoServer.Services.NpcIntegrity;

/// <summary>
/// Restores the engine invariant that every NPC in a location has a non-null
/// <c>Sprite</c>.
///
/// <para>
/// NPCs deserialize from the save via the parameterless ctor (Sprite null) and rely on
/// <c>reloadSprite → ChooseAppearance → TryLoadSprites</c> at load to rebuild it. When the
/// spritesheet asset exists per <c>DoesAssetExist</c> but throws on load (broken content
/// pack, missing native image decoder, corrupt file), <c>TryLoadSprites</c> swallows the
/// exception and the NPC survives load with <c>Sprite == null</c> — vanilla removes an NPC
/// at load only when its <em>data</em> is missing (<c>Game1.fixProblems</c>). The engine then
/// dereferences <c>Sprite</c> unguarded on the NPC's next scheduled departure
/// (<c>checkSchedule → prepareToDisembarkOnNewSchedulePath → routeEndAnimationFinished</c>),
/// inside <c>Game1.performTenMinuteClockUpdate</c>; the queued schedule entry is dequeued
/// only after that call returns, so the NRE repeats at every ten-minute boundary and each
/// throw aborts the clock update before <c>netWorldState.UpdateFromGame1()</c> — freezing
/// every client's clock. On this render-suppressed server nothing surfaces the shell earlier
/// (a rendering game crashes visibly in <c>NPC.draw</c>).
/// </para>
///
/// <para>
/// The heal gives such NPCs an empty <see cref="AnimatedSprite"/> — the exact state vanilla
/// itself produces when a spritesheet is cleanly missing: clients render the error-box
/// placeholder (<c>NPC.draw</c>) and every sprite path is null-texture-safe. The texture name
/// stays unset on purpose (a broken name would throw in clients' lazy <c>Texture</c> getter);
/// the engine's own daily retry (<c>dayUpdate → ChooseAppearance</c>) restores the real
/// texture once the asset loads again.
/// </para>
/// </summary>
public class NpcSpriteIntegrityService : ModService
{
    /// <summary>Sweeps triggered by SaveLoaded since mod start. Wiring proof for tests.</summary>
    public int SaveLoadedRuns { get; private set; }

    /// <summary>Sweeps triggered by DayStarted since mod start. Wiring proof for tests.</summary>
    public int DayStartedRuns { get; private set; }

    /// <summary>Context tag of the most recent sweep, or null if none ran yet.</summary>
    public string? LastRunContext { get; private set; }

    /// <summary>NPCs healed by the most recent sweep.</summary>
    public int LastRunHealedCount { get; private set; }

    /// <summary>NPCs healed across all sweeps since mod start.</summary>
    public int TotalHealed { get; private set; }

    public NpcSpriteIntegrityService(IModHelper helper, IMonitor monitor)
        : base(helper, monitor) { }

    public override void Entry()
    {
        // SaveLoaded is the seam where shells are created (the load-time sprite rebuild is
        // the only Sprite writer that can fail); DayStarted is a tripwire against unknown
        // seams, mirroring CabinManagerService's lobby-heal sweeps.
        Helper.Events.GameLoop.SaveLoaded += OnSaveLoaded;
        Helper.Events.GameLoop.DayStarted += OnDayStarted;
    }

    private void OnSaveLoaded(object sender, SaveLoadedEventArgs e)
    {
        SaveLoadedRuns++;
        HealSpritelessNpcs("save_loaded");
    }

    private void OnDayStarted(object sender, DayStartedEventArgs e)
    {
        DayStartedRuns++;
        HealSpritelessNpcs("day_started");
    }

    /// <summary>
    /// Names of all NPCs currently missing a sprite, across every location (interiors
    /// included). Empty on a healthy world. Game thread only.
    /// </summary>
    public List<string> FindSpritelessNpcs()
    {
        var broken = new List<string>();
        Utility.ForEachLocation(
            location =>
            {
                foreach (var npc in location.characters)
                {
                    if (npc.Sprite == null)
                    {
                        broken.Add(npc.Name);
                    }
                }
                return true;
            },
            includeInteriors: true
        );
        return broken;
    }

    /// <summary>
    /// Sweeps every location (interiors included — married spouses live in cabins) and heals
    /// each sprite-less NPC. Returns the number healed. Game thread only.
    /// </summary>
    public int HealSpritelessNpcs(string context)
    {
        var healed = 0;
        Utility.ForEachLocation(
            location =>
            {
                foreach (var npc in location.characters)
                {
                    if (npc.Sprite == null && TryHealNpc(npc, location, context))
                    {
                        healed++;
                    }
                }
                return true;
            },
            includeInteriors: true
        );

        LastRunContext = context;
        LastRunHealedCount = healed;
        TotalHealed += healed;
        return healed;
    }

    /// <summary>
    /// Never throws: a heal failure must not abort the sweep (or the SaveLoaded chain) —
    /// one unhealable NPC is strictly better than several unhealed ones.
    /// </summary>
    private bool TryHealNpc(NPC npc, GameLocation location, string context)
    {
        try
        {
            var textureName = npc.getTextureName();

            npc.Sprite = new AnimatedSprite();
            var data = npc.GetData();
            // Same size fallbacks the engine uses in routeEndAnimationFinished.
            npc.Sprite.SpriteWidth = data?.Size.X ?? 16;
            npc.Sprite.SpriteHeight = data?.Size.Y ?? 32;
            npc.Sprite.UpdateSourceRect();

            Monitor.Log(
                $"Healed sprite-less NPC '{npc.Name}' in '{location.NameOrUniqueName}' "
                    + $"({context}): its spritesheet '{textureName}' failed to load, which "
                    + "would freeze the world clock at the NPC's next scheduled departure. "
                    + "The NPC keeps an empty sprite (renders as an error box) until the "
                    + "asset loads again.",
                LogLevel.Warn
            );
            ModEventLog.Emit(
                "npc_sprite_healed",
                new
                {
                    npcName = npc.Name,
                    location = location.NameOrUniqueName,
                    textureName,
                    context,
                }
            );
            return true;
        }
        catch (Exception ex)
        {
            Monitor.Log(
                $"Failed to heal sprite-less NPC '{npc.Name}' in "
                    + $"'{location.NameOrUniqueName}' ({context}): {ex}",
                LogLevel.Warn
            );
            return false;
        }
    }
}
