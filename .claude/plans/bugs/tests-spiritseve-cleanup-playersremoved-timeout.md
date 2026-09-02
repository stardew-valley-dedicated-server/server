# SpiritsEve test: `WaitForPlayersRemovedByIdAsync` timeout during cleanup

**Status:** validation
**Priority:** 1 (low)
**GitHub Issue(s):** none
**Area:** tests
**Related:** none
**Observed:** once, in a full E2E suite run alongside cabin work; run id not recorded; not reproduced since
**Next step:** rerun `FestivalTests` under full-suite parallelism with server logs retained and tabulate festival-active-at-disconnect across pass/fail

## Symptom

In a full E2E suite run, `FestivalTests.SpiritsEve_DoesNotAutoEndImmediately` reported a
`WaitForPlayersRemovedByIdAsync` timeout. The **test body itself passed** — the timeout occurred in
the cleanup/teardown path (`DisposeAsync`), so the failure is an infrastructure/teardown artifact,
not a festival-logic regression. Seen exactly once; not reproduced since. Unrelated to the cabin
work it was observed alongside.

## Root cause

Not root-caused. What is known:

- The wait helper is `ServerApiClient.WaitForPlayersRemovedByIdAsync` — it gates on the server
  removing the disconnected players from its active list, per
  `disconnect-settles-client-not-server.md` (`DisconnectAsync` settles the client only).
- A timeout here means the server did not report the player(s) removed within the budget. On the
  cleanup path that can stem from: a slow/again-contended server at end-of-run, a player still mid
  festival/day-transition when disconnect landed (festival events keep a disconnected farmer counted
  until the event ends — see `host-automation.md` invariant 7), or genuine server slowness under the
  suite's parallel load.
- Because it was in cleanup and the assertions had already passed, it did not fail the scenario —
  but a cleanup timeout can still cascade (a reset `/newgame` racing a still-registered player 409s,
  retiring a healthy pooled server; see `test-broker-invariants.md`).

### Hypotheses to check when it recurs

1. **Festival-coupled removal lag.** `Multiplayer.removeDisconnectedFarmers` only runs when
   `Game1.CurrentEvent == null` (`host-automation.md` invariant 7). If SpiritsEve's teardown
   disconnects while an event/festival is still up, the farmer stays counted until it ends — the
   removal wait then races the event teardown. Check the run's `containers/server-*/container.log`
   for whether the festival was still active at disconnect.
2. **End-of-run contention.** Confirm from `server_started` / request-latency data whether the
   server was simply slow at that moment (parallel suite tail), not stuck.
3. **Budget vs teardown.** Confirm the removal budget used in the festival class's cleanup is sized
   for the worst-case festival-teardown window, not the happy path.

## Fix

Reproduce by running `FestivalTests` under full-suite parallelism a few times with server logs
retained; tabulate festival-active-at-disconnect vs clean per pass/fail
(`diff-flaky-runs-before-theorizing-mechanism.md`). If it never recurs, close as a one-off
tail-contention artifact; if it does, fix at whichever of the three hypotheses the diff localizes.
