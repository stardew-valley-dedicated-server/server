# Support multiple concurrent test-runner instances (same machine + same remote host)

## Goal

Run **two or more `make test` coordinators at the same time** without them tripping over
each other — both on the same developer machine (shared local Docker daemon) and against the
same remote SSH Docker host. The operator is responsible for dividing the *resources* (each
runner gets an appropriate `serverSlots`/`clientSlots` share and a physically separate
`STEAM_ACCOUNTS` value); the harness's job is to guarantee **no two runners share an identity,
an artifact path, a writable volume, or a destructive cleanup scope**.

This plan **supersedes and deletes** `.claude/plans/features/tests-vps-occupancy-gate.md`,
whose design was the opposite policy (abort a second run with "VPS busy"). The user's
decision: enable parallel runs instead of serializing them.

## How to read this plan

The work is split into two top-level buckets, deliberately **not** mixed:

- **Part A — Settled (we know the fix).** Problems 1–7 below, each with a decided fix or a
  verified no-change. This is the buildable work; an implementer can start here.
- **Part B — Open (decision or investigation still owed).** A short, separate list of the
  handful of choices left to the implementer and the one threshold that needs a runtime
  measurement. Nothing in Part B blocks starting Part A, but each item must be closed before
  the corresponding piece is called done.

Every claim below was verified against the source during an adversarial review; the
`## Adversarial self-verification` section at the end records what was checked and the
earlier-draft errors that were corrected.

---

# Part A — Settled (decided fix or verified no-change)

## What already works — do not touch

Confirmed by reading the code; listing so we don't over-engineer:

- **All host-facing ports are OS-assigned.** WebUI Kestrel binds `127.0.0.1:0`
  (`WebRenderer.cs:95`, port read back at `:262-265`); container ports use
  `.WithPortBinding(port, true)` (random host port); remote-host coordinator-side ports come
  from `TunnelManager.PickFreeLoopbackPort()` (`TcpListener(_,0)`). Two runners never collide
  on a port. **No change.**
- **The test-UI is fully origin-relative.** `useWebSocket.ts:43-44` builds `ws://${location.host}/ws`;
  `/api/state`, `/api/command`, `/artifacts/*` are all same-origin relative. Each runner
  opens its own browser tab at its own ephemeral port; the UI needs no port discovery. **No change.**
- **Per-instance Docker resource *names* are already unique** — container/network/saves-volume
  names are `{prefix}-{runId}` or GUID-suffixed, and `SharedSteamAuth` already keys its name
  *and* cleanup by `{hostId}-{runId}` (`SharedSteamAuth.cs:116-119`) precisely because it
  knew `runId` alone isn't unique across per-host instances. So the whole labeling scheme
  *already treats `runId` as the per-run discriminator* — the only gap is that `runId` itself
  can collide across two concurrent processes (see Problem 1).
- **SSH ControlPath already includes PID** (`TunnelManager`, `SHA256(hostId|runId|pid)`) for
  concurrent coordinators. **No change.**
- **Cleanup identity is already run-id-scoped** on the abort bulk-removal path
  (`EmergencyCleanup.BulkCleanupLabeledResources`, `:201-204` — keys on `sdvd.run-id` when
  present). The startup *sweep* is the exception and the one real destructive collision
  (Problem 2).
- **Artifact run-dir threading already exists** — the parent calls `RunMetadata.BeginRun()`
  then exports `SDVD_RUN_DIR` (`RunArtifactNames.RunDirEnv`) so the xUnit child writes to the
  same run dir (`Program.cs:49-53`, honored in `RunMetadata.BeginRun` `:88-100`). Per-run
  isolation *within* `TestResults/runs/{runId}/` is already correct; it only breaks when two
  runners compute the **same** `runId` (Problem 1).

## The actual collisions

### Problem 1 — `runId` is not unique across concurrent processes (the linchpin)

`runId = "{yyyy-MM-ddTHH-mm-ssZ}_{shortSha}"` (`RunMetadata.cs:104`). Two coordinators started
in the **same wall-clock second** on the **same commit** produce an **identical `runId`**.
Because `runId` is the sole discriminator for:
- artifact dir `TestResults/runs/{runId}/` (`RunMetadata.cs:106`, `TestArtifacts.RunDir`),
- the `sdvd.run-id={runId}` label on every container/network/volume,
- run-id-scoped cleanup (`BulkCleanupLabeledResources`),
- the reuse/registry keys keyed off it,

…a same-second/same-SHA collision means both runners **write the same artifact tree, share a
label namespace, and each other's run-id cleanup removes the other's containers**. This is the
root cause that makes several downstream "collisions" possible; fixing it removes them at once.

**Fix:** make `runId` process-unique by appending a short process-unique suffix when the run
dir is *not* externally supplied. In `RunMetadata.BeginRun` (`RunMetadata.cs:101-107`, the
`else` branch that mints a fresh runId):

```csharp
var shortSha = GetGitShortSha() ?? "unknown";
// Per-process suffix so two coordinators started in the same second on the same
// commit don't mint the same runId (which is the sole discriminator for artifact
// dirs, sdvd.run-id labels, and run-id-scoped cleanup). PID is unique among live
// processes on one host; the child inherits the parent's runId via SDVD_RUN_DIR
// and never reaches this branch, so the parent's PID is the stable identity.
var proc = Environment.ProcessId.ToString("x");
runId = $"{_timestamp:yyyy-MM-ddTHH-mm-ssZ}_{shortSha}_{proc}";
```

