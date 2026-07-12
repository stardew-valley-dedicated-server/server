# Cross-process occupancy gate for the shared VPS test runner

## Context

Local `make test` and CI `make test` both target the **same** remote Docker daemon (the VPS) over SSH. Each run creates containers/networks/volumes labeled `sdvd.test=true` + `sdvd.run-id={runId}`. The problem: when run B starts while run A is still live, run B's **startup sweep deletes run A's containers and kills run A mid-flight**.

The killer is `EmergencyCleanup.SweepStaleResourcesAsync` (`tests/JunimoServer.Tests/Helpers/EmergencyCleanup.cs:323-371`), invoked as the "Cleanup leftovers" phase in `Program.cs:536-597` right after preflight. It **always** sweeps the broad `sdvd.test=true` label (not run-id) — by design, to reap orphans from *prior* runs whose run-ids it can't know. On a shared daemon it can't distinguish a dead prior run's orphans from a concurrently-live run's containers. `TestRunRegistry` (`TestRunRegistry.cs`) is a process-local `HashSet<string>` — invisible across two OS processes, and not consulted by the sweep. **There is no cross-process coordination today.**

The only substrate both processes share is the Docker daemon, so the gate lives there.

**Outcome:** a starting run detects another live run on any shared host and aborts immediately, instead of sweeping it away. A crashed run's stale marker self-heals via a heartbeat that goes stale.

### Fixed decisions (from the user — design to these)
1. **On conflict: abort immediately** — fail fast with "VPS busy with run X, started Nm ago". No wait, no queue.
2. **Priority: symmetric** — first to acquire the lease wins; the other aborts. No preemption, no side wins.
3. **Stale-lease reclamation: heartbeat + container fast-path** (refined after adversarial review — see below).

### Why a plain container-presence liveness probe is WRONG (adversarial review, Critical)

Test containers are first created inside the xUnit child's `runner.Run()` at **`Program.cs:832`** — *minutes* after the gate, separated by the sweep (536), image build (599), image distribution (639, "~minutes for multi-GB images" per the code's own comment), and game-data distribution (702). So a **healthy** foreign run that's still in its own startup phase has created its lease but has **zero `sdvd.run-id` containers**. A probe that lists containers and treats `count==0` as "holder dead" would reclaim a live run's lease and let both runs proceed → the exact collision reintroduced, precisely in the common case (two runs starting within minutes is when build/distribution is in flight). The reclaim signal must therefore span the **whole run lifetime**, not just the container-execution window.

