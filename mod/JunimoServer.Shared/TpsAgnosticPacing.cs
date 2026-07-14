using System;
using HarmonyLib;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.BellsAndWhistles;
using StardewValley.Characters;
using StardewValley.Monsters;

namespace JunimoServer.Shared;

/// <summary>
/// Makes per-tick-constant gameplay code produce correct wall-clock outcomes at any tick rate.
///
/// <para>
/// Vanilla runs a hard-fixed 60 ticks/sec, so it freely mixes millisecond-accumulating code
/// (<c>x -= time.ElapsedGameTime.Milliseconds</c> — stays real-time at any TPS) with per-tick
/// constants (<c>x += 0.007f</c> per Update — runs <c>60/TPS</c>× slower). Both mods pin
/// <c>TargetElapsedTime = 1000/TPS</c> (server <c>ServerOptimizer</c>, test client <c>ModEntry</c>),
/// so at <c>SERVER_TPS=5</c> a per-tick constant advances 12× slower per real second. Only the
/// per-tick-constant class breaks; these patches compensate the gameplay-relevant instances so the
/// server simulates NPCs/fades at real-time speed and the test client behaves like a real player.
/// </para>
///
/// <para>
/// <b>Scale factor.</b> <see cref="TickScale"/> is <c>ElapsedGameTime.TotalMilliseconds / (1000/60)</c>
/// read from <c>Game1.currentGameTime</c> — exactly <c>1.0</c> at 60 TPS (a structural no-op on the
/// vanilla-default rate), <c>12.0</c> at TPS 5, and a non-integer like <c>2.4</c> at TPS 25. All
/// primitives handle fractional scales.
/// </para>
///
/// <para>
/// <b>Kill-switch.</b> <c>SDVD_TPS_AGNOSTIC_PACING=false</c> disables every patch (default on). The
/// patches are still installed; the per-call <see cref="Enabled"/> gate makes them pass through to
/// vanilla behavior, so an operator on a low-TPS server can restore the long-standing 12× pacing
/// without a rebuild.
/// </para>
/// </summary>
public static class TpsAgnosticPacing
{
    private const string KillSwitchEnvVar = "SDVD_TPS_AGNOSTIC_PACING";

    // 60 TPS is vanilla's fixed rate; one tick at 60 TPS is this many ms. TickScale is the ratio of
    // the actual tick duration to this reference, so a per-tick constant multiplied by TickScale
    // advances the same amount per real second regardless of the configured TPS.
    private const double VanillaTickMs = 1000.0 / 60.0;

    // Read once at first Apply(): the kill-switch is a boot-time env var (TPS itself is only set at
    // boot), so there is no need to re-read it per call. Nullable so the first read latches it.
    private static bool? _enabled;

    /// <summary>
    /// False only when <c>SDVD_TPS_AGNOSTIC_PACING</c> is explicitly set to <c>false</c>. Every patch
    /// checks this and falls through to vanilla when off.
    /// </summary>
    public static bool Enabled
    {
        get
        {
            if (_enabled == null)
            {
                var raw = Environment.GetEnvironmentVariable(KillSwitchEnvVar);
                _enabled = string.IsNullOrWhiteSpace(raw) || !bool.TryParse(raw, out var on) || on;
            }
            return _enabled.Value;
        }
    }

    /// <summary>
    /// The current tick's duration relative to a vanilla 60-TPS tick: <c>1.0</c> at 60 TPS, <c>12.0</c>
    /// at TPS 5, fractional at non-divisor rates. Returns <c>1.0</c> before the first tick (no
    /// <c>currentGameTime</c> yet) so a pre-tick call is a no-op rather than a divide-by-zero risk.
    /// </summary>
    public static float TickScale
    {
        get
        {
            var gameTime = Game1.currentGameTime;
            if (gameTime == null)
            {
                return 1f;
            }
            var ms = gameTime.ElapsedGameTime.TotalMilliseconds;
            if (ms <= 0)
            {
                return 1f;
            }
            return (float)(ms / VanillaTickMs);
        }
    }

