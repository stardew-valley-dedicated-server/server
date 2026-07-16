using System;
using System.Collections.Generic;
using HarmonyLib;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.BellsAndWhistles;
using StardewValley.Characters;
using StardewValley.Monsters;
using StardewValley.Projectiles;

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
    ///
    /// <para>The patches are always installed; the per-call <see cref="Enabled"/> gate makes them pass
    /// through to vanilla when the kill-switch is off. The boot log line records which mode is active so
    /// an A/B run (or an operator on a low-TPS server) can confirm the knob was consumed.</para>
    /// </summary>
    public static void Apply(Harmony harmony, IMonitor monitor)
    {
        monitor.Log(
            Enabled
                ? "TPS-agnostic pacing ENABLED — fades and creature/event movement run at wall-clock "
                    + "speed at any TPS."
                : $"TPS-agnostic pacing DISABLED via {KillSwitchEnvVar}=false — fades/movement run at "
                    + "the vanilla per-tick rate (~60/TPS× slower at reduced TPS).",
            LogLevel.Info
        );

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
        // NPC.MovePosition is a thin movementPause gate over base.MovePosition. The paired prefix captures
        // pre-call velocity so the postfix can tell whether this tick took the velocity path (knockback)
        // vs the walk path — only the walk path sub-steps (see MovePosition_SubStep_Postfix).
        harmony.Patch(
            original: AccessTools.Method(typeof(Character), nameof(Character.MovePosition)),
            prefix: new HarmonyMethod(
                typeof(TpsAgnosticPacing),
                nameof(MovePosition_CaptureVelocity_Prefix)
            ),
            postfix: new HarmonyMethod(
                typeof(TpsAgnosticPacing),
                nameof(MovePosition_SubStep_Postfix)
            )
        );

        // Monsters, farm animals, and children each have their OWN MovePosition override that does NOT
        // route through Character.MovePosition, so the seam above misses them — patch each directly with
        // the same prefix+postfix (shared carry + re-entrancy guard, virtual dispatch re-invokes the right
        // override). Each override keeps its per-step collision probe, so sub-stepping preserves
        // combat/pathing semantics at any TPS. The velocity/knockback path (present in Character and
        // Monster) runs once per tick, never sub-stepped — its friction decay is per-call (see postfix).
        harmony.Patch(
            original: AccessTools.Method(typeof(Monster), nameof(Monster.MovePosition)),
            prefix: new HarmonyMethod(
                typeof(TpsAgnosticPacing),
                nameof(MovePosition_CaptureVelocity_Prefix)
            ),
            postfix: new HarmonyMethod(
                typeof(TpsAgnosticPacing),
                nameof(MovePosition_SubStep_Postfix)
            )
        );
        harmony.Patch(
            original: AccessTools.Method(typeof(FarmAnimal), nameof(FarmAnimal.MovePosition)),
            prefix: new HarmonyMethod(
                typeof(TpsAgnosticPacing),
                nameof(MovePosition_CaptureVelocity_Prefix)
            ),
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
            prefix: new HarmonyMethod(
                typeof(TpsAgnosticPacing),
                nameof(MovePosition_CaptureVelocity_Prefix)
            ),
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

        // Projectiles: sub-step the WHOLE Projectile.update (primitive 2 as a PREFIX, not a postfix).
        // update() advances one physics step (updatePosition) AND checks collision once, then returns a
        // bool the location's RemoveWhere consumes to delete the projectile. A postfix can't honor that
        // return or a mid-flight collision, so we run the extra steps as full update() calls in the
        // prefix (each re-checks collision — no tunneling) with a zero-elapsed GameTime so the ms grace
        // timers (travelTime, hostTimeUntilAttackable, DebuffingProjectile's wavy phase) count once. If
        // any extra step signals removal/collision, we skip the real call and hand its result to
        // RemoveWhere. The server drives this whenever a client is in/viewing the location (incl.
        // MineShaft.UpdateMines), so a fireball at reduced TPS otherwise crawls at 60/TPS× speed.
        harmony.Patch(
            original: AccessTools.Method(typeof(Projectile), nameof(Projectile.update)),
            prefix: new HarmonyMethod(typeof(TpsAgnosticPacing), nameof(Projectile_Update_Prefix))
        );

        // Debris chunk physics: same prefix sub-step. updateChunks is fully self-contained (velocity/
        // gravity integration + pickup + wall/rest bounce, no external collision) and returns a bool for
        // RemoveWhere. Zero-elapsed extra calls keep the per-tick gravity/velocity constants advancing
        // while the ms lifetime timers (timeSinceDoneBouncing, timeBeforeReturnToDroppingPlayer) count
        // once. Debris settles ~60/TPS× too slowly otherwise (item drops drift before resting/collecting).
        harmony.Patch(
            original: AccessTools.Method(typeof(Debris), nameof(Debris.updateChunks)),
            prefix: new HarmonyMethod(typeof(TpsAgnosticPacing), nameof(Debris_UpdateChunks_Prefix))
        );

        // Gliders (fliers: Bat/Fly/Serpent/Ghost/AngryRoger — isGlider==true, always velocity-driven).
        // Their per-tick movement is: MovePosition (position += velocity; velocity *= decay) + the velocity
        // GENERATION in behaviorAtGameTick/updateAnimation (the ramp `xVelocity += num/6f`, turn
        // `rotation += PI/64f`). Part 1 (the velocity-path skip) makes MovePosition run once/tick, which is
        // correct decay but leaves the glider advancing only `velocity` px/tick = 60/TPS× slow. To reach
        // wall-clock speed the whole trio must replay N×/tick, in order, zero-time on extras so the ms
        // timers (and the one-shot side effects they gate — notably Ghost's tongue-projectile spawn) fire
        // once. Postfix on Monster.update fires once/tick per monster; it replays the trio for gliders.
        // Disambiguate: Character has two update overloads — update(GameTime, GameLocation) and
        // update(GameTime, GameLocation, long, bool) — so name-only lookup throws AmbiguousMatchException.
        // Monster overrides the 2-arg one; specify its parameter types.
        harmony.Patch(
            original: AccessTools.Method(
                typeof(Monster),
                nameof(Monster.update),
                new[] { typeof(GameTime), typeof(GameLocation) }
            ),
            postfix: new HarmonyMethod(
                typeof(TpsAgnosticPacing),
                nameof(Monster_Update_Glider_Postfix)
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

    // Extra sub-step calls pass this zero-elapsed GameTime (reused; single-threaded loop). Per-tick-constant
    // steps (`position ± speed`, gravity, velocity ramp) don't read `time` so they still advance, while every
    // ms/TotalSeconds term in the same body contributes ZERO on extras — only the real call carries the tick's
    // ms budget. Without it those timers would advance ~12× too fast at TPS 5 (stuck-emote, follow-direction
    // changes, and — critically — the one-shot effects they gate, e.g. Ghost's projectile spawn).
    private static readonly GameTime ZeroElapsedGameTime = new GameTime(
        TimeSpan.Zero,
        TimeSpan.Zero
    );

    /// <summary>
    /// Prefix paired with <see cref="MovePosition_SubStep_Postfix"/>: captures the entity's velocity
    /// BEFORE the vanilla call so the postfix can tell whether this tick took the velocity path (knockback
    /// / glider) or the walk path. Read post-call would be wrong — the velocity path decays (and may zero)
    /// the velocity within the same call.
    /// </summary>
    private static void MovePosition_CaptureVelocity_Prefix(Character __instance, out bool __state)
    {
        // True when the entity is velocity-driven this tick (knockback for any character, or a glider's
        // always-on flight). Captured before vanilla runs, since vanilla's velocity path mutates it.
        __state = __instance.xVelocity != 0f || __instance.yVelocity != 0f;
    }

    /// <summary>
    /// Shared sub-step postfix for any <see cref="Character"/> subtype's <c>MovePosition</c> override.
    /// The vanilla call already ran one full step (carrying this tick's ms budget); this runs the
    /// remaining <c>floor(carry += TickScale) - 1</c> steps with a zero-elapsed <see cref="GameTime"/> so
    /// only the per-tick-constant position delta advances (never scaled), while ms-based timers in the
    /// same body stay counted once. Collision probes and the event NPC arrival window (fixed 16px) behave
    /// exactly as at 60 TPS. Virtual dispatch re-invokes the correct override; the shared
    /// <see cref="_inSubStep"/> guard stops recursion across all patched overrides.
    ///
    /// <para><b>Velocity path is once-per-tick, NOT sub-stepped.</b> Every <c>MovePosition</c> variant
    /// takes a velocity path (`position += xVelocity; xVelocity -= xVelocity/k`) whenever
    /// <c>xVelocity/yVelocity != 0</c> — knockback for any character, and the always-on movement of gliders
    /// (fliers). That friction decay is per-CALL, so repeating the call would decay velocity N× per tick
    /// (~40%/tick for a Bat, ~99% for the base <c>Character.applyVelocity</c>'s 50%/call), collapsing
    /// knockback distance. So when the entity is velocity-driven this tick we skip the extra steps: the
    /// real call already ran the velocity path once, which is the correct per-tick decay. (Gliders then
    /// advance only once/tick — still slow at low TPS — and need their own ramp+move sub-step, the glider
    /// seam below. The walk path, driven by the per-tick constant <c>speed</c>, is what SHOULD sub-step.)</para>
    /// </summary>
    private static void MovePosition_SubStep_Postfix(
        Character __instance,
        xTile.Dimensions.Rectangle viewport,
        GameLocation currentLocation,
        bool __state
    )
    {
        if (!Enabled || _inSubStep)
        {
            return;
        }

        // Velocity-driven this tick (knockback / glider): the real call already ran the velocity path with
        // its once-per-tick friction decay. Repeating it would over-decay (the per-call `xVelocity -=
        // xVelocity/k` compounds), so do NOT sub-step — there is no per-tick-constant walk step to repeat.
        if (__state)
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
        GameLocation currentLocation,
        bool __state
    )
    {
        // Mirror Child.MovePosition's festival-delegation guard: that path routes through
        // base.MovePosition and is sub-stepped by the Character seam, so skip it here.
        if (Game1.eventUp && Game1.CurrentEvent != null && Game1.CurrentEvent.isFestival)
        {
            return;
        }

        MovePosition_SubStep_Postfix(__instance, viewport, currentLocation, __state);
    }

    /// <summary>
    /// Prefix on <see cref="Projectile.update"/>. Runs the extra sub-steps this tick as full
    /// <c>update</c> calls (each re-checks collision, so a fast projectile can't tunnel a target/wall),
    /// each with a zero-elapsed <see cref="GameTime"/> so the ms grace timers count only on the real
    /// call. If an extra step returns <c>true</c> (collided/expired → remove), the real call is skipped
    /// and that result is handed to the location's <c>RemoveWhere</c>; otherwise the original runs
    /// normally with the tick's real <see cref="GameTime"/>.
    /// </summary>
    private static bool Projectile_Update_Prefix(
        Projectile __instance,
        GameTime time,
        GameLocation location,
        ref bool __result
    )
    {
        return RunUpdateSubSteps(
            extraStep: () => __instance.update(ZeroElapsedGameTime, location),
            ref __result
        );
    }

    /// <summary>
    /// Prefix on <see cref="Debris.updateChunks"/>. Same sub-step-as-prefix pattern as
    /// <see cref="Projectile_Update_Prefix"/>: the zero-elapsed extra calls advance the per-tick
    /// gravity/velocity constants while the ms lifetime timers count once, and any extra step's
    /// <c>true</c> return (chunks emptied / lifetime expired) is propagated to <c>RemoveWhere</c>.
    /// </summary>
    private static bool Debris_UpdateChunks_Prefix(
        Debris __instance,
        GameTime time,
        GameLocation location,
        ref bool __result
    )
    {
        return RunUpdateSubSteps(
            extraStep: () => __instance.updateChunks(ZeroElapsedGameTime, location),
            ref __result
        );
    }

    /// <summary>
    /// Shared core for the projectile/debris update prefixes: run <paramref name="extraStep"/> the
    /// tick's extra-step count of times under the re-entrancy guard, short-circuiting if a step signals
    /// removal. Returns <c>true</c> to let the original run (no extra step removed the entity), or
    /// <c>false</c> with <paramref name="removeResult"/> set to skip the original and delete the entity.
    ///
    /// <para>The zero-time extras run BEFORE the real call, so ms grace/lifetime timers are consumed at
    /// the end of the tick's batch instead of the start — a ≤1-tick phase error that is symmetric (real
    /// call first would expire them up to a tick early instead of late) and inherent to not distributing
    /// the tick's ms across sub-steps, which is unsafe (see the glider docstring's Ghost one-shot
    /// hazard). Extras-first also lets a prefix skip the original cleanly on removal.</para>
    /// </summary>
    private static bool RunUpdateSubSteps(Func<bool> extraStep, ref bool removeResult)
    {
        if (!Enabled || _inSubStep)
        {
            return true;
        }

        int extraSteps = ExtraStepsThisTick();
        if (extraSteps <= 0)
        {
            return true;
        }

        _inSubStep = true;
        try
        {
            for (int i = 0; i < extraSteps; i++)
            {
                if (extraStep())
                {
                    // An extra step collided/expired: remove the entity now and skip the real call so
                    // collision/removal fires exactly once, on the step that reached it.
                    removeResult = true;
                    return false;
                }
            }
        }
        finally
        {
            _inSubStep = false;
        }

        // No extra step removed it — let the real (first) call run with the tick's real GameTime.
        return true;
    }

    // === Glider (flier) whole-AI-tick sub-step ===

    // updateAnimation is `protected virtual`, so it can't be called directly from here. Cache one open
    // delegate per concrete glider type (keyed on the runtime type so virtual dispatch lands on the right
    // override — Fly.updateAnimation vs Bat.updateAnimation). Built once per type, then reused every tick
    // (no per-call reflection on the game thread). The game loop is single-threaded, so a plain Dictionary
    // is fine.
    private static readonly Dictionary<Type, Action<Monster, GameTime>> _updateAnimationByType =
        new();

    private static readonly Action<Monster, GameTime> _noopUpdateAnimation = (_, _) => { };

    private static Action<Monster, GameTime> GetUpdateAnimation(Monster monster)
    {
        var type = monster.GetType();
        if (!_updateAnimationByType.TryGetValue(type, out var call))
        {
            // MethodDelegate over the concrete type's updateAnimation binds to that override, so a Fly
            // instance runs Fly.updateAnimation. Fall back to a no-op if a (modded) glider type has no
            // resolvable updateAnimation — never throw on the per-tick hot path; it still gets move + AI.
            var method = AccessTools.Method(type, "updateAnimation", new[] { typeof(GameTime) });
            call =
                method != null
                    ? AccessTools.MethodDelegate<Action<Monster, GameTime>>(method)
                    : _noopUpdateAnimation;
            _updateAnimationByType[type] = call;
        }
        return call;
    }

    /// <summary>
    /// Postfix on <see cref="Monster.update"/> that gives GLIDERS wall-clock movement at reduced TPS. A
    /// glider is always velocity-driven, so its real <c>MovePosition</c> (post part-1) advanced position
    /// once this tick and its ramp (in <c>behaviorAtGameTick</c>/<c>updateAnimation</c>) generated velocity
    /// once. This replays the whole per-tick trio — <c>MovePosition</c> + <c>behaviorAtGameTick</c> +
    /// <c>updateAnimation</c>, in vanilla order — the tick's extra-step count of times, with a zero-elapsed
    /// <see cref="GameTime"/> so ms/TotalSeconds timers (and the one-shot side effects they gate, e.g.
    /// Ghost's projectile spawn) fire only on the real call. Non-gliders are untouched (their walk path is
    /// already sub-stepped by the MovePosition seam; their velocity path is knockback, handled by part 1).
    ///
    /// <para><b>Why zero-time, and its one accepted deviation.</b> Zero-time on the extra calls is
    /// load-bearing for SAFETY: it freezes each monster's ms/TotalSeconds timers so the one-shot effects
    /// behind them fire at most once per real tick — most importantly Ghost's tongue-projectile spawn
    /// (gated by a <c>stateTimer</c> that a nonzero elapsed would let cross repeatedly, firing N
    /// projectiles). The travel SPEED is exact regardless (velocity integrates N×). The cost: Bat, Ghost,
    /// and AngryRoger reset their heading gate to <c>wasHitCounter = 0</c>, so their steering re-runs every
    /// replay (fully faithful) — but Fly and Serpent set <c>wasHitCounter = 5 + random</c>, which zero-time
    /// never decays across replays, so their HEADING updates once per real tick instead of tracking the
    /// 60-TPS ~16ms decay. Net: Fly/Serpent re-aim at a moving target slightly slower than true 60 TPS
    /// (their speed is unaffected). This is accepted — TPS is a fluctuating target so exact heading is
    /// already approximate, and the alternative (distributing the tick's ms across sub-steps) would
    /// re-introduce the Ghost N-projectile bug. Do NOT switch these extra calls to a nonzero elapsed time
    /// to "fix" Fly/Serpent heading without first guarding every ms-gated one-shot effect (Ghost is the
    /// dangerous one).</para>
    /// </summary>
    private static void Monster_Update_Glider_Postfix(Monster __instance, GameLocation location)
    {
        if (!Enabled || _inSubStep || !__instance.isGlider.Value)
        {
            return;
        }

        int extraSteps = ExtraStepsThisTick();
        if (extraSteps <= 0)
        {
            return;
        }

        var updateAnimation = GetUpdateAnimation(__instance);
        var viewport = Game1.viewport;

        _inSubStep = true;
        try
        {
            for (int i = 0; i < extraSteps && __instance.Health > 0; i++)
            {
                // Vanilla per-tick order: move on the current velocity, then regenerate velocity/heading
                // for the next step. Zero-time so friction decays once-per-real-tick-worth per step (the
                // per-tick constant, correct to repeat) while ms timers contribute nothing on extras. The
                // Health guard stops replaying a monster any of these calls just killed (NaN/off-map death).
                __instance.MovePosition(ZeroElapsedGameTime, viewport, location);
                __instance.behaviorAtGameTick(ZeroElapsedGameTime);
                updateAnimation(__instance, ZeroElapsedGameTime);
            }
        }
        finally
        {
            _inSubStep = false;
        }
    }

    /// <summary>
    /// The number of ADDITIONAL sub-steps to run this tick (beyond the one vanilla already ran).
    /// Advances the per-tick carry accumulator by <see cref="TickScale"/> and returns
    /// <c>floor(carry) - 1</c>, keeping the fractional remainder for the next tick so long-run distance
    /// stays exact at non-integer scales. Recomputed once per game tick and shared across all callers.
    ///
    /// <para>Additive only: a <see cref="TickScale"/> below 1 (TPS above 60) would need vanilla's own
    /// step SKIPPED some ticks, which this primitive can't do — so both mods clamp TPS to at most 60
    /// (<c>Env.ServerTps</c>, test-client <c>ModEntry</c>) and the scale is structurally ≥ 1.</para>
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
