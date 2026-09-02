# `DeletedCabin_DoesNotPoisonSubsequentJoins` flakes when the farmhand name never syncs before disconnect

**Status:** ready-to-implement
**Priority:** 1 (low)
**GitHub Issue(s):** none
**Area:** tests
**Related:** none
**Observed:** once, run `2026-07-16T07-04-54Z_ecdbb05` (worktree `steam-single-account-deadlock`, full suite under heavy host contention, `queueDurationTotal=54751s`); first recorded failure of this test, ledger all passed/canceled since 2026-05-28
**Next step:** audit `DisconnectAndWaitForSlotAsync` call sites to pick test-local vs shared placement, then add the wait

## Symptom

`PasswordProtectionTests.DeletedCabin_DoesNotPoisonSubsequentJoins` failed:

> `Delete should succeed: Farmhand 'Farmer62' not found`

`WaitForFarmhandDeletedByNameAsync` retried for the full 35s `FarmerDeleteTimeout`, ending with `failure_context=WaitForFarmhandDeletedByNameAsync_timeout` and `lastResultSuccess=false`.

## Root cause

The test disconnects before the farmhand's character data is guaranteed to have synced to the server.

The flow is:

`ConnectNewAsync(assertAuthenticated: true)` → `DisconnectAndWaitForSlotAsync` → `WaitForFarmhandDeletedByNameAsync(name)`

`ConnectNewAsync(assertAuthenticated: true)` guarantees authentication/warp, but does **not** guarantee that the farmhand's character data — specifically its name — has replicated into the server-side farmhand entry.

Under heavy load, that sync can lag substantially; the observed customization-sync p90 is ~20s. Disconnecting immediately after authentication can therefore race the sync and cut it off before the server ever learns the farmhand's name.

The failing run provides direct evidence:

* `"Farmer62"` appears only in `containers/client-5/container.log`:

  * `Set character data - Name: Farmer62` at 07:17:17
  * `[Spawn:AfterWarp] Player Name: Farmer62` at 07:17:18
* `"Farmer62"` never appears in `containers/server-1/container.log`.
* Sibling farmers of the same class (Farmer60/61/63) do appear in the server log, confirming that server logs record the farmhand name when the sync reaches the server.

As a result, the server-side farmhand entry can remain unnamed, and `WaitForFarmhandDeletedByNameAsync("Farmer62")` can never find it to delete it. Increasing the delete retry budget cannot fix the race; the required server-side name was never established.

This is consistent with the other sync-related failure in the same run: `SaveImportTests.Import_ForceReload_KicksThenFinalizes` timed out in `WaitForFarmhandByNameAsync` with `requireCustomized:true` for Farmer66 before cancellation.

## Fix

Before disconnecting, explicitly wait until the server knows the farmhand:

`ServerApi.WaitForFarmhandByNameAsync(name, requireCustomized: true)`

Insert that wait between `ConnectNewAsync` and `DisconnectAndWaitForSlotAsync`, using the 35s `CabinAssignmentTimeout` from the cleanup-timeout-alignment plan.

The remaining design question is where to put the synchronization barrier:

1. **Test-local:** add it directly to `DeletedCabin_DoesNotPoisonSubsequentJoins`.
2. **Shared:** add it inside `Farmers.ConnectNewAsync` when `assertAuthenticated` is requested.

Before choosing the shared location, audit other `DisconnectAndWaitForSlotAsync` call sites that disconnect immediately after `ConnectNewAsync`. If they rely on the same implicit “connected means server knows the farmhand” assumption, the shared fix is preferable; otherwise keep the synchronization requirement local to this test.

## Non-causes

* **Not the steam-deadlock branch diff.** The run contains zero `server_poisoned` events, so the R1/R3 paths never executed.
* **Not cleanup timeout/rebind behavior.** There are zero `"Cleanup timed out"` occurrences, and the cleanup-token rebind produced no behavior change.
* **Not lease cleanup.** The failure occurs in the test body's own delete loop, upstream of any lease-cleanup path.
* **Not an instance swap.** `config-a50586faedce` had exactly one instance for the entire run.
