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

**OPEN (see "Remaining work to close the plan" at the bottom):**
1. Stage 2 audit — a few sweep items still unclassified (Utility ambient spawns, world-update int-timers).
2. Stage 3 projectiles + debris — audited, NOT built; the server DOES simulate mine monsters/projectiles (master-authority), so this is a genuine deferral needing a `/test` combat probe to validate.
3. Two unexercised runtime gates — kill-switch A/B (not wired into container env) and the arbitrary/non-integer-TPS matrix (the fractional-carry path has never run in a test).

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

### Stage 3 — projectiles + debris: AUDITED, DEFERRED (not built) — necessity is marginal on this server

| Site | Mechanism (verified) | Why deferred |
|---|---|---|
| `Projectile.updatePosition` overrides (`BasicProjectile.cs:95-106`, `DebuffingProjectile`) | `velocity += acceleration; position += velocity` per call (all `NetField.Value`), unscaled — a fireball at TPS 5 travels 1/12 speed | **The server DOES simulate these** (correction — an earlier draft wrongly said it never does): mine monster AI + firing is gated on `Game1.IsMasterGame`, which is always true here (`multiplayerMode=2`, per `masterplayer-is-player-on-server`), and `MineShaft.UpdateMines` (`Game1.cs:5842`, in the server update loop) ticks every level in `activeMines` — a level is active whenever a client is in it. So a client fighting in the mines at `SERVER_TPS=5` faces server-simulated projectiles crawling at 1/12 speed (trivially dodgeable) — a **real gameplay defect**. Deferred anyway because: (1) **collision is checked OUTSIDE the physics step** — `Projectile.update` calls `updatePosition` then `isColliding` ONCE per tick, so sub-stepping only `updatePosition` still tunnels; a correct fix must sub-step the collision-bearing slice of `update` and count its `travelTime += ms` grace-period timer once (the zero-time technique); (2) **no E2E test spawns a monster/projectile and there is no `/test` combat probe**, so there is no runtime gate to validate a fix per this plan's own gate standard. This is a genuine deferral (build a combat probe + fix), NOT a proven-no-effect. |
| `Debris` chunk physics (`Debris.cs:763-824`) | `xVelocity += 0.8f` (capped), `position += velocity`, `yVelocity -= 0.25f/0.4f` per tick — gravity/Euler, unscaled | Physics + pickup + bounce checks are all self-contained in `updateChunks` (no external-collision tunneling, unlike projectiles — more tractable). Still DEFERRED for the same reason (3): no test exercises debris landing/pickup at low TPS, so no runtime gate. Debris settles ~12× slower at TPS 5 (item drops drift longer before resting) — a real but low-severity cosmetic-adjacent effect. |

**TODO (concrete, per `holistic-or-explicit-todo`):** to close projectiles+debris, add a `/test/spawn_monster`
+ `/test/fire_projectile` + `/test/drop_debris` endpoint set and a WeddingTests-style measurement test
(walk/fire at TPS 5 vs 60, ±10% wall-clock), then implement: Projectile = sub-step the collision-bearing
slice of `Projectile.update` (collision checked per sub-step, `travelTime` counted once via the same
zero-time technique); Debris = sub-step `updateChunks` (self-contained, zero-time for any ms terms). Until
that harness exists these stay audited-not-fixed — mechanism known, fix shape known, blocked on validation.
| `Debris` chunk physics (`Debris.cs:763-824`) | *(promoted from Stage 2 audit)* `xVelocity += 0.8f` (capped 8f), `position += velocity`, `yVelocity -= 0.25f/0.4f` per tick — gravity/velocity Euler integration, unscaled | Same as projectiles — sub-step the whole chunk update (velocity accumulation inside each sub-step). Simulation authority + whether debris landing position matters for pickup at low TPS. Gameplay-relevant (debris = collectible drops) |

**Stage 3 audit progress (2026-07-13):** Verified `Monster.MovePosition` (`Monster.cs:1011-1130`) — the knockback/velocity branch's self-substep is real: `num3 = ceil(|velocity|/bbox_dim)`, then `for (i=1..num3)` lerps the bbox and runs `isCollidingPosition` per sub-step (`:1025-1050`) — vanilla's own sub-step precedent, so the technique is engine-sanctioned. The walk-portion step (the non-velocity movement, further in the body) is the part still needing the Stage 1 sub-step primitive; `slideAnimationTimer -= ElapsedGameTime.Milliseconds` (`:1073`) is already ms-based. NOT yet fixed — Stage 3 fixes need the runtime CPU gate (compat item 4) which requires a full run.

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

