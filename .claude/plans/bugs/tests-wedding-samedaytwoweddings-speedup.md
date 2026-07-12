# Speed up `TwoFarmhandNpcWeddings_SameDay_BothCompleteWithoutHangingHost`

Review-only investigation (no edits applied). Grounded in run
`TestResults/runs/2026-06-28T19-16-38Z_864566d` (commit `864566d`, the current `wip`): `server-0`,
`client-0` (farmhand B, married Penny), `client-3` (farmhand A, married Abigail). This supersedes the
stale analysis in `wedding-host-final-location-and-test-duration.md`, whose timings predate two landed
changes (host home-warp; render-recorded-on-completion).

## Measured timeline (this run)

| Phase | Wall-clock | Cost | Note |
|---|---|---|---|
| Both clients leased (`client_acquired` ×2) | `19:29:15.096` | — | lease/cold-start overlaps ✓ |
| Farmhand A join (`join_attempt_started`→`connect_completed`) | `19:29:15.10 → 19:29:22.66` | ~7.5s | join gate held whole time |
| Farmhand B join | `19:29:22.66 → 19:29:29.69` | ~7s | **starts only at A's `connect_completed`** |
| `Polling_Wedding_EngagementReplicated` | — | 1.5s | |
| Ceremony 1 render (both clients) | `19:29:39 → 19:30:20` | ~41s | host readies via **backstop** 19:30:19.68 |
| Ceremony 2 render (both clients) | `19:30:21 → 19:31:00` | ~39s | host readies via **backstop** 19:30:59.08 |
| `Polling_Wedding_BothClientsRenderedBoth` | succeeds `19:31:00.1` | **79.9s** | the long pole (391 iters) |
| `BothCeremoniesRan` / `HostRecovered` / `HostReturnedHome` | — | 2.5s / 0.2s / 0.2s | now trivial |
| **`testBodyMs`** | — | **111.2s** | `activeDurationMs` 118.1s |

Evidence pointers:
- Join serialization: `diagnostics/infrastructure.jsonl` — B's `ManagedServer_JoinGate` `wait` event is
  `{"phase":"completed","durationMs":7558}`; B's `join_attempt_started` ts == A's `connect_completed` ts
  (`19:29:22.655`).
- Backstop endings: `server-0/container.log` — both "Readied wedding wait gate […] **(wall-clock
  backstop)**" lines; `ready_check_transition` for each gate shows the host's own ready
  (`numberReady` 1/3) lands ~0.2s **before** the clients' (2/3, 3/3).
- Render is real: `client-3/container.log` ceremony 1 is a single span `19:29:39 ceremony STARTED` →
  `19:30:21 Warping to Farm` with no idle gap.

---

## The four hints — findings

### Hint 1 — "clients join sequentially, should join in parallel": CONFIRMED, real, the biggest *safe* win — but the fix needs one verification before it's safe to apply.

`Farmers.ConnectBothConcurrentlyAsync` (`FarmerTestHelper.cs:152`) overlaps only the client **leasing**.
The two **joins** run fully back-to-back: ~7.5s + ~7s ≈ **14.5s**. B's join blocks 7558ms on the join
gate.

Mechanism: `ManagedServer._joinGate` (`SemaphoreSlim(1,1)`) is held across the *entire* join in
`ConnectionRetryHelper.JoinWithRetryAsync` (acquire `:82`, release in `finally` `:130`) and
`FarmerTestHelper.JoinSecondFarmerAsync` (`:223`/`:236`). That span includes the slow client-side work —
LAN connect, the `isGameAvailable()` join bounce, farmhand-slot load, character select + confirm — not
just the game-thread join.

**The test's own comment is factually wrong here.** `FarmerTestHelper.cs:136-142` and
`WeddingTests.cs:119-125` both claim "the per-server join gate serializes only the game-thread join …
B lands right behind A instead of waiting on A's whole lease+join." The run shows the opposite: B waits
on A's *whole* join. This comment should be corrected regardless of whether we change the gate.

