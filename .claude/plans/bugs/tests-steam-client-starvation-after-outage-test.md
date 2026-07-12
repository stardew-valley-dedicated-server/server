# Steam-client starvation: TotalConnectivityLoss kills the only Steam client, no test after it can get one → run-stall watchdog abort

Status: FIX DESIGNED, ready to implement — all decisions resolved, edits enumerated below.
Root cause confirmed from TWO consecutive runs with identical mechanism:
- `2026-06-30T09-01-14Z_864566d` — 149 passed, `aborted:true`, `child-stall-watchdog`,
  `notDispatched:1`, watchdog idle 632s, 1 outstanding lease.
- `2026-06-30T13-01-39Z_864566d` — 147 passed, `aborted:true`, `child-stall-watchdog`,
  `notDispatched:3`, watchdog idle 647s, 2 outstanding leases.

In both aborted runs the hung test is `GalaxyReloginGate_WhileHealthy_SkipsAndKeepsClientConnected`
(`status:other, duration:0` in `ctrf-report.json` — dispatched, never recorded).

## Symptom

The run completes nearly all tests, then a Steam test hangs with no further telemetry until the
last-resort run-stall watchdog cancels the whole run:

```
[watchdog] run stalled 632s with 1 lease(s) outstanding — cancelling run to break the deadlock
steam-farm0-CabinStack-c18 replacement FAILED on vps-1: The operation was canceled.
```

The `replacement FAILED … operation was canceled` line is a DOWNSTREAM SYMPTOM of the watchdog's
own `_runCts.Cancel()` (a background Steam-server replacement was mid-`CreateServerAsync`), NOT the
cause. `summary.json` is written correctly (`aborted:true`) — the watchdog backstop works; the run
just shouldn't have needed it.

## Root cause — the only Steam test client is destroyed mid-run and never recreated

The Steam config has ONE Steam-bearing test client. `SteamAccountAllocator` splits the slice as
account 0 → server pool, accounts 1+ → client pool (`SteamAccountAllocator.cs:48-49`); with the
current `STEAM_ACCOUNTS` sizing `ClientPoolSize == 1`, so exactly one Steam-bearing client
(`client-0`, `steamAccountIndex:1`) exists. It is produced ONCE by `ClientPool.PreWarmCoreAsync`
at prestart (prewarm is the only producer today).

`TotalConnectivityLoss_RecordsGalaxyReauthSignal` cuts the server container's network to induce a
total outage. It reconnects the SERVER container in `finally`, but the test CLIENT was severed and
cannot cleanly disconnect. At lease dispose, `ClientLease.DisposeAsync` catches the failed
`DisconnectAsync()` and calls `_pool.MarkClientDead(Container)` (`ClientLease.cs:80-87`).

`MarkClientDead` (`ClientPool.cs:829-849`):
- releases the Steam ACCOUNT index back to the allocator (so the allocator semaphore has a permit),
- KEEPS the dead container in `_allClients` (for recording extraction — `client-0` stays counted
  toward the cap `_maxContainers = host.ClientCapacity.Capacity`, `ClientPool.cs:93`),
- does NOT signal `_steamAvailable`,
- and there is NO path that recreates a Steam-bearing client.

So after `client_marked_dead` for `client-0`, the pool has zero live Steam-bearing clients and the
freed Steam account is never re-allocated (verified both runs: no `steam_account_allocated kind:client`
and no new `instance_created` after the death; highest client index stays `client-5`).

## Why the next Steam test hangs — and why not every run wedges

The next Steam-required lease calls `LeaseClientAsync(requireSteam:true)`:
- `PoolHasAnySteamBearingClient()` returns false (the dead carcass's `SteamAccountIndex` was reset
  by `ReleaseClientSteamAccount`), so the Steam-wait branch (`ClientPool.cs:182`) is skipped.
- **Below cap the pool already self-heals**: the patience window (`ClientPool.cs:282-330`) expires
  after 20s and the cold-start path (`ClientPool.cs:332-344`) creates a fresh Steam client with the
  freed account. This is why the bug doesn't fire every run.
- **At cap it wedges**: the dead `client-0` still counts in `_allClients`, so with live non-Steam
  clients from other configs the count sits at `_maxContainers` and the lease parks in the cap-wait
  loop (`ClientPool_LeaseAtCap`, `ClientPool.cs:241-274`), which only exits via
  `TryTakeClient(requireSteam:true)` — impossible, no Steam-bearing client will ever return — or a
  discard. Both failing runs were at cap.