#### Stage 2 audit progress (2026-07-13, in progress — NOT yet complete)

| Site | Mechanism | Authority | Classification |
|---|---|---|---|
| `Debris` chunk physics (`Debris.cs:763-824`) | `xVelocity += 0.8f` (capped), `position += velocity`, `yVelocity -= 0.25f/0.4f` per tick — gravity/velocity integration, unscaled | Server-simulated (location update) + per-instance | **Stage 3-class** (physics integration like projectiles) — sub-step the whole update. Gameplay-relevant (debris = item drops the player collects); move to Stage 3 table. NOT yet fixed. |
| `Event` viewport pan (`Event.cs:5165-5200`, `viewportTarget`) | `Game1.viewport.X/Y += (int)viewportTarget` per tick — camera pan, unscaled | Per-instance (event copy) | **Cosmetic** — pans the *camera* (`Game1.viewport`), does not gate event progression (no arrival/collision). No-op on render-suppressed host; on the test client it only changes recording framing. Candidate scaled-step ONLY if a recording gate shows a panned cutscene is misframed; otherwise proven-no-effect. Pending decision. |
| `GameLocation.UpdateWhenCurrentLocation` splash VFX (`GameLocation.cs:4091,4097,4112,4114`) | `random.NextDouble() < 0.1/0.005/0.5/0.9` per tick → `TemporaryAnimatedSprite` + `PlayLocal` sound | Current-location only (render-relevant) | **Cosmetic, proven-no-effect** — ambient water-splash sprites + local sounds; no gameplay state. Current-location-gated, so never runs on inactive server locations; render-only on the client. Matches the plan's "probability rolls are mostly cosmetic" prediction. |
| `TerrainFeatures/Tree` shake (`Tree.cs:449`) | `shakeTimer -= time.ElapsedGameTime.Milliseconds` | Server + per-instance | **Already ms-scaled** — do not touch. |
| `Buff.update` (`Buff.cs:229-249`) | `millisecondsDuration -= time.ElapsedGameTime.Milliseconds` | Server + per-instance | **Already ms-scaled** — proven-no-effect. |
| `FairyEvent.tickUpdate` (`FairyEvent.cs:66-171`) | `timerSinceFade += ms`, `fairyPosition.X -= ms*0.1f`, `fairyPosition.Y -= ms*0.2f` | Server (`FarmEvent`) | **Already ms-scaled** — proven-no-effect. |
| `WitchEvent.tickUpdate` (`WitchEvent.cs:108-161`) | `timerSinceFade += ms`, `witchPosition.X -= ms*0.4f`; Y-bob uses `TotalGameTime.Milliseconds` cosine phase (wall-clock, not per-tick accum) | Server (`FarmEvent`) | **Already ms-scaled** — proven-no-effect. |
| `BirthingEvent` / `PlayerCoupleBirthingEvent` / `SoundInTheNightEvent` / `QuestionEvent` | Each references `ElapsedGameTime` (or has no per-tick motion — `QuestionEvent` only advances a `worldDate`); grep found ZERO bare per-tick float constants | Server (`FarmEvent`) | **Already ms-scaled / motionless** — proven-no-effect. |

Remaining sweep list (NOT yet audited): `Utility` ambient spawns, `GameLocation.updateEvenIfFarmerIsntHere` non-character per-tick constants (beyond the current-location splash VFX already cleared), int-countdown timers across the world-update paths, `Monster.behaviorAtGameTick` timers (overlaps Stage 3). Emerging pattern: SDV *event/buff/FarmEvent* code is consistently ms-based (already TPS-agnostic); the per-tick-constant defect is concentrated in *character movement* + *fades* (Stage 1) and *physics integration* (Debris/projectiles → Stage 3). Stage 2 does NOT close until every entry lands fixed-or-proven.

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
3. Fractional scale correctness: the TPS 20 case (scale 3.0) and an intentionally non-integer one
   (e.g. TPS 25 → 2.4) show no drift (carry accumulator keeps long-run distance exact).
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
5. Kill-switch off → 12× behavior returns (knob consumed). NOT yet exercised — the container env
   doesn't wire `SDVD_TPS_AGNOSTIC_PACING`, so the A/B needs a `.WithEnvironment` addition first.