**Candidate fix (needs verification — do NOT apply blind):** narrow the join gate to the phase that
actually races `isGameAvailable()`, so only the brief server-handshake portion is serialized and the
slow character-creation portion of A and B overlaps.

- The gate exists because concurrent joins make the server bounce clients back to farmhand selection
  (`isGameAvailable() == false`) — documented at `ManagedServer.cs:232-236`, and the bounce is visible
  in the run (`join_bounce` for both clients). So the gate is load-bearing; it cannot simply be removed.
- The join splits into `connectOnceAsync` (LAN connect → wait-for-farmhands → load slots) then
  `CompleteJoinAsync` (slot select, character creation, auto-login) — `ConnectionHelper.cs:594-668`.
- **Verification required before any edit:** confirm the `isGameAvailable()` race is confined to the
  *connect/handshake* phase and that two clients sitting on the farmhand-selection menu +
  creating characters concurrently do NOT bounce each other. If so, the gate can wrap only
  `connectOnceAsync` (or up to slot-pick) and release before `CompleteJoinAsync`, letting the slow
  tail overlap. If the bounce can occur during slot-claim/character-confirm too, the gate must stay
  wide and **hint 1 is not reclaimable** — in that case fix only the misleading comment.
- This interacts with broker invariants: the same gate is what `test-broker-invariants.md` and the
  prestart "join convoy" logic (`TestResourceBroker.cs:2764`) rely on to serialize KeepConnected
  classes. Narrowing it is a shared-infra change — verify it doesn't reintroduce the convoy/bounce for
  *other* tests, not just this one (per `plan-discipline.md` adversarial-compat).

Est. win if verified safe: ~7s off the 111s body. Pure overhead, not fidelity.

### Hint 2 — "speed up each render step / skip safely": limited safe slack; the render itself is genuine and must not be cut.

Both ceremonies are ~41s/~39s of **real cutscene** at `CLIENT_TPS=5`, and both ended via the host's 20s
wall-clock backstop, **not** "others ready" — the clients reach their wait gate only ~0.2s *after* the
backstop fires (gate-1 client ready `19:30:19.87`, host ready `19:30:19.68`). So the clients are the true
long pole; the host is not idle-waiting on slow clients. Compressing render = cutting the visible-render
fidelity this E2E exists to prove. Do not raise TPS or skip beats wholesale.

The one safe knob: `WeddingCutscenePlayer.BeatPauseMs` is already **1500ms** (the prior plan's
recommendation landed). Two beats × two ceremonies ≈ 6s of pure holds + a per-ceremony post-warp
`pauseTime` linger. Trimming to ~1000ms saves ~2-3s while keeping the beat visible.
- **Gate (visual):** after the change, inspect `client_recording.mp4` / `client_2_recording.mp4` and
  confirm each "couple assembled" and "marriage pronounced" beat is still clearly readable. This is a
  visual post-condition, not a build check (`runtime-post-conditions-are-gates.md`).

### Hint 3 — "server teleports without fade / skips finalization": NO CHANGE — current host teardown is correct and load-bearing.

The host deliberately does not play the cutscene; it readies its gate slot and calls
`endBehaviors(["End","wedding"])`, then hand-clears menu/dialogue/fade (`AlwaysOn.cs:769-782`). Per
`host-automation.md` invariant 8, the render-suppressed host's fade-in handler is draw-coupled and would
otherwise strand it on a black screen with a dangling dialogue (the original reported freeze), and
clearing the fade *before* `eventFinished()` avoids an NRE on back-to-back ceremonies. Adding a "real"
fade/finalization here would re-introduce the freeze. Leave it.

Adjacent observation (not on the critical path, optional): the host advances its **own** copy's dialogue
boxes via `HandleDialogueBox`, which runs on `OnOneSecondUpdateTicked` — every **12s** at `SERVER_TPS=5`
(`one-second-update-ticked-fires-per-game-tick.md`). Clicks logged 12s apart at 19:29:53 / 19:30:05 /
19:30:17. This stretches the host's event toward the 20s backstop and is *why* both ceremonies fall
through to the backstop rather than ending on "others ready". It does **not** speed up the test (clients
gate at the same instant), so it's a gate-honesty cleanup at most, not a perf fix. Out of scope for a
speedup task.