- No per-test timeout breaks it: the Galaxy tests read `var ct = TestContext.Current.CancellationToken`
  (raw xUnit token, `GalaxyOutageReproTests.cs:74,262`), while the 300s per-test budget cancels only
  `TestCt` (the linked `_testTimeoutCts`). Budget cancellation never reaches the blocked acquire.

Result: the lease blocks until the run-stall watchdog (`RunStallTimeout` 600s) cancels `_runCts`.

**Residual trap beyond the plan's original 1(b)**: excluding the dead carcass from the cap count is
NOT sufficient on its own. After the Steam client dies, on-demand non-Steam leases can fill the freed
slot, putting the pool back at cap with only live non-Steam containers — a Steam lease then parks in
the same cap-wait loop forever (non-Steam clients return, `TryTakeClient(requireSteam:true)` keeps
failing, `_allClients.Count` never drops). When `PoolHasAnySteamBearingClient()` is false, NO return
can ever satisfy a Steam-required lease — bag, leased, and in-flight are all covered by that check —
so both the cap wait AND the patience wait are provably useless for it. The fix must route such a
lease straight to creation.

## Not the SSH-mux-wedge deadlock

Distinct from `host-poison-deadlocks-run.md`. The `ssh-master-vps-1.log` contains only
`Connection refused` (forwards to the torn-down poisoned Steam server, expected) — ZERO mux
exhaustion, zero forward-heal events. The host is healthy throughout.

## Fix — three layers, all edits enumerated

Design principle (mirrors `host-poison-deadlocks-run.md`): a destroyed shared resource must be
replaceable so a later demand is served, and every blocked waiter must be released or able to
proceed — not orphaned until a watchdog. Eager backfill-on-death (the original Layer 1a) is
dropped per `simplest-solution.md`: the lease-path self-heal below makes it redundant.

### Layer 1 (core) — the lease path self-heals

**Edit 1 — `Containers/GameClientContainer.cs` (~line 93, next to `SteamAccountIndex`):** add

```csharp
/// <summary>
/// True once <see cref="Infrastructure.ClientPool.MarkClientDead"/> has retired this
/// container: it stays in the pool's roster only for recording extraction at dispose
/// and must not count toward the live container cap.
/// </summary>
public bool IsMarkedDead { get; internal set; }
```

**Edit 2 — `ClientPool.MarkClientDead` (`ClientPool.cs:829-849`):** set the flag under the lock
(write-side fence pairing with the counting reads under the same lock):

```csharp
lock (_allClientsLock)
{
    client.IsMarkedDead = true;
}
```

**Edit 3 — `ClientPool.cs`: live-count helper** (dead carcasses hold no leasable client, only a
recording to extract at dispose — they must not consume a cap slot):

```csharp
// Requires _allClientsLock held.
private int CountLiveClientsLocked()
```

(loop over `_allClients`, count `!c.IsMarkedDead`.)

**Edit 4 — `ClientPool.LeaseClientAsync`: use the live count at all three cap/patience sites:**
- cap pre-check (~line 236-239): `currentCount = CountLiveClientsLocked() + _inFlightCreations;`
- cap-loop recompute (~line 270-273): same;
- patience window (~line 285-289): `outstandingClients = CountLiveClientsLocked() - _available.Count;`

**Edit 5 — `ClientPool.LeaseClientAsync`: Steam self-heal route.** Insert between the steam-wait
branch (ends ~line 230) and the cap check:

```csharp
// Self-heal: a Steam-required lease with NO Steam-bearing client anywhere in the
// pool (bag, leased, or in flight — the last one died via MarkClientDead) can never
// be satisfied by a returning client, so the cap and patience waits below would
// park it until the run-stall watchdog. Go straight to cold-start create; the
// account freed by the death is re-allocated to the fresh container. May exceed
// _maxContainers by one transiently when live non-Steam clients hold the cap —
// bounded by the allocator slice (a concurrent second Steam lease routes to the
// steam-wait branch via _inFlightSteamCreations) and preferable to a wedged run.
if (requireSteam && _accountAllocator != null && !PoolHasAnySteamBearingClient())
{
    TestLog.Client("No Steam-bearing client left in pool; creating a replacement");
    InfrastructureEventLog.Emit("steam_client_selfheal", new { available = _available.Count });
    client = await CreateClientGuardedAsync(
        requireSteam: true,
        reason: "steam_selfheal",
        StartPriority.Normal,
        ct
    );
    return new ClientLease(this, client, serverKey);
}
```

