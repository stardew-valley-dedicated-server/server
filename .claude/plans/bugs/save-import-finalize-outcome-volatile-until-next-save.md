# Save-import finalize outcome is volatile until the next save

## Gap

`CabinManagerService.TryFinalizeOnLoad` consumes the single-shot finalize intent and mutates the
live world (bind, cabin resolve/build, contents + household + cellar moves) — but nothing persists
the world at that point. The mutations reach disk only at the next save (day-save, or a
`farmhand`-command save-now). A restart in that window loses the finalize outcome while the intent
is already cleared: the owner stays map-bound (store file writes through) and customized (Layer A
XML), but the cabin build and every content/NPC/cellar move silently un-happen, and the finalizer
never re-runs. The owner re-homes into a spare cabin on their first join; the farmhouse contents
stay in the Server master's farmhouse — internal-only, so effectively unreachable.

Observed indirectly while diagnosing the multi-ownership test tail: a reload right after finalize
(with no intervening save) loaded the pristine imported file, replaying the world as if the
finalizer had never run.

## Why it doesn't bite the E2E suite today

Test flows either day-save (SleepToSave), `/test/force_save`, or (since the release/rebind
save-always fix) run a `farmhand` command before any reload, so a save always lands in between.
The exposed window is the operator path: `saves import --reload` → restart before any day-save.

## Fix sketch

Persist the world once at the end of `TryFinalizeOnLoad` (success AND partial-failure exits — the
partial world is the documented "partially moved but stable" contract, so persisting it is
correct). Caveat to resolve first: the finalizer runs inside the `SaveLoaded` handler, and
`SaveNow` (synchronous `SaveGame.getSaveEnumerator` pump) has never been exercised at that point
in the load sequence — verify a save inside `SaveLoaded` is safe, or defer the save one tick via
the `GameThreadOneShot` pattern. Cost: ~5 lines + one E2E (finalize → restart WITHOUT any save →
assert cabin/contents survived).
