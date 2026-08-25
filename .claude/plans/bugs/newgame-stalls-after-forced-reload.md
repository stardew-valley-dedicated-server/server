## Problem

`POST /newgame` can occasionally wait for its full 120-second handler timeout and return `504` after a forced save reload followed by client disconnect/reconnect activity.

The failure has occurred more than once on a shared server instance. Other `/newgame` requests in the same runs completed normally in about 12 seconds. In the observed runs, this happened roughly once in 13 `/newgame` calls.

The affected test is marked as an infrastructure failure. Under StopOnFail, later tests are canceled as a consequence of the first failure; those later cancellations are not separate failures.

## What we know

The new game itself is created successfully and quickly. In the investigated occurrence, the following all completed within the first second:

* `RequestNewGame`
* `ExitToTitle`
* `CreateNewGame`
* `NewDay(0f)`
* `loadForNewGame`
* The SMAPI `SaveLoaded` callbacks
* Cabin setup, pet setup, automation setup, and the host warp

The server generates the `504` itself (`ApiService.cs:4814/4949`); this is not a reverse-proxy timeout or a lost response.

After creation finishes, `/newgame` waits on `ComputeDayTransitionComplete()` (`ApiService.cs:1664-1672`) until the 120-second timeout. The completion check remains false while:

```text
Game1.newDaySync != null
&& Game1.newDaySync.hasInstance()
&& !Game1.newDaySync.hasFinished()
```

or while `DayOfMonth == 0`.

The game thread is not frozen during this wait. The server continues ticking at the expected 5 ticks per second. The recording shows the day-transition screen remaining active with no players connected. Two frames 85 seconds apart show the tick counter advancing from 2130 to 2555, exactly 425 ticks, confirming that the game loop is running normally throughout the wait.

The `snapshot_skipped_newday` latch (`ApiService.cs:1238`) also continues throughout the timeout. It is emitted while `Game1.newDay` is true and only resets after a completed snapshot with `Game1.newDay == false`. In the failing run it appears near the start of the request and again immediately before the timeout.

This shows that the initial fade and `_newDayAfterFade` phase completed. The request is instead blocked by the later completion condition, most likely the `newDaySync` state or the day-of-month check.

At roughly the 120-second timeout, the day-transition machinery starts moving again. A second `newDay` streak begins and `cabin_owner_changed` fires about 300 ms after the handler returns `504`. The run is torn down before we can observe the transition completing. What releases the transition at that point is not yet proven.

The `/screenshot` endpoint is not involved; it only reads the backbuffer. There is also no retry in `GameCreatorService`.

## Leading candidate: stale server connection

Both known occurrences followed tests that kicked or disconnected clients immediately before a forced reload. `Game1.server` and its connection tables persist across `ExitToTitle`, so a disconnected or half-dead connection may still be present when the new-day ready checks are created.

`newDaySync` may therefore be waiting for a peer that is no longer usable. If that connection is eventually removed by the transport's dead-peer timeout, it would explain both the roughly 120-second stall and the sudden progress immediately after the `/newgame` handler gives up.

This is the leading hypothesis, but it needs live confirmation.

## Other known mechanism: empty-server auto-pause

There is a separate mechanism where `HostPaused` prevents `Game1.UpdateOther` from running (`Game1.cs:4308 -> 6436`). With zero players, that can stop the day-transition screen from progressing.

This was hardened on 2026-07-20 so that auto-pause does not engage while `ComputeDayTransitionComplete()` is false.

That closes the empty-server fade-phase hazard, but it does **not** explain this occurrence. The `snapshot_skipped_newday` evidence shows that the transition had already progressed past the fade phase before the 120-second stall.

## Next step

Add a diagnostics probe, either to `/diagnostics/state` or as a log entry on each `OneSecond` tick while a transition has been pending for more than 10 seconds.

Record:

* `Game1.newDay`
* `Game1.gameMode`, `Game1.currentMinigame`, `Game1.Date.DayOfMonth` (to confirm the other two `ComputeDayTransitionComplete()` false branches are not the cause)
* `fadeToBlackAlpha`
* `newDaySync.hasInstance()`
* `newDaySync.hasFinished()`
* `Game1.netReady.GetNumberReady()`
* `Game1.netReady.GetNumberRequired()`
* The individual ready-check state if available
* `showingEndOfNightStuff`
* `IsPaused`
* The server's live connection count and connection IDs
* The connection state for those peers

Then repeatedly reproduce the same sequence on one server instance:

1. Connect a client.
2. Kick or disconnect it.
3. Force a save reload.
4. Immediately call `/newgame`.
5. Repeat.

The goal is to capture exactly which condition is holding `ComputeDayTransitionComplete()` false during the 120-second wait.

If a stale or disconnected peer is blocking `newDaySync`, fix the connection lifecycle or ready-check membership—for example, by removing dead peers during `ExitToTitle` or excluding disconnected peers from the ready check.

Do not fix this by increasing or weakening the `/newgame` timeout or completion gate. The existing gate intentionally waits for both `SaveLoaded` and `ComputeDayTransitionComplete()` to avoid an earlier race. The rule in `.claude/rules/tests-assert-via-http-api.md` documents this contract.

## Reproduction

Run:

```bash
make test-llm FILTER="SaveImportTests"
```

The serial `SaveImportTests` chain puts a forced reload and client disconnect immediately before the next test's `/newgame`. This produced the failure in the observed runs.

The same failure pattern also appeared in `CabinPositionPersistenceTests`, so it is not specific to save-import behavior. The common pattern is reload + client churn followed by `/newgame` on the same server instance.
