# Mutating API endpoints don't guard concurrent calls or return consistent blocked responses

**Status:** validation
**Priority:** 2 (medium)
**GitHub Issue(s):** none
**Area:** server
**Related:** [`newgame-stalls-after-forced-reload.md`](newgame-stalls-after-forced-reload.md); [`tests-transport-fault-typed-classification.md`](tests-transport-fault-typed-classification.md)
**Observed:** not observed as a failure; found by reading `GameManagerService.RequestNewGame` while diagnosing run `2026-08-25T03-54-45Z_c4f041c`
**Next step:** run the Step 1 audit of mutating endpoints and decide reject vs coalesce per operation class

## Symptom

A long-running mutating endpoint can be re-entered while its operation is still in
flight, and the second call interferes with the first instead of being rejected or
coalesced. Blocked/rejected calls also don't share one status/body contract, so a
consumer can't tell "busy, retry later" from "invalid request" from a real failure.

Exemplar — `POST /newgame` (`ApiService.HandlePostNewGameAsync` →
`GameManagerService.RequestNewGame`):

- `RequestNewGame` rejects only a concurrent `/reload` (guards on `_reloadCompletion`).
  It does **not** reject a concurrent `/newgame`: a second call overwrites
  `_newGameCompletion` with a fresh TCS, resets `_pendingNewGameConfig`/`_gameStarted`,
  and re-runs `ExitToTitle` — restarting an in-flight new game rather than being turned
  away. The first caller's completion task is orphaned.
- The only duplicate guard that exists is `connectedClients > 0 → 409` in the handler,
  which doesn't cover the concurrent-creation case.
- Consequence for the test harness: because a duplicate `/newgame` isn't a clean
  rejection, `/newgame` can't be marked retry-safe (`RetrySafeRequest`), so a
  forward-scoped transport fault mid-`/newgame` propagates raw instead of healing — the
  behaviour the classification branch's post-condition #2 now documents as fail-fast
  (`tests-transport-fault-typed-classification.md`).

`RequestReloadSave` shows the intended shape: it rejects when the sibling op is pending
and coalesces overlapping `/reload` requests. The gap is that this discipline is
per-endpoint and asymmetric, not a shared contract.

## Fix

### Requirements (what "hardened" means for a mutating endpoint)

1. **Atomic / idempotent** — the operation either completes once or is safely repeatable;
   a retry never leaves half-applied state or launches a second concurrent run.
2. **Interference-proof** — an additional call while one is in flight is rejected or
   coalesced onto the in-flight operation; it never mutates the in-flight run's state.
3. **Consistent blocked response** — a rejected/blocked call returns one canonical
   status (`409` busy / `503` not-ready), body shape, and structure across endpoints, so
   callers and the retry layer can classify it deterministically.

### Step 1 — Audit every mutating endpoint

Survey each route dispatched in `ApiService` (and the test-client API surface) and
classify. Produce a table: endpoint → handler → mutates state? → long-running? → current
concurrency guard → gap vs the three requirements → recommended remedy
(single-in-flight guard / coalesce / idempotency key / none needed for pure reads).

Anchor points to model against: `HandlePostNewGameAsync`, `HandlePostReloadAsync`,
`GameManagerService.RequestNewGame` / `RequestReloadSave` (the completion-TCS pattern),
and the `/test/*` state setters (per `test-state-setter-runs-engine-reconcile.md`, these
run engine reconciliation and are also mutating). Read-only snapshot endpoints
(`/status`, `/players`, `/cabins`, `/farmhands`, …) are out of scope unless they mutate.

### Step 2 — Define the shared blocked-response + single-in-flight contract

- One helper/pattern for "an operation of this class is already in flight" that returns
  the canonical `409` with a typed body (reuse `NewGameResponse`-shaped `Success=false` +
  `Error`, or a shared envelope if the audit shows divergent bodies).
- Decide per operation class: **reject** (new game, destructive ops) vs **coalesce**
  (idempotent reloads/settings) — mirror `RequestReloadSave`'s coalescing where the
  result is identical regardless of which caller triggered it.
- Ensure the completion-TCS lifecycle can't orphan a waiter or resolve the wrong caller
  (the existing "both TCSs armed → false success" note in `RequestNewGame` is the hazard
  to generalize).

### Step 3 — Harden `/newgame` as the exemplar

- In `RequestNewGame`, reject a concurrent `/newgame` symmetrically with the existing
  `_reloadCompletion` guard (reject when `_newGameCompletion != null`), returning the
  canonical `409` `NewGameResponse`.
- Confirm the in-flight run's state (`_pendingNewGameConfig`, `_gameStarted`,
  `_newGameCompletion`) can no longer be trampled by a second call.

### Step 4 — Re-enable auto-heal for `/newgame`

Once a duplicate `/newgame` is a guaranteed clean rejection (not a restart), mark the
request retry-safe (`RetrySafeRequest` in `ServerApiClient.CreateNewGameAsync`) so a
forward-heal retry after a master kill re-issues it safely, and revert
`tests-transport-fault-typed-classification.md` post-condition #2 back to the auto-heal
expectation. Gate this step on Step 3 landing — marking it safe before the guard exists
reintroduces the double-creation risk.

### Step 5 — Apply the pattern to the endpoints the audit flags

Extend the Step 2 contract to each endpoint Step 1 marked as gapped; mark the ones that
become safe-to-repeat as `RetrySafeRequest` at their call sites.

## Verification

- Two concurrent `/newgame` calls: the second returns `409` with the canonical body; the
  first completes normally; the recording shows exactly one `ExitToTitle`/`CreateNewGame`
  cycle (per `passing-test-isnt-proof-the-scenario-ran.md`, confirm from the run
  artifact, not just the status code).
- Forced master kill mid-`/newgame` (resilience plan) retries after the master is
  restored and the test passes (reverses the current fail-fast post-condition).
- Every audited mutating endpoint either has a concurrency guard or a documented reason
  it needs none, and blocked calls across endpoints return the one canonical shape.

## Relationship to other plans

- `tests-transport-fault-typed-classification.md` — post-condition #2 flips back to
  auto-heal when Step 4 lands.
- `newgame-stalls-after-forced-reload.md` — a distinct `/newgame` 504-stall bug
  (`newDaySync` waiting on a stale peer), not concurrency; independent of this plan.
