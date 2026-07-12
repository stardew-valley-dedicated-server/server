# Persist all in-memory-only runner data to disk artifacts

## Context

The test runner accumulates a large in-memory state model (`TestRunState`) from
events that arrive over the `SetupEventBus` named pipe. That bus is **disk-free
by design** (`SetupEventBus.cs:18-27`) — its events drive the live web UI and the
in-memory snapshot, but are never written to the run-artifact tree. The durable
artifacts (`summary.json`, `ctrf-report.json`, `infrastructure.jsonl`, per-test
screenshots/recordings) only capture a subset.

This surfaced when a remote-VPS run failed (host `vps-1` poisoned mid-run) and
the per-container CPU/memory history needed to diagnose "saturated vs network
drop" existed only in memory. We fixed that one case by adding
`diagnostics/instance-stats.jsonl` (flushed at run-end from
`TestRunState._instances[*].StatsHistory`). An exhaustive audit of `TestRunState`
then found **every other in-memory-only accumulation** — this plan persists all
of them, for full consistency.

**Why the report bundle doesn't count:** `ReportGenerator` embeds the full
snapshot (`ToSnapshotJson`) into `report/index.html`, but it no-ops unless the
test-UI SPA is built (`ReportGenerator.cs:57-63`). **0 of 79 recent runs** had a
`report/` dir — headless/CI/VPS runs never build the SPA, so "in the snapshot"
means "lost on the runs that matter."

**Outcome:** four new `diagnostics/*.jsonl` sinks so the full UI-visible state is
recoverable from saved artifacts on every run, headless included.

## Already done (the template this plan generalizes)

`instance-stats.jsonl` shipped via four edits, all verified working
(`instance-stats.jsonl` = 1.04 MB / 2229 lines in the latest run):
- `TestRunState.WriteInstanceStatsJsonl(path)` — public method, iterates a private
  collection under `_lock`, one compact line per row via the existing `Serialize`
  helper (`DiagnosticEmitJson`, same compact format as `infrastructure.jsonl`).
- `RunArtifactNames.InstanceStatsJsonl` const + layout doc line.
- `TestRunArtifactWriter.WriteIfNotWritten(view, state)` — already takes `state`;
  a `try/catch` block calls a private `WriteInstanceStats(state)` helper that
  ensures `diagnostics/` exists and delegates to the `TestRunState` method.
- `RunRecorder.WriteRunArtifacts()` passes `State` through.

**Every sink below reuses this exact pattern.** No new plumbing: the `state`
parameter and the idempotent every-exit-path write seam already exist.

## Audit findings — the complete set of unpersisted data

Verified against run `2026-06-25T05-58-51Z_8ad3480`: none of the `SetupEventBus`
event vocabularies appear in `infrastructure.jsonl`. Data that *is* on disk
(`server_acquired`, `capacity_*`, `container_*`, `peer_*`, `screenshot_captured`,
`recording_*`, `health.*`) lands there only because its producer *separately*
calls `InfrastructureEventLog.Emit` — independent of the UI pipe.

Grouped into four natural sinks (🔴 = not on disk anywhere today):

### Sink A — `instance-history.jsonl` (highest diagnostic value)
Per-instance lifecycle narrative. Source: `_instances[*]` (`InstanceState`,
`TestRunState.cs:2269`).
- 🔴 `History` (`List<InstanceHistoryEntry>` — `Timestamp/Event/TestName/Reason/
  ServerInstanceId/ClientInstanceId`): created/leased/returned/disposed/poisoned/
  connected/disconnected — *which test held which container, and why it was
  poisoned* (the gap that would have answered the vps-1 failure directly).
- 🔴 Current instance status fields, emitted once per instance as a trailing
  "final state" line (or folded into history): `Status`, `Connected`, `Disposed`,
  `CurrentTest`, `PoisonReason`, `ConnectedServerId`, `VncUrl`, `SetupStatus`,
  `RecordingPath`, plus identity (`InstanceId/HostId/InstanceType/ServerKey/Label`).

### Sink B — `run-events.jsonl` (the UI event stream)
Source: `_eventLog` (`List<object>`, **bounded ring buffer, 5000 entries**,
`TestRunState.cs:48`; `AddEventLog` does `RemoveAt(0)` on overflow, `:2066`).
Every `Apply*` (26 call sites) adds an anonymous event object here; this is the
stream the UI replays to late-connecting clients. **Not in the snapshot** —
purely live + in-memory.
- 🔴 `_eventLog` — 27 low-frequency event types (`run_started`, `test_started/
  running/passed/failed/skipped`, `test_enrichment`, `instance_*`, `setup_*`,
  `screenshot`, `recording`, `diagnostic`, `error`, `flaky_tests`, …). The
  high-frequency broker events (`http_served`, `wait`, `poll_*`) do **not** flow
  here — verified absent from the `Apply*` event vocabulary.