6. Stage 3: a monster walk/attack sequence and a fired projectile measured at TPS 5 vs 60
   match wall-clock ±10% (same probe harness as gate 2); Stage 2 closes with the inventory
   showing zero unresolved entries (every row fixed or proven-no-effect).

## Resolved decisions & remaining implementation choices

1. **Stages 1 + Stage-3-creature-movement committed; creature/combat pacing being restored to vanilla
   wall-clock difficulty** (user decision 2026-07-13). Update (2026-07-14): projectiles + debris are a
   genuine open deferral (real server-side defect, blocked on a `/test` combat harness to validate) —
   see "Remaining work" item 4. This is NOT "acceptable"/waved-off; it's tracked open work with a
   concrete recipe. Everything else is fixed or proven-no-effect.
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
   *baseline* trace, NOT a second per-tick stepper the patch must also compensate. The remaining
   **runtime gate** (instrument `fadeToBlackAlpha` per tick in one wedding run) is now a confirmation
   of this static conclusion, not an open mechanism question — deferred to the test-run phase.

## Remaining work to close the plan (ordered; nothing left implicit)

The plan closes only when every inventory row is fixed-or-proven and the mission gate (correct
wall-clock at arbitrary TPS) is observed. What is left, in recommended order:

1. **Non-integer-TPS runtime gate (highest value — the one untested code path).** Every test so far ran
   at `SERVER_TPS=5` = integer scale 12.0; the fractional-carry branch (`_moveCarry` keeping ~0.4
   remainders) has NEVER executed under test. Re-run `WeddingTests` (and ideally a schedule-walk probe)
   at a non-integer `SERVER_TPS` (e.g. 24 → scale 2.5) and confirm NPC/Lewis movement completes in the
   same wall-clock ±10% with no drift/jitter. Worst case if skipped: at a non-integer production TPS,
   NPCs walk subtly wrong. Cheapest close: one `make test-llm FILTER=WeddingTests` run with
   `SERVER_TPS`/`CLIENT_TPS` overridden to 24.
2. **Kill-switch A/B + config-consumed gate.** Wire `SDVD_TPS_AGNOSTIC_PACING` into the container env
   (`ServerContainer.cs` + the client container builder, via `.WithEnvironment` reading
   `TestEnvLoader.Get`), then run `WeddingTests` with it `=false` and confirm movement reverts to ~12×
   slow (proves the knob is consumed, per `verify-documented-config-is-consumed`). The env var is NOT in
   any user-facing docs yet, so no operator is currently misled; if/when it's documented, this gate must
   pass first. Worst case if skipped: the documented escape hatch silently doesn't disable — low
   severity (default-on is the tested path).
3. **Stage 2 sweep — finish the audit.** Remaining unclassified per-tick sites: `Utility` ambient
   spawns, `GameLocation.updateEvenIfFarmerIsntHere` non-character per-tick constants, world-update
   int-countdown timers. Classify each fixed / proven-no-effect (most SDV world code has been ms-based
   so far — expect mostly proven-no-effect). Extend the Stage 2 table until the sweep list is empty.
4. **Stage 3 projectiles + debris (genuine deferral — real defect, needs a harness).** The server DOES
   simulate mine monsters/projectiles (resolved above), so a fireball at low TPS really crawls for a
   client in the mines. To close: (a) add a `/test/spawn_monster` + `/test/fire_projectile` +
   `/test/drop_debris` endpoint set and a measurement test (fire at TPS 5 vs 60, ±10% wall-clock); then
   (b) implement — Projectile: sub-step the collision-bearing slice of `Projectile.update` (so
   `isColliding` runs per sub-step, no tunneling) with `travelTime += ms` counted once via the zero-time
   technique; Debris: sub-step `updateChunks` (self-contained — physics+pickup+bounce all in one loop —
   zero-time for any ms terms). The creature-movement half already fixes the monsters' *walking*; only
   their *projectiles* remain slow.

**One durable landmine to remember:** the movement sub-step passes `ZeroElapsedGameTime` to extra steps
so ms-based accumulators count once. Any future patch that sub-steps a method must apply the same
technique for any ms-based term inside it (`blockedInterval`, `travelTime`, timers) — running them N×
per tick is the recurring bug class. The position/velocity per-tick constants are what SHOULD sub-step;
ms-based timers/corrections must NOT.