**Resolution: a heartbeat.** The holder refreshes a timestamp on its lease every ~30 s from a background task that lives the entire parent-process lifetime. A foreign lease is reclaimable only when its heartbeat is **stale** (older than ~2.5 min) AND it has no live containers. A live run never trips this — it refreshes continuously through build/distribute/execute; a crash self-heals in ~2.5 min. Container-presence remains a positive "definitely alive ⇒ abort now" fast-path. (CI's job `timeout-minutes: 120` is the worst-case run length; a static TTL would have to be ≥2 h, blocking everyone that long after a crash — the heartbeat avoids that.)

## Design

### Lease marker = a labeled Docker **volume**, heartbeat carried by re-creation

The marker must be (a) visible to both processes via the existing `host.ApiClient` Docker API, (b) torn down by the existing run-id cleanup, (c) **survive** the broad `sdvd.test=true` sweep that runs right after acquire, and (d) carry a **refreshable** heartbeat timestamp.

A **volume** labeled `sdvd.run-id={ourRunId}` + `sdvd.lease=true` + `sdvd.heartbeat-utc={epochMs}`, but **NOT `sdvd.test=true`**, satisfies (a)–(c):
- Broad sweep filters volumes by `sdvd.test=true` (`EmergencyCleanup.cs:356-362`) → no match → **cannot delete our lease**. Decisive reason it's a volume-without-`sdvd.test`, not a container (a container marker would be swept). Verified: no other volume-removal path in the repo touches it — all removals are exact-name or filtered by `sdvd.test=true` / `sdvd.run-id={ourRunId}`, and every container remove passes `RemoveVolumes=false` (`DockerOps.cs:63,145`); no `volume prune`.
- `sdvd.run-id` label means abort-path bulk removal (`BulkRemoveVolumesByLabelAsync`, `EmergencyCleanup.cs:201-204,280-289`) tears it down for free.

**Heartbeat carrier — (d).** Docker volume **labels are immutable** after create, so the heartbeat can't be an in-place label update. Refresh by **rm-then-create under the same name** with a fresh `sdvd.heartbeat-utc` label, reusing `DockerOps.RemoveVolumeAsync` + `CreateVolumeAsync` (no new Docker surface, no marker image, no exec). The rm→create gap is sub-millisecond; mitigation for a foreign probe catching the gap is below (it re-reads, and an absent lease just means "acquire normally" — never a false reclaim of a live run, because the holder immediately re-creates).

### New helper `OccupancyGate` (`tests/JunimoServer.Tests/Helpers/OccupancyGate.cs`)

```csharp
internal static class OccupancyGate
{
    private const string LeaseLabel = "sdvd.lease";          // marker discriminator
    private const string RunIdLabel = "sdvd.run-id";         // holder identity
    private const string HeartbeatLabel = "sdvd.heartbeat-utc"; // epoch ms, refreshed
    internal static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan HeartbeatStaleAfter = TimeSpan.FromSeconds(150); // ~5 missed beats
    internal static string LeaseVolumeName(string runId) => $"sdvd-lease-{runId}";

    internal sealed record HostGateResult(
        string HostId, bool Acquired, string? BlockingRunId, DateTime? BlockingStartedUtc);

    internal static Task<HostGateResult> TryAcquireAsync(
        DockerHost host, string ourRunId, TimeSpan? perCallTimeout, CancellationToken ct);

    internal static Task RefreshAsync(DockerHost host, string ourRunId, CancellationToken ct);
    internal static Task ReleaseAsync(DockerHost host, string ourRunId, CancellationToken ct);
    internal static void ReleaseSync(DockerHost host, string ourRunId);
}
```

**`TryAcquireAsync` per host** (timeout cadence identical to the sweep — `null` local, 5 s remote, `EmergencyCleanup.cs:331-333`):
1. **Enumerate** lease markers: `host.ApiClient.Volumes.ListAsync(filter sdvd.lease=true)`.
2. For each foreign marker (`holderRunId != ourRunId`), classify:
   - **Alive (abort)** if EITHER: (a) a liveness probe `ListContainersAsync(All=true, filter sdvd.run-id=holderRunId)` returns `count > 0` (fast-path), OR (b) the lease's `sdvd.heartbeat-utc` is **fresh** (`now - heartbeat < HeartbeatStaleAfter`). Return `Acquired:false, BlockingRunId:holderRunId`. Do **not** touch the foreign volume.
   - **Dead (reclaim)** only if BOTH: no containers AND heartbeat **stale** (or missing/malformed — see orphan rule). Then `DockerOps.RemoveVolumeAsync(client, volume.Name)`. Continue.
3. **Acquire**: `CreateVolumeAsync(client, LeaseVolumeName(ourRunId), { sdvd.lease=true, sdvd.run-id=ourRunId, sdvd.heartbeat-utc=now })`.
4. **Read-back arbiter** (race resolution, below): re-enumerate; if a foreign lease now also exists and is alive-by-the-step-2 rule, lowest `(runId)` wins; loser removes its own just-created volume and returns `Acquired:false`.

**Orphan rule (malformed/missing labels):** a lease volume whose `sdvd.run-id` is absent/unparseable can't be probed and isn't `sdvd.test=true`, so nothing would ever delete it → permanent host block. Treat **missing-or-malformed `sdvd.run-id` OR missing-or-malformed `sdvd.heartbeat-utc`** as a **reclaimable orphan** (remove it). A well-formed live lease always has both, freshly stamped, so this never reclaims a healthy run.

**`RefreshAsync`:** rm + create `LeaseVolumeName(ourRunId)` with a new `sdvd.heartbeat-utc`. **`ReleaseAsync`/`ReleaseSync`:** remove `LeaseVolumeName(ourRunId)` (404-tolerant). `ReleaseSync` uses `DockerOps.RemoveVolumeSync` (`DockerOps.cs:246-256`).

Reuse `DockerOps.LabelFilter` by widening it `private`→`internal static` (`DockerOps.cs:258-268`) instead of duplicating. `ParseRunIdTimestamp(runId)` parses the `yyyy-MM-ddTHH-mm-ssZ` prefix (`RunMetadata.cs:104`) for the "started Nm ago" message (null on failure).

### Heartbeat refresher (parent process)

After the gate acquires on all hosts, start one background loop (`Task.Run` wrapped in `ExecutionContext.SuppressFlow()` per `asynclocal-pitfalls.md` — it outlives any test) that every `HeartbeatInterval` calls `RefreshAsync` for each acquired host, until a cancellation token is signalled at run-end. The parent stays alive and single-purpose for the whole run (it owns the renderer, tunnels, abort handlers), so this is the correct host for the refresher; it covers exactly the pre-container startup window the container-probe is blind to. Refresh failures are logged best-effort and don't abort the run (a transient daemon blip shouldn't kill a healthy run); a *sustained* failure just lets our own heartbeat go stale, which is correct (we may genuinely be dying).

