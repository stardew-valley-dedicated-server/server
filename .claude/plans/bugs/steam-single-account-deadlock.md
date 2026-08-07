# Steam single-account deadlock (CI run 29355730052)

## Status
Root-caused and **log-verified** against the run's job log (E2E (VPS) job 87162807554). Fix scope chosen: **R2 + R3 + R1** (below). Not yet implemented.

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

**R1 — trigger: the poison kills the healthy Steam client.** `TotalConnectivityLoss` ends with `PoisonServer` (`GalaxyOutageReproTests.cs:242`), which cancels the server's ErrorToken. The lease-cleanup disconnect (`ClientLease.DisposeAsync` → `Container.DisconnectAsync()`, `ClientLease.cs:76-88`) then throws `A task was canceled` → `MarkClientDead`. The test's comment ("the client is already disconnected and no client op remains", `GalaxyOutageReproTests.cs:240`) is **false** — the lease disposal's disconnect is a remaining client op. `MarkClientDead` (`ClientPool.cs:829-849`) releases the Steam account, sets `SteamAccountIndex = -1`, and keeps the dead container in `_allClients`.

**R2 — the actual deadlock: a Steam-required cold-start can starve forever at the container cap.** With no Steam-bearing client, `PoolHasAnySteamBearingClient()` correctly routes Steam leases to the cold-start cap path (`ClientPool.cs:232-274`). But the cap check `_allClients.Count + _inFlightCreations` counts the dead container (6/6 with only 5 live), and the cap loop only proceeds to create after a **discard** lowers the count — returns don't, idle LAN clients are never discarded, and the dead-marked one is deliberately never discarded. So the replacement Steam client can never be created. The wedge persisted after every LAN client sat idle in the bag (18:28 onward). The same wedge is reachable with 6 *live* LAN containers and no dead one — any run where the pool hits cap before a post-client-loss Steam lease arrives.

**R3 — duplicate same-key server creation: the dedup window.** `ReplaceServerInBackgroundAsync` increments `_creationsInFlight` only **after** the poisoned server's `DisposeAfterDrainAsync` (`TestResourceBroker.cs:1785-1829`) — up to 60s (`PoisonDrainTimeout`). In that window the on-demand spawn predicate (`:1109-1126`) sees `inFlight==0` and a reset queue and spawns a second creation for the same key. Both then race for account 0; the loser (`server-9`) acquires a host server slot (`:1502`) *before* the account (`:1537`) and holds it while blocked indefinitely.

Ordering intact from the original investigation: `TotalConnectivityLoss` poisoning its server is **intentional and documented** (`test-broker-invariants.md`) — on remote Docker the daemon never restores the published-port forward, so the server must be retired. Do not "fix" the port-forward.

## Refuted framings (do not re-derive)

- **"LAN tests saturate the 6 client slots, starving the Steam tests."** Refuted by the log: from 18:28 all 5 live LAN clients were idle in `_available` and the wedge persisted. The blocker is the phantom cap count + no make-room path, not LAN leaseholders.
- **"Circular wait between the replacement and server-8's tests."** Not circular. Account 0 is released at idle shutdown when refs and demand hit zero (`TestResourceBroker.cs:2211-2228`, observed for `lan-farm0-CabinStack-c2` at 18:24:20). Had the 3 tests been able to lease clients, `server-8` would have idled out, freed account 0, and `server-9` would have booted pointlessly and been reaped by the no-demand safety net (`:1339`). The `server-9` wedge is waste, not the run-killer — **fixing the server race alone would NOT have saved this run.**
- **"The Steam client lease path is deadlock-free as long as the single Steam client returns."** The premise is exactly what broke: the client did not return — it was marked dead by R1.

## Fixes (chosen scope)

### R2 — ClientPool cap make-room (must-fix, the deadlock)
Invariant: **a Steam-required lease must never park at the cap while an idle discardable client exists.** In `LeaseAsync`'s cap-wait loop (`ClientPool.cs:241-274`), when `requireSteam && !PoolHasAnySteamBearingClient()`: before parking on `_returnSignal`, take an idle client from `_available` and `DiscardClient` it (removes it from `_allClients`, dropping the count below `_maxContainers`), then fall through to the cold-start create. If the bag is empty, wait for a return and retry the take-and-discard on wake. Bookkeeping is already safe: discarding an idle non-Steam client touches no `_steamAvailable` tickets, and `DiscardClient` signals `_returnSignal` for other waiters. Covers both the dead-phantom variant (this run) and the all-live-LAN variant.

