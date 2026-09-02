# Transport faults are classified by message substrings

**Status:** ready-to-implement
**Priority:** 2 (medium)
**GitHub Issue(s):** none
**Area:** tests
**Related:** [`tests-tunnel-transport-resilience.md`](tests-tunnel-transport-resilience.md); [`server-endpoint-concurrency-idempotency.md`](server-endpoint-concurrency-idempotency.md)
**Observed:** run `2026-08-25T03-54-45Z_c4f041c`, `HttpIOException(ResponseEnded)` left unclassified after a master kill; seen once
**Next step:** implement; `/newgame` stays not retry-safe until the endpoint concurrency plan lands its guard

## Symptom

`TransportFaultClassifier.ClassifySingle` decides whether a fault is forward-scoped
(retry after re-opening the forward) or host-scoped (poison) partly via
`LooksLikeBrokenConnection`, which substring-matches five English phrases in
`IOException.Message`. In run `2026-08-25T03-54-45Z_c4f041c` the harness killed a live
ssh master mid-request; the resulting `HttpIOException` ("The response ended
prematurely.", `HttpRequestError.ResponseEnded`) matched none of them, so
`ForwardHealingHandler` did not engage and the test failed on a fault the harness caused.

## Root cause

Message text varies by .NET version, OS (Cygwin ssh on Windows vs Linux), and culture.
Every case the method tries to guess has a typed signal.

## Fix

- Delete `LooksLikeBrokenConnection`. Classify only by: `SocketException.SocketErrorCode`,
  `HttpRequestException.HttpRequestError`, `HttpIOException.HttpRequestError`
  (`ConnectionError` → forward-scoped), `EndOfStreamException`.
- `ResponseEnded` is forward-scoped **only inside the owned-action window** (next bullet).
  A server process crashing mid-response produces the identical
  `HttpIOException(ResponseEnded)`; classified forward-scoped unconditionally it would
  reopen → reset → exhaust the 45 s heal budget → `InfrastructureSkipException`, reporting
  a real crash as an infrastructure skip. Outside the window it stays application-level.
- Unknown shapes return `(reason: "unclassified: <Type>", forwardScoped: false)` and
  emit a `transport_fault_unclassified` event with the full exception chain — never
  `(null, false)` silently.
- Replace the inference "is this forward-scoped?" with owned-state checks where the caller
  has them. The action is performed by the *runner* (`Program.cs` master monitor), not the
  xUnit child where the classifier runs, so `LastTransportAction(hostId)` reads the
  runner-published `diagnostics/transport-state.json` (resilience plan, principle 7) — a
  child-local `TunnelManager` field would always be empty.
- Idempotency: `ForwardHealingHandler` retries only requests whose call site marks them
  retry-safe (a request option). This flips today's default (every request through the
  handler retries, ~20 call sites) — audit each site when adding the option. `POST
  /newgame` is marked safe only after reading the server handler: in the incident the
  first `/newgame` had *completed* server-side, so the retry issues a second `/newgame`
  while the first load is in flight; confirm the handler serialises or rejects that.

## Verification

- Unit test: each `HttpRequestError`/`SocketError` value maps to a documented verdict;
  a synthetic `HttpIOException(ResponseEnded)` is forward-scoped.
- Forced master kill mid-`/newgame` fails fast: `/newgame` is not retry-safe (a duplicate
  call restarts an in-flight new-game rather than being rejected — `RequestNewGame` only
  guards against a concurrent `/reload`, not a concurrent `/newgame`), so a forward-scoped
  fault on it propagates raw. Making it retry-safe needs a server-side concurrency guard
  first — tracked in `server-endpoint-concurrency-idempotency.md`. A marked
  request (any GET, `/reload`) retries after the master is restored and passes.
