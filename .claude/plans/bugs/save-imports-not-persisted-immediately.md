# Save-imports are not saved immediately

**Status:** ready-to-implement
**Priority:** 3 (high)
**GitHub Issue(s):** none
**Area:** server
**Related:** none
**Observed:** once, while diagnosing the multi-ownership test tail (reload immediately after finalize)
**Next step:** confirm saving from inside `SaveLoaded` is safe, then add the save at the end of `TryFinalizeOnLoad`

## Symptom

`CabinManagerService.TryFinalizeOnLoad` applies the finalize request and changes the live world (bind, cabin resolve/build, contents, household, and cellar moves), but does not save those changes to disk. They are only persisted on the next save (day-save or `farmhand`-command save-now).

If the game restarts before that save, the finalize result is lost even though the finalize request has already been cleared. The owner stays map-bound (store file writes through) and customized (Layer A XML), but the cabin build and all content/NPC/cellar moves are lost. The finalizer does not run again.

The owner then moves into a spare cabin on their first join, while the farmhouse contents remain in the Server master's farmhouse and are effectively unreachable.

We saw this while diagnosing the multi-ownership test tail: reloading immediately after finalize, without saving first, loaded the original imported file as if finalization had never happened.

## Root cause

### Why the E2E suite does not catch it

The test flows always save before reloading:

* `SleepToSave`
* `/test/force_save`
* A `farmhand` command, since the release/rebind save-always fix

The problem is therefore mainly in the operator flow:

`saves import --reload` → restart before any day-save

## Fix

Save the world once at the end of `TryFinalizeOnLoad`, both when finalization succeeds and when it partially fails.

Partial finalization is already documented as a valid "partially moved but stable" state, so saving that state is correct too.

One thing to check first: the finalizer runs inside the `SaveLoaded` handler, and `SaveNow` (the synchronous `SaveGame.getSaveEnumerator` pump) has not been used at this point in the load sequence before. Verify that saving from inside `SaveLoaded` is safe.

If it is not, defer the save by one tick using the `GameThreadOneShot` pattern.

Cost: ~5 lines of code.

## Verification

One E2E test: **finalize → restart without saving → assert that the cabin and contents survived**