The re-check of `PoolHasAnySteamBearingClient()` (already checked false at line 182) closes the
TOCTOU where a prewarm/concurrent creation appeared in between — if it did, fall through to the
normal cap/patience path, which can then legitimately wait for that client's return.

**Edit 6 — `ClientPool.cs` doc updates** (3 comments now stale):
- cold-start comment (~line 332-337): the Steam case no longer reaches it (self-heal route handles
  it); it is now the non-Steam create path (plus `requireSteam` with a null allocator, which
  allocates nothing).
- `PoolHasAnySteamBearingClient` doc (~line 443-451): "must take the cold-start cap-and-create
  path" → names the self-heal route.
- `MarkClientDead` doc (~line 823-828): note the flag + that the next Steam-required lease
  self-heals via `steam_selfheal`.

### Layer 2 — per-test timeout must reach a stuck acquire (raw-token sweep)

Switch test-body reads of `TestContext.Current.CancellationToken` to `TestCt` so the 300s
per-test budget (armed at queue exit, `TestBase.cs:194`) cancels a stuck acquire. Verified safe:

- **Durations**: `TotalConnectivityLoss` passes in 85–145s across the last 5 runs
  (`GalaxyReloginGate` ~20s); its internal 5-min reconnect budget is a defensive bound never
  approached. The slowest passing test in the latest full run is 144s total — no test is near the
  300s active budget, so the sweep cannot newly time out a legitimate test.
- **All 28 target classes derive from `TestBase`** (verified; `LobbyCommandsTestBase` is
  `abstract : TestBase`, so derived classes inherit `TestCt`).
- **Zero usages in static contexts** (verified by mapping every usage to its enclosing method
  signature) — the instance property is reachable everywhere.
- **Pre-arm uses are safe**: `TestCt` falls back to the raw xUnit token until `_testTimeoutCts`
  exists (`TestBase.cs:139`).

Two mechanical replacement patterns, applied per file:
- `var ct = TestContext.Current.CancellationToken;` → `var ct = TestCt;`
- inline argument `TestContext.Current.CancellationToken` → `TestCt`

**Edit 7 — `GalaxyOutageReproTests.cs:74,262`** (the two sites that let this bug reach the
watchdog). The step-5 `finally` restore correctly keeps its own fresh `restoreCts` — unchanged.

**Edit 8 — sweep the remaining 27 files (201 sites, counts as of this audit):**
PasswordProtectionTests 26, CabinStrategyTests 22, ServerApiTests 18, FarmhandManagementTests 16,
HostAutomationTests 15, RenderingTests 14, SaveImportTests 12, CabinPositionPersistenceTests 9,
PlayerTrackingTests 8, PasswordProtectionDisruptiveTests 7, FarmerCreationTests 7,
ServerSettingsTests 6, NoPasswordTests 5, NavigationTests 4, FestivalTests 4,
CabinPlacementValidationTests 4, SteamAppIdTests 3, ModFarmDisambiguationTests 3,
FarmMapTypeTests 3, CropSaverTests 3, CabinStrategyNoneTests 3, LobbyCommandsTestBase 2,
CabinConcurrencyTests 2, AbandonedClaimTests 2, WeddingTests 1, HostFarmhouseUpgradeGuardTests 1,
CabinStrategyFarmhouseStackTests 1.

Do NOT touch `Infrastructure/**`, `Fixtures/**`, `Helpers/**` — the only remaining
`TestContext.Current.CancellationToken` references there are two comments in `TestBase.cs`
(103, 135) plus the `TestCt` fallback itself (`TestBase.cs:139`, uses `?.`); fixture/helper code
takes `ct` parameters by design.

### Layer 3 — fail fast when a Steam lease provably cannot be satisfied