    /// <summary>
    /// Registers every pacing patch on the given Harmony instance (fades, and the movement sub-step for
    /// villagers/event-actors, monsters, farm animals, children, plus farmer event movement). Idempotent
    /// per instance (Harmony rejects a duplicate patch of the same method by the same owner). Call from
    /// each mod's unconditionally-run startup path.
    /// </summary>
    public static void Apply(Harmony harmony)
    {
        // Fades: scale the per-call globalFade delta (primitive 1 — a continuous quantity with no
        // collision/trigger semantics, so a scaled delta is exact). Prefix inflates globalFadeSpeed by
        // TickScale, vanilla runs its own body + completion, postfix restores it.
        harmony.Patch(
            original: AccessTools.Method(typeof(ScreenFade), nameof(ScreenFade.UpdateGlobalFade)),
            prefix: new HarmonyMethod(typeof(TpsAgnosticPacing), nameof(UpdateGlobalFade_Prefix)),
            postfix: new HarmonyMethod(typeof(TpsAgnosticPacing), nameof(UpdateGlobalFade_Postfix))
        );

        // NPC / event-actor walking: sub-step the vanilla per-tick move (primitive 2 — stepwise logic
        // with per-step collision/arrival semantics, so we run the step floor(carry) times rather than
        // scaling the delta). Postfix on Character.MovePosition covers villagers AND event actors, since
        // NPC.MovePosition is a thin movementPause gate over base.MovePosition.
        harmony.Patch(
            original: AccessTools.Method(typeof(Character), nameof(Character.MovePosition)),
            postfix: new HarmonyMethod(
                typeof(TpsAgnosticPacing),
                nameof(MovePosition_SubStep_Postfix)
            )
        );

        // Monsters, farm animals, and children each have their OWN MovePosition override that does NOT
        // route through Character.MovePosition, so the seam above misses them — patch each directly with
        // the same sub-step postfix (shared carry + re-entrancy guard, virtual dispatch re-invokes the
        // right override). Each override keeps its per-step collision probe, so sub-stepping preserves
        // combat/pathing semantics at any TPS. Monster's knockback/velocity branch already self-substeps
        // large velocities (ceil(|v|/bbox) loop) and decays velocity per call — running the whole method
        // floor(carry) times reproduces vanilla's 60Hz knockback decay exactly.
        harmony.Patch(
            original: AccessTools.Method(typeof(Monster), nameof(Monster.MovePosition)),
            postfix: new HarmonyMethod(
                typeof(TpsAgnosticPacing),
                nameof(MovePosition_SubStep_Postfix)
            )
        );
        harmony.Patch(
            original: AccessTools.Method(typeof(FarmAnimal), nameof(FarmAnimal.MovePosition)),
            postfix: new HarmonyMethod(
                typeof(TpsAgnosticPacing),
                nameof(MovePosition_SubStep_Postfix)
            )
        );
        // Child.MovePosition delegates to base.MovePosition during festivals — that path is already
        // sub-stepped by the Character seam above, so the Child postfix must skip it to avoid
        // double-stepping (see Child_MovePosition_Postfix).
        harmony.Patch(
            original: AccessTools.Method(typeof(Child), nameof(Child.MovePosition)),
            postfix: new HarmonyMethod(
                typeof(TpsAgnosticPacing),
                nameof(Child_MovePosition_Postfix)
            )
        );

        // Farmer event/cutscene movement: scale getMovementSpeed's event branch (primitive 1). Safe
        // here — unlike NPCs — because scripted event moves bypass the collision probe entirely
        // (Farmer.MovePositionImpl `|| flag`, decompiled Farmer.cs) and the event arrival margin
        // self-scales with the returned speed (`16f + movementSpeed`, decompiled Event.cs), so a scaled
        // delta can neither tunnel a collider nor overshoot the arrival window.
        harmony.Patch(
            original: AccessTools.Method(typeof(Farmer), nameof(Farmer.getMovementSpeed)),
            postfix: new HarmonyMethod(
                typeof(TpsAgnosticPacing),
                nameof(Farmer_GetMovementSpeed_Postfix)
            )
        );
    }

    // === Primitive 2: per-tick sub-step carry (movement) ===

    // One global accumulator per game tick: the fractional part of TickScale that hasn't yet been
    // spent on a whole extra step. Recomputed against the tick's own scale each frame so every
    // character advances uniformly. Not per-character — the carry is a property of the tick, not the
    // actor — so a location full of NPCs all sub-step the same integer count this tick.
    private static double _moveCarry;
    private static uint _moveCarryTick = uint.MaxValue;

