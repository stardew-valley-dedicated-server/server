# Cancelled gate-only exclusive acquire leaks the gate and wedges the shared server

## Gap

`ManagedServer.AcquireExclusiveGateOnlyAsync` (the path an Exclusive test takes when it reuses a
KeepConnected session) claims the gate FIRST — sets `_exclusiveDone`, `_exclusiveOwnerClass`, and
`_exclusiveOwnerToken` under `_exclusiveLock` — and only then awaits the other-refs-drain poll
loop, which honors the per-test ct (`Task.Delay(100, ct)` + `ThrowIfCancellationRequested`). A
cancellation landing inside that wait throws out of the method with the gate still claimed, and
nothing can release it:

- `PersistentSessionCoordinator.InitializeKeepConnectedAsync` assigns `HoldsExclusive = true`
  only after the await returns, so `ReleaseExclusiveGate()` no-ops.
- The lease's `ExclusiveToken` is never written, so even an unconditional release would present
  a stale token.

Every later test on that server then blocks at the exclusive TCS (`AddRefExclusiveAwareAsync` /
`AddRefAndAcquireExclusiveAsync`) until `DrainExclusiveGateOnPoison` fires — which needs a server
poison — or the run ends. A cancelled test init becomes a hung remainder-of-run, not a failed
test.

The sibling broker path (`AddRefAndAcquireExclusiveAsync`) already handles this: both its awaits
sit in catch blocks that undo the gate claim (null the TCS/owner/token, `TrySetResult`) when the
acquisition dies. The gate-only variant never got the equivalent.

## Trigger conditions (why it hasn't bitten yet)

Needs all three: an Exclusive class reusing a KeepConnected persistent session, other refs still
draining at acquire time, and the per-test ct cancelling exactly inside that window. Cancellation
there is rare (the drain usually resolves in well under a poll interval), and stopOnFail aborts
tend to end the run anyway — but a per-test timeout mid-drain on a healthy run produces the
silent-wedge shape.

## Fix sketch

Wrap the post-claim waits in try/catch; on throw, call `ReleaseExclusive(token, testName)` and
rethrow. Token-specific by construction — no owner-class fallback (a class-granular fallback
could clear a successor claim, the same weakness the token exists to close; review feedback).
Reusing `ReleaseExclusive` also inherits the correct waiter semantics for free: same-class
*gate-only* callers return 0 and never queue, but same-class callers arriving via
`AddRefAndAcquireExclusiveAsync` DO register as `_exclusiveClassWaiters` behind a held gate, and
a valid release passes them the turn instead of nulling the TCS out from under them. Cost: ~6
lines. Deterministic guard tests (`ExclusiveGateOwnershipTests` harness): (1) acquire with a
pre-cancelled ct while a reflected `_refCount > 1` keeps the drain loop alive → the throw leaves
`HasExclusiveGate == false`; (2) a successor acquisition from another class then completes —
`HasExclusiveGate == false` alone does not prove the TCS was released.