### Hint 4 — "host teleported to an incorrect place between weddings; is the teleport necessary; move to Farm": final state already fixed; the between-ceremonies Farm hop is necessary.

- **Final landing already correct.** `WarpHostHomeAfterWeddings` (`AlwaysOn.cs:819`) is committed (in
  HEAD `8ad3480`) and fires after the day's *last* wedding. The final server frame
  (`screenshots/result.png`) shows the host **inside its FarmHouse** ("Server has made it to bed!"), and
  `Polling_Wedding_HostReturnedHomeAfterCeremonies` passed. The "incorrect place" in the hint is the
  *old* stranded-on-Farm behavior, already resolved.
- **Between-ceremony Farm hop is required, not a bug.** The host warps Town→Farm at `19:30:19` (ceremony
  1 `eventFinished` exit) then Farm→Town at `19:30:20` (ceremony 2 entry) — ~1s on the Farm. It MUST land
  on a non-temporary location for `StartNextQueuedWeddingIfIdle` to re-fire `checkForEvents` for wedding
  2 (`AlwaysOn.cs:881`, `IsTemporary: false`); the Town ceremony map is temporary, the Farm is not.
  Warping home to FarmHouse instead would not satisfy the queued wedding's Farm/Town location
  requirement (`AlwaysOn.cs:788-793`). So "move to Farm between weddings" is what already happens, and it
  can't be replaced with FarmHouse. The host is hidden, so the ~1s is cosmetic only.

---

## Recommended scope (in priority order)

1. **Investigate + (if verified) narrow the join gate** so A and B's slow join tails overlap — the only
   meaningful safe win (~7s). Gated on confirming the `isGameAvailable()` bounce is confined to the
   connect phase and that narrowing doesn't reintroduce the convoy/bounce for KeepConnected classes
   elsewhere. If it can't be confined, drop the change.
2. **Fix the misleading comment** in `FarmerTestHelper.cs:136-142` and `WeddingTests.cs:119-125`
   regardless of (1): the gate serializes the *whole* join, not just the game-thread portion.
3. **`BeatPauseMs` 1500→~1000ms** (~2-3s), with a visual-recording gate.
4. **No host-side mod changes** for this task: hint 3's teardown is correct; hint 4 is already fixed and
   the Farm hop is required.

## Explicitly out of scope
- Cutting ceremony render time (the fidelity the test proves).
- The generic recording-extraction teardown tail (`artifactsMs` 3.4s + `cleanupMs` 3.6s here — small,
  shared infra, not wedding-specific).
- Moving `HandleDialogueBox` off the 12s loop — a separate gate-honesty cleanup with no speedup, not a
  perf change to bundle here.

## Adversarial self-check (per `adversarial-review-split-findings.md`)
- **Confidence split.** Hints 3 and 4 are settled by *runtime evidence* (the screenshot, the log lines,
  the committed code) — not deferrals. Hint 1's *serialization* is a measured fact (7558ms gate wait);
  hint 1's *fix* is a candidate that I am NOT asserting is safe — it is explicitly conditioned on a
  verification I have not run (whether character-creation under the gate is what prevents the bounce).
  Calling it "safe" without that read would be the over-claim this rule warns against.
- **Not OOS-as-cover.** The misleading-comment fix (rec 2) is small and adjacent; it's listed as a
  do-it-regardless item, not deferred behind the gate investigation.
- **Stale prior plan.** `wedding-host-final-location-and-test-duration.md`'s "Issue 1" (host stranded on
  Farm) and its "39s BothCeremoniesRan tail" are both obsolete — the home-warp landed and the render gate
  now records on completion, so `BothClientsRenderedBoth` (not `BothCeremoniesRan`) is the long pole.
