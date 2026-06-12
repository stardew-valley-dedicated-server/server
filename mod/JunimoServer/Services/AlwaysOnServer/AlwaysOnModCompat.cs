using System;
using StardewModdingAPI;
using StardewValley;

namespace JunimoServer.Services.AlwaysOn;

/// <summary>
/// Compatibility workarounds for THIRD-PARTY MODS (not base game). Base-game automation lives in
/// <see cref="AlwaysOnServer"/>; this class is the bounded home for behavior the server needs only
/// because a specific external mod is installed. None of these mods are compile-time dependencies,
/// so detection is by runtime type name and interaction is via reflection — both quarantined here so
/// they don't bleed into the clean base-game paths. Each member names the specific mod it targets.
/// </summary>
public class AlwaysOnModCompat
{
    // --- SpaceCore (Luck Skill and other custom-skill mods) ---

    /// <summary>
    /// SpaceCore's custom level-up menu (<c>SpaceCore.Interface.SkillLevelUpMenu</c>). A separate
    /// IClickableMenu subclass that does NOT derive from vanilla LevelUpMenu — so the base-game
    /// <c>HandleLevelUpMenu</c> never matches it. Type name + fields verified stable across SpaceCore
    /// releases 1.5.7→develop (2021→2025).
    /// </summary>
    private const string SpaceCoreLevelUpMenuTypeName = "SpaceCore.Interface.SkillLevelUpMenu";

    private readonly IModHelper _helper;
    private readonly IMonitor _monitor;

    public AlwaysOnModCompat(IModHelper helper, IMonitor monitor)
    {
        _helper = helper;
        _monitor = monitor;
    }

    /// <summary>
    /// Dismiss SpaceCore's custom level-up menu so the headless server isn't frozen by it.
    ///
    /// On a dedicated server no one can click OK. The menu sits on <c>Game1.activeClickableMenu</c>,
    /// and the new-day sequence (<c>Game1._newDayAfterFade</c> / the end-of-night block in
    /// <c>Game1._update</c>) only pops the next end-of-night menu while <c>activeClickableMenu == null</c>
    /// — so an undismissed menu stalls the new day permanently (the reported freeze).
    ///
    /// Setting <c>isActive = false</c> makes the menu self-close on its next <c>update()</c> (its
    /// update begins with <c>if (!isActive) { exitThisMenu(); return; }</c>, identical to vanilla),
    /// which nulls <c>activeClickableMenu</c> and lets the sequence proceed. The farmer keeps the skill
    /// level (it is derived from persisted XP); only the optional, usually-empty <c>DoLevelPerk</c>
    /// hook is skipped — the same trade-off the base-game handler makes for professions.
    ///
    /// No-op (and zero reflection) when SpaceCore is absent: the type-name check simply never matches.
    /// Call on the 1 Hz cadence alongside the base-game menu handlers.
    /// </summary>
    public void HandleSpaceCoreLevelUpMenu()
    {
        var menu = Game1.activeClickableMenu;
        if (menu == null || menu.GetType().FullName != SpaceCoreLevelUpMenuTypeName)
        {
            return;
        }

        try
        {
            _helper.Reflection.GetField<bool>(menu, "isActive").SetValue(false);
            _helper.Reflection.GetField<bool>(menu, "informationUp").SetValue(false);
            _monitor.Log("[Automation] Skipping SpaceCore level up menu", LogLevel.Info);
        }
        catch (Exception ex)
        {
            // Field/type could change in a future SpaceCore version (names verified stable across
            // every released git source 2021→2025, so unlikely). Recoverable: the menu stays up one
            // more second and this retries next tick. Log the actual type so a maintainer can re-sync
            // fast. Never log at Error (poisons E2E error detection — see .claude/rules/debugging.md).
            _monitor.Log(
                $"[Automation] Failed to dismiss SpaceCore level up menu ({menu.GetType().FullName}): {ex.Message}",
                LogLevel.Warn
            );
        }
    }
}
