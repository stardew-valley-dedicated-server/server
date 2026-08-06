# `/newgame` never completes — 504 after 120s with a healthy game thread and zero players

Status: narrowed, not root-caused. Next read identified.

Seen once in four full-suite runs on 2026-08-03 (`09-31-27Z`). Not new — PR #496's plan records the
same failure on a different test (`CabinPositionPersistenceTests.DummyCabin_AfterMoveAndReconnect_…`,
run `2026-07-13T21-24-09Z`).

## Symptom

`SaveImportTests.Import_AsIs_PreservesOwnerAsHost` fails with `504 (Gateway Timeout)`,
`failureCategory: "infrastructure"`, on `POST /newgame` after `durationMs: 120406`.

## Evidence (run `2026-08-03T09-31-27Z_38d72b9`, `containers/server-4`)

- `09:42:11-13` the save-import reload succeeds cleanly (`Loading Save`, `SaveLoaded`,
  `save_import_finalized`, cabins synced).
- `09:42:36` `[API] New game requested` → `[GameCreator] After NewDay(0f): newDay=True,
  fadeToBlack=True` → `setGameMode(playingGameMode (3))` → `Warping to Farm`.
- `09:42:37` onward: `snapshot_skipped_newday` repeats — and keeps repeating **past** the timeout
  (still firing at `09:44:39`, after the 504 at `09:44:36`).
- `09:44:36` `http_served {"path":"/newgame","status":504,"durationMs":120406}`.

So the transition was genuinely unfinished; this is not a lost or mis-routed response. The 504 is
emitted by the mod itself (`ApiService.cs:4814/4949`), and `snapshot_skipped_newday` only fires while
`Game1.newDay` is true (`:1238`).

## What separates it from the lobby wedge

The server's own recording settles it. Two frames 50s apart show the on-screen counter at
TICK 3319 → 3569 — exactly 250 ticks in 50s, i.e. 5/sec, matching `SERVER_TPS`. The game thread is
**healthy and ticking**, the overlay reads "0 Players Online", and the world is black.

```bash
ffmpeg -ss <seconds> -i containers/server-4/full_recording.mp4 -frames:v 1 out.png
```

(Recording is 719s; t≈659 and t≈709 were the two frames used. Run artifacts under `TestResults/runs/`
are local and get pruned.)

That rules out the mechanism of `day-transition-wedge-with-lobby-player.md`, where the thread is
frozen and a peer is being waited on. Here there is nobody to wait for and the loop is running.

## Next read

`/newgame` completion is gated on `ComputeDayTransitionComplete()`, which returns false while
`Game1.newDaySync != null && hasInstance() && !hasFinished()` (`ApiService.cs:1664-1672`). With zero
other players, establish which of these holds:

1. the transition is genuinely stalled mid-flight, or
2. it completed but a `newDaySync` instance is left un-finished / un-destroyed, so the completion
   contract never resolves.

`NewDaySynchronizer.finish()` only sends `finished` when `Game1.IsServer`
(`decompiled/.../StardewValley/NewDaySynchronizer.cs:114-121`), and `hasFinished()` reads that var
(`:123-126`) — start there. Also check `newDaySync.destroy()` (`Game1.cs:4294`) on the
return-to-title path that `/newgame` takes, since this run reloaded a save immediately before.

Relevant context: `.claude/rules/tests-assert-via-http-api.md` documents that `/newgame`'s
completion was deliberately gated on **both** `SaveLoaded` and `ComputeDayTransitionComplete()` to
fix an earlier flake — so this gate is load-bearing and must not simply be loosened.

## Note on the prior explanation

PR #496's plan closes its occurrence as "stalled ~120s at the host's reverse proxy under end-of-run
saturation", with an isolated re-run passing. Three observations contradict that reading for this
occurrence: the 504 was emitted by the server itself (it appears in the server's own
`http_served` event), `snapshot_skipped_newday` continues past the timeout, and the game thread was
ticking at full rate throughout. Re-confirm before accepting either explanation.

## Relationship to PR #494

None. Server-side, zero players connected, no join involved. Also observed on PR #496's branch.
