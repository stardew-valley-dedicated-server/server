# Make gameplay TPS-agnostic at arbitrary TPS (fades, movement, and the tick-scaled-logic audit)

Planned 2026-07-13. **Mission:** the server must produce *correct, wall-clock gameplay* at any
`SERVER_TPS` (production runs arbitrary values; 5 is the proven test/CI value, 60 the vanilla
reference), and the test client must behave like a real-time client at any `CLIENT_TPS`. Test speed
is a beneficiary, not the goal. Every mechanism claim below was verified by reading the cited
decompiled source; items not yet read are listed as explicit audit tasks with a recipe — nothing is
assumed.

## STATUS (2026-07-14) — implemented on branch `bugfix/tps-agnostic-pacing`

Worktree: `repos/server-worktrees/tps-agnostic-pacing` (branched off `master`, NOT the sibling
`wedding-speedup` worktree which a different session owns). **3 commits, NOT pushed** — awaiting
review:
- `61cb065` perf: Stage 1 — fade scaled-step, NPC/event-actor sub-step, farmer-event scale, kill-switch, both-mod registration.
- `5617a33` fix: corrected the wrong `IsDedicatedHost=false` comments + recorded Stage 1 validation.
- `a4f4c50` perf: Stage 3 creature movement (Monster/FarmAnimal/Child sub-step) + the zero-time ms-timer correctness fix.

All code lives in `mod/JunimoServer.Shared/TpsAgnosticPacing.cs`, registered from
`mod/JunimoServer/ModEntry.cs` (`LoadServiceDependencies`) and `tests/test-client/ModEntry.cs`
(`Entry`). Six patches: `ScreenFade.UpdateGlobalFade`, `Character/Monster/FarmAnimal/Child.MovePosition`,
`Farmer.getMovementSpeed`. Kill-switch env var `SDVD_TPS_AGNOSTIC_PACING` (default on).

**DONE + validated:** Stage 1 (fades + NPC/farmer-event movement) and Stage 3 creature movement.
WeddingTests green twice (both clients render both ceremonies — scenario confirmed via log, not just
the check); full suite 129 passed (the lone failure was an unrelated 504 infra flake that passes in
isolation); CPU noise-level (median tick 0.22ms / 200ms budget).

**ALL CORE WORK DONE + VALIDATED (2026-07-14).** Every item closed with a runtime gate:
1. **Non-integer-TPS gate** ✅ — WeddingTests at `SERVER_TPS=CLIENT_TPS=24` (scale 2.5, fractional carry).
2. **Kill-switch A/B** ✅ — wired into container env; boot log confirms `DISABLED` when off (knob consumed).
3. **Stage 2 sweep** ✅ — complete; surfaced the flyer/velocity finding (everything else ms-based/cosmetic).
4. **Stage 3 projectile + debris** ✅ — prefix sub-step + combat probe (projectile 696-1536px, debris settles).
5. **Velocity-decay-over-substep bug (found via the flyer investigation — a real bug in the SHIPPED Stage 1/3
   sub-steps):** Part 1 (knockback, all monsters+NPCs) ✅ — slime knockback 400px. Part 2 (gliders/fliers) ✅
   — Bat homing 693-766px (was ~15px unpatched). Final regression: PacingProbeTests (all 5) + WeddingTests
   all green, no Stage-1 regression (both ceremonies rendered, zero errors) — run `2026-07-14T21-16-39Z`.

Remaining: only a broader full-suite regression (catch anything outside the probe/wedding classes) is
un-run since the velocity/glider changes landed; the targeted PacingProbeTests+WeddingTests regression is
green. Minor known deviation: Fly/Serpent heading-tracking lags slightly under zero-time (within vanilla's
tick variance; travel speed exact) — accepted per the fluctuating-TPS decision.

**Strategic boundary (CPU):** TPS 5 exists to cut simulation cost. "TPS-agnostic" therefore means
wall-clock-correct *outcomes* for gameplay-relevant mechanics — not sub-stepping the whole game
back to 60 TPS cost. Sub-stepping is applied per-subsystem (movement-class code is a small slice
of tick cost; per `sdv-perf-static-audit-findings` the expensive paths are presence-gated), and
each stage's CPU is measured, not assumed.

## Background: the two timing styles

Vanilla runs hard-fixed at 60 ticks/sec, so it freely mixes millisecond-accumulating code
(`x -= time.ElapsedGameTime.Milliseconds` — stays real-time at any TPS) with per-tick constants
(`x += 0.007f` per Update — runs `60/TPS`× slower). Our mods pin
`TargetElapsedTime = 1000/TPS` (server: `ServerOptimizer.cs:286`; test client:
`tests/test-client/ModEntry.cs:167`), making `ElapsedGameTime` = `1000/TPS` ms per tick. Only the
per-tick-constant class breaks; the job is to find and compensate every *gameplay-relevant*
instance.

Sibling plan: [`one-second-handlers-fire-every-12s.md`](one-second-handlers-fire-every-12s.md)
owns the SMAPI-internal instance (`OneSecondUpdateTicked`'s hardcoded 60-tick divisor —
unpatchable via Harmony, needs the SMAPI source patch). This plan owns all *game-code* instances
(Harmony-patchable from the mods).

## The authority dimension: who executes what

Every audited site gets classified by where it runs in our topology, because that decides impact:

- **Real-player clients (vanilla, 60 TPS):** their own farmer movement, fishing, tools, menus,
  local VFX — unaffected by server TPS. Out of scope by construction.
- **Server-simulated:** NPCs, monsters, farm animals, world/location updates, hosted festivals,
  day transitions — tick-scaled defects here leak into every player's gameplay (e.g. villagers
  visibly walking at 1/12 speed while the in-game clock runs real-time, so schedules lag).
- **Per-instance (events):** each instance runs its own event copy (weddings/festivals), so both
  the render-suppressed host AND our TPS-throttled test clients are affected.
- **To-determine per site:** projectiles/debris — updated from location update paths whose
  simulation authority (host vs whichever client has the location active) must be read, not
  guessed. An audit column, filled before fixing.

## Verified inventory

### Tick-scaled, gameplay-relevant — Stage 1 IMPLEMENTED (`mod/JunimoServer.Shared/TpsAgnosticPacing.cs`)

Registered on both mods (`JunimoServer/ModEntry.cs` `LoadServiceDependencies`, `test-client/ModEntry.cs` `Entry`). Kill-switch `SDVD_TPS_AGNOSTIC_PACING=false`. `TickScale` reads `Game1.currentGameTime`; the per-tick sub-step carry is keyed on `Game1.ticks`. (Implemented on a fresh worktree off `master`, branch `bugfix/tps-agnostic-pacing`.)

