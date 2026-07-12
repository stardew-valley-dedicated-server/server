# GalaxyOutageReproTests NotFound flake — NIC-cut server error poisons the server mid-test

Status: INVESTIGATED, not fixed. Intermittent. The failing run's artifacts are NOT local
(this test is 15/15 pass locally across runs since it was added in #440, 2026-06-18), so the
mechanism below is from reading the harness path end-to-end, not from a pass/fail artifact diff.
It is coherent and `TotalConnectivityLoss` is the one test structurally exposed to it.

## Symptom
`GalaxyOutageReproTests.TotalConnectivityLoss_RecordsGalaxyReauthSignal` fails with a bare Docker
exception (no harness wrapping, `phase: test_body`):
```
Docker API responded with status code='NotFound', response='{"message":"No such container: <id>"}'
```
A Docker call hit a container that was already removed — the failure class the harness names at
`TestLifecycle.cs:406` ("DockerContainerNotFound when abort cleanup removed this test's container").

## What the test does (`GalaxyOutageReproTests.cs:71-246`)
Leases a Steam client and connects, suspends the health watchdog, cuts the server container's only
network (`NetworkOutageHelper.DisconnectAsync`), holds for the outage dwell (default 10s), reconnects
in a `finally`, then waits **up to ~10 minutes** for recovery: Steam reconnect (5m), Galaxy recovery
(3m), fresh Steam lobby (2m). Poisons the server (`PoisonServer(..., TestRetiredServer)`) at the end
because the NIC cut leaves it unusable for reuse.

## Root cause
The NIC cut makes the headless SMAPI server log `ERROR`/`FATAL` (Steam/Galaxy/socket failures during
the outage). `ServerContainer` scans every log line for `\b(ERROR|FATAL)\b` (`ServerContainer.cs:961`)
and on a match calls `_errorCancellation.Cancel()` (`:999`), which fires `PoisonServer` via the
callback registered at `ManagedServer.cs:945-952`.

This poison path is **NOT gated by `SuspendHealthChecks()`**. `_healthSuspended` only short-circuits
the health-watchdog loop (`ManagedServer.cs:1023`); the log-error scan runs regardless. So the test
poisons its own server *during the outage* — the exact thing it suspended the watchdog to avoid.

Poison → `OnServerPoisoned` → background `ReplaceServerInBackgroundAsync` →
`DisposeAfterDrainAsync(PoisonDrainTimeout = 60s)` (`TestResourceBroker.cs:221`, `:1798`). The drain
waits for the lease's refcount to reach 0, **but only for 60s**, then disposes the container anyway
(`ManagedServer.cs:1496-1537`). The test's body holds the lease far past 60s (the recovery waits), so
the drain **times out and removes the container while the test is still running**.

A subsequent **unguarded** Docker call then hits the removed container. The prime suspect is
`NetworkOutageHelper`: its `ReconnectAsync` / `DisconnectAsync` / `GetAttachedNetworkIdAsync` call
`ConnectNetworkAsync` / `DisconnectNetworkAsync` / `InspectContainerAsync` directly via Docker.DotNet
with **no 404 guard** (`NetworkOutageHelper.cs:39-114`), and `ReconnectAsync` runs in the test's
`finally` with a fresh 30s token even after cancellation. A throw there is the bare test result in
`phase: test_body` — matching the error shape exactly.

Not the leak: `DockerOps.*` already swallow `DockerContainerNotFoundException` + 404
(`DockerOps.cs:67-72`); `ServerContainer.DisposeContainerSafely` catches; the recorder has its own
dead-container latch (`ContainerRecorder.cs:131-149`). `NetworkOutageHelper` is the one place making
raw Docker calls in the test's own chain without that guard.

## Why only this test
It is the only test that (a) deliberately drives its server into an error state (NIC cut) that trips
the log-error scan, (b) has a body that outlives the 60s drain, and (c) makes raw Docker calls
mid-body. Remove any one and the race closes.

## Fix (two levers — do both)
1. **Root cause — stop the mid-test poison.** Suppress the log-error→poison path during the
   intentional outage, mirroring how the test already suspends the watchdog. The cut is *expected* to
   produce server errors; treating them as a poison-worthy crash is the defect. Options:
   - Have `SuspendHealthChecks()` also gate the `_errorCancellation` poison callback (a
     `_healthSuspended` check in the `ManagedServer.cs:945-952` callback), so one suspend covers both
     the watchdog and the log-error scan; or
   - Add a dedicated outage-scoped error-scan suspend bracketing the cut in the test.
   Prefer whichever keeps the suspend/resume visible at the call site (see `NetworkOutageHelper`'s
   class doc, which already mandates the watchdog bracket). This stops the poison → dispose entirely,
   so the container survives the whole test.
2. **Hardening — make `NetworkOutageHelper` 404-tolerant.** Wrap its three Docker.DotNet calls to
   swallow `DockerContainerNotFoundException` / 404, mirroring `DockerOps.ForceRemoveContainerAsync`
   (`DockerOps.cs:67-72`). Defense-in-depth: even if a container vanishes, the reconnect/finally
   doesn't surface a raw `NotFound`. `ReconnectAsync` hitting a gone container should no-op (nothing
   to reconnect); `GetAttachedNetworkIdAsync` already throws `InvalidOperationException` on no-network
   — extend it to treat a gone container the same controlled way.

The end-of-test `PoisonServer(TestRetiredServer)` is correct and must stay — it retires the
NIC-damaged server so no later test reuses it (`test-broker-invariants.md`). Fix (1) only suppresses
the *spurious* mid-outage poison from the log-error scan, not the intentional end-of-test one.

## Verification (this flakes under load — a green run does not prove the fix)
After (1), a real run's `diagnostics/infrastructure.jsonl` must show **no `server_poisoned` event for
this test during the outage window** (between the cut and the end-of-test retire). Its absence is the
signal. Reproduce against the full suite under load, not a single FILTER run; the race needs the
~10min recovery body to outlast the 60s drain.

## Compatibility check
- The log-error scan also guards every *other* test against real server crashes — gating it on
  `_healthSuspended` must NOT leave it suspended outside an intentional transition. Confirm every
  `SuspendHealthChecks` caller pairs with a `ResumeHealthChecks` (or, for this test, that the server
  is poisoned/retired so the scan never needs to resume).
- This test leaves the watchdog suspended for the rest of the run by design
  (`GalaxyOutageReproTests.cs:144-154`); the server is retired immediately after, so the never-resumed
  error scan is harmless here (no later test reuses this server).
