# TimePaused_WhenNoPlayersConnected flakes when the prior exclusive owner leaves a festival day

Status: root-caused from run `2026-07-16T06-49-35Z_ecdbb05` (worktree steam-single-account-deadlock, full-suite run). Not yet fixed.

## Symptom
`HostAutomationTests.TimePaused_WhenNoPlayersConnected` failed `Server should report IsPaused=true with no players connected`: the `GET /wait/status?isPaused=true&timeout=9999` long-poll 408'd after 10s. StopOnFail then canceled the remaining 102 tests. Flaky — the same test passed in the 2026-06-30 and 2026-07-12 runs.

## Root cause — the test inherits a festival day from the previous exclusive owner
- `EggFestival_MainEventStartsWithCountdownSkip` (FestivalTests) drives the shared `config-0d4f7a75eb68` server onto the Egg Festival day via `SetDate(day-1)` + sleep-through, and does not restore a non-festival date. It released the exclusive gate at 06:57:14; `TimePaused` acquired it the same second (`exclusive_acquired` at 06:57:14.842).
- `AlwaysOn.HandleAutoPause` (per-tick) intentionally skips the pause write on festival days: `numPlayers == 0 && !isFestivalDay` is the only branch that sets `IsPaused=true` for an empty server, and `SDateHelper.IsFestivalToday()` is date-based. With the world left on spring 13, nothing ever writes `IsPaused` — the 10s wait can never succeed.
- Log-verified: the `[Festival]` trace (`AlwaysOnFestivals.HandleFestivalStart:262`, printed only while `Game1.whereIsTodaysFest != null`) runs every second through the whole failure window (06:57:17→06:57:47+) in `containers/server-1/container.log`.
- The test already guards against leftover players (`Polling_HostAutomation_NoPlayers`) but not against a leftover festival date.

## Fix sketch
In `TimePaused_WhenNoPlayersConnected` (and its siblings asserting pause/time behavior on the shared server), pin a known non-festival date before asserting — `ServerApi.SetDate` to e.g. spring 2 — mirroring the existing no-players guard. Note `test-state-setter-runs-engine-reconcile.md`: `/test/set_date` must run the new-day reset (it does — `whereIsTodaysFest` cleared), so the date pin also clears the festival pointer. Alternatively (broader): FestivalTests restore a non-festival date at class end, but the per-test guard is the self-contained fix that doesn't depend on every festival test's cleanup discipline.

## Non-causes (checked)
- Not the steam-deadlock branch's diff: zero `server_poisoned` events in the run, so its R1/R3 paths never executed; the cleanup-token rebind is inert on healthy servers.
- Not the 12s `OneSecondUpdateTicked` cadence: `HandleAutoPause` runs per-tick in `OnUpdateTicked`.