### Where the gate runs in `Program.cs`

Insert an **"Occupancy gate" phase between the preflight `catch`/`return 2` (ends `Program.cs:530`) and the "Cleanup leftovers" sweep (starts `Program.cs:536`)**. Preflight (506) has materialized every `host.ApiClient`; the sweep hasn't run, so nothing is deleted. The gate **must precede the sweep** so that when the sweep runs, either we hold the only live lease (remaining `sdvd.test=true` resources are genuine orphans, safe to reap) or we already aborted — there is no window where our sweep fires against a foreign run we detected as live (we `return 2` before reaching line 536).

Phase body mirrors the surrounding structure (`OnSetupPhaseStarted` / per-host `OnSetupStep` / `OnSetupPhaseCompleted`, category `"Runner"`), looping `hostPool.Hosts`. On `!Acquired` (live foreign holder) or a gate exception (daemon unreachable mid-gate), copy the **preflight-fail recipe verbatim** (`Program.cs:515-530`): emit `run_aborted` (`cause = "vps_busy"` / `"occupancy_gate"`, payload = run-ids/host-id only, never `SshDestination`), mark phase failed, `recorder.SetAbortReason`, `recorder.WriteRunArtifacts`, `await renderer.DisposeAsync()`, `return 2`. Run-ids carry no VPS IP, so they're safe for the public CI log (unlike `SshDestination`, scrubbed per `Program.cs:495-504`); route gate-exception free-text through `ScrubForLog` anyway. On success per host, register the release action (cleanup below) and, after the loop, start the heartbeat refresher.

### The race window (simultaneous acquire)

`docker volume create` is **idempotent and names differ per run-id**, so a create-conflict is no arbiter. Resolve with the **read-back** (step 4): re-enumerate; if a foreign lease is alive-by-step-2 (containers OR fresh heartbeat — at simultaneous-start both have a *fresh* heartbeat, so each sees the other as live via the heartbeat branch, NOT the container branch), apply **lowest run-id string wins** (UTC-timestamp prefix → "earlier wins", `_shortSha` breaks same-second ties). Loser removes its own lease and aborts. Both run the identical total-order compare → exactly one survivor, symmetric. This is why the heartbeat (not container presence) is the read-back's liveness signal: at gate time neither peer has containers, but both have fresh heartbeats. Equal-run-id edge (same second AND `shortSha=="unknown"` git-failure fallback, `RunMetadata.cs:103,252-256`): append a process-unique suffix to the lease identity used for the tiebreak (e.g. PID) so the order is total even when run-ids collide. Docker offers no atomic compare-and-set, so read-back + total-order is the standard substitute; starts are rare and runs are minutes long, so the window is tiny — but the read-back makes the outcome deterministic rather than "both proceed". **Implement the read-back.**

