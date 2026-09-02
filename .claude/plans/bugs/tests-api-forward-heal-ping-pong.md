# Two healers re-open the same API forward against each other

**Status:** validation
**Priority:** 1 (low)
**GitHub Issue(s):** none
**Area:** tests
**Related:** [`tests-tunnel-transport-resilience.md`](tests-tunnel-transport-resilience.md); [`tests-transport-incident-diagnostics.md`](tests-transport-incident-diagnostics.md)
**Observed:** run `2026-08-25T03-54-45Z_c4f041c`, 04:10:01–04:10:52 UTC, 17 re-opens of one forward; seen once
**Next step:** wait for the diagnostics plan's `forward_heal_attempt` events to confirm the mechanism; if the resilience plan's direct-published-port change lands, delete the per-server forward and this path instead of fixing it

## Symptom

Run `2026-08-25T03-54-45Z_c4f041c`, 04:10:01–04:10:52 UTC: the API forward for
`server-config-83c379e136c6-1` (mapped port 32775) was re-opened 17 times, strictly
alternating between a test-context caller (the cleanup `/newgame` request's
`ForwardHealingHandler`) and an unattributed caller (the `ManagedServer` health
watchdog's own `ServerApiClient`, started via `Task.Run` outside any test context).
Each `ServerContainer.ReopenApiForwardAsync` disposed the port the other had just opened.

## Root cause

**The "each disposed the other's fresh port" reading does not fit the dedupe code.**
`HealApiForwardAsync` captures `portBeforeWait` before taking the lock and returns early
when `ApiPort` has moved since — a caller that faulted on the *old* port never re-opens.
Seventeen strictly alternating re-opens therefore mean each caller faulted on the *fresh*
port: every reopen "succeeded" (the `-L` listener bound) but the channel behind it was
unusable — the master's channels were broken, which is the canary's `Wedged` state. The
per-attempt trigger is not recorded, so this is unconfirmed until the diagnostics plan's
events exist.

## Fix

- Single supervisor per forward: `HealApiForwardAsync` first probes the *current* forward
  end-to-end (`GET /health` through it with a short timeout). A loopback connect is not a
  probe: an `ssh -L` listener accepts locally even when its channel is dead (exactly the
  incident state), so "listener accepts → `already_healthy`" would report healthy during
  the wedge. A refused/absent listener re-opens; a failed end-to-end probe on a bound
  listener is a wedged master, not healable here, and reports `master_wedged`.
- Re-open is rate-limited per forward (one per master supervisor window); a second request
  inside the window waits for the in-flight heal and reuses its result.
- Every heal call emits `api_forward_heal` with `caller` (`request`, `watchdog`),
  `trigger` (exception type + `HttpRequestError`/`SocketError`), `portBefore`,
  `portAfter`, `outcome` (`reopened`, `already_healthy`, `master_dead`, `master_wedged`,
  `rate_limited`).
- Watchdog-originated events carry the instance id explicitly so they are attributable
  without a test context.

## Verification

- Forced forward drop (`ssh -O cancel` the port by hand): exactly one `api_forward_heal
  outcome=reopened`, subsequent calls report `already_healthy`.
- No two `tunnel_forward_opened` events for one mapped port within the rate-limit window.
