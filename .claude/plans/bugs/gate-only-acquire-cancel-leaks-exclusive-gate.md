# Cancelled gate-only exclusive acquire leaks the gate and wedges the shared server

**Status:** ready-to-implement
**Priority:** 2 (medium)
**GitHub Issue(s):** none
**Area:** tests
**Related:** none
**Observed:** not observed, found by reading `ManagedServer.AcquireExclusiveGateOnlyAsync`
**Next step:** wrap the post-claim awaits in the token-specific rollback and add the two `ExclusiveGateOwnershipTests` guards

## Root cause

`ManagedServer.AcquireExclusiveGateOnlyAsync` (the path an Exclusive test takes when it reuses a KeepConnected session) claims the gate **before any cancellable await**: it sets `_exclusiveDone`, `_exclusiveOwnerClass`, and `_exclusiveOwnerToken` under `_exclusiveLock`, then awaits the other-refs-drain poll loop. That loop honors the per-test ct (`Task.Delay(100, ct)` + `ThrowIfCancellationRequested`).

If cancellation lands during that wait, the method throws with the gate still claimed, but the normal lease cleanup cannot release it:

* `PersistentSessionCoordinator.InitializeKeepConnectedAsync` assigns `HoldsExclusive = true` only after the await returns, so `ReleaseExclusiveGate()` no-ops.
* The lease's `ExclusiveToken` is never written, so even an unconditional release from the lease would present a stale token.

Every later test on that server can then block at the exclusive TCS (`AddRefExclusiveAwareAsync` / `AddRefAndAcquireExclusiveAsync`) until `DrainExclusiveGateOnPoison` fires — which requires the server to be poisoned — or the run ends. A cancelled test initialization therefore becomes a hung remainder-of-run rather than a failed test.

The sibling broker path (`AddRefAndAcquireExclusiveAsync`) already handles this failure mode: both of its awaits are covered by catch blocks that undo the gate claim when acquisition fails. The gate-only variant has no equivalent rollback.

### Trigger conditions

All three conditions are required:

1. An Exclusive class is reusing a KeepConnected persistent session.
2. Other refs are still draining when exclusive acquisition starts.
3. The per-test ct cancels during that drain window.

This is rare because the drain normally completes well within a poll interval, and stopOnFail aborts tend to end the run anyway. A per-test timeout during the drain on an otherwise healthy run, however, produces the wedge.

## Fix

Once the gate has been claimed, wrap the subsequent acquisition awaits in `try/catch`; on any throw, call `ReleaseExclusive(token, testName)` and rethrow.

The release must be **token-specific**: do not add an owner-class fallback. A class-granular fallback could clear a successor's valid claim, which is precisely the race the token is intended to prevent.

Reusing `ReleaseExclusive` also preserves the existing waiter semantics:

* same-class *gate-only* callers return 0 and never queue;
* same-class callers arriving through `AddRefAndAcquireExclusiveAsync` do register as `_exclusiveClassWaiters` behind a held gate;
* a valid release hands the gate to those waiters rather than nulling the TCS out from under them.

The fix is ~6 lines and uses the existing ownership/release mechanism rather than introducing a second rollback path.

## Verification

Add deterministic guards in the `ExclusiveGateOwnershipTests` harness:

1. **Cancellation releases the gate.** Start acquisition with a pre-cancelled ct while a reflected `_refCount > 1` keeps the drain loop alive. The acquisition throws and `HasExclusiveGate == false`.
2. **Cancellation preserves waiter handoff.** After the failed acquisition, a successor acquisition from another class completes successfully. This verifies that the release actually wakes the appropriate waiter; `HasExclusiveGate == false` alone does not prove that the TCS was released correctly.