### R3 — close the broker dedup window (small)
In `OnServerPoisoned` (`TestResourceBroker.cs:1690-1718`), take `_creationLocks[brokerKey]` and increment `_creationsInFlight` atomically with the `queue.Reset()` before scheduling the replacement; delete the late increment in `ReplaceServerInBackgroundAsync` (`:1829`) but keep its `finally` decrement (the pairing now spans two methods — comment it). The on-demand predicate can then never see `inFlight==0` during the dispose-drain: the two-live-Steam-servers state becomes unreachable. Key-scoped, no lock-order change, LAN untouched.

### R1 — don't let the poison kill the healthy client (small)
First trace which token actually canceled `Container.DisconnectAsync()` (the cancel landed 27s after `PoisonServer`). Then either have the outage test disconnect in-body before poisoning and set `ClientLease.AlreadyDisconnected`, or bind the lease-cleanup disconnect to a cleanup-scoped token. Fix the false comment at `GalaxyOutageReproTests.cs:240-241` in the same change. Even with R2, R1 saves a full Steam-client reboot (~60s) mid-batch in every outage run.

### Rejected / deferred
- **Account-before-slot ordering in `CreateServerAsync`** (the original option-A first clause): only matters when two *distinct* Steam server configs contend on one host; the run log confirms the suite has exactly one (`steam-farm0-CabinStack-c18`). Hardening, not needed for this incident.
- **More Steam server accounts** (original option C): unnecessary once R1–R3 land; operational cost.

## Docs sweep (same change)
- `test-broker-invariants.md`: poison-retirement section — lease cleanup after `PoisonServer` must not run client ops on the poisoned token; ClientPool section — document the Steam cold-start make-room invariant.

## Verification gates (runtime, not static — `runtime-post-conditions-are-gates.md`)
- **R2+R1:** run the Steam configs (`GalaxyOutageReproTests`, `AbandonedClaimTests`, `JoinServerTests`, `ServerApiTests`) on one host; in the run trace, after `TotalConnectivityLoss` either `client-0` returns alive (R1) or a replacement Steam client is created after one idle-client discard (R2) — and all Steam tests complete.
- **R3:** grep the trace for `Steam server account 0 allocated` — exactly one live Steam server at any time; after a poison, only the replacement creates (no second `starting up ... (server-N)` for the same key during the drain window).
- **Full suite:** green is necessary, not sufficient (`passing-test-isnt-proof-the-scenario-ran.md`) — read the account-0 alloc/release sequence and confirm no unresolved `steam_account_pool_insufficient` wait.
- Checklist from the broker rules: no `WaitingCount` eviction reintroduced in `ReleaseAsync`; poison-replacement still boots a fresh server for pending demand; LAN path untouched by R2 (branch is `requireSteam && !PoolHasAnySteamBearingClient()` only); `SERVER_TPS=5` unchanged.

## Loose ends (tracked, not in scope)
- `_returnSignal` accumulates unbounded releases with no waiters → a newly-arriving cap waiter hot-drains stale tickets (93 log lines in ~2ms). Cosmetic.
- A dead-kept container occupies a cap slot until run end. Defensible resource-wise once R2 exists (it does still burn host resources); worth a sentence in ClientPool docs.

## Key source references
- `tests/JunimoServer.Tests/Infrastructure/ClientPool.cs:232-274` — cap loop; only discards lower the count. `:829-849` — `MarkClientDead` keeps the container counted. `:452-471` — `PoolHasAnySteamBearingClient`.
- `tests/JunimoServer.Tests/Infrastructure/ClientLease.cs:76-88` — cleanup disconnect failure → `MarkClientDead`.
- `tests/JunimoServer.Tests/Infrastructure/TestResourceBroker.cs:1667-1745` — `OnServerPoisoned`; `:1785-1829` — dispose-drain before the in-flight increment (the dedup window); `:1109-1126` — on-demand spawn predicate; `:1502` vs `:1537` — slot acquired before Steam account; `:2211-2228` — idle shutdown releases the account.
- `tests/JunimoServer.Tests/Infrastructure/SteamAccountAllocator.cs:43-70` — single server account (`_serverSem` size 1).
- `tests/JunimoServer.Tests/GalaxyOutageReproTests.cs:235-245` — intentional poison-and-retire; comment at `:240` is wrong (R1).