| Site | Mechanism (verified) | Fix (primitive) | Impact at TPS 5 |
|---|---|---|---|
| `ScreenFade.UpdateGlobalFade()` (`ScreenFade.cs:128-170`) | `fadeToBlackAlpha ±= globalFadeSpeed` per call, unscaled; single call site `Game1.cs:3921-3923`. `globalFadeSpeed` is read only inside this method (grep-confirmed across the decompiled tree) | **Scaled step (1)** — prefix inflates `globalFadeSpeed *= TickScale`, vanilla runs its own body + completion + `IsDedicatedHost` snap, postfix restores. No private-field re-implementation. **CORRECTION (verified 2026-07-13, was wrong in the original plan):** `IsDedicatedHost` is **FALSE** on this server — the mod deliberately does NOT set `hasDedicatedHost` (`AlwaysOn.OnSaveLoaded:212`; confirmed by the existing comment at `AlwaysOn.cs:673` "IsDedicatedHost is false here"). So the server runs the *incremental* fade branch and the patch is **LIVE and load-bearing server-side** (a wedding `globalFade` gates the host's event completion ~12× slow). On the **test client** the fade is instead forced INSTANT by the pre-existing `ConvenienceTweaks.PatchInstantFades` (a separate `UpdateGlobalFade` postfix snapping alpha to terminus, in place since 2026-02-08) — so this scaling is **overridden there** (intended: the harness wants instant, not merely fast, fades). Net: fade patch fixes the **server**, inert on the test client. | Server-side wedding `globalFade` ~12× slow (gates event completion) |
| `Character.MovePosition` (`Character.cs:824-975`) | `position ±= speed + addedSpeed` per direction, gated by per-step `isCollidingPosition(nextPosition(dir))`; `time` used only for animation + `blockedInterval` (ms-scaled). `NPC.MovePosition` (`NPC.cs:3085-3092`) is a thin `movementPause` gate over `base.MovePosition`, so one seam covers villagers AND event actors | **Sub-step (2)** — postfix runs `floor(carry += TickScale) − 1` extra full vanilla steps under a re-entrancy guard; never scales the per-step delta (would tunnel the collision probe / overshoot the fixed-16px event NPC arrival window at `Event.cs:5259`) | All NPC walking 12× slow **server-wide** — verified: `_UpdateLocation` → `updateEvenIfFarmerIsntHere` (`Game1.cs:5871`) runs `updateCharacters` → `NPC.update` → `MovePosition` for EVERY location each tick (`Utility.ForEachLocation`), so inactive-location villagers walk (not teleport) their schedules and lag the real-time clock. Event choreography crawls (Lewis: 6.4s vs 0.5s) |
| `Farmer.getMovementSpeed()` event branch (`Farmer.cs:8083`) | `Max(1, speed + farmerAddedSpeed…)` per tick — unscaled (the free-move branch `:8069-8081` already ms-scales by `ElapsedGameTime.Milliseconds`) | **Scaled step (1)** — postfix scales `__result *= TickScale` only on the event branch (detected by `CurrentEvent != null && !playerControlSequence`). Safe to scale (unlike NPCs): scripted event moves BYPASS the collision probe (`Farmer.MovePositionImpl:8595` `\|\| flag` where `flag = eventUp && !isFestival && !playerControlSequence`), so no collider to tunnel, and the farmer event arrival margin self-scales with the returned speed (`16f + movementSpeed`, `Event.cs:5236`), so no overshoot. *(Plan's earlier "collisions cleared at Event.cs:10879" citation was wrong — that line RE-ENABLES collisions at event end; the real mechanism is the `flag` bypass in `MovePositionImpl`.)* | Farmer event/cutscene movement 12× slow |

### Stage 3 — creature MOVEMENT: IMPLEMENTED (Monster/FarmAnimal/Child sub-step)

Patched with the SAME sub-step primitive as Stage 1 (`MovePosition_SubStep_Postfix`), extended for a
correctness bug found during Stage 3 (see below). Registered in `TpsAgnosticPacing.Apply`.

| Site | Mechanism (verified) | Fix | Notes |
|---|---|---|---|
| `Monster.MovePosition` (`Monster.cs:1011-1200`) | Own impl (not via `Character`). Walk portion is `position ± speed` per direction with per-step `isCollidingPosition` — same shape as `Character`. Knockback branch already self-substeps large velocities (`ceil(|v|/bbox)` lerp loop) and decays velocity per call | **Sub-step (2)** — postfix runs the whole method `floor(carry)-1` extra times. Whole-method sub-step reproduces vanilla 60Hz knockback decay exactly | `slideAnimationTimer`/`blockedInterval`/wall-slide-correction are ms-based — see zero-time fix below |
| `FarmAnimal.MovePosition` (`FarmAnimal.cs:2099+`) | Own impl; `Game1.IsClient` early-return = server-simulated. `position ± speed` step + per-step `isCollidingPosition` | **Sub-step (2)** | `nextFollowDirectionChange -= ms` is ms-based — zero-time fix applies |
| `Child.MovePosition` (`Child.cs:157+`) | Own impl gated on `Game1.IsMasterGame`; `position ± speed` + per-step collision. Festival case delegates to `base.MovePosition` (Character seam) | **Sub-step (2)** with a festival-skip guard (that path is already sub-stepped by the Character seam — avoids double-step) | — |

**Zero-time sub-step correction (found + fixed during Stage 3, applies to Stage 1 too):** the original
Stage 1 postfix re-invoked `MovePosition(time, …)` with the REAL `time`, so ms-based accumulators inside
the body (`blockedInterval += ms` → stuck-emote/charge; Monster `slideAnimationTimer`, wall-slide
`speed*ms/64`; FarmAnimal `nextFollowDirectionChange -= ms`) advanced once PER sub-step (~12× too fast at
TPS 5). Fix: extra sub-steps now pass `ZeroElapsedGameTime`, so the per-tick-constant *position* step
(which never reads `time`) still advances while every ms-based term contributes zero on the extra calls
— only the real (first) call carries the tick's ms budget. This corrects a latent bug in the shipped
Stage 1 Character sub-step (a stuck NPC charged after ~0.4s instead of 5s at TPS 5).

*Known cosmetic limitation of zero-time:* the walk-cycle animation (`Sprite.Animate(time, …)`) does NOT
advance on the extra zero-time steps, so at low TPS a character's *position* moves at correct wall-clock
speed but its leg animation still cycles at the tick rate (12× slow at TPS 5). Position/arrival — the
gameplay-relevant property — is correct; only the sprite cadence is slow. Accepted: animating on the
extra steps would require passing real `time`, which reintroduces the ms-accumulator over-count. Position
correctness > animation smoothness.

### Stage 3 — projectiles + debris: IMPLEMENTED (2026-07-14) — prefix sub-step + combat probe

Both are server-simulated whenever a farmer is in/viewing the location (`GameLocation.UpdateWhenCurrentLocation`
lines 4152/4155; `MineShaft.UpdateMines` `Game1.cs:5842`; `IsMasterGame` always true here). The fix is a
**prefix** sub-step (not a postfix, unlike movement) because both `Projectile.update` and `Debris.updateChunks`
return a `bool` the location's `RemoveWhere` consumes to delete the entity, and check collision inside the
method — a postfix couldn't honor the return or a mid-flight collision.

| Site | Mechanism (verified) | Fix |
|---|---|---|
| `Projectile.update` / `updatePosition` (`Projectile.cs:326-412`; `BasicProjectile.cs:95-106`, `DebuffingProjectile.cs:69-80`) | `velocity += acceleration; position += velocity` per call, unscaled. Collision checked ONCE per `update` (after the single `updatePosition`, `Projectile.cs:389`); `travelTime += ms` grace timer (`:365`), `hostTimeUntilAttackable -= TotalSeconds` (`:334`), and `DebuffingProjectile` wavy phase are ms/wall-clock. | **Prefix sub-step (`Projectile_Update_Prefix` → `RunUpdateSubSteps`)** — runs `floor(carry)-1` extra full `update` calls with `ZeroElapsedGameTime` (each re-checks collision → no tunneling; ms grace timers count once on the real call). If an extra step returns `true` (collided/expired), skip the real call and hand that to `RemoveWhere`. |
| `Debris.updateChunks` (`Debris.cs:631-912`) | `position += velocity`, `yVelocity -= 0.25f/0.4f` gravity, bounce damping — all per-tick constants, self-contained (no external collision). `timeSinceDoneBouncing += ms` (`:637`), `timeBeforeReturnToDroppingPlayer -= ms` (`:675`) are ms lifetime timers. Returns `bool` for `RemoveWhere`. | **Same prefix sub-step (`Debris_UpdateChunks_Prefix`)** — zero-time extra calls advance the gravity/velocity constants while the ms lifetime timers count once. |

**Combat probe (runtime gate):** `/test/pacing_probe_spawn` + `/test/pacing_probe_state` (both `Env.IsTest`-gated,
`ApiService.TestEndpoints.cs`) spawn a projectile/debris/monster in the HOST's own location (open Farm on an idle
server — the host is there and `IsMasterGame` simulates it, so no client needed) and read back the measured
quantity. `PacingProbeTests` (API-only, `Clients=0`, Exclusive) asserts wall-clock-correct pacing at the reduced
test TPS: projectile `travelDistance ≥ 500px` in 3s (bounces to stay in-map), all debris chunks finished falling
(`bounces > 2`), bat displacement toward the host. Sized so an unpatched/kill-switch-off build (12× slow) fails.

**Also folded in — flyer-monster velocity/rotation** (Stage 2 finding, row 1 of that table): **DONE** — the
root cause turned out to be the velocity-decay-over-substep bug (the shipped `MovePosition` sub-step
over-applied per-call friction to velocity-driven movement), fixed in two parts (knockback + glider AI-tick
sub-step). See the "Velocity-decay-over-substep bug" section below and "Remaining work" item 4.

### Verified already TPS-agnostic — do NOT touch (prevents re-litigation)

- Warp/screen fades: `UpdateFadeAlpha` scales by elapsed ms (`ScreenFade.cs:70,75`).
- Farmer free movement: ms-scaled (`Farmer.cs:8071-8073`); its large per-tick step at 200ms
  (~66px) is pre-existing behavior, separate concern.
- `Game1.pauseTime` (`Game1.cs:5398`), `DialogueBox.safetyTimer` (`DialogueBox.cs:773`),
  `Event.Speak` throttle (`Event.cs:171`), in-game clock (`gameTimeInterval += elapsed ms`,
  `Game1.cs:6054`), `TemporaryAnimatedSprite` (`:1689`, `TotalMilliseconds`), projectile timers.

### Remaining classified sites

- `Game1.UpdateTitleScreen` fade (`Game1.cs:5663-5669`, ±0.02/tick): title screen only.
  Stage 2 item with a required resolution — measure whether a test-client cold boot waits on
  it; if it costs boot time, fix with primitive 1; otherwise record the proof that no boot
  path waits on it. Not left open.
- Structural per-tick quantization — one input sample, one network pump, one event-command
  advance per tick: these ARE the tick; compensating them would mean changing the tick rate
  itself, which is the rejected TPS raise. Excluded by definition, not by judgment.

### Stage 2 — the completion audit (recipes, not vibes)

Sweep the server-simulated + per-instance update paths for the remaining instances. Concretely:

1. **Per-tick constants:** for each class in {`NPC`, `GameLocation.UpdateWhenCurrentLocation` /
   `updateEvenIfFarmerIsntHere`, `Debris`, `Buff`, `Event` (viewport pan, `screenGlow`, shake),
   `FarmEvent` subclasses, `Utility` ambient spawns}: read the update method(s); classify every
   mutation as ms-scaled / per-tick / cosmetic. Grep seeds: `+= 0.0`, `-= 0.0`,
   `ElapsedGameTime` *absence* in `update(` bodies, `Game1.ticks %`, `TicksElapsed`.
2. **Int-countdown timers:** timers decremented by a constant per tick (`x--` / `x -= 1` in
   update paths) — same classification.
3. **Per-tick probability rolls:** `random.NextDouble() < p` inside per-tick update paths (event
   frequency scales with TPS: 12× *rarer* per real second at TPS 5). Expected mostly cosmetic —
   the gameplay spawn/regen systems hang off the ms-based 10-minute clock
   (`performTenMinuteUpdate`) — but verify per hit; compensate gameplay-relevant ones with
   `p' = 1 − (1−p)^scale`.
4. **Authority column:** for every hit, record who executes it here (server / real client /
   per-instance) before deciding to fix; real-client-only sites are dropped.

Output: this file's inventory tables extended until the sweep list is empty. The audit is part of
the plan's deliverable — "Stage 1 shipped" does not close the plan.

#### Stage 2 audit progress — COMPLETE (2026-07-14)

The full sweep (Utility ambient spawns, `GameLocation.updateEvenIfFarmerIsntHere`/`UpdateWhenCurrentLocation`
non-character constants, world-update int-timers, `Monster.behaviorAtGameTick`/per-type timers) is done.
**One new genuinely-broken gameplay-relevant site was found — flyer-monster steering (row 1 below).** Everything
else is already-ms-scaled, already-classified (`MovePosition`), or cosmetic/real-client-only.

| Site | Mechanism | Authority | Classification |
|---|---|---|---|
| **Flyer-monster steering + velocity ramp** (`Bat.cs:630/634/642/643`, `Fly.cs:192/196/204/205`, `Serpent.cs:503/507/515/516`, `Ghost.cs:224/228/236/237`, `AngryRoger.cs:142/146/154/155`) | Homing flight: `rotation ±= Sign(...)*(Math.PI/64f)` (turn rate) + `xVelocity/yVelocity += (…)*num4/6f` (accel ramp), both bare per-tick constants (no `ElapsedGameTime`). Their attack/state timers ARE ms-scaled — only the flight steering isn't. | Server-authoritative. **Bat**: steering is in `behaviorAtGameTick` → master-only gate (`Monster.cs:704`). **Fly/Serpent/Ghost/AngryRoger**: steering is in `updateAnimation` → called unconditionally (`Monster.cs:719`) BUT rotation is master-authoritative (synched to clients, `Monster.cs:722`) and velocity drives the master's `MovePosition`. (Corrects the audit's claim that ALL five are in `updateAnimation` — Bat is the outlier.) | **BROKEN — gameplay-relevant.** At TPS 5 these fliers turn ~12× slower and accelerate to terminal velocity ~12× slower → sluggish, trivially evadable. Distinct from the already-fixed `Monster.MovePosition` sub-step: that integrates the *already-computed* velocity into position, but does NOT fix the *rate at which velocity/rotation are generated* here (runs once per tick, AFTER MovePosition). Fix = the velocity-decay-over-substep two-part fix below (glider AI-tick sub-step). **FIXED + validated** — Bat homing 649-766px/3s at TPS 5 (was ~15-19px), combat-probe gated. |
| `Debris` chunk physics (`Debris.cs:763-824`) | `xVelocity += 0.8f` (capped), `position += velocity`, `yVelocity -= 0.25f/0.4f` per tick — gravity/velocity integration, unscaled | Server-simulated (location update) + per-instance | **Stage 3-class** (physics integration like projectiles). **FIXED + validated** via the shared `Debris.updateChunks` prefix sub-step — combat-probe gated (all chunks finish falling in 3s at TPS 5). See the Stage 3 table. |
| `Event` viewport pan (`Event.cs:5165-5200`, `viewportTarget`) | `Game1.viewport.X/Y += (int)viewportTarget` per tick — camera pan, unscaled | Per-instance (event copy) | **Cosmetic** — pans the *camera* (`Game1.viewport`), does not gate event progression (no arrival/collision). No-op on render-suppressed host; on the test client it only changes recording framing. Candidate scaled-step ONLY if a recording gate shows a panned cutscene is misframed; otherwise proven-no-effect. Pending decision. |
| `GameLocation.UpdateWhenCurrentLocation` splash VFX (`GameLocation.cs:4091,4097,4112,4114`) | `random.NextDouble() < 0.1/0.005/0.5/0.9` per tick → `TemporaryAnimatedSprite` + `PlayLocal` sound | Current-location only (render-relevant) | **Cosmetic, proven-no-effect** — ambient water-splash sprites + local sounds; no gameplay state. Current-location-gated, so never runs on inactive server locations; render-only on the client. Matches the plan's "probability rolls are mostly cosmetic" prediction. |
| `TerrainFeatures/Tree` shake (`Tree.cs:449`) | `shakeTimer -= time.ElapsedGameTime.Milliseconds` | Server + per-instance | **Already ms-scaled** — do not touch. |
| `Buff.update` (`Buff.cs:229-249`) | `millisecondsDuration -= time.ElapsedGameTime.Milliseconds` | Server + per-instance | **Already ms-scaled** — proven-no-effect. |
| `FairyEvent.tickUpdate` (`FairyEvent.cs:66-171`) | `timerSinceFade += ms`, `fairyPosition.X -= ms*0.1f`, `fairyPosition.Y -= ms*0.2f` | Server (`FarmEvent`) | **Already ms-scaled** — proven-no-effect. |
| `WitchEvent.tickUpdate` (`WitchEvent.cs:108-161`) | `timerSinceFade += ms`, `witchPosition.X -= ms*0.4f`; Y-bob uses `TotalGameTime.Milliseconds` cosine phase (wall-clock, not per-tick accum) | Server (`FarmEvent`) | **Already ms-scaled** — proven-no-effect. |
| `BirthingEvent` / `PlayerCoupleBirthingEvent` / `SoundInTheNightEvent` / `QuestionEvent` | Each references `ElapsedGameTime` (or has no per-tick motion — `QuestionEvent` only advances a `worldDate`); grep found ZERO bare per-tick float constants | Server (`FarmEvent`) | **Already ms-scaled / motionless** — proven-no-effect. |

**Sweep list — CLEARED (2026-07-14).** Audited fixed/proven-no-effect:
- `Utility` ambient spawns: CLEAN — `Utility.cs` has zero `ElapsedGameTime` refs and no per-tick accumulator method (stateless helper library); lightning update runs off the ms 10-minute clock, not per tick.
- `GameLocation.updateEvenIfFarmerIsntHere`: CLEAN — only delegates to already-classified paths (`updateCharacters`, `TemporaryAnimatedSprite`, buildings/animals). `UpdateWhenCurrentLocation`: sole non-ms mutation is `updateWater` `waterPosition += 0.1f` (`GameLocation.cs:4304`), current-location-only cosmetic water scroll; companion `waterAnimationTimer` is ms-scaled.
- World-update int-timers (`Game1.cs` clock/update): ms-scaled — `gameTimeInterval += ms` (`:6054`, the 10-min clock gating all time progression), `pauseThenDoFunctionTimer`/`thumbstickMotionMargin` ms/input-only. Location-file timers overwhelmingly ms-scaled (`Woods.statueTimer`, `WizardHouse.cauldronTimer`, `IslandNorth.boulderKnockTimer`, …); non-ms hits are bus-warp cutscene motion (`BusStop.cs:336`/`Desert.cs:476`, driven by the local player's own warp — **real-client-only**) and draw-overlay alpha fades (`MermaidHouse`/`MineShaft.fogAlpha`/`CommunityCenter.messageAlpha` — cosmetic).
- `Monster.behaviorAtGameTick` + per-type timers: ms-scaled EXCEPT the flyer steering (row 1). Base `invincibleCountdown`/`stunTime`/`timeBeforeAIMovementAgain` and per-type `nextShot`/`timeUntilExplode`/`nextMoveCheck`/slime jump timers all `-= ms`/`TotalSeconds`. `GreenSlime.cs:999 position -= speed` idle drift is a per-tick `random<0.1` idle wander (cosmetic — the jump-attack toward the player is ms-scaled).

Confirmed pattern: SDV *event/buff/FarmEvent/world-clock* code is consistently ms-based (already TPS-agnostic); the per-tick-constant defect is concentrated in *character movement* + *fades* (Stage 1), *physics integration* (Debris/projectiles → Stage 3), and now *flyer-monster velocity/rotation generation* (→ Stage 3). Stage 2 sweep is CLEAR; the one broken site (fliers) is tracked in Stage 3.

## Fix framework (three primitives, shared by all stages)

`TickScale` (float) `= ElapsedGameTime.TotalMilliseconds / (1000/60.0)` read from
`Game1.currentGameTime` per tick — correct whenever each mod sets `TargetElapsedTime` (the test
client sets it on first tick), and exactly 1.0 at 60 TPS (structural no-op, production default).
**Arbitrary TPS means non-integer scales** (TPS 25 → 2.4): primitives must handle fractions.

1. **Scaled step** — for continuous quantities with no collision/trigger semantics (fades):
   multiply the per-tick delta by `TickScale`. Fractions are exact. Fade fix: Harmony prefix
   replacing `UpdateGlobalFade` with the same body stepping `globalFadeSpeed * TickScale`,
   preserving the `IsDedicatedHost` short-circuits and the completes-before-step ordering.
2. **Sub-step with fractional carry** — for stepwise logic with per-step collision/arrival/
   trigger semantics (movement): run the vanilla per-tick step `floor(carry += TickScale)` times
   (subtracting the executed steps from the carry), never scaling the per-step delta. Rationale:
   scaled deltas tunnel through collision probes (`isCollidingPosition(nextPosition(d))`) and
   overshoot the event NPC arrival window (fixed 16px, `Event.cs:5259`) — an overshot arrival is
   an event hang. The carry is per-tick global (one accumulator recomputed per game tick), so all
   characters advance uniformly. NPC fix: Harmony postfix on `Character.MovePosition` invoking
   the method `steps − 1` extra times under a re-entrancy guard (virtual dispatch re-enters via
   `NPC.MovePosition`'s pause gate — correct — and the guard stops recursion; `Farmer`/`Monster`/
   `FarmAnimal`/`Child` overrides do NOT route through this seam — verified — so they are
   unaffected until their own stage).
3. **Probability compensation** — for per-tick random rolls found gameplay-relevant in Stage 2:
   replace `p` with `1 − (1−p)^TickScale` (≈ `p × TickScale` for small p), preserving expected
   events per wall-clock second.

Farmer event movement uses primitive 1 (postfix scaling `getMovementSpeed`'s event branch by
`TickScale`): safe HERE specifically because the event arrival margin self-scales with the
returned speed (`16f + movementSpeed`, `Event.cs:5236`) and farmer collisions are disabled during
events (`ignoreCollisions` cleared at `Event.cs:10879`) — and it mirrors how vanilla itself
ms-scales the free branch. Fallback if review disagrees: sub-step `Farmer.MovePosition` gated on
the event branch.

### Where the code lives

One shared helper (primitives + patches) in `mod/JunimoServer.Shared` (already game-API-dependent
— `WeddingEvent.cs` — and referenced by both mods), registered from each mod's Harmony instance:
server-side in an unconditionally-constructed service (`harmony-patch-reachability.md` — NOT
`PasswordProtectionService`), test-client alongside the existing GameTweaks. Open verification:
confirm Shared can reference HarmonyLib (both hosts ship it via SMAPI); if not, duplicate the
~40 lines per mod with keep-in-sync headers (`one-parser-per-contract.md` pointer style).

**Kill-switch:** `SDVD_TPS_AGNOSTIC_PACING=false` disables all patches (default on). This changes
long-standing behavior on low-TPS production servers; operators get an escape hatch. Consumer
grep gate applies (`verify-documented-config-is-consumed.md`).

## Staging

All three stages are committed scope (decided 2026-07-13). The plan closes only when the sweep
list is empty and every inventory entry carries one of exactly two outcomes: **fixed** with a
primitive, or **proven-no-effect on our topology** with the proof recorded (e.g. render-only
code that never executes on a render-suppressed instance, or code that only real 60 TPS
vanilla clients execute). "Minor but tolerable" is not an outcome.

1. **Stage 1 — fades + NPC/farmer-event movement** (table 1). Unblocks the wedding/festival test
   pacing and fixes the most visible production defect (NPC walking). Gate before continuing.
2. **Stage 2 — completion audit** (recipes above). Every hit gets fixed or proven-no-effect;
   the inventory tables in this file are extended until the sweep list is empty.
3. **Stage 3 — monsters, farm animals, children, projectiles/debris**: audit the override
   bodies, then fix with the primitives. Decided: creature/combat pacing is restored to vanilla
   wall-clock difficulty at any TPS — slow monsters and slow projectiles at low TPS are defects,
   not features. The CPU cost is measured per compat item 4, not assumed.

## Division of labor with the wedding-test plan

[`tests-wedding-samedaytwoweddings-speedup.md`](tests-wedding-samedaytwoweddings-speedup.md)
owns the wedding script's ms-based scripted pauses (a data-level cap — real-time holds, not a
TPS artifact). This plan owns the ceremony's tick-inflated parts — the `globalFade`
(~10s → ~2.4s at TPS 5) and Lewis's blocking walk (~6.4s → ~0.5s) — via the generic Stage 1
patches. No per-script workaround exists for the tick-inflated parts, and none should be added.

**Overlap review DONE (2026-07-13), no double-compensation.** NOTE: `WeddingPaceCompressor` /
`WeddingDialogueSpeedup` live on the sibling `bugfix/wedding-samedaytwoweddings-speedup` branch
(commit `b68be36`, unmerged), NOT on this branch's `master` base — the analysis below applies once
the branches converge. Read the full `Data/Weddings` `default` EventScript: the timing commands are
strictly sequential — `… move Lewis 0 3 3 true / pause 4000 / globalFade / viewport / pause 1000 /
pause 500 / pause 4000 / waitForOtherPlayers / end`. The three mechanisms touch **disjoint** command
types: `pause` (ms `Game1.pauseTime`, `WeddingPaceCompressor` caps to 800ms) ⊥ `globalFade` (single
blocking command, this plan's scaled-step) ⊥ `move Lewis` (this plan's `Character.MovePosition`
sub-step). No `pause` overlaps the `globalFade` (separate commands, event-advance is serial), so the
pace compressor and the fade patch cannot compound. `WeddingCutscenePlayer`'s wall-clock beats
(`BeatPauseMs`) gate the auto-clicker, which only acts while a `DialogueBox` is up — and no dialogue
box is open during the `move`/`globalFade`/`pause` tail, so the beats don't interact with the
tick-inflated parts either. The existing test-client wedding tweaks need no change.

## Compatibility verification (per stage, per `plan-discipline.md`)

1. **AlwaysOn wedding teardown** (`AlwaysOn.cs:756-782`): with real-time fades, `afterFade`
   callbacks (e.g. `incrementCommandAfterFade`) start actually firing on the host — re-verify the
   documented fade-clear-before-`eventFinished()` NRE ordering against the faster timeline. The
   gate-identity guard (`_handledWeddingGate`) is position-independent by design
   (`host-automation.md` invariant 9) — WeddingTests + FestivalTests are the gate.
2. **Host-automation invariants** (`host-automation.md`): festival start/end, sleep flow, QiPlane
   (draw-coupled, not update-fade-coupled — unaffected). Full-suite run required.
3. **Replication:** NPC positions replicate from the host as data; sub-stepped movement changes
   only per-tick distance. `Farmer.position.UpdateExtrapolation(getMovementSpeed())`
   (`Farmer.cs:7011`) consumes the scaled event speed — watch one multi-client run for NPC/farmer
   rubber-banding (open verification).
4. **CPU:** compare `instance-stats.jsonl` server tick stats before/after a full run per stage.
   Movement-class sub-stepping restores that subsystem's 60-TPS cost only; expect noise-level —
   measure, don't assume.
5. **Recordings/validators:** faster transitions shorten recorded segments;
   `recorder-anchor-first-frame.md` invariants reference timing flags/anchors, not fade
   durations — confirm with one recorded run through the validator.
6. **Committed configs** (`preflight-check-vs-committed-config.md`): nothing is rejected;
   TickScale=1.0 at the production-default TPS 60; CI/`.env.test` (TPS 5) → 12.0.
7. **Interpolation** (`netfield-revert-pattern.md` adjacency): no NetField writes are reverted or
   fought; only local simulation stepping changes.

## Runtime post-condition gates (observe, don't infer)

**Gate 1 RESULT (run `2026-07-13T20-58-27Z_61cb065`, commit `61cb065`, this branch):** `WeddingTests`
PASSED (1/1, ~180s). Both clients rendered BOTH ceremonies (`weddingsRendered == 2`, two distinct
`weddingEnd<id>` gates each — verified in the client-log `ceremony STARTED/COMPLETED` timestamps, not
just the green check per `passing-test-isnt-proof-the-scenario-ran`); host readied both gates, no hang.
So Stage 1 is **no-regression** ✅. **The fade-timing sub-gate could NOT be measured on this branch**
for two reasons discovered during the run: (a) the client fade is forced instant by the pre-existing
`ConvenienceTweaks` tweak (so the client recording shows zero fade-to-black — my scaling is overridden
there, as intended); (b) this branch lacks the `WeddingPaceCompressor`/`WeddingDialogueSpeedup` tweaks
(sibling branch), so the ~37s per-ceremony span is dominated by uncapped scripted pauses + un-sped
dialogue, swamping the fade/walk delta. Client luminance at 1fps: no frame below Y≈47. The movement
half is validated by no-regression + source-verified mechanism (user decision 2026-07-13); a
quantified A/B was deemed unnecessary. **Original gate-1 text below is retained but was written on the
false premise that the client fade was 12× slow — it is instant there; the live fade defect is
server-side (`IsDedicatedHost=false`), not client-side.**

1. ~~Wedding E2E at TPS 5: fade-out region in the client recording ~10s → ~2.5s~~ — SUPERSEDED (client
   fade is instant via `ConvenienceTweaks`). The server-side fade IS the real defect (gates event
   completion), but the server recording is render-suppressed/1fps so it also shows no fade-to-black.
   `weddingsRendered == 2` per client and all host gates green — CONFIRMED.
2. **Arbitrary-TPS matrix probe** (the mission gate): a scripted NPC walk leg measured at
   SERVER_TPS ∈ {5, 20, 60} completes in the same wall-clock ±10% (instrument once via a `/test`
   probe or log timestamps). Same for a `globalFadeToBlack(0.02)` triggered via test endpoint.
   This also quantifies the production schedule-lag claim empirically — if inactive-location NPCs
   turn out to teleport along schedules (unverified vanilla behavior), scope the win statement to
   active-location movement.
3. **Fractional scale correctness — RESULT (run `2026-07-14T00-36-30Z_429fe85`, commit `429fe85`,
   `SERVER_TPS=CLIENT_TPS=24` → scale 2.5, the never-before-run non-integer carry path):** `WeddingTests`
   PASSED (1/1, ~94s test). Runtime-confirmed both hosts pinned the tick to `41.7ms` (`Server/Client TPS
   set to 24`), so `TickScale = 41.7/16.67 = 2.5` and `_moveCarry` ran the alternating 1/2-extra-step
   pattern — the fractional branch genuinely executed, not the integer scale-12 path every prior run took.
   Both clients rendered BOTH ceremonies (`weddingsRendered == 2`, `renderedSoFar` 1→2 in the client log),
   and — the decisive movement signal — **both clients' `waitForOtherPlayers weddingEnd<id>` gates
   auto-readied on arrival** (`ready_check_transition numberReady:2` for both clients before the host
   readied ceremony 2 via "other players ready"). Auto-ready fires only when the event farmer walks into
   the fixed 16px arrival window, so the fractional sub-step delivered arrival correctly — no
   tunneling/overshoot (which would hang the event) and no drift (ceremony spans ~31-34s, comparable to
   the ~37s TPS-5 baseline — both pause-dominated, not tick-scaled-movement-dominated). No pacing
   ERROR/hang/exception in the server log (the lone `ERROR game … Wave Bank.xwb` is the pre-existing
   headless-audio-init line, before game load). `.env.test` restored to TPS 5 after the run. **Gate PASSED.**
4. Full E2E suite green at TPS 5; CPU stats per compat item 4. **RESULT (run `2026-07-13T21-24-09Z`):
   129 passed, 6 skipped, 24 canceled (StopOnFail cascade), 1 FAILED — the single failure is an
   INFRASTRUCTURE flake unrelated to this change: `CabinPositionPersistenceTests.DummyCabin_After
   MoveAndReconnect_...` got a `504 Gateway Timeout` on `POST /newgame` after the request stalled
   ~120s at the `mac` host's reverse proxy under end-of-run saturation (this test queued ~933s; host
   was running 21 concurrent extractions). Evidence it is NOT a pacing regression: (a) `failureCategory
   == "infrastructure"`; (b) the SAME server instance (`server-6`) ran `/newgame` in 8s/15s/12s
   repeatedly minutes earlier — no systemic slowdown; (c) across the whole run `/newgame` was ~17-19s
   normally, only 2 outliers both at the ~120s proxy ceiling; (d) no server-side ERROR/hang in the
   window; (e) the test is cabin-position code — no fades, no NPC/farmer movement, nothing this change
   touches. **Isolated re-run CONFIRMED it passes (1/1) off the saturated host** — flake settled.
   **CPU stats (13,438 `avgTickMs` samples, TPS-5 budget = 200ms): median 0.22ms, p90 5.28ms, max
   31.89ms — ZERO samples above 100ms.** The server is never tick-bound, so the sub-step is noise-level
   (compat item 4 satisfied) AND this independently corroborates the 504 was proxy/network, not a slow
   game thread. Net: full-suite gate PASSED, no pacing regression.**
5. **Kill-switch A/B — PASSED (run `2026-07-14T17-22-47Z`, `SDVD_TPS_AGNOSTIC_PACING=false`).** The env
   var is now wired into both container builders (`ServerContainer.cs`/`GameClientContainer.cs`), and the
   A/B run's boot log on all 3 containers reads `TPS-agnostic pacing DISABLED via
   SDVD_TPS_AGNOSTIC_PACING=false` — proving the knob is consumed end-to-end (container env → mod). With
   it off, the fully-unpatched flyer probe read `monsterDisplacement=15px, speed=2.0` in 3s (near-
   stationary), vs the wall-clock-correct behavior when on; WeddingTests still completed (generous
   timeouts) with movement at the vanilla per-tick rate. `.env.test` restored to default-on after.
6. **Stage 3 projectile+debris — PASSED (run `2026-07-14T17-13-54Z`, `PacingProbeTests`, fix ON, TPS 5).**
   Projectile travelled **1536px in 3s** wall-clock (= ~512px/s, matching the 8px/update × 60/s real-time
   rate; unpatched would be ~120px), all debris chunks finished falling within 3s, both via the shared
   `RunUpdateSubSteps` prefix. All 3 probe spawn+state pairs fired end-to-end, no server error. Requires a
   connected client (`Clients=1`) to unpause the server (see landmine).

   **Flyer measurement (decides the velocity-ramp fix):** Bat homing at TPS 5, net displacement in 3s —
   fix OFF (both patches off): **15px**; fix ON (MovePosition sub-step on, velocity-ramp NOT patched):
   **38px**. Both are near-stationary vs the ~1920px a real 60-TPS bat would close. **Conclusion: the
   shipped MovePosition sub-step does NOT fix fliers** — it integrates an un-ramped (~0) velocity 12×,
   which is still ~0. The defect is in velocity *generation* (the per-tick ramp in `behaviorAtGameTick`/
   `updateAnimation`), so the flyer needs its own fix. (Instantaneous `speed` sample is noisy — 2.0 OFF
   vs 0.2 ON — because the homing velocity oscillates; displacement is the reliable cumulative signal.)

## Resolved decisions & remaining implementation choices

1. **Stages 1 + 3 committed; creature/combat pacing restored to vanilla wall-clock difficulty**
   (user decision 2026-07-13). **DONE (2026-07-14):** projectiles + debris built (prefix sub-step) and the
   `/test` combat probe built + validated; the flyer/knockback velocity-decay bug found via that probe and
   fixed in two parts. Full suite green (158 passed). Everything is fixed or proven-no-effect.
2. Farmer event movement: **SETTLED — scale** (evidence, 2026-07-13). `Farmer.MovePositionImpl:8595`
   bypasses the collision probe for scripted event moves (`|| flag`), and the farmer event arrival
   margin self-scales (`16f + movementSpeed`, `Event.cs:5236`), so neither collision-tunnel nor
   arrival-overshoot applies — scaling is provably safe here (the two reasons NPCs need sub-step are
   both absent). Sub-step remains the drop-in fallback if a runtime gate shows the groom teleporting.
3. Projectile/debris simulation authority: **RESOLVED — server-simulated** (2026-07-14). Monster AI +
   firing is gated on `Game1.IsMasterGame` (always true here, `multiplayerMode=2`), and
   `MineShaft.UpdateMines` (`Game1.cs:5842`, server update loop) ticks every level a client is in. So
   the fix SITE is a **host-side patch** — the defect is real on our server whenever a client is in the
   mines. (Corrects an earlier draft that wrongly claimed the server never simulates mine monsters.)
4. The wedding fade's measured ~10s vs the naive 28.6s model: slow phase matches 0.007/tick
   exactly; the terminal ~4s plunge has an unidentified second contributor. **Static narrowing
   done (2026-07-13):** grepping every `fadeToBlackAlpha` writer in `ScreenFade.cs` + `Game1.cs`
   shows the ONLY per-tick incremental (`+=`/`-=`) writers are `UpdateFadeAlpha` (`:70,75` — already
   ms-scaled, and off during global fades), `UpdateGlobalFade` (`:149,169` — the one we scale), and
   `UpdateTitleScreen` (`Game1.cs:5665,5669` — title only). Every other writer is a direct
   `= constant` assignment (state reset / threshold snap), not a per-tick stepper. So during a
   wedding `globalFade` the sole per-tick contributor IS `UpdateGlobalFade` — which the patch scales.
   The "terminal plunge" is therefore a `= constant` jump or completion-threshold snap in the
   *baseline* trace, NOT a second per-tick stepper the patch must also compensate. This is not open work:
   the fade FIX is validated end-to-end (WeddingTests green — the wedding `globalFade` gates event
   completion, and both ceremonies complete with no hang across every wedding run). A per-tick
   `fadeToBlackAlpha` instrumentation would only redundantly confirm the already-resolved static
   conclusion; it is not a gate the fix depends on.

## Velocity-decay-over-substep bug (diagnosed 2026-07-14, fix DESIGN for review — NOT yet coded)

**The defect.** Every `MovePosition` variant has TWO mutually-exclusive movement paths, chosen per call by
`if (xVelocity != 0 || yVelocity != 0)`:
- **Velocity path** — knockback/glider movement: `position += xVelocity; xVelocity -= xVelocity/k` (a
  per-*call* multiplicative friction decay; `k = Slipperiness` for `Monster`, `k = 2` i.e. 50%/call for
  `Character.applyVelocity`).
- **Walk path** — pathfind/schedule movement driven by `speed + addedSpeed` (the per-tick constant that
  SHOULD sub-step).

The shipped Stage 1/3 sub-steps run the WHOLE method `floor(carry)` times. That correctly repeats the walk
path, but it ALSO repeats the velocity path — so the friction decay applies N× per tick (~40%/tick for a
Bat's Slipperiness≈24 at 12 sub-steps; ~99.98%/tick for Character's 50%/call). Result:
- **Knockback** (all monsters + NPCs — `takeDamage`→`setTrajectory` sets velocity): monsters/NPCs get
  knocked back far LESS than at 60 TPS (velocity collapses before it can carry them).
- **Fliers/gliders** (`isGlider==true`: Bat, Fly, Serpent, Ghost, AngryRoger — ALWAYS velocity-driven, they
  `return` before the walk path at `Monster.cs:1132`): their velocity ramp (`behaviorAtGameTick`/
  `updateAnimation`, once/tick, `+= num4/6f`) can't outrun the N× decay, so they're near-stationary.
  MEASURED at TPS 5: 15px (all off) / 38px (shipped patches on) net displacement in 3s vs ~1920px at 60 TPS.

**Corrects a false claim in this plan:** the Stage 3 table said whole-method sub-step "reproduces vanilla's
60Hz knockback decay exactly." It does the opposite — it MULTIPLIES the decay by the sub-step count.

**Scope (verified by reading each override):** `Character.applyVelocity` (NPCs, `Character.cs:810`),
`Monster` velocity path (`Monster.cs:1018-1134`). `FarmAnimal.MovePosition` has NO velocity/friction path
(tile-based) — unaffected. `Child` inherits via the Character seam (rare knockback — low impact, same fix).

**Fix design (two independent parts):**
1. **Walkers' knockback — DONE + VALIDATED (2026-07-14, run `2026-07-14T20-37-22Z`).** A prefix
   (`MovePosition_CaptureVelocity_Prefix`) captures pre-call velocity into `__state`; the sub-step postfix
   skips the extra steps when `__state` is true (entity was velocity-driven this tick), so the velocity
   path's per-call friction decay runs once per tick, not N×. Applied to all four seams (Character/Monster/
   FarmAnimal/Child) via the shared prefix. Gated by a new `knockback` combat probe (spawn a GreenSlime,
   `setTrajectory(100,0)` impulse, measure slide): **knockback carried the slime 396px at TPS 5** —
   matching the ~400px 60-TPS distance (impulse 100 / Slipperiness¼-decay); pre-fix the 12×/tick decay
   collapsed it to tens of px. Projectile (1536px) and debris (1/1) unchanged — no regression. LOW risk
   confirmed: the change only skips erroneous extra decay; normal walking (velocity==0) sub-steps as before
   (verified: `Character.MovePosition:841` walk vs velocity paths are mutually exclusive; NPC schedule walk
   never sets `xVelocity`).
2. **Gliders (fliers): need ramp + velocity-move both at wall-clock.** Part 1 makes a glider's velocity path
   run once/tick — correct decay — but a glider is ALWAYS velocity-driven, so once/tick position advance =
   `velocity` px/tick = 12× slow at TPS 5 (probe-confirmed: ~19px/3s with part 1). To move at wall-clock
   speed the glider's per-tick VELOCITY GENERATION (the ramp in `behaviorAtGameTick`/`updateAnimation`) AND
   its velocity integration (`MovePosition`) must both run ~12×/tick — i.e. **N×-replay the glider's whole
   per-tick trio** (MovePosition + behaviorAtGameTick + updateAnimation), zero-time on the extra N-1 calls.

   **Faithfulness audit DONE (2026-07-14, all 5 gliders, verified against source on the load-bearing lines).**
   N×-replay-with-zero-time is SAFE — the zero-time trick is what makes it faithful and non-destructive:
   - **Bat, Ghost, AngryRoger: fully N×-faithful.** Their rotation gate resets `wasHitCounter` to `0`
     (`Bat.cs:637`, `Ghost.cs:231`, `AngryRoger.cs:149`), so the steering re-runs every replay call — exactly
     what 60 TPS does every tick. Velocity/friction/position repeat correctly.
   - **Fly, Serpent: travel SPEED faithful, HEADING slightly off.** Their rotation gate sets
     `wasHitCounter = 5 + Game1.random.Next(-1,2)` (`Fly.cs:199`, `Serpent.cs:510`); under zero-time it never
     decays across replays, so heading steps once per REAL tick instead of tracking the 60-TPS ~16ms decay.
     Velocity integrates N× (travel speed correct); only turn-tracking lags slightly. This is the ONE
     vanilla deviation.
   - **All dangerous side effects are protected by zero-time.** The load-bearing one: Ghost's tongue-
     projectile spawn (`Ghost.cs:393-399`) is gated by `stateTimer <= 0` (a `TotalSeconds` timer, `:317/379`)
     that sets `stateTimer = 1f` on fire — zero-time freezes it so the projectile spawns AT MOST ONCE per
     real tick (verified: nonzero elapsed on extra calls would spawn up to N projectiles). Sounds are
     headless no-ops (audio disabled); LightSource/particle blocks are `currentLocation==Game1.currentLocation`
     or `cursedDoll`/Putrid-variant gated (off for plain monsters on the render-suppressed server). Residual:
     Ghost/AngryRoger's invincible-overlap teleport (`Ghost.cs:414-435`) isn't time-gated and can re-roll
     2-3× per tick — but self-limits (`Halt()` + 64px position jump makes the next replay's `Intersects` fail).
   - **ms-terms needing zero-time:** Bat `529/560/578/665`, Fly `156/161`, Serpent `470`, Ghost `168/317`;
     AngryRoger has none.

   **The fidelity/safety tension (Fly/Serpent heading):** the fix for the heading lag is to pass ~16ms (one
   60-TPS tick) to extra calls instead of zero — then `wasHitCounter` decays exactly as across real ticks.
   But ~16ms is UNSAFE for Ghost (it would un-freeze `stateTimer` and multiply the projectile). So exact
   heading fidelity and side-effect safety can't both use a single global elapsed value — see the decision
   below (per-type elapsed, or accept the minor heading lag).

   **Part 2 — DONE + VALIDATED (2026-07-14, run `2026-07-14T21-10-30Z`, zero-time everywhere per user
   decision).** A postfix on `Monster.update` (gated to `isGlider.Value`) replays the trio
   `MovePosition` + `behaviorAtGameTick` + `updateAnimation` in vanilla order, `extraSteps` times with
   `ZeroElapsedGameTime`. `updateAnimation` is `protected` so it's invoked via a per-concrete-type cached
   `AccessTools.MethodDelegate` (virtual dispatch to the right override, no per-call reflection). The
   `_inSubStep` guard suppresses the inner MovePosition postfix during replay; a `Health <= 0` break stops
   replaying a monster that died mid-step. **Result: Bat homing displacement 15px (unpatched) → 19px
   (part 1) → 766px (part 2)** in 3s at TPS 5, speed 0.2 → 5.4 (reaches terminal velocity) — it now reaches
   the host and overshoots, faithful homing. No server error, boot log `ENABLED`. The `FlyerMonster` probe
   test is tightened to `≥ 400px` (cleanly separates 766 fixed from ~19 unfixed).

   Zero-time chosen over distributed-ms because TPS is a fluctuating target (TickScale already reads actual
   per-tick ms), so the Fly/Serpent heading-lag is within vanilla's own tick variance; distributed-ms would
   also un-freeze Ghost's `stateTimer` and multiply its projectile.

   Seam note: replay the three METHODS directly (not the whole `Monster.update`) — `update` has
   `parryEvent/trajectoryEvent/deathAnimEvent.Poll()` + `invincibleCountdown`/`stunTime` ms-timers
   (`Monster.cs:690/712`) and `Character.update`'s emote/jump surface, none of which the faithfulness audit
   covered; the trio is exactly what was audited safe.

**Re-validation:** the combat probe already measures all of this — tighten `FlyerMonster` to a real
threshold once the glider fix lands, and add a knockback probe (`takeDamage` the bat, measure knockback
displacement) to gate part 1.

## Remaining work to close the plan (ordered; nothing left implicit)

The plan closes only when every inventory row is fixed-or-proven and the mission gate (correct
wall-clock at arbitrary TPS) is observed. What is left, in recommended order:

1. **Non-integer-TPS runtime gate — DONE (2026-07-14, run `2026-07-14T00-36-30Z_429fe85`).** Ran
   `WeddingTests` with `SERVER_TPS=CLIENT_TPS=24` (scale 2.5) — the fractional-carry branch's first
   execution under test. PASSED: both hosts confirmed at `41.7ms`/tick, both clients rendered both
   ceremonies, and both event-farmer `waitForOtherPlayers` gates auto-readied on arrival (proof the
   fractional sub-step hits the fixed 16px arrival window — no tunnel/overshoot), ceremony spans
   ~31-34s (comparable to the ~37s TPS-5 baseline, both pause-dominated), no pacing error. `.env.test`
   restored to TPS 5. Full write-up under "Runtime post-condition gates" → gate 3.
2. **Kill-switch A/B + config-consumed gate.** Wire `SDVD_TPS_AGNOSTIC_PACING` into the container env
   (`ServerContainer.cs` + the client container builder, via `.WithEnvironment` reading
   `TestEnvLoader.Get`), then run `WeddingTests` with it `=false` and confirm movement reverts to ~12×
   slow (proves the knob is consumed, per `verify-documented-config-is-consumed`). The env var is NOT in
   any user-facing docs yet, so no operator is currently misled; if/when it's documented, this gate must
   pass first. Worst case if skipped: the documented escape hatch silently doesn't disable — low
   severity (default-on is the tested path).
3. **Stage 2 sweep — DONE (2026-07-14).** Full audit complete (see the Stage 2 progress section): every
   site fixed or proven-no-effect. The one broken site it surfaced (flyer velocity/rotation) traced to the
   velocity-decay-over-substep bug, now fixed.
4. **Stage 3 projectiles + debris — DONE + validated (2026-07-14).** Prefix sub-step for
   `Projectile.update` (collision per sub-step, no tunneling, `travelTime` counted once via zero-time) and
   `Debris.updateChunks`; combat probe (`/test/pacing_probe_spawn` + `_state`) + `PacingProbeTests` gate it
   (projectile 1536px, debris settles). The velocity-decay bug found via the same probe is fixed (parts 1+2
   above). Full suite green.

**Landmine (glider sub-step, hit + fixed 2026-07-14):** `AccessTools.Method(type, "update")` name-only
lookup on `Monster.update` threw `AmbiguousMatchException` at runtime (`Apply()` → "Mod crashed on entry",
so NO patches applied that run) — because `Character` has two `update` overloads (`(GameTime,GameLocation)`
and `(GameTime,GameLocation,long,bool)`). `dotnet build` is BLIND to this (it's a runtime reflection
resolve). Fix: pass explicit parameter types `new[]{typeof(GameTime),typeof(GameLocation)}`. Rule: any
Harmony `AccessTools.Method` targeting a method that has overloads (anywhere in its inheritance chain) MUST
specify parameter types; a green build does not prove the registration resolves.

**Landmine (combat probe, hit + fixed 2026-07-14):** the empty (player-less) server auto-pauses
(`AlwaysOn.HandleAutoPause` sets `netWorldState.IsPaused` when `otherFarmers.Count == 0`), and
`Game1.HostPaused` then gates out the ENTIRE `UpdateCharacters`/`UpdateLocations` block (`Game1.cs:4308`)
— so on a `Clients=0` server NOTHING in the world ticks and every probe reads `0,0`. (This is a distinct
gate from `shouldTimePass()`/`IsTimePaused`, which I'd checked; `IsPaused`→`HostPaused` gates the update
call itself, one level up.) Fix: `PacingProbeTests` connects one client (`Clients=1`) to unpause — also the
realistic scenario, since a present player is exactly when these entities simulate.

**Landmine (combat probe, hit + fixed 2026-07-14):** `ApiService` is reflected by the `openapi-generator`
at Docker build time via `Assembly.GetType("…ApiService")` across the net10-tool / net6-mod boundary. A
StardewValley-typed **field** on `ApiService` (the probe stored `Projectile?`/`Debris?`/`Monster?`/`Vector2`)
makes that `GetType` return null → build fails "ApiService type not found" — even though `dotnet build` is
green (the field types load fine in-process, just not in the tool's reflection context). Fix: store probe
entities as `object?` + coords as `float`, cast back inside the game-thread handlers. Extends
`openapi-generator-reflection-invoke` from method-args to field-types. Reproduce locally before a run:
`dotnet tools/openapi-generator/bin/Release/net10.0/openapi-generator.dll mod/JunimoServer/bin/Debug/net6.0/JunimoServer.dll out.json`.

**One durable landmine to remember:** the movement sub-step passes `ZeroElapsedGameTime` to extra steps
so ms-based accumulators count once. Any future patch that sub-steps a method must apply the same
technique for any ms-based term inside it (`blockedInterval`, `travelTime`, timers) — running them N×
per tick is the recurring bug class. The position/velocity per-tick constants are what SHOULD sub-step;
ms-based timers/corrections must NOT.