- **Verified unique residue (why this sink earns its place):** `diagnostic` and
  `error` events originate from xUnit's `OnDiagnosticMessage`/`OnErrorMessage`
  (`Program.cs:799,806` → `ApplyDiagnostic`/`ApplyError`) and are **0 on disk**
  anywhere today (not in `infrastructure.jsonl`, not in the parent log) — the
  only durable home for xUnit-level diagnostics/errors. The sink also uniquely
  preserves the *unified ordering* of the run's UI event stream and
  `test_running` transitions. Test outcomes (summary/ctrf), instance lifecycle
  (Sink A), and per-test output (Sink C) overlap — that's fine; this sink's
  justification is the diagnostic/error residue + unified timeline, not novelty
  of every line.
- **Not a `one-writer-per-artifact.md` violation:** `infrastructure.jsonl` is the
  broker-side vocabulary (`server_acquired`, `capacity_*`), `run-events.jsonl` is
  the UI/xUnit-side vocabulary — different artifacts, different producers, no
  shared schema to drift. Both stay.
- **Truncation caveat (HONEST):** unlike the other three sinks (which write their
  source collection in full), this one's source is a lossy 5000-entry ring
  buffer. A normal full suite produces ~700–1200 such events (bounded by test
  count ~218 incl. canceled, ×~4 lifecycle events, + 8 instances + setup +
  ~35 screenshots + diagnostics) — **well under 5000, so no truncation in
  practice.** But a pathological run (huge suite, flood of `test_output`) could
  evict the earliest events silently. **Mitigation (must implement):** at flush,
  if `_eventLog.Count == MaxEventLogSize`, write a leading
  `{"event":"run_events_truncated","cap":5000}` marker line so a reader can never
  mistake a truncated file for a complete one. Do **not** raise/remove the cap —
  it bounds live memory and isn't hit in practice; the marker makes the rare
  truncation visible instead of silent.

### Sink C — `test-details.jsonl` (per-test extras beyond summary/ctrf)
Source: `_collections[*].Classes[*].Tests[*]` (`TestState`, `TestRunState.cs:2124`).
summary.json/ctrf already carry outcome/error/category/timing for *failed* tests;
these are the UI-only extras, for **all** tests:
- 🔴 `Output` (per-test stdout + `EmitTestAnnotation` lines)
- 🔴 `StackTrace` for passed/skipped/canceled tests (failed-test stacks are in
  summary.json; non-failed are snapshot-only)
- 🔴 `FailureContext` (server-state dump at failure)
- 🔴 `RecordingSkipReasons`, `UsedInstances`, `DiscoveryOrder`, `ExecutionOrder`,
  `StartTime`, `RunningStartTime`
- 🟡 `Recordings[].TimelineOffset` + `WallClockDuration` (paths already in CTRF;
  offsets snapshot-only) — include here for completeness.
- Excluded (internal-only, deliberately never serialized): `EnrichmentOutcome`,
  `OutcomeSource` — these are reconciliation provenance, not observable state;
  leave them out (documenting the exclusion, per `holistic-or-explicit-todo.md`).

### Sink D — `setup-phases.jsonl` (prestart/warmup narrative)
Source: `_setupPhases` (`List<SetupPhaseState>`, `TestRunState.cs:34`).
- 🔴 Per phase: `Category/PhaseName/CollectionName/Status/StartTime/EndTime/
  ErrorMessage` + `Steps[]` (`StepName/Status/Details/Output/Timestamp`). Emit
  per (phase, step).
- **Scope (verified — do not oversell):** `_setupPhases` is **prestart-only**. It
  captures the run-start narrative — parent-side phases (preflight, cleanup,
  image distribution, game-data distribution; `Program.cs:496,543,646,709`) and
  child-side per-collection prestart phases (via `ApplySetupPhaseStarted`/
  `ApplySetupStep`). It does **not** contain mid-run server re-provisioning —
  the broker doesn't emit a new setup phase on re-allocate-after-failure. That
  per-instance repair narrative lives in `InstanceState.SetupSteps`, surfaced by
  **Sink A** (instance-history). So Sink D is the run-start phase view, not a
  "canonical" all-of-setup view; Sink A carries the per-instance setup detail.
  (The parent-side phases also appear coarsely in `infrastructure.parent.jsonl`
  as `docker_preflight`/`image_build_*`/`ssh_preflight`; Sink D adds the
  step-level breakdown and the child-side collection phases that the parent log
  lacks.)

