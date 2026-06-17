# Make `leaseRelease` a consistent per-test metric

## Problem

`leaseRelease` (timed in `TestLifecycle.cs:307-313` as `ResourceLease.DisposeAsync()` →
`TestResourceBroker.ReleaseAsync()`) is ~0.01s for most tests but ~30s for a few. The
spike is **not** a property of the spiking test — it's the cost of tearing down a shared
server container (ffmpeg stop + recording extraction + Docker stop-grace), charged to
whichever of a config's concurrent tests happens to be the **last to release** it.

### Mechanism (verified from source)

`ReleaseAsync` (`TestResourceBroker.cs`) does different work per branch:

- **Reuse** (other tests still need the config): decrement refcount, return → ~0.01s.
- **PerTest isolation** (`IsolationMode.PerTest`, line 2021): `await managed.DisposeAsync()`
  **synchronously on the test thread** → ~30s.
- **Demand-exhausted eviction** (`remaining <= 0 && remainingTests <= 0`, line 2052):
  `await managed.DisposeAsync()` **synchronously** at line 2070 → ~30s, then a sibling
  sweep (line 2085) that *already backgrounds* its heavy disposes (2117-2140).

`managed.DisposeAsync()` → `ServerContainer.DisposeAsync()` (`ServerContainer.cs:857`)
runs: log-stream drain (`drain-before-consume-disposal.md`), ffmpeg `StopAsync`,
`RetrieveFullRecordingAsync` gated by `host.ExtractLimiter`, `instance_recording` emit,
container removal. `docker exec`-heavy extraction degrades ~24× under parallel load
(`minimize-exec-count-and-cut-unconsumed-diagnostic-execs.md`), which is why it dominates.

### Why the fix is "make it consistent", not novel

Three other sites already background this exact `DisposeAsync` chain off the test thread:
1. **Eviction path** `TryEvictIdleServerForAsync` (line 1811-1864) — the complete template.
2. **Sibling sweep** inside the release path itself (line 2117-2140).
3. **Per-test deferral** via `EnqueueBackgroundTask` (line 97).

The synchronous primary dispose at line 2070 (and 2039) is the odd one out. Making it
match the eviction template removes the per-test skew with zero new design surface.

## Approach (chosen)

1. **Background the primary dispose** at lines 2039 (PerTest) and 2070 (demand-exhausted),
   following the eviction template (`_runCts` short-circuit → `ReleaseSlotEarly()` sync →
   background heavy `DisposeAsync` with `SuppressFlow`). `leaseRelease` then becomes
   uniformly ~0.01s (refcount release only) for every test.
2. **Stamp teardown duration on `server_disposed`** so the cost stays auditable at the
   server level (where it belongs), not lost.

Wall-clock is **not hidden**: `_backgroundDisposeTasks` is drained at end of run via
`Task.WhenAll` (`TestResourceBroker.cs:2359`), so the run can't finish until teardown
completes. The cost moves *off per-test timing* (overlapping later tests during the run,
extending final shutdown otherwise) — the same trade-off siblings/eviction already make.
Per `test-timing.md`, per-test numbers were never wall-clock-additive, so this stops
*over*-representing teardown on one arbitrary test rather than under-counting it.

## Edits (exactly 4)

### Edit 1 — `TestResourceBroker.cs` ~2021-2041, PerTest branch

Replace the synchronous `await managed.DisposeAsync()` (line 2039) with the eviction
template: `_runCts` short-circuit (sync dispose during shutdown so nothing enqueues past
the drain), then `managed.ReleaseSlotEarly()` synchronously, then enqueue the heavy
dispose via `_backgroundDisposeTasks` + `SuppressFlow`, timing it and stamping
`durationMs` on a `server_disposed` emit. Keep `_servers.TryRemove` + `ReleaseSteamAccount`
synchronous (observers must see the server leave the pool immediately, matching the
sibling-sweep ordering at 2089-2090). Move the existing `server_disposed` emit so it
carries `durationMs` from the backgrounded task (emit at dispose completion, not before).

### Edit 2 — `TestResourceBroker.cs` ~2052-2071, demand-exhausted primary

Same transformation for the primary dispose at line 2070. Note `_runCts`-short-circuit
already exists implicitly via `ShutdownCoordinator.IsShuttingDown` upstream (line 2009) —
but add the `_runCts.IsCancellationRequested` guard for parity with the eviction path,
since `Task.WhenAll` drain happens in `DisposeAsync` and a late enqueue would leak a
container (the exact bug the eviction guard at 1811 documents).

### Edit 3 — `server_disposed` event: add `durationMs`