After Layer 1, the only remaining indefinite block for a Steam lease is the cold-start path waiting
on `SteamAccountAllocator.AllocateClientAsync` when no account will ever be released (an account
leak). Legitimate worst case there is ~0s semaphore wait (accounts release eagerly on
death/discard/dispose) plus the ≤90s sidecar readiness probe inside `AllocateAsync`
(`AllocationReadinessBudget`), so a 120s bound cannot false-trip. Note the readiness-probe phase
swallows cancellation and dequeues anyway (existing fall-through, `SteamAccountAllocator.cs:184-187`),
so the bound effectively converts only the semaphore-starvation case — exactly the intended trigger.
Prewarm allocates from a full pool at prestart (wait ≈ 0) and catches per-client exceptions, so it
cannot trip the bound either.

**Edit 9 — `Helpers/TestTimings.cs`** (Fixture Setup Timeouts region): add

```csharp
public static readonly TimeSpan SteamAccountAllocationBound = TimeSpan.FromSeconds(120);
```

with a doc comment stating the ~90s readiness-probe worst case and that expiry means an account
leak (no release coming), so the lease fails fast as infrastructure instead of blocking to the
watchdog.

**Edit 10 — `ClientPool.CreateClientAsync` (~line 626):** wrap the allocation:

```csharp
using var allocCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
allocCts.CancelAfter(TestTimings.SteamAccountAllocationBound);
try
{
    steamAccountIndex = await _accountAllocator!.AllocateClientAsync(allocCts.Token);
}
catch (OperationCanceledException) when (!ct.IsCancellationRequested)
{
    InfrastructureEventLog.Emit(
        "steam_account_allocation_timeout",
        new
        {
            clientIndex,
            boundSec = (int)TestTimings.SteamAccountAllocationBound.TotalSeconds,
        }
    );
    throw new InfrastructureSkipException(
        $"No Steam client account became available within "
            + $"{TestTimings.SteamAccountAllocationBound.TotalSeconds:0}s — all accounts are "
            + "held and none released (likely an account leak after a client death). "
            + "Skipped — infrastructure, not a test defect."
    );
}
```

Propagation verified end-to-end: `CreateClientGuardedAsync`'s catch runs
`PoisonIfTransportFaultAsync` (an `InfrastructureSkipException` doesn't classify as transport → no
poison, no blip-retry) and rethrows → `LeaseClientAsync` → `ResourceLease.LeaseClientAsync`
(pass-through, `ResourceLease.cs:76-103`) → `GetClientAsync` → test body → xUnit dynamic skip via
the `$XunitDynamicSkip$` message prefix. `TestBase.IsInfrastructureTransportFault` already treats
`InfrastructureSkipException` as infrastructure (`TestBase.cs:372`), so no StopOnFail cascade
(`stoponfail-cascade-miscounted-as-failed`). Namespaces need no new usings (`ClientPool` is in
`JunimoServer.Tests.Infrastructure` and already has `using JunimoServer.Tests.Helpers`).

## Compatibility (verified, resolved)

- **LAN vs Steam transports.** LAN clients have `SteamAccountIndex == -1`; the self-heal route and
  the allocation bound are gated on `requireSteam && _accountAllocator != null` — LAN leasing is
  unchanged. `requireSteam` with a null allocator (Steam demanded, no accounts configured) still
  falls through to the old cold-start path, which allocates nothing — unchanged.
- **Cap accounting (`test-broker-invariants.md`).** `DisposeAsync` iterates `_allClients` at
  teardown, and marked-dead containers stay in it — recording extraction unaffected.
  `_inFlightCreations` accounting is untouched; the live-count helper only excludes
  `IsMarkedDead`. The self-heal route can exceed `_maxContainers` by one transiently on this rare
  death-recovery path; overshoot is bounded by the allocator slice (`_inFlightSteamCreations`
  routes concurrent Steam leases to the steam-wait branch).
- **`_steamAvailable` ticket bookkeeping.** Unchanged — tickets stay 1:1 with Steam-bearing
  clients in the bag. The self-heal client goes directly to its lease (never enters the bag, no
  Release); its eventual `ReturnClient` releases the ticket, same as any Steam client. The
  "never Release on Discard/MarkClientDead" invariant (`ClientPool.cs:69-75`) holds as-is.