### Lease cleanup — one uniform model via `EmergencyCleanup.Register`

At each successful acquire: `EmergencyCleanup.Register($"occupancy-lease-{host.Id}", () => OccupancyGate.ReleaseSync(host, ourRunId))`. This single registration covers **every** exit path — the model every container already uses:
- **Graceful Ctrl+C** (`BeginAbort`, `Program.cs:201-216`): on successful drain it calls `SkipBulkSweepOnExit()` (line 208), so `RunAll` **skips** the run-id bulk pass — the registered Action is then the **only** lease release. (Load-bearing, not belt-and-suspenders — the first plan draft mislabeled this; verified at `Program.cs:206-209`, `EmergencyCleanup.cs:157-179`.)
- **Force abort** (`ForceExitNow`, `Program.cs:330`) and **double-Ctrl+C** (`Program.cs:233`): run `BulkCleanupLabeledResources`, which removes the lease by `sdvd.run-id` anyway; the Action is redundant there.
- **ProcessExit / SIGHUP**: `RunAll` fires registered Actions *before* the (clean-path-skipped) bulk sweep, so the gate's own `return 2` and a clean `return` from `Main` both release. `host.ApiClient` is still live at ProcessExit (`HostPool.Instance` is a plain singleton, never `await using`d; `Program.cs` never disposes it).
- **Clean exit**: the outer `finally` (~`Program.cs:944`) sets `SkipBulkSweepOnExit()`. To release **promptly** (so a queued re-run isn't briefly blocked) rather than only at teardown, add before `SkipBulkSweepOnExit()`: cancel the heartbeat refresher, then a per-host `OccupancyGate.ReleaseAsync` loop, then `EmergencyCleanup.Unregister($"occupancy-lease-{host.Id}")` so it doesn't double-fire.

### Multi-host & local-only

Per-host lease, matching the already-per-host capacity/sweep/preflight model (`test-broker-invariants.md`). Disjoint host sets never collide; runs sharing any host collide only there (first-acquirer wins). **Partial acquire** (got host0, host1 busy → abort): the per-host `Register` releases host0's lease via ProcessExit's `RunAll` (the gate's `return 2` bypasses the abort handlers, so the Register — not the bulk pass — is what frees host0). **Local-only** runs are gated identically, not skipped — two local `make test` runs share the local daemon and the same sweep collision (`KillTestChildren`'s start-time heuristic at `Program.cs:388-437` shows sibling local runs are real); per-host loop runs with unbounded local timeout.

### Adversarial self-verification — failure modes

1. **Healthy foreign run still in startup (0 containers).** Heartbeat is fresh → step-2 branch (b) classifies it **alive** → we abort. Fixed (was the Critical). ✔
2. **Simultaneous acquire.** Read-back arbiter on fresh-heartbeat liveness + lowest-run-id (PID-suffixed for the git-failure equal-id edge) → exactly one survivor. ✔
3. **Crash before any container.** Heartbeat stops; after `HeartbeatStaleAfter` (~2.5 min) a foreign probe sees stale heartbeat AND no containers → reclaim. Self-healing, bounded. ✔
4. **Gate killed by its own sweep.** Lease omits `sdvd.test=true`; sweep filters volumes by `sdvd.test=true` (`EmergencyCleanup.cs:356-362`) → no match. ✔
5. **Holder dies between probe and acquire.** We aborted on its fresh heartbeat; user re-runs; ~2.5 min later it's reclaimable. Fail-fast-then-rerun is the chosen contract. ✔
6. **Malformed/missing lease labels.** Orphan rule reclaims (missing run-id OR heartbeat ⇒ removable) → no permanent block. ✔
7. **Gate probe/acquire/refresh throws (daemon unreachable).** Gate exception → hard abort `cause=occupancy_gate` (never silently proceed). Refresher failure → best-effort log, no abort, our heartbeat goes stale (correct if we're dying). Preflight proved reachability seconds earlier, so gate-time failure is rare. ✔
8. **Heartbeat rm→create gap caught by a foreign probe.** Foreign sees lease momentarily absent → proceeds to *acquire normally* (not a reclaim of a live run — there's nothing to reclaim). Holder re-creates immediately; on the foreign read-back both see two fresh leases → lowest-run-id arbiter resolves. Worst case is a rare spurious abort of the foreign run, never a both-proceed. ✔
9. **Multi-host disjoint/overlap + partial-acquire.** Per-host leasing + per-host Register release. ✔
10. **Re-entrant/leftover-of-ours lease.** `holderRunId == ourRunId` skip + `CreateVolumeAsync` idempotency = safe no-op. ✔

## Files to change

- **`tests/JunimoServer.Tests/Helpers/OccupancyGate.cs`** (new) — `OccupancyGate`: `HostGateResult`, `TryAcquireAsync` (enumerate → heartbeat/container liveness → reclaim/abort → create → read-back arbiter), `RefreshAsync`, `ReleaseAsync`/`ReleaseSync`, `LeaseVolumeName`, `ParseRunIdTimestamp`, the heartbeat constants. Reuses `DockerOps.{CreateVolumeAsync,RemoveVolumeAsync,RemoveVolumeSync,LabelFilter}`.
- **`tests/JunimoServer.Tests/Helpers/DockerOps.cs`** — widen `LabelFilter` `private`→`internal static` (reuse, no behavior change).
- **`tests/JunimoServer.TestRunner/Program.cs`** — insert the "Occupancy gate" phase at the 530↔536 seam (per-host `TryAcquireAsync`; abort recipe copied from 515-530; `EmergencyCleanup.Register` per acquired host); start the `SuppressFlow`-wrapped heartbeat refresher after the loop; in the outer `finally` near 944, cancel the refresher + per-host `ReleaseAsync` + `Unregister` **before** `SkipBulkSweepOnExit()`.
- **`tests/JunimoServer.Tests/Helpers/EmergencyCleanup.cs`** — no code change; relied-on invariants (volume sweep filters `sdvd.test=true`; abort bulk removal keys on `sdvd.run-id`) live here.

## Verification

E2E-only project — **no unit-test layer**; helpers are integration-tested and verified by inspecting a real run's JSONL output (per `CLAUDE.md`).

1. **Build clean**: `dotnet build` TestRunner + Tests (0 warnings/errors).
2. **Happy path**: run a fast subset (`make test FILTER=…`). Confirm the "Occupancy gate" phase shows `acquired` per host; the run completes; after clean exit `docker volume ls --filter label=sdvd.lease=true` on the host is empty.
3. **Heartbeat liveness during startup (the Critical fix)**: start run A; while A is still in **image build / distribution** (zero containers yet), start run B. **Expected:** B aborts with `VPS busy with run {A}` (it saw A's *fresh heartbeat*, not containers); A continues unharmed. Confirm by checking `docker volume inspect sdvd-lease-{A}` shows `sdvd.heartbeat-utc` advancing every ~30 s.
4. **Conflict during execution**: same as 3 but start B while A has live containers — B aborts via the container fast-path.
5. **Stale-lease reclaim**: `kill -9` run A after gate-acquire; start B *after* `HeartbeatStaleAfter` (~2.5 min) — B sees stale heartbeat + no containers, reclaims A's lease, acquires, proceeds. Start B *before* 2.5 min — B aborts (heartbeat still fresh). Both branches confirm the heartbeat window.
6. **Sweep-survival** (failure-mode 4): in a normal run confirm `sdvd-lease-{runId}` still exists *after* the "Cleanup leftovers" sweep phase (`docker volume ls --filter label=sdvd.lease=true` during the run).
7. **Inspect `infrastructure.parent.jsonl`** for `run_aborted` with `cause:"vps_busy"` on the conflict path.
