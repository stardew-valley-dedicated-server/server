# Make gameplay TPS-agnostic at arbitrary TPS (fades, movement, and the tick-scaled-logic audit)

Planned 2026-07-13, no code changes yet. **Mission:** the server must produce *correct, wall-clock
gameplay* at any `SERVER_TPS` (production runs arbitrary values; 5 is the proven test/CI value, 60
the vanilla reference), and the test client must behave like a real-time client at any
`CLIENT_TPS`. Test speed is a beneficiary, not the goal. Every mechanism claim below was verified
by reading the cited decompiled source; items not yet read are listed as explicit audit tasks with
a recipe — nothing is assumed.

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
| `ScreenFade.UpdateGlobalFade()` (`ScreenFade.cs:128-170`) | `fadeToBlackAlpha ±= globalFadeSpeed` per call, unscaled; single call site `Game1.cs:3921-3923`. `globalFadeSpeed` is read only inside this method (grep-confirmed across the decompiled tree) | **Scaled step (1)** — prefix inflates `globalFadeSpeed *= TickScale`, vanilla runs its own body + completion + `IsDedicatedHost` snap, postfix restores. No private-field re-implementation. NO-OP on the render-suppressed host (fade snaps instantly there); bites on the real test client | Cutscene fades ~12× slow (wedding: ~10s measured vs ~2.4s real-time) |
| `Character.MovePosition` (`Character.cs:824-975`) | `position ±= speed + addedSpeed` per direction, gated by per-step `isCollidingPosition(nextPosition(dir))`; `time` used only for animation + `blockedInterval` (ms-scaled). `NPC.MovePosition` (`NPC.cs:3085-3092`) is a thin `movementPause` gate over `base.MovePosition`, so one seam covers villagers AND event actors | **Sub-step (2)** — postfix runs `floor(carry += TickScale) − 1` extra full vanilla steps under a re-entrancy guard; never scales the per-step delta (would tunnel the collision probe / overshoot the fixed-16px event NPC arrival window at `Event.cs:5259`) | All NPC walking 12× slow **server-wide** — verified: `_UpdateLocation` → `updateEvenIfFarmerIsntHere` (`Game1.cs:5871`) runs `updateCharacters` → `NPC.update` → `MovePosition` for EVERY location each tick (`Utility.ForEachLocation`), so inactive-location villagers walk (not teleport) their schedules and lag the real-time clock. Event choreography crawls (Lewis: 6.4s vs 0.5s) |
| `Farmer.getMovementSpeed()` event branch (`Farmer.cs:8083`) | `Max(1, speed + farmerAddedSpeed…)` per tick — unscaled (the free-move branch `:8069-8081` already ms-scales by `ElapsedGameTime.Milliseconds`) | **Scaled step (1)** — postfix scales `__result *= TickScale` only on the event branch (detected by `CurrentEvent != null && !playerControlSequence`). Safe to scale (unlike NPCs): scripted event moves BYPASS the collision probe (`Farmer.MovePositionImpl:8595` `\|\| flag` where `flag = eventUp && !isFestival && !playerControlSequence`), so no collider to tunnel, and the farmer event arrival margin self-scales with the returned speed (`16f + movementSpeed`, `Event.cs:5236`), so no overshoot. *(Plan's earlier "collisions cleared at Event.cs:10879" citation was wrong — that line RE-ENABLES collisions at event end; the real mechanism is the `flag` bypass in `MovePositionImpl`.)* | Farmer event/cutscene movement 12× slow |

### Tick-scaled, gameplay-relevant — audit the full body, then fix (Stage 3)

| Site | Verified so far | To audit before fixing |
|---|---|---|
| `Monster.MovePosition` (`Monster.cs:1011+`) | Own implementation, does NOT route through the `Character` seam (no `base.MovePosition` call). Knockback branch already self-substeps large velocities (`ceil(|v|/bbox)` loop, `:1030-1040`) — vanilla precedent for the technique | The walk portion's step mechanics; knockback decay factors; `behaviorAtGameTick` timers. Decided: fix to vanilla wall-clock combat pacing |
| `FarmAnimal.MovePosition` (`FarmAnimal.cs:2099+`) | Own implementation; `Game1.IsClient` early-return proves server-side simulation | Step mechanics, grazing/follow timers |
| `Child.MovePosition` (`Child.cs:157+`) | Own implementation, bypasses the `Character` seam (verified via override grep) | Body unread — audit then fix (farmhouse children are world NPCs like any other) |
| `Projectile.updatePosition` overrides (`BasicProjectile.cs:95-106`, `DebuffingProjectile.cs:69-80`) | `position += velocity; velocity += acceleration` per call, unscaled — a shaman fireball at TPS 5 travels at 1/12 speed (trivially dodgeable). Projectile *timers* are ms-based (`Projectile.cs:334,365,431`) | Simulation authority (host vs client-with-location); `Debris` physics same question. Fix note: sub-step the WHOLE `updatePosition` (velocity += acceleration inside each sub-step) — that reproduces vanilla's 60Hz Euler integration exactly, so trajectories match a real client bit-for-bit; scaling only the position delta would distort them |

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

1. Wedding E2E at TPS 5: fade-out region in the client recording ~10s → ~2.5s (same luminance
   method as baseline); Lewis's walk no longer a multi-second crawl; per-ceremony span drops
   ~12–14s; `weddingsRendered == 2` per client and all host gates green.
2. **Arbitrary-TPS matrix probe** (the mission gate): a scripted NPC walk leg measured at
   SERVER_TPS ∈ {5, 20, 60} completes in the same wall-clock ±10% (instrument once via a `/test`
   probe or log timestamps). Same for a `globalFadeToBlack(0.02)` triggered via test endpoint.
   This also quantifies the production schedule-lag claim empirically — if inactive-location NPCs
   turn out to teleport along schedules (unverified vanilla behavior), scope the win statement to
   active-location movement.
3. Fractional scale correctness: the TPS 20 case (scale 3.0) and an intentionally non-integer one
   (e.g. TPS 25 → 2.4) show no drift (carry accumulator keeps long-run distance exact).
4. Full E2E suite green at TPS 5; CPU stats per compat item 4.
5. Kill-switch off → 12× behavior returns (knob consumed).
6. Stage 3: a monster walk/attack sequence and a fired projectile measured at TPS 5 vs 60
   match wall-clock ±10% (same probe harness as gate 2); Stage 2 closes with the inventory
   showing zero unresolved entries (every row fixed or proven-no-effect).

## Resolved decisions & remaining implementation choices

1. **All three stages committed; creature/combat pacing fixed to vanilla wall-clock difficulty**
   (user decision 2026-07-13). Audit outcomes are restricted to fixed or proven-no-effect —
   nothing deferred, nothing "acceptable".
2. Farmer event movement: **SETTLED — scale** (evidence, 2026-07-13). `Farmer.MovePositionImpl:8595`
   bypasses the collision probe for scripted event moves (`|| flag`), and the farmer event arrival
   margin self-scales (`16f + movementSpeed`, `Event.cs:5236`), so neither collision-tunnel nor
   arrival-overshoot applies — scaling is provably safe here (the two reasons NPCs need sub-step are
   both absent). Sub-step remains the drop-in fallback if a runtime gate shows the groom teleporting.
3. Projectile/debris simulation authority: the audit determination selects the fix SITE — a
   host-side patch if server-simulated, or a recorded proof that only real 60 TPS vanilla
   clients execute the code (in which case there is no defect on any instance we run). Either
   way the inventory entry closes as fixed-or-proven, never waved off.
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
