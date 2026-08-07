# Speed up `TwoFarmhandNpcWeddings_SameDay_BothCompleteWithoutHangingHost`

## Status — IMPLEMENTED + REVIEWED (2026-07-14), unpushed, merge-ready

Parts A + C + D + B are implemented, adversarially reviewed (incl. a final pass verifying every
load-bearing claim against decompiled sources), and E2E-verified on HEAD. All open-before-merge items
resolved. **Not pushed.**

- **Branch/worktree:** `bugfix/wedding-samedaytwoweddings-speedup` in
  `../server-worktrees/wedding-speedup` (base `master` @ `d98e3b9`). Three commits:
  `13e9c1f` (test-client: Parts A/C/D), `79bfb57` (test infra: Part B), and `2f1290d` (test-client:
  linger-guard comment correction). All build clean; the commits touch **only `tests/`** (nothing
  under `mod/`).
- **Files:** Part A/C/D — `tests/test-client/GameTweaks/WeddingPaceCompressor.cs` (new),
  `WeddingDialogueSpeedup.cs` (new), `WeddingCutscenePlayer.cs` (final-beat + linger fixes),
  `ModEntry.cs`. Part B — `Helpers/ConnectionHelper.cs` (join gate acquire/release moved into
  `JoinWorldCoreAsync`, released at approval), `TestBase.cs`, `Infrastructure/Fixture/`
  `ConnectionRetryHelper.cs` + `FarmerTestHelper.cs`, `Infrastructure/ManagedServer.cs`, `WeddingTests.cs`.
- **Measured improvement — controlled same-config A/B at `SERVER_TPS=5` (mac host, baseline `d98e3b9`
  vs branch):** WeddingTests body **112.3s → 86.8s (−23%)**; both ceremonies **81s → 55s (−32%)**;
  concurrent-2nd-farmhand join-gate wait **7.7s → 6.4s**. Baseline reproduced this plan's stated
  numbers exactly, validating the setup as comparable.
