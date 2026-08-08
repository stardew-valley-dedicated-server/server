# Steam single-account deadlock (CI run 29355730052)

## Status
Root-caused and **log-verified** against the run's job log (E2E (VPS) job 87162807554). Fix scope: **R2 + R3 + R1**.
- **R2 landed on master separately** (#495, `fix(tests): recreate the only Steam client after an outage kills it`): `IsMarkedDead` carcasses no longer count toward the container cap, and a Steam-required lease with no Steam-bearing client anywhere routes straight to a cold-start create (`steam_client_selfheal`) instead of parking — plus a `TestCt` sweep (the 300s per-test budget reaches a stuck acquire) and a 120s Steam-account allocation bound (account-leak fail-fast).
- **R3 + R1 + docs sweep**: this change.

## Symptom
E2E (VPS) run `29355730052` aborted at **157/160 (151 passed, 6 skipped)** after ~10 min of zero progress. Two watchdogs fired:
- In-process broker watchdog first: `[watchdog] run stalled 629s with 2 lease(s) outstanding — cancelling run to break the deadlock` (18:38:25), `abortReason: child-stall-watchdog`.
- CI stall-watchdog: `no test progress for 733s (threshold 720s)` (18:40:10), then `CatastrophicError: exit code 137`.

Exit 137 is the SIGKILL consequence of the watchdog cancelling `_runCts`, not the cause. This was a genuine deadlock; the watchdogs worked as designed.

## Verified timeline (from the job log)

| Time | Event |
|---|---|
| 18:14:57 | Steam server account 0 → `server-4` (config `steam-farm0-CabinStack-c18`, the only Steam config in the suite) |
| 18:17:19.095 | `server-4` poisoned by `TotalConnectivityLoss` (intentional retirement, see below); account 0 released; replacement scheduled (`refs=1, pending=4`) |
| 18:17:19.0953 | **on-demand creation `server-8` spawns for the same key** — same millisecond as the poison callback, inside the dedup window (R3) |
| 18:17:46 | `client-0` — the pool's **only Steam-bearing client** — `disconnect failed, marking dead: A task was canceled`; Steam account 1 released, container kept in `_allClients` for recording extraction (R1) |
| 18:17:56 | replacement `server-9` begins creating (after ~37s dispose-drain of `server-4`) |
| 18:22:35 / 18:23:03 | `server-8` wins a server slot, takes account 0, becomes ready; pending Steam tests attach to it |
| 18:23:12 → 18:27:56 | the 3 client-needing Steam tests wedge in `ClientPool` — 93× `At container cap (6/6), waiting for return...` (R2) |
| 18:24:20 | `server-9` gets a server slot, then blocks forever in `AllocateServerAsync` on `_serverSem` (account 0 held by live `server-8`) |
| 18:28 → 18:38 | all LAN tests done, their 5 clients **idle in the bag**; zero progress |
| 18:38:25 | broker watchdog cancels `_runCts`; `server-9` slot released; `replacement FAILED on vps-1: The operation was canceled.` |

The three unfinished tests — `JoinServer_WithInviteCodeFromApi`, `AbandonedClaim_OnDisconnect_IsClearedDurably`, `GalaxyReloginGate_WhileHealthy` — all held client-capacity slots and healthy `server-8` by 18:23:44. They never touched `server-9`. (`ServerApi_GetInviteCode` needs no client and passed at 18:23:05.)

## Root cause — three layered causes, in causal order

**R1 — trigger: the poison kills the healthy Steam client.** `TotalConnectivityLoss` ends with `PoisonServer` (`GalaxyOutageReproTests.cs:242`), which cancels the server's ErrorToken. `TestBase.GetClientAsync` binds the client's ambient HTTP token to that ErrorToken (`TestBase.cs:456`), so BOTH cleanup client-op sites die: the graceful title navigation (`TestLifecycle.RunCleanupCoreAsync`) and the lease-cleanup disconnect (`ClientLease.DisposeAsync` → `Container.DisconnectAsync()`), which throws `A task was canceled` → `MarkClientDead`. The test's original comment ("the client is already disconnected and no client op remains") was **false** — the lease disposal's disconnect is a remaining client op. `MarkClientDead` releases the Steam account, sets `SteamAccountIndex = -1`, and keeps the dead container in `_allClients`.

**R2 — the actual deadlock: a Steam-required cold-start starved forever at the container cap.** With no Steam-bearing client, the Steam lease routed to the cap path, where the check `_allClients.Count + _inFlightCreations` counted the dead container (6/6 with only 5 live) and only discards lowered the count — returns don't, idle LAN clients are never discarded, the dead-marked one deliberately never. Fixed by #495 (see Status).

**R3 — duplicate same-key server creation: the dedup window.** `ReplaceServerInBackgroundAsync` incremented `_creationsInFlight` only **after** the poisoned server's `DisposeAfterDrainAsync` — up to 60s (`PoisonDrainTimeout`). In that window the on-demand spawn predicate saw `inFlight==0` and a reset queue and spawned a second creation for the same key. Both then raced for account 0; the loser (`server-9`) acquired a host server slot *before* the account and held it while blocked indefinitely.

Ordering intact from the original investigation: `TotalConnectivityLoss` poisoning its server is **intentional and documented** (`test-broker-invariants.md`) — on remote Docker the daemon never restores the published-port forward, so the server must be retired. Do not "fix" the port-forward.

## Refuted framings (do not re-derive)

- **"LAN tests saturate the 6 client slots, starving the Steam tests."** Refuted by the log: from 18:28 all 5 live LAN clients were idle in `_available` and the wedge persisted. The blocker is the phantom cap count + no make-room path, not LAN leaseholders.
- **"Circular wait between the replacement and server-8's tests."** Not circular. Account 0 is released at idle shutdown when refs and demand hit zero (`TestResourceBroker.cs`, observed for `lan-farm0-CabinStack-c2` at 18:24:20). Had the 3 tests been able to lease clients, `server-8` would have idled out, freed account 0, and `server-9` would have booted pointlessly and been reaped by the no-demand safety net. The `server-9` wedge is waste, not the run-killer — **fixing the server race alone would NOT have saved this run.**
- **"The Steam client lease path is deadlock-free as long as the single Steam client returns."** The premise is exactly what broke: the client did not return — it was marked dead by R1.

## Fixes

### R2 — Steam lease must never park on waits it cannot win (landed, #495)
Landed on master before this change, with a different mechanism than this plan originally sketched: instead of a make-room discard at the cap, `MarkClientDead` carcasses are excluded from the live cap count (`IsMarkedDead` / `CountLiveClientsLocked`), and a Steam-required lease with no Steam-bearing client anywhere self-heals straight to a cold-start create. Covers both the dead-phantom variant (this run) and the all-live-LAN variant.

### R3 — close the broker dedup window (this change)
In `OnServerPoisoned`, take `_creationLocks[brokerKey]` and increment `_creationsInFlight` atomically with the `queue.Reset()` before scheduling the replacement; the late increment in `ReplaceServerInBackgroundAsync` is deleted and its whole body moved inside the try whose `finally` decrements (the pairing spans two methods — matching the caller-bumps/callee-decrements split the `CreateAndResolveAsync` spawn sites already use). The on-demand predicate can then never see `inFlight==0` during the dispose-drain: the two-live-Steam-servers state becomes unreachable. Key-scoped, no lock-order change (`ServerQueue` is lock-free), LAN untouched.

### R1 — don't let the poison kill the healthy client (this change)
Token trace: the canceled token was the client's ambient `GameTestClient.CancellationToken`, bound to the server's ErrorToken (at incident time in `TestBase.GetClientAsync`; now canonically in `ResourceLease.LeaseClientAsync` for primary and additional clients alike) and consumed by every ambient-token HTTP call (`PostAsync`, parameterless `GetAsync`). Fix at both cleanup client-op sites: `TestLifecycle.RunCleanupCoreAsync` rebinds the ambient token to the cleanup budget for the graceful title-navigation phase, and `ClientLease.DisposeAsync` runs the fallback disconnect on its own `CleanupTimeout`-bounded token — then always resets the ambient token to `None` before pool return (also closing a latent leak: a reused client container previously carried the *previous* test's ErrorToken until the next primary lease rebound it). A disconnect failure now means the client itself is broken, so `MarkClientDead` is deserved. The false comment in `GalaxyOutageReproTests` is corrected in the same change. Even with R2, R1 saves a full Steam-client reboot (~60s) mid-batch in every outage run.

### Adversarial-review hardening (same branch, second commit)
- `ManagedServer.PoisonServer` is now one-shot (`Interlocked` gate; a suppressed second poison logs its reason). Without it, two racing poison sources invoked `OnServerPoisoned` twice and scheduled two same-key replacements — the double-creation race R3 closes on the on-demand path stayed reachable via double-poison.
- `TestLifecycle.RunCleanupCoreAsync`: a cleanup-budget expiry during the disconnect phase no longer rethrows past the farmer-delete block — it falls through so the delete loop's cancellation handling poisons the lease (`PoisonOnCleanupFailureIfNeeded`). Previously (pre-existing, marginally widened by the rebind) expiry there skipped the poison and leaked the test's farmers onto a reusable server.
- ErrorToken binding moved to the single choke point `ResourceLease.LeaseClientAsync`, so additional clients (`TestBase.LeaseClientAsync`, `SecondFarmer`) get the same fast-abort-on-server-death binding the primary always had; the `TestBase.GetClientAsync` one-off is gone.

### Rejected / deferred
- **Make-room discard at the cap** (this plan's original R2 mechanism): rejected in favor of #495's self-heal — the discard reintroduces a wait path when the bag is empty (the exact shape that wedged) and destroys a warm LAN container as a side effect; the self-heal's transient cap overshoot is bounded by the Steam client slice (1) and the path becomes a rare backstop once R1 prevents the client death.
- **Account-before-slot ordering in `CreateServerAsync`**: only matters when two *distinct* Steam server configs contend on one host; the run log confirms the suite has exactly one (`steam-farm0-CabinStack-c18`). Hardening, not needed for this incident.
- **More Steam server accounts**: unnecessary once R1–R3 land; operational cost.

## Docs sweep (same change)
- `test-broker-invariants.md`: poison-retirement bullet — lease cleanup after `PoisonServer` runs client ops on cleanup-scoped tokens, never the ErrorToken; new DO-NOT bullets for the Steam self-heal invariant and the `_creationsInFlight` bump placement.

## Verification gates (runtime, not static — `runtime-post-conditions-are-gates.md`)
- **R2+R1:** run the Steam configs (`GalaxyOutageReproTests`, `AbandonedClaimTests`, `JoinServerTests`, `ServerApiTests`) on one host; in the run trace, after `TotalConnectivityLoss` `client-0` returns alive (R1: no `client_marked_dead`, a `client_returned` instead) — and all Steam tests complete. (`steam_client_selfheal` remains the R2 backstop if the client is genuinely broken.)
- **R3:** grep the trace for `Steam server account 0 allocated` — exactly one live Steam server at any time; after a poison, only the replacement creates (no second `starting up ... (server-N)` for the same key during the drain window).
- **Full suite:** green is necessary, not sufficient (`passing-test-isnt-proof-the-scenario-ran.md`) — read the account-0 alloc/release sequence and confirm no unresolved `steam_account_pool_insufficient` wait.
- Checklist from the broker rules: no `WaitingCount` eviction reintroduced in `ReleaseAsync`; poison-replacement still boots a fresh server for pending demand; LAN path untouched; `SERVER_TPS=5` unchanged.

## Loose ends (tracked, not in scope)
- `_returnSignal` accumulates unbounded releases with no waiters → a newly-arriving cap waiter hot-drains stale tickets (93 log lines in ~2ms). Cosmetic.
- A dead-kept container keeps running until run end (it no longer counts toward the cap since #495, but still burns host resources).

## Key source references
- `tests/JunimoServer.Tests/Infrastructure/ClientPool.cs` — cap loop (`CountLiveClientsLocked`), `steam_client_selfheal` route, `MarkClientDead`.
- `tests/JunimoServer.Tests/Infrastructure/ClientLease.cs` — cleanup disconnect on a cleanup-scoped token; ambient-token reset before pool return.
- `tests/JunimoServer.Tests/Infrastructure/Fixture/TestLifecycle.cs` — graceful-disconnect phase rebinds the ambient token to the cleanup budget.
- `tests/JunimoServer.Tests/Infrastructure/TestResourceBroker.cs` — `OnServerPoisoned` (bump + reset atomically, before scheduling); `ReplaceServerInBackgroundAsync` (decrement-only finally); on-demand spawn predicates.
- `tests/JunimoServer.Tests/Infrastructure/SteamAccountAllocator.cs` — single server account (`_serverSem` size 1).
- `tests/JunimoServer.Tests/GalaxyOutageReproTests.cs:235-249` — intentional poison-and-retire; comment now documents the cleanup-token behavior.
- `tests/JunimoServer.Tests/Clients/GameTestClient.cs` — ambient `CancellationToken` consumed by `PostAsync`/parameterless `GetAsync`.
