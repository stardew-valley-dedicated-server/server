## Problem

`POST /newgame` can remain incomplete for two minutes and return a `504`, even though the game server is still running normally.

The failure has been reproduced more than once in the full test suite. It is not specific to one test.

In the failing case:

* Save loading completes successfully.
* `/newgame` starts a new day and switches to the playing game mode.
* The server continues reporting `snapshot_skipped_newday` because `Game1.newDay` remains true.
* After 120 seconds, the server itself returns `504`.
* The game thread continues ticking normally during and after the timeout.
* There are zero connected players.

This means the request is genuinely waiting for the new-day transition to finish; it is not a lost HTTP response or a frozen game thread.

## What we know

The `/newgame` request waits for `ComputeDayTransitionComplete()`.

That returns false while:

```text
Game1.newDaySync != null
&& Game1.newDaySync.hasInstance()
&& !Game1.newDaySync.hasFinished()
```

The important question is therefore why that condition never becomes complete when there are no other players.

There are two likely possibilities:

1. The day transition is actually stuck partway through.
2. The transition has finished, but `newDaySync` is left in a state where `hasFinished()` never becomes true.

`NewDaySynchronizer.finish()` only marks the synchronizer finished when `Game1.IsServer` is true, while `hasFinished()` reads that state. The synchronizer cleanup path, including `newDaySync.destroy()`, should also be checked because `/newgame` follows a save-reload path immediately before starting the new day.

## Investigation

Start with `NewDaySynchronizer.finish()` and `hasFinished()` to establish how the server-side completion state is set and cleared.

Then trace `newDaySync.destroy()` and the return-to-title/save-reload path to check whether an old synchronizer can survive into the new `/newgame` request.

Do not simply remove or loosen the `ComputeDayTransitionComplete()` check. The `/newgame` completion contract was deliberately changed to wait for both `SaveLoaded` and the day transition after an earlier race, so the fix needs to make the transition state resolve correctly rather than hide the problem.

## Evidence that rules out the lobby wedge

The server recording shows the game thread continuing to run at the expected rate while `/newgame` is stuck. The on-screen tick counter advances by 250 ticks over 50 seconds, matching the configured server tick rate, while the world remains black and shows zero players online.

This rules out the previously investigated failure mode where the game thread freezes while waiting for a connected peer.

## Previous explanation

Do not assume the earlier explanation that this was a reverse-proxy timeout caused by end-of-run saturation.

For this failure, the server itself emits the `504`, `snapshot_skipped_newday` continues after the timeout, and the game thread remains healthy. Those observations point to `/newgame` waiting indefinitely for its own day-transition completion condition.

## Scope

This is a game/server issue, not a test-runner issue.

The existing tests are useful for reproducing it, but the fix should address why the game's day-transition completion state can remain unresolved.