- **Regression checks:** baseline full suite **154/154** @ TPS=5; branch full suite passed **154/154**
  once (at TPS=60) and passed 97-then-infra-504 (`SaveImportTests`, a class these changes don't touch)
  at TPS=5 before a StopOnFail cascade — treated as a remote-mac transport flake, not a code signal.
  All join-gate waits stayed bounce-free (`bounce:0`); gate hold narrowed to pre-approval as designed.
- **Final-review run (2026-07-14, on HEAD `79bfb57` @ TPS=5, remote mac):** `WeddingTests` **1/1**,
  testBody **86.29s** (matches the A/B branch figure). Scenario confirmed from artifacts, not the green
  checkmark (`passing-test-isnt-proof-the-scenario-ran.md`): server log shows **two** distinct
  `Readied wedding wait gate` lines, both via the honest `other players ready` path; both clients report
  `renderedSoFar=2`; the fixed final beat fires **2×/client** (was 0 in baseline); pace transform capped
  8 segments (script not drifted); no pause-swallow (ceremony 2 = 28s ≥ ceremony 1 = 27s). Both projects
  build clean on HEAD.
- **Open-before-merge items — all resolved this review:** (1) run-on-HEAD confirmed (above). (2)
  `.env.test` verified at `SERVER_TPS=5`. (3) no sibling-plan leakage — diff touches zero `mod/` files
  and carries no `TpsAgnosticPacing.Apply`.

The combined ~50–55s target requires the sibling
[`tick-scaled-pacing-fades-movement.md`](tick-scaled-pacing-fades-movement.md) Stage 1 (owns the
ceremony's tick-inflated fade + walk), which is a **separate branch** and not part of this work.

---

## Design (as investigated 2026-07-13)

Grounded in run `TestResults/runs/2026-06-28T19-16-38Z_864566d` (commit `864566d`): `server-0`,
`client-0` (farmhand B, Penny), `client-3` (farmhand A, Abigail). Evidence: the run's
`diagnostics/infrastructure.jsonl`, client container logs, per-frame luminance analysis of
`client_recording.mp4` (1 fps), the decompiled game sources, and
`decompiled/content-1.6.15-24356/Data/Weddings.json`.

**Verdict:** testBody 111.2s → ~73–76s with Parts A–D, → ~50–55s combined with the sibling plan's
Stage 1. All parts committed scope — no optional items remain (decisions resolved 2026-07-13: Part D
included, final beat fixed, `BeatPauseMs` stays 1500).

## Measured timeline (baseline run)

| Phase | Wall-clock | Cost |
|---|---|---|
| Farmhand A join (gate wait 0) | 19:29:15.10 → 19:29:22.66 | 7.6s |
| Farmhand B join (gate wait **7.56s** = A's whole join) | 19:29:22.66 → 19:29:29.69 | 7.0s |
| Engagements + replication poll | → 19:29:31.9 | 2.2s |
| Sleep + day transition | → ~19:29:39 | ~7s |
| Ceremony 1 (both clients render) | 19:29:39 → 19:30:20 | 41s |
| Ceremony 2 | 19:30:21 → 19:30:59 | 38s |
| Host-side gates + married asserts | → 19:31:03 | ~3s |

---

## Part A — Cap the ceremony's scripted pauses (client-side data edit; ~25s)

### Anatomy of one 41s ceremony at CLIENT_TPS=5 (code-verified + video-verified)

The wedding script is `Data/Weddings` `EventScript["default"]`
(`decompiled/content-1.6.15-24356/Data/Weddings.json`); each instance builds its OWN event
copy from its OWN content (`Utility.getWeddingEvent` → `DataLoader.Weddings(Game1.content)`,
`Utility.cs:2252-2279`). Timing semantics split into two classes because the client pins
`TargetElapsedTime = 1000/CLIENT_TPS` (`tests/test-client/ModEntry.cs:167`), so
`ElapsedGameTime` = 200ms/tick at TPS 5:

- **Millisecond-based = real-time at any TPS**: `pause N` (`Game1.pauseTime`, decremented by
  real ms in `updatePause`, `Game1.cs:5398`, advancing the event on expiry at `:5459`),
  `Event.Speak`'s 500ms per-box throttle (`Event.cs:171-175`), `DialogueBox.safetyTimer`
  750ms (`DialogueBox.cs:60,773`), non-continue `faceDirection`'s 500ms holds
  (`Event.cs:559-562`).
- **Per-tick = 12× slower than a real 60 TPS client**: `globalFade` alpha steps per tick
  (`ScreenFade.UpdateGlobalFade`, `ScreenFade.cs:128-170`), NPC walking px/tick
  (`move Lewis 0 1 0`).

| Component | Cost | Class |
|---|---|---|
| 11 scripted `pause` commands (4000+500+2000+1000+500+1000+2000+4000+1000+500+4000) | 20.5s | real-time scripted holds — **this plan** |
| Blocking `move Lewis 0 1 0` (64px at ~2px/tick) | ~6.4s | tick-inflated — sibling plan |
| Blocking `globalFade` (no speed arg) | ~10s measured | tick-inflated — sibling plan |
| 4 `speak` + 2 `message` boxes (throttle + safetyTimer + clicks) + `faceDirection` holds | ~8s | real-time engine constants (Part D can trim the safetyTimer share) |
| WeddingCutscenePlayer "couple assembled" beat | 1.5s | test beat (Part C) |
| Gate sync | ~0.2s | — |

Video luminance confirms the split: Town cutscene body ~24s, fade-out ~10s, black epilogue
(post-fade pauses + 2 `message` boxes) ~8s. The tick-inflated components (fade + walk,
~12–16s/ceremony) are owned by
[`tick-scaled-pacing-fades-movement.md`](tick-scaled-pacing-fades-movement.md), whose
TPS-agnostic fades/movement restore real-client pacing generically. This plan owns only the
ms-based scripted pauses, which no TPS change can touch.

### The change: a `Data/Weddings` transform in the test-client mod

New GameTweak (e.g. `tests/test-client/GameTweaks/WeddingPaceCompressor.cs`) subscribing
`helper.Events.Content.AssetRequested` for `Data/Weddings`, editing
`StardewValley.GameData.WeddingData.EventScript` values only (never `Attendees`). Split each
script on `/` and rewrite only segments exactly matching `pause <int>` →
`pause min(<int>, 800)`: 20.5s → 7.9s per ceremony (−12.6s; −~25s across both, since the
clients render concurrently and client-side savings are wall-clock 1:1).

Unmatched segments pass through untouched, so a future script change degrades to vanilla
pacing (slower test), never a broken test. Log one Trace line with the number of rewritten
segments so a defeated transform is visible in the client log.

Projected per-ceremony span from the cap alone: 41s → ~28s. Parts C–D and the sibling
plan's Stage 1 cut further — combined target ~12–16s (see the verdict).

### Why the test's value survives

- Every command still executes through the vanilla `Event` machinery — nothing is skipped,
  which is the line the original deadlock bug was about. All regression targets are
  structural: both ceremonies fire, each client plays its copy to the
  `waitForOtherPlayers weddingEnd<id>` gate (`weddingsRendered` latches on gate-reached —
  unchanged), the host participates/resets/starts ceremony 2, teardown + marriages assert
  as before.
- Weddings are per-instance unsynced (the gate is the only sync point), so per-instance
  pacing divergence is the existing design — the render-suppressed host already diverges
  massively.
- **No host-side floor**: `AlwaysOn.HandleWeddingEvent` runs per-tick and readies the host
  the moment `OthersReadyForWedding(gate)` is true (`AlwaysOn.cs:737-749`), so faster
  clients pull gate completion earlier 1:1. Today every ceremony ends via the 20s wall-clock
  backstop because clients take ~41s; with shorter ceremonies the gate ends via the honest
  "other players ready" path (or a near-tie with the backstop — both proven today). The
  backstop stays as-is: it is stall protection, and its firing order is immaterial to the
  clients' own gate completion.
- Only WeddingTests uses weddings; the server mod's copy of the script is untouched (the
  host's own pacing is irrelevant — it force-ends its copy on ready).
- The pause cap is a genuine pacing deviation (a real client holds the 4s beats), accepted
  with eyes open: no assertion or vanilla code path depends on pause durations — the gate,
  not elapsed time, is the sync point.

---

## Part B — Narrow the join gate to release at approval (~2.4s here, plus suite-wide convoy relief)

### Measured join breakdown (infrastructure.jsonl)

| Phase | A | B |
|---|---|---|
| Gate wait | 0 | **7.56s** (A's whole join) |
| LAN connect → farmhand list | ~3.9s | ~3.4s |
| Slot select → approval (`character_menu_detected`) | ~1.2s | ~0.9s |
| Character creation + world-ready tail | ~2.5s | ~2.8s |

### Verified mechanism (decompiled)

- The farmhand request is sent at slot-select (`FarmhandMenu.FarmhandSlot.Activate` →
  `sendPlayerIntroduction`, `FarmhandMenu.cs:43-54`); approval is `checkFarmhandRequest`
  (`GameServer.cs:522`). Character customization opens AFTER approval, in-world, on a
  client-authoritative farmer root (`Client.cs:226-229`) — a mid-customization client is
  indistinguishable server-side from any connected player.
- `isGameAvailable()` (`GameServer.cs:459-470`) has NO "another client joining" condition.
  Concurrent-join interference is strictly pre-approval: same-slot pick ("already in use")
  and the farmhand-deletion race (`TryAssignFarmhandHome` → `Cabin.AssignFarmhand` deletes
  an unclaimed owner, `NetWorldState.cs:781-809`, `Cabin.cs:92-104`) — the incident behind
  `NetworkTweaker.CheckFarmhandRequest_SafeLookup_Prefix` (`NetworkTweaker.cs:87-108`).
- Blank farmhands are created pre-homed to their cabin (`Cabin.CreateFarmhand`,
  `Cabin.cs:46-66`), so on fresh cabins the deletion race needs post-day-transition
  home-loss — rare but real; it is why the pre-approval phase must stay serialized.

### The change

Move gate acquisition from the three call sites (`ConnectionRetryHelper.cs:82/147`,
`FarmerTestHelper.JoinSecondFarmerAsync:229`) into `JoinWorldCoreAsync`: acquire per
attempt before `connectOnceAsync`, release (try/finally) at the approval point —
character-menu detection for uncustomized slots (the bounce-retry loop stays under the
gate), world-ready for customized slots. The creation/world-ready/login tail then overlaps
the next join.

- Deterministic: each gate-holder's farmhand list is generated after the previous approval —
  no stale-list races, no reliance on bounce-retry as the normal path.
- Rejected alternative: gating only select→approval with concurrent connects (~6.5s here)
  reintroduces the stale-list races the gate exists to prevent — probabilistic bounces as
  the normal path.
- No other consumer: only the three join paths acquire the gate (grep-verified). The
  broker's convoy logic (`TestResourceBroker.cs:2766-2783`) treats the gate as a capacity
  cost, not a correctness contract — shorter holds also shave ~2.5–3s per queued join on
  end-of-run convoys (estimate; verify on a full run per `test-timing.md`).

### Comment fix (do regardless)

`FarmerTestHelper.cs:134-143` and `WeddingTests.cs:119-125` claim the gate serializes "only
the game-thread join"; the run proves B waited A's entire join (7558ms `ManagedServer_JoinGate`
wait). Rewrite to match the implemented boundary.

---

## Part C — Test-client beat fixes (~1–3s + correctness)

1. **The "marriage pronounced" final beat never fires** (verified: no such log line in
   either client log; only "couple assembled" + post-warp lingers appear).
   `IsAtFinalDialogue` (`WeddingCutscenePlayer.cs:268-278`) requires a `DialogueBox` up at
   `CurrentCommand >= length-2`, but by the wait gate all boxes are closed and
   `ReadyCheckDialog` is not a `DialogueBox`. **Fix the predicate** (decided): fire on the
   last `speak` command's box — find the last `speak` index in `eventCommands`. Video
   readability is the beats' purpose, and with capped pauses the recording gets denser.
   Cost +1.5s/ceremony, accepted.
2. **Suppress the between-ceremony post-warp linger**: `PauseAfterWeddingWarp` sets
   `Game1.pauseTime = 1500` after every ceremony exit warp, but `Game1.pauseTime` is the SAME
   global the script's `pause` command uses — a linger active when ceremony 2's first `pause`
   executes swallows that pause (its expiry advances the event, `Game1.cs:5457-5460`).
   Empirically visible in the baseline: ceremony 2 ran ~3s shorter than ceremony 1. Gate the
   linger on `Game1.weddingsToday is not { Count: > 0 }` (the guard
   `AlwaysOn.WarpHostHomeAfterWeddings` uses) so it can't fire while another ceremony is still
   queued. Confirmed at runtime: with the guard, ceremony 2 (28s) is no longer shorter than
   ceremony 1 (27s) — the swallow is gone.
3. `BeatPauseMs` stays 1500 (decided) — beat visibility in the recordings outranks the ~1s
   a trim would save. No change; recorded here so it isn't re-proposed.

---

## Part D — Zero `DialogueBox.safetyTimer` during weddings (~9–10s; committed, decided 2026-07-13)

Vanilla itself zeroes this timer for automated participants:
`safetyTimer = ((!Game1.IsDedicatedHost) ? 750 : 0)` (`DialogueBox.cs:462`; more
dedicated-host pacing exemptions at `:775`). The test client is exactly such an automated
participant, so a Harmony patch (established GameTweaks pattern) zeroing `safetyTimer` when
`Game1.CurrentEvent?.isWedding == true` mirrors sanctioned engine behavior rather than
inventing new timing. 6 boxes × ~0.8s ≈ 4.8s/ceremony (~9.5s both).

Accepted costs (decided with the inclusion):
- Boxes then live ~2 ticks (~0.4s); at CLIENT_FPS=1 recording, dialogue text will mostly not
  appear in video frames — the beats (Part C), not dialogue text, are the recordings'
  readability anchor.
- One more engine-behavior patch to maintain (scoped to weddings, code not data).
- `Event.Speak`'s separate 500ms throttle (`Event.cs:171`) keeps a ~0.5–0.7s per-box floor
  and stays untouched: it is event-machinery pacing shared by every instance, not an input
  guard with a vanilla automation exemption — no sanctioned precedent to mirror, unlike the
  safetyTimer.

---

## Explicitly rejected (each verified, not assumed)

- **Raising CLIENT/SERVER_TPS (globally or runtime)**: the dominant ceremony cost is
  real-time scripted pauses, which TPS cannot touch; a raise splits the pooled-client +
  proven-stable TPS=5 config (`server-tps-headless.md`) and CI parity. The tick-inflated
  remainder is the sibling plan's job via targeted patches, not a TPS change.
- **Clock speed**: time is frozen during weddings (`isWedding`); already used for the
  sleep-through.
- **Skipping/fast-forwarding the event**: the original deadlock bug.
- **Compressing the day transition (~7s)**: real `newDaySync` + `queueWeddingsForToday`
  across 3 peers — that path IS the scenario; only compressible via SERVER_TPS (rejected).
- **Full-overlap join** (gate only around select→approval): see Part B.
- **Host-side mod changes**: `HandleWeddingEvent` teardown is correct and load-bearing
  (`host-automation.md` invariant 8); the between-ceremony Farm hop is required for
  `StartNextQueuedWeddingIfIdle` (invariant 9). Final host landing already correct
  (`WarpHostHomeAfterWeddings`).
- **`Polling_TestBase_PostTransitionSettle` (4s)**: overlaps ceremony 1 server/client-side —
  not on the critical path.

## Implementation order + runtime gates (`runtime-post-conditions-are-gates.md`)

1. **Parts A + C + D together** (all test-client; one WeddingTests run validates all three):
   - both clients report `weddingsRendered == 2`; per-ceremony span in client logs ~25s ±4s
     (~12–16s if the sibling plan's Stage 1 has landed);
   - server log "Readied wedding wait gate" shows `other players ready` (or backstop
     near-tie — either is fine, both proven);
   - the fixed final beat fires twice per client (log line); the linger guard holds — no
     between-ceremony pause-swallow, so ceremony 2 is not shorter than ceremony 1 (the linger
     itself fires per post-ceremony warp, not once per ceremony, which is harmless);
   - wedding dialogue boxes advance within ~2 ticks (D active), and a non-wedding dialogue
     interaction elsewhere in the suite still pays the vanilla safetyTimer (D correctly
     scoped);
   - visual pass over both client recordings: assembled-couple + pronounced beats, kiss,
     fade, Farm arrival all still readable;
   - client log shows the transform's segment count (script not drifted).
2. **Part B** (shared infra, separate commit) → run WeddingTests + the KeepConnected-heaviest
   classes; verify B's `ManagedServer_JoinGate` wait ≈ A's connect+approval (~5s) not A's
   full join, zero `join_bounce` lines with bounce > 0, and no new bounce/convoy regressions
   in a full-suite run.

## Adversarial self-check (`adversarial-review-split-findings.md`)

- **Settled by runtime evidence**: join serialization + phase split (JSONL), ceremony
  anatomy (video luminance + script + command sources), the dead final beat and the
  linger-eats-pause interaction (client logs: no "marriage pronounced" line; ceremony 2 ~3s
  shorter), no-host-floor (code: per-tick others-ready path).
- **Decisions resolved by the user 2026-07-13** (none remain open): Part D included; final
  beat fixed (not deleted); `BeatPauseMs` stays 1500.
- **Known interactions preserved**: 20s backstop unchanged; `CeremonyEndWarpWindowMs` (4s)
  still covers the exit warp; `CeremoniesResolveTimeout` (240s) untouched — generous
  headroom is intentional for genuine-hang detection.