- The **child** (xUnit test assembly) always takes the `externalRunDir` branch (`:94-100`),
  so it inherits the parent's suffixed `runId` verbatim — parent and child stay in lock-step.
  Only the *parent* mints, so PID is a stable per-run identity.
- **Distributed-worker mode** (`RunStartMsEnv`/`SDVD_RUN_DIR` supplied by a coordinator) is
  unaffected — it too takes the external branch. The suffix only appears when a coordinator
  mints its own fresh runId, which is exactly the concurrent-`make test` case.
- The `run-metadata.json`/`summary.json`/`latest.txt` consumers treat `runId` as an opaque
  string (dir name); the extra `_{pid}` segment is inert to them. (`latest.txt` is itself a
  shared last-writer-wins pointer — acceptable, see Problem 5.)

**Verification hook:** after the fix, two `make test` in the same second produce two distinct
`TestResults/runs/…` dirs and two distinct `sdvd.run-id` label values.

### Problem 2 — startup sweep destroys the *other* runner's live resources (destructive)

`EmergencyCleanup.SweepStaleResourcesAsync` (`EmergencyCleanup.cs:323-371`) runs as the
"Cleanup leftovers" phase on **every** startup (`Program.cs:718-733`) and force-removes **all**
containers/networks/volumes labeled `sdvd.test=true` across all hosts — deliberately the broad
label, not run-id, because its purpose is to reap orphans from *prior dead runs* whose run-ids
it can't know (`:311-315`). On a shared daemon, if runner B starts while runner A is mid-run,
**B's sweep rips out A's running containers.** This is the single most dangerous hazard.

The design tension is real: the sweep genuinely needs the broad label to reap dead orphans,
but the broad label can't tell "dead orphan" from "another live runner's container." The
occupancy-gate plan resolved this by *aborting* B; we instead **exclude live runners' resources
from the broad sweep** using a liveness signal the sweep can read from the shared daemon.

**Why run-id-scoping alone is NOT sufficient (rejected simpler option).** The obvious "just
remove our own `sdvd.run-id` instead of the broad label" fails because the broad sweep is the
**only** reaper of orphans whose run-id nothing knows — a run SIGKILLed/OOM-killed/power-lost
before either `RunAll` or the abort path ran. Verified: the process-exit path
(`BulkCleanupLabeledResources`, `:201-204`) is *already* run-id-scoped and safe; the **startup
broad sweep is the sole unknown-run-id orphan reaper** (grep confirms exactly two callers of
the bulk-remove helpers; nothing in the Makefile or `.github/workflows/` does a
`docker system prune` of test resources; the CI VPS daemon is persistent across runs). Pure
run-id scoping would leak hard-crash orphans forever on the VPS — and a leftover named volume
can *shadow* a fresh one, which is why the sweep runs before the image build (`Program.cs`
comment at the phase). So the sweep must stay broad but learn to **exclude live runs**.

**Fix — a per-run liveness heartbeat; the broad sweep skips fresh-heartbeat run-ids.** This is
the ping-and-reap model: each live runner keeps a marker "answering"; the sweep reaps any
run-id that has stopped answering for too long, and preserves any that's still fresh. Docker
gives no push-liveness, so "is it answering?" is a periodically-refreshed timestamp label
rather than an ICMP ping — but the shape is exactly that.