Add `durationMs` to the `server_disposed` emit (currently at 2025, 2060, 2091). Timed
around the backgrounded `DisposeAsync`. For the synchronous-shutdown short-circuit path,
time it inline. Update any event-catalog reference for `server_disposed`
(`docs/developers/events-schema.md` / `Helpers/InfrastructureEventLog.cs` — grep first;
per `event-catalog-no-inline-enums.md` reference the emitting class, don't inline-list).

### Edit 4 — comments

Update the comment block at 2077-2084 (it already describes the sibling-sweep backgrounding
as protecting "the last test's leaseReleaseMs critical path") to reflect that the **primary**
dispose is now backgrounded too — the whole release-path eviction is off-thread, consistent
with `TryEvictIdleServerForAsync`. Per `comments-earn-their-length.md` and
`no-refactor-history-in-code.md`: state the current rule, no change-history.

## Cross-step invalidation check

- **Edits 1 & 2 both enqueue to `_backgroundDisposeTasks`** — the existing `Task.WhenAll`
  drain (2359) already covers them; no new drain site. ✓
- **`ReleaseSlotEarly()` + later `DisposeAsync` double-release**: guarded by `_slotReleased`
  (`ManagedServer.cs:25-26`, Interlocked) — the eviction path relies on this exact guard,
  so backgrounding after an early release is safe. ✓
- **`TryReuseFreedSlotAsync(host)`** (called at 2040, 2155): currently runs *after* the
  synchronous dispose. After backgrounding, the slot is freed by `ReleaseSlotEarly()`
  *before* enqueue, so `TryReuseFreedSlotAsync` still sees `Available > 0` (it already
  tolerates "slot may not be visible yet", line 1881-1885). Order is preserved: call it
  after `ReleaseSlotEarly()`, not after the backgrounded dispose. ✓
- **Sibling sweep** (2085-2142) is unchanged — it already backgrounds. Edit 2 must keep
  the sweep running *after* the primary's slot release + enqueue, same as today. ✓
- **`SuppressFlow` attribution**: required so the backgrounded dispose doesn't carry the
  last test's `TestContext.Current` onto `recording_extracted` / `instance_recording` /
  `server_disposed` (`asynclocal-pitfalls.md`). The eviction + sibling paths already do
  this; mirror it. ✓

## Runtime post-conditions (GATES — must run, not infer; `runtime-post-conditions-are-gates.md`)

These are runtime observations, not static facts. Confirm against a real run:

1. **`leaseRelease` is uniform.** After the change, the per-test breakdown tree shows
   `leaseRelease` ≈ refcount cost (sub-second) for **every** test, including the ones that
   previously spiked to ~30s. Check the timing tree output / `summary.json` breakdown.
2. **No lost teardown.** Total run duration is not materially lower in a way that implies
   skipped work (it shouldn't drop much — teardown overlaps or tails the run; it was never
   fully on the critical path per `test-timing.md`). `server_disposed.durationMs` is
   populated and ≈ the ~30s previously seen on `leaseRelease`.
3. **No orphan containers.** Run completes with `Task.WhenAll(_backgroundDisposeTasks)`
   draining cleanly; no leaked `sdvd.test=true` containers/volumes after the run
   (emergency-cleanup sweep finds nothing). Recordings (`full_recording.mp4`) still land
   on disk and `instance_recording` still reaches the UI.
4. **Magnitudes are observed, not assumed.** The ~30s / ~0.01s figures in this plan are
   the user's report + source reading; measure them on the validating run.

Validate with a small filtered run that forces an eviction (a config whose tests all
finish, triggering demand-exhaustion) — e.g. `make test FILTER=<class>` for a class that
owns its config — then inspect the breakdown + `infrastructure.jsonl` `server_disposed`.

## Out of scope

- The `lastKeepDispose` metric (KeepConnected sessions, `FinalizeSessionAsync`) — different
  path, not part of this skew. Leave as-is.
- **Per-test recording-*clip* extraction** (`artifacts` → `recordingMs`, via
  `TestArtifactCollector.FinalizeRecordingAsync`) — a *separate* flow from the full-recording
  teardown this plan fixes. Different file (per-test clip vs. `full_recording.mp4`), different
  metric (`recordingMs` vs. `leaseReleaseMs`), different code path. Its attribution is
  deliberately outcome-gated and is **not** uniformly backgrounded:
  - **Failed** test → clip extraction is `await`ed synchronously and **does** land on
    `recordingMs` (by design — the clip must be on disk before the `test_failed` event so
    the runbook can find it).
  - **Passing**, mode=`all` → deferred via `EnqueueBackgroundTask`; `recordingMs` ≈ 0.
  - **Passing**, mode=`failure` → no-op (`retention_passed` short-circuit); `recordingMs` ≈ 0.

  This plan does not touch this flow. The two don't interact: `RetrieveFullRecordingAsync`
  (full recording, inside `ServerContainer.DisposeAsync`, charged to `leaseRelease`) is
  distinct from per-clip `orchestrator.FinalizeAsync` (charged to `recordingMs`). If
  uniform `recordingMs` on *failed* tests is also wanted, that's a separate decision —
  flag it, don't fold it in here (the failed-test sync await is a deliberate runbook
  ordering constraint, not the same accidental skew).
- `cleanupMs` parent: it currently includes `leaseReleaseMs` as a child (cleanup stopwatch
  spans the dispose, `TestLifecycle.cs:264-406`). After the change the child shrinks to
  ~0.01s, so `cleanupMs` naturally drops too — no separate edit needed.
