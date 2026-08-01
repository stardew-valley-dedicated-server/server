# /newgame 504 when it lands during a forced-reload's settle window

## Symptom

`POST /newgame` intermittently takes its full 120s handler budget and returns 504; the caller
test fails `infrastructure`; under StopOnFail the rest of the run cancels (later failures are
cascade victims with `TaskCanceledException` / socket-abort shapes and near-identical
`failedAt` timestamps). Two occurrences on 2026-07-20 (local host):
`SaveImportTests.Import_SwapHost_AllowsSameBindIdOnMultipleFarmhands` (run
2026-07-20T03-50-12Z, after `Import_ForceReload_KicksThenFinalizes`) and
`CabinPositionPersistenceTests.DummyCabin_ReconnectAfterMove_JoinSucceedsAndMasterUnchanged`
(run 2026-07-20T14-52-51Z). Both followed a reload + client-churn sibling test on the same
server instance. Baseline: 12 other `/newgame` calls in the same runs served in ~12s.

## Localization (run 2026-07-20T03-50-12Z, server-1, request 03:58:31 → 504 04:00:31)

Established from the container log, the per-test server recording, and decompiled sources:

- The creation itself was instant: `RequestNewGame` → `ExitToTitle` → `CreateNewGame` →
  `NewDay(0f)` → `loadForNewGame` → SMAPI SaveLoaded chain (cabin build, pet, automation
  enabled, host warp) ALL completed within 03:58:31. The handler then waited only on the
  completion gate (`ComputeDayTransitionComplete`).
- The game loop ran at full speed the whole window: recording frames show the test overlay
  ticking 2130 → 2555 over 85s = exactly SERVER_TPS 5. Screen stayed black (day-0 transition
  visuals) with only the overlay.
- The park is INSIDE the day-transition machinery, not before it: the
  `snapshot_skipped_newday` latch (ApiService) emitted at 03:58:31.74 AND again at
  04:00:31.94, and the latch only resets after a completed snapshot with `Game1.newDay ==
  false` — so the fade + early `_newDayAfterFade` finished promptly, `newDay` went false, and
  the gate stayed closed on the LATER predicate (`newDaySync.hasInstance() &&
  !hasFinished()`, or `DayOfMonth == 0`). At ~04:00:31.9 a SECOND newDay streak began and a
  `cabin_owner_changed` (day-start cabin pass) fired — the machinery moved again ~300ms after
  the handler gave up; the run tore down before completion was observable.
- The rescue was NOT the artifacts `/screenshot` (it only reads the backbuffer) and there is
  no retry in `GameCreatorService`. What re-entered the newDay state at +120s is UNPROVEN.

## Root-cause candidates (ranked)

1. **Stale server-side connection in the ready-check set.** Both occurrences followed tests
   that kicked/disconnected clients right before a reload; `Game1.server` and its connection
   tables persist across `ExitToTitle`. A half-dead connection participating in the
   `newDaySync` ready checks would park the finish phase until the transport's own dead-peer
   timeout drops it — matching the ~120s park, the spontaneous "machinery moved again"
   rescue, and the reload+churn correlation. Needs live confirmation.
2. **Empty-server auto-pause freezing a transition phase.** PROVEN mechanism, HARDENED
   2026-07-20: `HostPaused` gates `Game1.UpdateOther` (Game1.cs:4308 → 6436), the only pump
   for the newDay screen fade — `HandleAutoPause` pausing at 6:00 with 0 players could park
   the fade phase of any empty-server transition. `HandleAutoPause` now unpauses while
   `ComputeDayTransitionComplete()` is false. This closes the fade-phase hazard but does NOT
   explain the observed park (which was past the fade, per the latch evidence).

## Next step: instrumented repro

Add a diagnostics probe (fields on `/diagnostics/state` or a log line each OneSecond tick
while a transition is pending >10s): `Game1.newDay`, `fadeToBlackAlpha`,
`newDaySync.hasInstance()/hasFinished()`, per-ready-check `GetNumberReady/Required`
(`Game1.netReady`), `showingEndOfNightStuff`, `IsPaused`, and the server's live connection
count/ids. Then hammer the repro shape (connect client → kick/disconnect → `saves reload
--force` → immediate `/newgame`, looped) and read which wait holds the 120s. Fix at that wait
(likely: exclude disconnecting/dead peers from ready checks, or drop dead connections at
`ExitToTitle`), not at the HTTP timeout.

## Repro

`make test-llm FILTER="SaveImportTests"` — the serial Exclusive chain back-to-backs a kick +
forced reload into the next test's `/newgame`; hit ~1/13 `/newgame` calls in the observed
runs. The CabinPositionPersistence occurrence shows any reload+churn → `/newgame` sequence
on a shared instance can hit it.