    // Re-entrancy guard: our postfix calls MovePosition again, which re-enters this postfix. The guard
    // stops the recursion so each real tick produces exactly (extraSteps) additional calls, not a
    // geometric explosion.
    [ThreadStatic]
    private static bool _inSubStep;

    // Extra sub-step calls pass a zero-elapsed GameTime (reused, single-threaded game loop). The
    // per-tick-constant POSITION step (`position ± speed`) does not read `time`, so it still advances;
    // the ms-based terms in the same body (`blockedInterval += ms`, `slideAnimationTimer -= ms`, the
    // wall-clock slide-correction `speed * ms/64`, FarmAnimal's `nextFollowDirectionChange -= ms`)
    // contribute ZERO on the extra calls — so only the real (first) call carries the tick's ms budget.
    // Without this those accumulators would advance once per sub-step (~12x too fast at TPS 5),
    // triggering stuck-emote/charge and follow-direction changes far too early.
    private static readonly GameTime ZeroElapsedGameTime = new GameTime(
        TimeSpan.Zero,
        TimeSpan.Zero
    );

    /// <summary>
    /// Shared sub-step postfix for any <see cref="Character"/> subtype's <c>MovePosition</c> override.
    /// The vanilla call already ran one full step (carrying this tick's ms budget); this runs the
    /// remaining <c>floor(carry += TickScale) - 1</c> steps with a zero-elapsed <see cref="GameTime"/> so
    /// only the per-tick-constant position delta advances (never scaled), while ms-based timers in the
    /// same body stay counted once. Collision probes, the event NPC arrival window (fixed 16px), and
    /// monster knockback decay behave exactly as at 60 TPS. Virtual dispatch re-invokes the correct
    /// override; the shared <see cref="_inSubStep"/> guard stops recursion across all patched overrides.
    /// </summary>
    private static void MovePosition_SubStep_Postfix(
        Character __instance,
        xTile.Dimensions.Rectangle viewport,
        GameLocation currentLocation
    )
    {
        if (!Enabled || _inSubStep)
        {
            return;
        }

        int extraSteps = ExtraStepsThisTick();
        if (extraSteps <= 0)
        {
            return;
        }

        _inSubStep = true;
        try
        {
            for (int i = 0; i < extraSteps; i++)
            {
                __instance.MovePosition(ZeroElapsedGameTime, viewport, currentLocation);
            }
        }
        finally
        {
            _inSubStep = false;
        }
    }

    /// <summary>
    /// Sub-step postfix for <see cref="Child.MovePosition"/>. Identical to
    /// <see cref="MovePosition_SubStep_Postfix"/> except it SKIPS the festival case: during a festival
    /// <c>Child.MovePosition</c> delegates to <c>base.MovePosition</c> (the <see cref="Character"/> seam),
    /// which the Character postfix already sub-steps — running this postfix too would double-step. The
    /// festival delegation condition mirrors <c>Child.MovePosition</c> (decompiled): event up, current
    /// event is a festival.
    /// </summary>
    private static void Child_MovePosition_Postfix(
        Child __instance,
        xTile.Dimensions.Rectangle viewport,
        GameLocation currentLocation
    )
    {
        // Mirror Child.MovePosition's festival-delegation guard: that path routes through
        // base.MovePosition and is sub-stepped by the Character seam, so skip it here.
        if (Game1.eventUp && Game1.CurrentEvent != null && Game1.CurrentEvent.isFestival)
        {
            return;
        }

        MovePosition_SubStep_Postfix(__instance, viewport, currentLocation);
    }

    /// <summary>
    /// The number of ADDITIONAL sub-steps to run this tick (beyond the one vanilla already ran).
    /// Advances the per-tick carry accumulator by <see cref="TickScale"/> and returns
    /// <c>floor(carry) - 1</c>, keeping the fractional remainder for the next tick so long-run distance
    /// stays exact at non-integer scales. Recomputed once per game tick and shared across all callers.
    /// </summary>
    private static int ExtraStepsThisTick()
    {
        uint tick = GetCurrentTick();
        if (tick != _moveCarryTick)
        {
            _moveCarryTick = tick;
            _moveCarry += TickScale;
            // Whole steps this tick include the one vanilla already ran; consume them from the carry.
            int wholeSteps = (int)Math.Floor(_moveCarry);
            _moveCarry -= wholeSteps;
            _stepsRemainingThisTick = Math.Max(0, wholeSteps - 1);
        }
        return _stepsRemainingThisTick;
    }