## Approach

Apply the stats template four more times. Each sink = one method on
`TestRunState`, one `RunArtifactNames` const, one write block + private helper in
`TestRunArtifactWriter`. `RunRecorder` and the `WriteIfNotWritten` signature are
**already done** — no further change there.

### Per-sink changes (×4: A, B, C, D)

1. **`TestRunState.cs`** — add `public void Write<Sink>Jsonl(string path)`:
   - Under `_lock`, iterate the relevant private collection, build one anonymous
     object per row, serialize each with the existing `Serialize(...)` helper
     (compact, matches `instance-stats.jsonl`), `File.WriteAllLines(path, lines)`.
   - Skip empty rows so the file is empty/cheap when the data wasn't produced
     (mirrors the stats method's empty-history skip — an empty source yields a
     0-byte file, consistent with shipped `WriteInstanceStatsJsonl`).
   - **Sink B only:** if `_eventLog.Count == MaxEventLogSize` at flush, prepend a
     `{"event":"run_events_truncated","cap":MaxEventLogSize}` line so a full
     (ring-buffer-evicted) log is never mistaken for a complete one.
   - Reuse the field sets already projected in `BuildSnapshot` (instance history
     `:1905`-ish, setup phases `:1816`, per-test `:1722`) — same inline-anonymous
     pattern the file uses in 3+ places already. Do **not** extract a shared
     projection helper: the snapshot needs nested shapes, the JSONL needs flat
     stamped lines; a shared helper would force a named DTO and fight the
     anonymous-type idiom (rejected for the same reason during the stats work).
   - **Lock-held write is intentional and safe** (matches shipped
     `WriteInstanceStatsJsonl`): the methods hold `_lock` across iterate +
     `Serialize` + `File.WriteAllLines`. This differs from `GetArtifactView`
     (copy-under-lock, serialize-outside) but is fine here because all
     `WriteRunArtifacts` call sites fire *after* `runner.Run()` returns and the
     `AssemblyRunner` is disposed (`Program.cs:836-840`, write at `:891`) — the
     xUnit child has exited, so no `Apply*` producer contends on `_lock` during
     the write. Build-then-write (`List<string>` then one `WriteAllLines`) means
     a throw during row-building never leaves a partial file.
   - **`JsonElement` fields are safe to serialize** (`FailureContext` in Sink C,
     and `_flakyTests`/`_runMetadataData` elsewhere): they arrive via
     `el.Deserialize<T>()` (`DiagnosticEmitJson.cs:53`), which materializes
     self-contained elements that survive the source `JsonDocument`'s disposal —
     proven in production by `_flakyTests` (same no-explicit-`Clone` path)
     already round-tripping into `summary.json`. (The manual `.Clone()` at
     `EventDispatcher.cs:82,91` exists only because those two use raw
     `TryGetProperty`, which *does* return a live view; not a missing safety.)

2. **`RunArtifactNames.cs`** — add the const + extend the `diagnostics/` layout
   doc line:
   - `InstanceHistoryJsonl = "instance-history.jsonl"`
   - `RunEventsJsonl = "run-events.jsonl"`
   - `TestDetailsJsonl = "test-details.jsonl"`
   - `SetupPhasesJsonl = "setup-phases.jsonl"`

3. **`TestRunArtifactWriter.cs`** — in `WriteIfNotWritten`, add four `try/catch`
   write blocks (mirroring the existing `WriteInstanceStats` block) + four private
   `Write<Sink>(state)` helpers, each ensuring `diagnostics/` exists and delegating
   to the `TestRunState` method. Each block is independently guarded so one
   failing sink can't suppress the others (matches the existing per-artifact
   error isolation).

### Files
- `tests/JunimoServer.TestRunner/Rendering/Web/TestRunState.cs` (4 new methods)
- `tests/JunimoServer.Tests/Helpers/RunArtifactNames.cs` (4 consts + doc)
- `tests/JunimoServer.TestRunner/Rendering/Web/TestRunArtifactWriter.cs` (4 blocks + 4 helpers)

### Reused, not rebuilt
- `WriteInstanceStatsJsonl` / `WriteInstanceStats` — the literal template.
- The `Serialize` helper (`TestRunState.cs:2018` → `DiagnosticEmitJson`, compact).
- `BuildSnapshot`'s existing projections for each collection — copy field sets.
- `WriteIfNotWritten(view, state)` seam + `RunArtifactNames.DiagnosticsDir`.
- `RunRecorder.WriteRunArtifacts()` — unchanged (already threads `State`).