- **Steam-account reuse while the dead container still runs (protocol-invariant check,
  NEW finding).** The self-heal re-allocates the dead client's account while its container is
  still running (kept for recording extraction). Safe: the Steam session lives in the shared
  steam-auth sidecar (containers only fetch app tickets — no per-container SteamKit session, so
  no `LogonSessionReplaced` trample), and the test-client signs into Galaxy exactly once at
  startup (`SignInSteam` has one call site, `ClientAuthService.cs:413`) with NO re-login loop —
  `onLost` only flips state to "lost" (`ClientAuthService.cs:635-639`). If Galaxy kicks the old
  session, the dead container goes "lost" and stays there (its Error log is client-side — loud,
  not poison, per `debugging.md`); if sessions coexist, no interaction. No tug-of-war either way.
  Same-account re-sign-in from a fresh container is already exercised every run by the Steam
  SERVER replacement after `PoisonServer` (account 0).
- **Run-token lifetime / AsyncLocal.** No background task is spawned (Layer 1a dropped); the
  self-heal create runs inside the leasing test's own call, on the test's ct — correct
  attribution, no `SuppressFlow` needed.
- **Exclusive + KeepConnected.** The `TestCt` switch only changes which token the test body
  observes; the exclusive-gate drain uses the broker-side tokens, unchanged.
- **`SERVER_TPS=5` / headless.** No timing assumptions added; all layers are event/slot-driven.
- **Watchdog interaction.** With Layer 1 the lease completes normally; the run-stall watchdog
  stays the last-resort net (0 trips expected).

## Validation (runtime gates, post-implementation)

1. **Build**: `dotnet build tests/JunimoServer.Tests/JunimoServer.Tests.csproj` — also the
   compile-time net for the Layer 2 sweep.
2. **Repro run**: `make test FILTER="GalaxyOutageRepro|AbandonedClaim|NavigationTests"`
   (AbandonedClaimTests and two NavigationTests methods are the other Steam-requiring tests).
   Pre-fix, a Steam test after `TotalConnectivityLoss` hangs to the watchdog when the pool is at
   cap. Post-fix gates, read from the run's `infrastructure.jsonl` + `summary.json`
   (`passing-test-isnt-proof-the-scenario-ran.md` — confirm the events, not just green):
   - `client_marked_dead{clientIndex:...}` (the outage test's client) followed by
     `steam_client_selfheal` + `client_created{reason:"steam_selfheal"}` +
     `steam_account_allocated{kind:"client"}` for the next Steam lease;
   - `aborted:false`, `notDispatched:0`, zero watchdog trips.
   Note: `MarkClientDead` fires EVERY run of the outage test, so `steam_selfheal` appearing once
   per full run (before the next Steam test) is the fix working, not an anomaly.
3. **Full suite**: same pass count as baseline, `aborted:false`, 0 watchdog trips.
4. **Layer 2 fault-injection (optional but recommended)**: temporarily comment out the Edit-5
   self-heal block, run the repro — the stuck Steam test must now fail at ~300s with
   `cancellation_detected{budgetCtsCancelled:true}`, NOT at the 600s+ run watchdog. Restore the
   block. If skipped, say so in the completion report
   (`runtime-post-conditions-are-gates.md`).

## Key references
- `tests/JunimoServer.Tests/Infrastructure/ClientPool.cs` — `LeaseClientAsync` (steam-wait 182-230,
  cap 232-274, patience 282-330, cold-start 332-344), `TryTakeClient` (359-424),
  `PoolHasAnySteamBearingClient` (452-471), `CreateClientGuardedAsync` (473-601),
  `CreateClientAsync` allocation (626), `MarkClientDead` (829-849), `_maxContainers` (93).
- `tests/JunimoServer.Tests/Infrastructure/ClientLease.cs:80-87` — the `MarkClientDead` trigger.
- `tests/JunimoServer.Tests/Containers/GameClientContainer.cs:93` — `SteamAccountIndex`
  (Edit-1 site).
- `tests/JunimoServer.Tests/Infrastructure/SteamAccountAllocator.cs:88-121,131-200` —
  `AllocateAsync` semaphore + readiness probe (the Layer-3 bound's target).
- `tests/JunimoServer.Tests/Infrastructure/InfrastructureSkipException.cs` — dynamic-skip carrier.
- `tests/JunimoServer.Tests/Infrastructure/TestBase.cs:130-139,161-195,304-391` — `TestCt`, budget
  arming, infrastructure-skip classification.
- `tests/JunimoServer.Tests/GalaxyOutageReproTests.cs:74,262` — raw-`ct` sites (Edit 7).
- `tests/test-client/Auth/ClientAuthService.cs:413,635-639` — single sign-in, no re-login loop
  (account-reuse safety).
