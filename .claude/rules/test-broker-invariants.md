---
paths:
  - "tests/JunimoServer.Tests/Infrastructure/**"
  - "tests/JunimoServer.Tests/Helpers/**"
---

# Test broker invariants

Load-bearing invariants for the test resource broker, capacity gating, and session reuse. Re-introducing any of these has historically re-broken the suite.

## Capacity & Deadlocks — DO NOT

- **DO NOT release capacity between KeepConnected tests** — causes container deadlock AND exclusive deadlock. Sessions hold capacity for entire class duration.
- **DO NOT re-add `WaitingCount` eviction in `ReleaseAsync`** — causes server ping-pong at `MaxServers=1` (15 restarts, 20min vs 6.6min). The fix is `WaitForServerAvailableAsync` blocking without holding resources. (`WaitingCount` IS read live in `TestResourceBroker.cs:973` for a *post-create* eviction — that's a different site and stays. The rule is specifically about adding it back to `ReleaseAsync`.)
- **DO NOT use `[TestServer(Exclusive = true)]` on classes sharing a server with KeepConnected** — deadlock: KeepConnected holds ref, exclusive can't drain.
- **DO NOT re-add client pre-warming in `ManagedServer`** — races with `LeaseClientAsync`, creates duplicate clients. `ClientPool` handles creation-on-demand.

## Test Infrastructure

- **Capacity is per-host, not global.** Each `DockerHost` owns its own `HostCapacityQueue` for server slots and another for client slots, exposed as `host.ServerCapacity` / `host.ClientCapacity`. Each queue carries the priority + settle-window semantics. There is no global capacity gate — multi-host runs gate per-host, and single-host runs route through `HostPool.Instance.First`.
- **Reuse cache is per-host by structure, not by hash input.** `ManagedServer` is bound to its host's `ClientPool`, so the server config hash does NOT include `host.Id` — collisions across hosts are structurally impossible. A test placed on `host0` cannot reuse a server on `host1` even when the configs match (no cross-host container traffic).
- **`MaxClients` for config hash is test demand, not host slot count.** `host.ClientSlots` is a *scheduling* knob; `StartingCabins` derivation is unchanged. Tests still declare `clientsNeeded` via `[TestServer(...)]` and the broker enforces capacity per host.
- **Host disconnect cascades a class's tests.** When a host fails a transport-class call (broken pipe / connection reset / daemon-socket gone), `HostPool.Place` filters the host out for new placements and the cascade self-labels as infrastructure: the `host_disconnected` *event* (with `sshMasterLogTail`) is emitted by `DockerHost.Poison` and projected into `summary.json.infrastructureErrors`; the active test's failure record is stamped `failureCategory: "infrastructure"` (host-poison stamp in `TestBase.RecordTestFailure`); and tests waiting on the broker fail with `ServerUnavailableException`, which classifies as `"infrastructure"`. A `KeepConnected` session pinned to a class on a poisoned host fails the rest of that class the same way (N-test cascade). This is by design — re-placing the class on a different host would defeat the no-auto-retry rule and silently mask the disconnect. The transport-vs-app decision lives in ONE classifier, `TransportFaultClassifier.ClassifyHostTransportFault`, consulted at both mid-run creation seams via `DockerHost.PoisonIfTransportFaultAsync` — server creation (`TestResourceBroker.CreateAndResolveAsync`) and on-demand client leasing (`ClientPool.CreateClientGuardedAsync`). A *bare* `TimeoutException` is ambiguous (slow-but-live server vs dead tunnel) and only poisons when corroborated by a failed `ssh -O check` against the host's master (`TunnelManager.IsMasterAliveAsync`, remote hosts only) — do NOT "simplify" this into poisoning on every timeout, or a slow server will cascade healthy tests. The `-O check` probes master-process liveness, not tunnel liveness, so it has a bounded (~30s, self-healing) false-negative window for the bare-timeout-while-master-still-alive case; a genuinely dead forward throws `SocketException`/`HttpRequestException`, which the classifier catches directly without corroboration.
- **The `-f`-forked SSH master's `-E` log must use `LogLevel=INFO` and only captures the silent-timeout drop.** OpenSSH's "Timeout, server not responding" line (the `ServerAliveCountMax`-exceeded drop) is `LOG_INFO`, so `LogLevel=ERROR` suppresses the one line the `-E` log exists to capture — use `INFO`. Capture is drop-mode-dependent: a silent/timeout drop lands in `-E`, but an abrupt RST reaches **neither** `-E` nor a shell `2>>` (the `-f` fork reparents the child's stderr). So `-E` is silent-timeout-focused; the RST case surfaces as a `SocketException` the classifier catches directly — don't expect the fd-2 redirect to rescue it.
- Each host's `StartLimiter` bounds concurrent Docker container starts on that daemon (default `SDVD_MAX_CONCURRENT_STARTS`, optional per-host `concurrentStarts` override). Poison releases pending waiters. Three priority bands: **High** (server starts), **Normal** (on-demand client leases), **Low** (client pre-warm). Highest-priority waiter wins on every release; FIFO within a band. **No preemption** — a High waiter cuts the queue but cannot evict a slot already held by a Low waiter, so worst-case server-start contention with a full Low queue is bounded by the slowest in-flight pre-warm. Prestart fans out servers and pre-warm concurrently; the priority bands prevent the 2026-04-30 starvation where a flood of clients held all limiter slots past the 120s server `StartupTimeout`.
- `Release()` drains the queue synchronously. Separate release + reacquire is NOT atomic — other waiters get served in between.

## Session Liveness

- **A liveness gate must cross-check client belief against server truth.** `PersistentSession.IsAliveAsync` originally only checked the test-client's `IsConnected` flag. The client can believe it's connected after the server has evicted the peer — reuse then proceeded and `GrantAdmin` failed for 10s with "player not found" (`getAllFarmers()` didn't contain the uid). Any "is this session still usable?" check that reads only one side of the connection is not a liveness gate. Require both: client-side `GetState().IsConnected` AND server-side `WaitForPlayerByIdAsync(uid)` within a short budget (`SessionRevalidationBudget`, 2s).
- **`/players` snapshot ⇔ `getAllFarmers()` for connected peers.** Both derive from `Game1.otherFarmers` — `/players` iterates `otherFarmers.Roots` via `FarmerCollection`, and `getAllFarmers()` filters `farmhandData` by `isActive() == otherFarmers.ContainsKey(uid)`. So `/players` is a reliable precondition gate for any `getAllFarmers()`-based mod endpoint.

## Polling Budgets

- **Outer polling budget must be ≠ the inner per-request timeout, or retries are impossible.** `TestTimings.PollingRequestTimeout` (5s) is a *per-HTTP-request* guard inside helpers like `WaitForPlayerByIdAsync` (`ServerApiClient.cs:1469`). Passing it as the *outer* polling timeout means a single slow request eats the whole budget — zero retries. Define a separate outer budget (e.g. `SessionRevalidationBudget = 2s` mirrors `FarmerRemovalBudget = 2s`) sized for the happy path on a cached endpoint (`/players` responds in <50ms). Inner and outer timeouts live next to each other in `TestTimings.cs`; easy to mix up.

## Server Config Keys

- **`config-{hash}` keys identify reusable servers.** Same config produces the same server key, regardless of `SharedAssembly` vs `SharedClass` lifetime — the broker reuses an existing matching server.
- **Config hash inputs**: password, starting cabins, farm type, max players, allow IP connections, Steam authentication, cabin strategy. Changing any of these splits the cache.
- **Different `clientsNeeded` test demand produces different config hashes.** `StartingCabins` is derived from a test's `[TestServer(Clients = N)]` value, so two tests requesting different N split the cache even with otherwise identical config.

## Diagnostic Snapshots

- **Snapshot/diagnostic reads of contention state must be racy-but-pure reads.** When adding a snapshot delegate that reads state for an event payload (refcount, queue depth, available slots), the read must NOT acquire a lock the wait path itself holds (deadlock risk) and must NOT read non-thread-safe collections concurrently. `SortedSet<T>.Count`, `List<T>.Count` guarded by an external lock — both unsafe to read outside that lock. Safe alternatives: `ConcurrentBag<T>.Count`, `Volatile.Read(ref _intField)`, `volatile` fields, or plain value-type reads (racy but won't crash; "approximately right" is acceptable for diagnostics). Caught during design of wait-tracing snapshots: a proposed `queueDepth = Waiters.Count` would have read a `SortedSet<Waiter>` guarded by `ClientCapacity.Lock` from outside that lock — concurrent with `Add`/`Remove` it could throw or return garbage. Same shape applies to `_allClients.Count` in `ClientPool` (guarded by `_allClientsLock`). Snapshot delegates additionally must have no side effects.

**Why:** Each invariant is a real bug we hit and engineered around. The capacity rules were the most expensive — `WaitingCount`-eviction caused 20-minute runs at `MaxServers=1` until the wait-without-holding fix; `[TestServer(Exclusive=true)]` + KeepConnected deadlocked on the first attempt to combine them. The session-liveness gate was added after `GrantAdmin` failed for 10s in `getAllFarmers()` because the client believed it was still connected.

**How to apply:** When editing broker, `ClientCapacity`, `ClientPool`, `ManagedServer`, `PersistentSession`, or `TestTimings`, walk the relevant section above first. Especially before "simplifying" any of the DO NOT rules — the apparent simplification is the bug being warned against.