## Consistency & non-goals (named, not silently dropped)
- **No snapshot blanket-dump.** Persisting `ToSnapshotJson()` wholesale would
  duplicate summary/ctrf/stats data and tie durability to the SPA-gated report.
  Four targeted JSONL sinks keep one-writer-per-artifact clean.
- **No de-dup against `infrastructure.jsonl`.** Sink B (UI event stream) and
  `infrastructure.jsonl` (broker stream) are different vocabularies/projections;
  both stay. Not a `one-writer-per-artifact.md` violation (different artifacts).
- **No new growth caps / sampling.** Sinks A/C/D write their source collections
  in full; the data is already in memory, the files only expose it. Sink B's
  source (`_eventLog`) carries a *pre-existing* 5000-entry ring-buffer cap — not
  added by this plan and not hit by a normal run (~700–1200 events). We do **not**
  change that cap (it bounds live memory); instead Sink B writes a
  `run_events_truncated` marker line when the buffer was full, so a rare
  truncation is visible rather than silent (see Sink B above). The "full
  consistency" goal therefore holds for A/C/D unconditionally and for B up to the
  documented, marked cap.
- **No test-ui consumption.** The UI reads this data live over the WebSocket;
  these files are post-mortem/CI sinks. No `runner-ui-pipeline-plumbing.md` hops.
- **No distributed-mode handling.** No multi-worker coordinator exists (single
  `AssemblyRunner`); `_instances`/`_eventLog` are per-process. Add nothing
  speculative — when distributed mode lands it will revisit all per-worker
  diagnostics (incl. the existing `instance-stats.jsonl`) together.
- **Internal-only fields excluded** from Sink C (`EnrichmentOutcome`,
  `OutcomeSource`) — reconciliation provenance, not observable state.

## Verification
1. **Build:** `dotnet build tests/JunimoServer.TestRunner/JunimoServer.TestRunner.csproj`
   + the Tests project (for the new `RunArtifactNames` consts).
2. **Run with a small filter** (stats/UI events on by default), e.g.
   `make test FILTER=CabinStrategyTests`. Then, in
   `TestResults/runs/<newest>/diagnostics/`, confirm each new file:
   - `instance-history.jsonl` — non-empty; a line per lifecycle transition;
     carries `instanceId`, `event` (created/leased/…), `testName`, `reason`.
   - `run-events.jsonl` — non-empty; spot-check it parses and carries the UI
     event stream (test_started/passed, instance_*, diagnostic, error). Confirm
     `diagnostic`/`error` lines appear (these are 0 in infrastructure.jsonl — the
     unique-residue check). On a normal run, confirm NO `run_events_truncated`
     marker (line count < 5000); to exercise the marker, a stress run isn't
     required — a unit-level check that the marker is emitted when the buffer is
     full is sufficient.
   - `test-details.jsonl` — a line per test; carries `displayName`, `status`,
     `usedInstances`, ordering/timing; annotations present for tests that emit
     them (e.g. a `DownloadValidationTests` run).
   - `setup-phases.jsonl` — non-empty; phases with steps + timestamps.
   Each line must `jq .`-parse. This is the runtime gate
   (`runtime-post-conditions-are-gates.md`): a green build does not prove the
   files are written — inspect the artifacts.
3. **Empty-data paths:** confirm a sink whose source produced nothing writes an
   empty file (or is skipped) without throwing — e.g. `setup-phases.jsonl` on a
   fully-reused-server run, `test-details` annotations on tests that emit none.
4. **Abort path:** Ctrl+C mid-run; confirm the abort-path `WriteRunArtifacts`
   (`Program.cs:153`) still emits all four files (idempotent `_written` latch ⇒
   no double-write from the outer finally). The four sinks inherit the existing
   per-path drain behavior unchanged — graceful drains 5s before write
   (`:868`→`:891`), Ctrl+C drains 500ms (`:140`→`:153`), and the force-exit nuke
   path (`:371`) does **not** drain, so its files may miss the last in-flight
   events. This is pre-existing and identical for every artifact (summary.json
   included); the sinks add no new timing guarantee and need none.
5. **Early-failure paths write partial-but-honest files.** Preflight/image-build/
   game-data failures (`:534/:638/:736`) fire `WriteRunArtifacts` before tests
   run, then `return` (no fall-through). Expect `instance-history`/`run-events`
   near-empty, `test-details` all-`pending`, `setup-phases` populated up to the
   failed phase — an accurate snapshot of where the run died, not a bug.