Substrate is a **labeled Docker volume**, not a marker *container* — the volume reuses the
existing `DockerOps.{CreateVolumeAsync,RemoveVolumeAsync}` surface with **zero new Docker
primitives**, whereas a marker container would drag in image-pull, start, and (because the
harness auto-streams and records every container's logs) new recording/log-stream exclusions.

Concretely:
1. **A liveness marker per run.** The parent, right after `BeginRun`, creates a volume
   `sdvd-live-{runId}` labeled `sdvd.live=true` + `sdvd.run-id={runId}` +
   `sdvd.heartbeat-utc={epochMs}` on each configured host, and **NOT** `sdvd.test=true` (so
   the broad sweep can never delete the marker itself — it filters volumes by `sdvd.test=true`,
   `:356-362`). One background task (`Task.Run` under `ExecutionContext.SuppressFlow()`, per
   `asynclocal-pitfalls.md` — it outlives every test) refreshes `sdvd.heartbeat-utc` every ~30 s.
   Volume labels are immutable after create, so "refresh" = remove + recreate under the same
   name; the ordering of that remove/recreate to avoid a sweep catching the gap is **open item
   B1**. Registered for teardown via `EmergencyCleanup.Register($"live-marker-{host.Id}", …)`;
   a crashed run's marker simply stops being refreshed and goes stale.
2. **The sweep consults live markers before reaping.** `SweepStaleResourcesAsync` first lists
   `sdvd.live=true` volumes on the host, builds the set of `sdvd.run-id`s whose
   `sdvd.heartbeat-utc` is **fresh** (`now - heartbeat < HeartbeatStaleAfter`; ~150 s is the
   starting value but its safety under load is **open item B3**), then filters the force-remove
   passes to **skip any resource whose `sdvd.run-id` is in that set**. No run-id, unknown
   run-id, or stale-heartbeat run-id ⇒ genuine orphan, reaped as today. Our *own* marker is
   fresh (we start the heartbeat before the sweep), so we never reap ourselves.
3. **Ordering in `Program.cs`.** Create the marker + start the refresher **before** the
   "Cleanup leftovers" phase (insert between preflight completion and the phase at `:718`), so
   when the sweep runs, our marker and every other live runner's marker already exist.

This keeps the sweep's orphan-reaping intact (dead runs still cleaned; self-heals in ~2.5 min)
while making it **non-destructive to concurrent live runs**.

> **Scope note.** This is deliberately *lighter* than the deleted occupancy-gate plan — that
> plan additionally needed a read-back arbiter, a total-order tiebreak, and abort plumbing
> because a fresh foreign heartbeat triggered an **abort**. Here a fresh foreign heartbeat only
> triggers **skip-from-sweep**, so none of that arbitration is needed: two live runners simply
> both see each other as fresh and both preserve. The reusable parts are just the volume marker,
> the ~30 s refresher, and the staleness comparison — a `LiveRunMarker` helper
> (`tests/JunimoServer.Tests/Helpers/LiveRunMarker.cs`) with `StartAsync` / `StopAsync` /
> `GetLiveRunIdsAsync(host)`. No `TryAcquire`, no arbiter, no abort path.

### Problem 3 — shared writable `server_steam-session` volume (operator-config, not code)

`ServerContainerOptions.SteamSessionVolume = "server_steam-session"` (`:24`) is mounted
**read-write** into the steam-auth sidecar (`SharedSteamAuth.cs:129`). Two runners' sidecars
writing the same session volume concurrently race/corrupt Steam session state. The `server_game-data`
volume (`:19`, `GameClientOptions.cs:23`) is mounted **read-only** (`ServerContainer.cs:259` `/data/game`)
so concurrent reads are fine — do **not** make it per-runner (that would defeat the shared game-data
cache the `GameDataDistributor` populates).

There is also a *protocol-level* constraint: Steam allows **one live login per account**, and
both runners build/log-in with `STEAM_ACCOUNTS[0]` today. Per
`protocol-invariant-not-file-workaround.md`, this is enforced by Steam on the *account
identifier*, not the file — so the fix is disjoint accounts, not disjoint session files.

**Per the user's decision (document constraints, no auto-split), the plan does NOT add
cross-process account/volume coordination.** Instead:

1. **Wire the existing `SDVD_VOLUME_PREFIX` escape hatch into the main path** so an operator
   *can* give each runner a disjoint steam-session volume by setting one env var. Today only
   `DownloadValidationFixture.cs:37-40` reads it; `ServerContainerOptions`, `GameClientOptions`,
   and the broker hardcode `"server_game-data"`/`"server_steam-session"`. Make all three derive
   from the prefix:
   - `ServerContainerOptions.cs:19,24` → `GameDataVolume = $"{VolumePrefix}_game-data"`,
     `SteamSessionVolume = $"{VolumePrefix}_steam-session"`, where
     `VolumePrefix = Environment.GetEnvironmentVariable("SDVD_VOLUME_PREFIX") ?? "server"`
     (mirror `DownloadValidationFixture`'s resolution exactly — one canonical read, per
     `one-parser-per-contract.md`; consider a `TestVolumes.Prefix` static so all four sites
     share one definition).
   - `GameClientOptions.cs:23` → same for `GameDataVolume`.
   - Confirm `TestResourceBroker.cs:604-605,726,855` and `ClientPool.cs:613` propagate the
     option values (they read `defaults.GameDataVolume` etc., so they inherit automatically —
     verify, don't assume).
   - `Program.cs:865` (`new ServerContainerOptions().GameDataVolume`) inherits automatically.
2. **Document the coexistence contract** (see Docs section): to run two Steam-enabled runners
   against the same daemon, each must set (a) a **physically different `STEAM_ACCOUNTS`** value
   with non-overlapping accounts — *not* a "slice" knob (none exists; `SteamAccountSlicer` only
   partitions within one run's host fleet, `SteamAccountSlicer.cs:57`) — and (b) a distinct
   `SDVD_VOLUME_PREFIX` (so `server_steam-session` becomes e.g. `runnerA_steam-session` /
   `runnerB_steam-session`). LAN-only runs (no Steam) need neither.

> Note the game-data volume also gets prefixed by (1). That's fine — an operator who sets a
> distinct prefix just gets a distinct (initially empty) game-data cache that the
> `GameDataDistributor` will populate on first use. If they want to *share* the read-only
> game-data cache while isolating only the session, that's a finer split we are explicitly
> **not** building (documented as a known limitation): the prefix is coarse-grained by design,
> matching the existing `DownloadValidationFixture` semantics.

### Problem 4 — `KillTestChildren` can kill a sibling runner's xUnit child

`Program.cs`'s abort path (`KillTestChildren`, `:524-573`) enumerates processes named
`JunimoServer.Tests` and kills any started *after* our own start time — the code comment
already **admits** "We accept the residual risk of killing a concurrent sibling test run
started after us." With two concurrent runners this stops being residual: runner A's Ctrl+C
kills runner B's test child (started after A).

**We do not hold a `Process` handle to the child** — verified. The child is spawned *inside*
xUnit v3's `AssemblyRunner` via `LocalOutOfProcessTestProcessLauncher` (`Program.cs:300`,
`:988`, `:1013` — `await runner.Run()`); the public `AssemblyRunner` API doesn't surface the
child PID. So "hold the handle" is not an option here (an earlier draft of this plan assumed
it was — it's wrong).

**Fix — parent-PID lineage match (the clean idiom).** The child is *our direct OS child*, so
scope the kill to "processes whose parent is us," not "processes started after us." Concretely:
before spawning, set an env var the parent owns — `SDVD_PARENT_PID = Environment.ProcessId` —
which the child inherits; `KillTestChildren` then keeps only candidates whose inherited
`SDVD_PARENT_PID` equals our PID. This is unambiguous (a sibling runner's child carries the
*sibling's* PID), needs no start-time heuristic, and reuses the same `GetProcessesByName`
enumeration already in place.
- Reading a candidate's inherited env var cross-process is the one platform wrinkle; the
  standard idiom is: **the child re-publishes its own identity where the parent can read it
  cheaply** — e.g. the child writes its PID into a file under our own `RunDir`
  (`TestResults/runs/{runId}/child.pid`) on startup, and `KillTestChildren` reads that one
  file and kills exactly that PID (+ `entireProcessTree: true` for any grandchildren; the
  ssh ControlMaster is detached and journal-reaped, not part of any process tree). No
  env-block reflection, no WMI. This is
  simpler than parent-PID reflection and fully cross-platform (`simplest-solution.md`).
- The child already writes into `RunDir` (it inherits `SDVD_RUN_DIR`), so the pid file lands in
  the right per-runner directory automatically — a sibling runner writes to *its* RunDir, so
  the files never collide.

Confirm the child has an early startup hook to write the pid file (`TestSummaryFixture.InitializeAsync`
is the natural site — it already calls `BeginRun`).

### Problem 5 — shared stable-root artifact paths (mixed: one fix, two documented)

With `runId` made unique (Problem 1), the per-run tree `TestResults/runs/{runId}/` no longer
collides. Three paths root at the **stable** `TestResults` root instead of `RunDir`, so
concurrent runners share them:

- **`TestResults/report/` — FIX: root under `RunDir`.** `ReportGenerator.cs:105`
  (`Path.Combine(_testResultsPath, "report")`) writes the `--report` static bundle to the
  *stable* root, verified — **not** under `{runId}`. Two concurrent `make test-web-report`
  runners overwrite each other's bundle. Change `ReportGenerator` to root the bundle at
  `Path.Combine(RunDir, "report")` so each runner keeps its own. (This is the one real fix in
  Problem 5; the earlier draft mis-assumed it was already per-run.)
- **`TestResults/latest.txt` — DOCUMENT: last-writer-wins.** `TestRunArtifactWriter.cs:508`
  writes it to `_outputDir` (stable root, by design — it's the "most recent run" pointer). Two
  runners overwrite it; "latest" becomes whichever finished last. Convenience shortcut only;
  each runner's own UI tab is authoritative. Do not add locking (`simplest-solution.md`) —
  document the semantics.
- **`tests/test-ui/public/mock-artifacts/mock-data.json`** (`WebRenderer.WriteMockData`,
  `:559-590`) — vite-dev mock workflow only, **not** the live `--web` UI. Harmless
  last-writer-wins. Note it, no change.

### Problem 7 — `flakiness.jsonl` cross-run append (same-machine race + cross-machine no-merge)

`FlakinessTracker.RecordRun` (`FlakinessTracker.cs:53`) does
`new StreamWriter(TestResults/flakiness.jsonl, append: true)` and writes N lines (one per test)
once per run from `TestSummaryFixture.DisposeAsync`. The file is at the **stable** root
(`:16-17`), cross-run by design so `ComputeFlakiness` can read the last 20 runs.

**Same machine, two concurrent runners:** both open their own append `StreamWriter` at run-end.
.NET `StreamWriter` buffers and flushes in chunks with no per-line append atomicity guarantee,
so two runners finishing near-simultaneously can **interleave partial lines** → corrupt JSONL.
`ComputeFlakiness` already skips malformed lines (`:131` `catch { }`), so the failure mode is
*silent data loss* (a few flakiness entries dropped), not a crash.

**Decided default: serialize the append with a machine-wide named mutex** —
`Mutex("sdvd-flakiness-jsonl")` held only for the ~N-line write burst, released immediately.
One runner's burst completes before the other's starts; no interleave. Minimal, matches the
existing build-lock idiom. The alternative (per-run `RunDir/flakiness.jsonl` + a globbing read
path) is heavier and only warranted if we want *zero* shared writers — kept as **open item B2**
in case the mutex proves insufficient, but the mutex is the plan of record.

**Two machines** (your question): `flakiness.jsonl` lives at each machine's own repo-root
`TestResults/` — it is a **local, per-checkout file** (`TestResults/` is gitignored, never
synced). So two machines have **entirely separate** flakiness histories: no shared writer, no
corruption, but also **no merged view** — each machine's `ComputeFlakiness` sees only its own
runs. This is a *design fact to state*, not a bug: flakiness detection is per-machine. A merged
cross-machine history would need a real shared store (a central append endpoint / synced
artifact bucket) — explicitly **out of scope** here (`holistic-or-explicit-todo.md`: name the
gap, don't scaffold it). The mutex in option 1 is same-machine only and correct for that scope.

### Problem 6 — Docker image build lock (NON-ISSUE — verified, no change)

The build lock (`tests/JunimoServer.Tests/Helpers/DockerImageBuilder.cs:171-209`) is already
correct for concurrent `make test`. Acquisition is `FileStream(BuildLockFile, OpenOrCreate,
ReadWrite, FileShare.None)` in a retry loop: a contending process gets `IOException` and
**retries** (`:200`) — it never opens a competing handle, so two builders cannot run at once.
The `File.Delete` on release happens *after* `lockStream.Close()` (`:189-192`) and its failure
is swallowed (`:194`); the only effect of the delete is a brief window where the file is absent,
after which the next acquirer creates a fresh one and still holds the sole `FileShare.None`
handle. The "build simultaneously" race an earlier draft described **cannot occur**. No change.
(Also note: the file lives under `Helpers/`, not `TestRunner/Distribution/`.)

## Files to change

- **`tests/JunimoServer.Tests/Helpers/RunMetadata.cs`** — append `_{pid:x}` to the minted
  `runId` (Problem 1), only in the fresh-mint `else` branch (`:101-107`).
- **`tests/JunimoServer.Tests/Helpers/LiveRunMarker.cs`** *(new)* — heartbeat live-marker helper
  (Problem 2): `StartAsync` (create marker volume + kick the ~30 s refresher under
  `SuppressFlow`), `StopAsync` (cancel refresher + remove marker), `GetLiveRunIdsAsync(host)`
  (list `sdvd.live=true` volumes, return run-ids with fresh `sdvd.heartbeat-utc`). No
  acquire/arbiter/abort — a fresh foreign heartbeat only means skip-from-sweep. Reuses
  `DockerOps.{CreateVolumeAsync,RemoveVolumeAsync}`; refresher does create-then-remove to avoid
  the sweep-catches-the-gap race.
- **`tests/JunimoServer.Tests/Helpers/EmergencyCleanup.cs`** — `SweepStaleResourcesAsync` calls
  `LiveRunMarker.GetLiveRunIdsAsync(host)` and skips resources whose `sdvd.run-id` is in that
  fresh set (Problem 2). Needs the list responses to expose each resource's `sdvd.run-id` label
  so the filter can run — verify the bulk-remove helpers surface labels or add a
  list-then-filter step at this call site.
- **`tests/JunimoServer.Tests/Helpers/DockerOps.cs`** — if `LiveRunMarker` or the sweep filter
  needs a shared label-filter helper, widen the existing one `private`→`internal` (reuse, no
  behavior change).
- **`tests/JunimoServer.Tests/Containers/ServerContainerOptions.cs`** — derive
  `GameDataVolume`/`SteamSessionVolume` from `SDVD_VOLUME_PREFIX` (Problem 3).
- **`tests/JunimoServer.Tests/Containers/GameClientOptions.cs`** — derive `GameDataVolume` from
  the same prefix (Problem 3).
- **(optional) `tests/JunimoServer.Tests/Helpers/TestVolumes.cs`** *(new, if extracting the
  prefix read)* — single canonical `SDVD_VOLUME_PREFIX` resolution consumed by
  `ServerContainerOptions`, `GameClientOptions`, and `DownloadValidationFixture`
  (per `one-parser-per-contract.md`).
- **`tests/JunimoServer.TestRunner/Program.cs`** — start the live-marker heartbeat before the
  "Cleanup leftovers" phase and register its teardown (Problem 2); set `SDVD_PARENT_PID` before
  spawning and rewrite `KillTestChildren` to kill the child pid recorded in
  `RunDir/child.pid` instead of the start-time heuristic (Problem 4).
- **`tests/JunimoServer.Tests/Fixtures/TestSummaryFixture.cs`** — write `RunDir/child.pid`
  early in `InitializeAsync` (next to the existing `BeginRun` call) so the parent can target
  exactly its own child (Problem 4).
- **`tests/JunimoServer.TestRunner/Rendering/Web/ReportGenerator.cs`** — root the `--report`
  bundle at `Path.Combine(RunDir, "report")` instead of `_testResultsPath/"report"` (`:105`)
  so concurrent runners don't clobber each other's bundle (Problem 5).
- **`tests/JunimoServer.Tests/Helpers/FlakinessTracker.cs`** — guard the append burst
  (`:48-78`) with a machine-wide named `Mutex("sdvd-flakiness-jsonl")` so two runners' writes
  don't interleave into corrupt JSONL (Problem 7, same-machine only).
- **`.env.test.example`** — extend the existing `SDVD_VOLUME_PREFIX` doc line (`:141-142`) with
  the concurrent-runner coexistence note; add a `STEAM_ACCOUNTS` note that concurrent
  Steam-enabled runners need **physically different** `STEAM_ACCOUNTS` values (there is no
  per-runner slice knob — the slicer only divides across one run's host fleet). (Problem 6
  needs no file change — verified non-issue.)
- **`docs/developers/testing/e2e-testing.md`** / **`remote-host-setup.md`** — new "Running
  multiple runners concurrently" section (see Docs), including the *Capacity & TPS sizing*
  constraints/gotchas (per-process slot multiplication, memory arithmetic from the committed
  per-container caps, TPS-stays-at-5, start/extraction burst stacking).
- **DELETE `.claude/plans/features/tests-vps-occupancy-gate.md`** — superseded by this plan
  (opposite policy). Grep first for any other file citing it and update/remove the reference
  (`scope-means-no-reads-or-writes.md` / `one-parser-per-contract.md` link-repair discipline).

## Docs — the coexistence contract (operator-facing)

Add a concise section stating exactly what an operator must do to run N runners concurrently:

1. **Local, LAN-only (no Steam):** works out of the box after this change — distinct `runId`
   per process, no destructive cross-sweep, shared read-only game-data cache. Just run
   `make test` twice. Each auto-opens its own UI tab on its own port. Divide each host's
   `serverSlots`/`clientSlots` in `SDVD_DOCKER_HOSTS` so the sum across runners doesn't
   overcommit the machine (the harness does **not** auto-divide — capacity accounting is
   per-process by design, `test-broker-invariants.md`).
2. **Steam-enabled, shared daemon:** additionally give **each** runner (a) a **physically
   different `STEAM_ACCOUNTS` value** with non-overlapping accounts — a separate `.env.test` or
   env override, because `SteamAccountSlicer` (`SteamAccountSlicer.cs:57`) only divides accounts
   across *one run's host fleet* and has no cross-runner awareness; two runners with the same
   `STEAM_ACCOUNTS` both grab account 0 and Steam's one-login-per-account protocol rule kicks
   one out. And (b) a **distinct `SDVD_VOLUME_PREFIX`** so the writable `…_steam-session` volume
   is per-runner. The read-only `…_game-data` cache is also split by the prefix; sharing it
   while isolating only the session is a finer split we don't support.
3. **Known limitations to state plainly** (`holistic-or-explicit-todo.md` — no hidden gaps):
   - Capacity/slots are **not** coordinated cross-process; the operator sizes each runner's
     slots so the total fits the host — see the *Capacity & TPS sizing* subsection below for the
     concrete arithmetic and gotchas.
   - `TestResults/latest.txt` is last-writer-wins across concurrent runners ("latest" = last to
     finish).
   - `SDVD_VOLUME_PREFIX` is coarse (splits both game-data and session together).
   - **Flakiness history is per-machine** — `flakiness.jsonl` is a local, gitignored file, so
     runs on two different machines never merge into one flakiness view; each machine tracks its
     own. Merged cross-machine flakiness is out of scope (would need a shared store).

Follow `verify-documented-config-is-consumed.md`: after wiring `SDVD_VOLUME_PREFIX` into the
main path, `grep -rn SDVD_VOLUME_PREFIX` must show consumers in `ServerContainerOptions`,
`GameClientOptions`, and `DownloadValidationFixture` (not just the doc line).

### Capacity & TPS sizing — constraints and gotchas (operator-facing)

State these plainly in the docs. Where a concrete value appears it's the repo's own committed
config (the sample `SDVD_DOCKER_HOSTS`, the proven `SERVER_TPS`); memory is given as a formula
in the operator's *measured* per-container footprint, because no memory caps ship yet (caveat
below) — nothing here is an invented figure.

**The core gotcha: capacity is per-process, so it multiplies.** Each runner reads
`SDVD_DOCKER_HOSTS` independently and enforces `serverSlots`/`clientSlots` per-host in its *own*
process (`test-broker-invariants.md`: "Capacity is per-host, not global" — and there is no
cross-*process* gate either). So **two runners against the same host each claim the full slot
count** → up to 2× the containers, 2× the concurrent `docker create+start` burst, and 2× the
run-end video-extraction load. The harness will not stop this; the operator must divide.

**How to divide.** For N concurrent runners sharing one host, split that host's budget across
their configs so the *sums* fit the machine:
- `Σ serverSlots ≤` the host's real server-container ceiling; same for `Σ clientSlots`.
- `Σ concurrentStarts` and `Σ concurrentExtractions` likewise — each defaults to a host's
  `serverSlots+clientSlots` when unset (`.env.test.example:52-62,76-89`), so if you halve the
  slots per runner those caps follow automatically; if you pin them, halve them too.
- Simplest recipe: take the single-runner `SDVD_DOCKER_HOSTS` you'd normally use and give each
  of the N runners `slots / N` (rounded down, min 1 server + ≥1 client to stay useful).

**Memory is the binding constraint — size it from each container's real footprint.** Peak RAM a
*single* runner can demand on a host is roughly:

```
serverSlots × (server footprint)  +  clientSlots × (client footprint)
   +  (steam-capable ? one steam-auth sidecar footprint : 0)
```

so **two runners sharing a host roughly double it.** For the sample local host
(`serverSlots:3, clientSlots:6`, `.env.test.example:69`) that's 3 servers + 6 clients + a
sidecar per runner, i.e. **≈2× the containers** when a second runner shares the daemon — either
the machine has the headroom, or each runner takes half the slots (`slots / N`). Measure the
actual per-container footprint on the target machine (the Web UI's live memory graph, or
`docker stats`) rather than assuming — a headless server (`SERVER_FPS=0`) and a client differ,
and it varies with the save/scenario.

> **Caveat — no per-container memory caps ship today.** There is *currently no*
> `SDVD_*_MEMORY_MB` limit in the committed config or container code (verified: `ResourceLimitEnv`
> and `HostConfig.Memory` don't exist yet; the per-container-limits design lives in the
> unimplemented plan `.claude/plans/features/tests-container-resource-limits.md`). So today an
> overcommit does **not** fail fast per-container — it thrashes host swap or trips the kernel OOM
> killer (**exit 137**, surfaced as `container_oom_killed`). Until that plan lands, the only lever
> is **sizing `Σ slots` conservatively under physical RAM**. If/when it lands, setting the
> `SDVD_*_MEMORY_MB` caps turns an overcommit into a clean per-container kill and makes concurrent
> sizing safer — cross-reference it, but don't cite specific cap values here (they're proposed,
> not committed).

**TPS/FPS are shared, not per-runner — and don't raise them for concurrency.** `SERVER_TPS`,
`CLIENT_TPS`, `SERVER_FPS` come from the shared environment, so every concurrent runner uses the
same values. The proven-stable headless value is **`SERVER_TPS=5`** (`server-tps-headless.md`:
CI and `.env.test` run the whole suite at 5; the `.env.example` "20-30" prose is conservative
docs, not a floor). Keep `SERVER_FPS=0` for test servers (rendering/recording off) unless you
specifically need recordings. The gotcha: **more concurrent containers is a CPU-scheduling load,
not a reason to change TPS** — raising TPS to "compensate" only adds per-tick work across all
the extra containers and makes overcommit worse. Leave TPS at 5 and control load via slots.

**Start/extraction bursts stack too.** `concurrentStarts` bounds the `docker create+start`
accept-queue pressure that the named-pipe timeout fix exists for (`docker-test-resources.md`);
`concurrentExtractions` bounds run-end ffmpeg+tar load. Both are per-process, so N runners can
put N× the burst on one daemon at the same moment (all starting, or all finishing, together).
If you see start timeouts or extraction stalls under concurrency, lower these per runner — same
per-process-doesn't-coordinate reason as slots.

## Verification (E2E-only project — inspect JSONL of a real run, no unit layer)

1. **Build clean:** `dotnet build` TestRunner + Tests (0 warnings/errors).
2. **Unique runId (Problem 1):** launch two `make test FILTER=<oneClass>` within the same
   second on the same commit; confirm two distinct `TestResults/runs/…_{pid}` dirs and two
   distinct `sdvd.run-id` label values (`docker ps --filter label=sdvd.test=true --format '{{.Label "sdvd.run-id"}}'`).
3. **Non-destructive sweep (Problem 2, the critical one):** start runner A (LAN, fast class);
   while A has live containers, start runner B against the same daemon. **Expected:** B's
   "Cleanup leftovers" phase reaps only orphans and **leaves A's containers running**; A
   completes unharmed; both produce full artifact trees. Confirm A's `sdvd-live-{runIdA}`
   volume's `sdvd.heartbeat-utc` advances every ~30 s during the overlap
   (`docker volume inspect`). This directly exercises the fix — per
   `runtime-post-conditions-are-gates.md`, run it, don't reason about it.
4. **Stale-marker self-heal:** `kill -9` runner A after its marker exists; start runner B after
   `HeartbeatStaleAfter` (~2.5 min) — B's sweep sees A's stale heartbeat and reaps A's orphaned
   containers/marker. Start B *before* 2.5 min — A's resources are preserved. Confirms the
   staleness window both directions.
5. **Sibling-child not killed (Problem 4):** two concurrent runners A and B; confirm each writes
   a distinct `RunDir/child.pid`; Ctrl+C runner A mid-run; confirm A kills only the pid in
   *A's* `child.pid` and runner B's xUnit child survives and B completes (B's `summary.json` has
   the expected passed count, B's container.logs show no mid-run kill).
6. **Steam coexistence (Problem 3), manual/documented:** two Steam-enabled runners with
   **physically different** `STEAM_ACCOUNTS` (non-overlapping) + distinct `SDVD_VOLUME_PREFIX`;
   confirm no `LogonSessionReplaced` in either run's `containers/steam-auth-*/container.log`, and
   two distinct `…_steam-session` volumes exist. (Per `passing-test-isnt-proof-the-scenario-ran.md`:
   read the sidecar log to confirm both logged in cleanly, don't infer from a green suite.)
7. **Report + flakiness isolation (Problems 5, 7):** two concurrent `make test-web-report`
   runners → confirm two distinct `TestResults/runs/{runId}/report/` bundles (neither clobbered).
   Two concurrent runs finishing near-simultaneously → confirm `flakiness.jsonl` has no truncated
   lines (`jq . flakiness.jsonl` parses every line) — the named mutex serialized the appends.

## Adversarial self-verification

- **Does Problem 1's PID suffix break the child?** No — the child always takes the
  `externalRunDir` branch (`RunMetadata.cs:94-100`), inheriting the parent's full suffixed
  runId; only the parent mints. Distributed-worker mode also takes the external branch. Verified
  against `Program.cs:49-53` (parent exports `SDVD_RUN_DIR` before spawning the child).
- **Could two runners still collide on PID?** PIDs are unique among *live* processes on one
  host; two live coordinators cannot share a PID. Cross-host runs don't share a `TestResults`
  tree anyway. Combined with the second-timestamp + SHA, collision is impossible for live peers.
- **Why not the simpler run-id-scoped sweep?** Because the startup broad sweep is the *sole*
  reaper of unknown-run-id orphans (hard-crash), verified: the process-exit path is already
  run-id-scoped, and nothing else prunes (`grep` of Makefile + workflows). Run-id scoping alone
  would leak crash orphans forever on the persistent VPS daemon. Rejected with reason, not
  waved off.
- **Does the heartbeat sweep re-introduce orphan leakage?** No — only *fresh-heartbeat* run-ids
  are preserved; stale and unknown run-ids are reaped exactly as today (self-heals in ~2.5 min).
- **Does the live-marker volume get swept by our own broad sweep?** No — it omits
  `sdvd.test=true`; the sweep filters volumes by that label (`EmergencyCleanup.cs:356-362`).
- **Heartbeat rm→create gap caught by a foreign sweep?** The refresh momentarily removes then
  recreates the marker volume. If a foreign sweep enumerates `sdvd.live` in that sub-ms gap, it
  won't see our run-id as fresh → *could* reap our resources. This is the one real race in the
  heartbeat design; the choice of mitigation is **open item B1**.
- **Problem 4 pid-file approach — failure modes.** (a) Child crashes before writing `child.pid`
  → parent falls back to the current name+start-time kill (documented degradation, not worse
  than today). (b) Stale `child.pid` from a prior run in the same `RunDir` → impossible, since
  `RunDir` is now per-process-unique (Problem 1), so each runner's pid file is fresh. (c) PID
  reuse between write and kill → bound by reading the pid file at abort time, not caching it;
  `entireProcessTree:true` still scopes to that pid's tree only.
- **Earlier-draft errors this review corrected** (recorded so they don't creep back): "hold the
  child Process handle" (we have none — xUnit spawns it out-of-process); the build-lock
  "simultaneous build" race (verified impossible); `DockerImageBuilder.cs` path
  (`Helpers/`, not `TestRunner/Distribution/`).
- **Is `SDVD_VOLUME_PREFIX` a fail-fast risk against committed config?** No — it defaults to
  `"server"`, reproducing today's exact volume names when unset (`preflight-check-vs-committed-config.md`);
  existing single-runner configs are unaffected.
- **Build-time Steam login across two runners (verified safe).** `DockerImageBuilder` logs in
  `STEAM_ACCOUNTS[0]` at build time (`:82`), *before* any slicing, and builds steam-auth+server
  in parallel but serializes the test-client build behind the server precisely because they
  share account 0 and Steam kicks the first session (`:232-238`). Across two runners the
  machine-wide build lock (`FileShare.None`, Problem 6) serializes the *whole* build, so only
  one runner is logged in as account 0 at a time. No cross-runner build-login collision — no
  new work. (This is why the "physically different `STEAM_ACCOUNTS`" contract is a *runtime*
  requirement for the sidecars, not a build-time one — the build always uses account 0 and is
  serialized.)
- **`HeartbeatStaleAfter` (~150 s) vs a slow run under load.** The refresher fires every ~30 s
  and the stale window is ~150 s (≈5 missed beats); a live but daemon-backpressured runner must
  still land *one* refresh within any 150 s window or a sibling reaps its live resources. 5
  beats of slack should absorb transient backpressure, but this is the one threshold whose
  safety I cannot prove statically — carried as **open item B3** (a runtime measurement).
- **Capacity overcommit on a shared host** is *documented, not solved* — per the user's
  decision. State it as a known limitation, not a silent gap.

---

# Part B — Open (decision or investigation still owed)

Nothing here blocks starting Part A. Each item names what's undecided, the options, and how to
close it. Close the matching Part-A piece only after its Part-B item is resolved.

### B1 — Heartbeat marker refresh: how to avoid the rm→create gap *(implementer decision)*

**Owner of the choice:** implementer, at code-time. **Blocks:** Problem 2 "done".
Refreshing an immutable-label volume means remove + recreate, leaving a sub-ms window where a
foreign sweep could see our marker absent and reap our live resources. Two safe options — pick
one and note which in the code:
- **(a) create-then-remove** — write a *new* marker (e.g. a second name or a create-before-delete
  under a temp name, then swap) so a fresh marker is continuously present. Simplest if the
  create-before-delete can be expressed without a name clash.
- **(b) sweep double-check** — before reaping a run-id, if it has *running containers* re-read
  the live set once after a short delay; only reap if still absent. Belt-and-suspenders, keeps
  the refresher trivially remove-then-create.
**Recommendation:** (a) if a clean create-before-delete is expressible; else (b). Not a blocker
to start — it's a localized choice inside `LiveRunMarker`.

### B2 — Flakiness writer: mutex vs per-run file *(fallback only)*

**Owner:** implementer, only if the default fails. **Blocks:** nothing — the mutex is the plan
of record (Problem 7). Item exists so the fallback is on record: if the machine-wide
`Mutex("sdvd-flakiness-jsonl")` proves insufficient (e.g. a runner crashes mid-write holding
the mutex, or the write burst is long enough to matter), switch to per-run
`RunDir/flakiness.jsonl` + a globbing `ComputeFlakiness`. Only pursue if the mutex's own
verification (Verification step 7) shows a problem.

### B3 — `HeartbeatStaleAfter` sizing under load *(runtime investigation)*

**Owner:** whoever runs the Problem-2 overlap verification. **Blocks:** trusting the ~150 s
default in production. Static reasoning can't prove a load-backpressured-but-live runner always
refreshes within 150 s (Docker API degrades ~24× under parallel-startup load per
`minimize-exec-count-and-cut-unconsumed-diagnostic-execs.md`). **How to close:** during
Verification step 3/4 under a *full-suite* load, log the actual inter-refresh gaps and confirm
the max gap stays comfortably under `HeartbeatStaleAfter`. If it doesn't, widen the window (and
correspondingly the self-heal time). Per `runtime-post-conditions-are-gates.md`, treat ~150 s as
a starting guess to be measured, not a proven value.