    // The extra-step count computed for the current tick, so repeated MovePosition calls within one
    // tick (multiple NPCs) each get the same count without re-advancing the carry.
    private static int _stepsRemainingThisTick;

    private static uint GetCurrentTick()
    {
        // Game1.ticks increments once per Update; a stable per-tick key so the carry advances once
        // per real tick even though MovePosition is called many times per tick (once per character).
        return (uint)Game1.ticks;
    }

    // === Primitive 1: scaled step (fades, farmer event movement) ===

    /// <summary>
    /// Prefix on <see cref="ScreenFade.UpdateGlobalFade"/> that temporarily inflates the public
    /// <c>globalFadeSpeed</c> field by <see cref="TickScale"/>, lets vanilla run, then a paired postfix
    /// restores it. Vanilla's own body does the <c>fadeToBlackAlpha ± globalFadeSpeed</c> step (now
    /// scaled) and its own completion check, so the private <c>afterFade</c> callback, its identity
    /// re-check, the <c>Game1.nonWarpFade</c> handling, and the <c>Game1.IsDedicatedHost</c> instant-snap
    /// short-circuits are all preserved untouched — a cutscene fade just reaches its 0/1 terminus in the
    /// same wall-clock time at any TPS. <c>globalFadeSpeed</c> is read only inside this method (verified
    /// against the decompiled tree), so inflating it for the call's duration is invisible elsewhere.
    /// Skips scaling when the kill-switch is off (falls through to vanilla pacing).
    ///
    /// <para>On the JunimoServer host <c>IsDedicatedHost</c> is FALSE (the mod deliberately leaves
    /// <c>hasDedicatedHost</c> unset — see <c>AlwaysOn.OnSaveLoaded</c>), so the instant-snap ternary
    /// takes its ELSE branch and the fade runs the incremental step this patch scales — i.e. the patch
    /// is live and load-bearing server-side. On the test client the fade is instead forced instant by
    /// <c>ConvenienceTweaks.PatchInstantFades</c> (a separate postfix that snaps alpha to terminus), so
    /// this scaling is overridden there — intended: the test harness wants instant fades, not merely
    /// fast ones.</para>
    /// </summary>
    private static void UpdateGlobalFade_Prefix(ScreenFade __instance, out float __state)
    {
        __state = __instance.globalFadeSpeed;
        if (Enabled)
        {
            __instance.globalFadeSpeed *= TickScale;
        }
    }

    private static void UpdateGlobalFade_Postfix(ScreenFade __instance, float __state)
    {
        // Restore the original so a later reader (or the next tick before GlobalFadeTo* resets it) sees
        // the vanilla value, not the inflated one. Vanilla clamps alpha with Math.Max/Min, so an
        // inflated step that overshoots 0/1 lands exactly on the terminus and fires completion on the
        // correct tick.
        __instance.globalFadeSpeed = __state;
    }

    /// <summary>
    /// Postfix on <see cref="Farmer.getMovementSpeed"/>. Scales only the EVENT branch's result by
    /// <see cref="TickScale"/> — the free-move branch already ms-scales in vanilla, so we detect it by
    /// its signature (it multiplies by <c>ElapsedGameTime.Milliseconds</c>, making it already
    /// wall-clock-correct) and leave it untouched. The event branch is the one that returns a raw
    /// <c>Max(1, speed + farmerAddedSpeed…)</c> per tick with no ms scaling.
    /// </summary>
    private static void Farmer_GetMovementSpeed_Postfix(Farmer __instance, ref float __result)
    {
        if (!Enabled)
        {
            return;
        }

        // The free-move branch runs when there is no event, or the event is a player-control sequence
        // (decompiled Farmer.getMovementSpeed) — and that branch is already ms-scaled by vanilla. Only
        // the scripted-event branch (event up, not player-controlled) needs compensation.
        var ev = Game1.CurrentEvent;
        if (ev == null || ev.playerControlSequence)
        {
            return;
        }

        __result *= TickScale;
    }
}
