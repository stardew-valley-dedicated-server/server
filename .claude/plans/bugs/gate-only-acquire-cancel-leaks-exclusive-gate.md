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

Wrap the post-claim waits in try/catch mirroring `AddRefAndAcquireExclusiveAsync`'s cleanup: on
throw, under `_exclusiveLock`, if this acquisition's token is still the active one (or the gate
owner is still this claim and no same-class waiters queued), null `_exclusiveDone` /
`_exclusiveOwnerClass` / `_exclusiveOwnerToken` and `TrySetResult` the TCS; rethrow. Cost: ~12
lines. Deterministically guard-testable in `ExclusiveGateOwnershipTests` (same in-memory harness):
acquire with a pre-cancelled ct while a reflected `_refCount > 1` keeps the drain loop alive,
assert the throw leaves `HasExclusiveGate == false`.
