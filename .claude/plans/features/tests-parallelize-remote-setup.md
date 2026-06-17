# Parallelize remote-host setup: preflight → fan-out(A, B)

## Context

The test runner's per-run setup (`tests/JunimoServer.TestRunner/Program.cs`, ~lines
489–752) executes five remote-host phases **strictly sequentially**: Preflight →
Cleanup leftovers → Build images → Image distribution → Game-data distribution. The
two VPS-state phases (cleanup, game-data — both mutate the remote via `host.ApiClient`)
and the two image phases (build, distribute) touch **disjoint remote state** and are
independent once the SSH forward is open. Running them sequentially serializes two
transfers that could overlap; on first provision of a fresh remote this overlaps a
~250 MB game-data volume push with the image build+push.

**Target structure** — preflight stays a shared prerequisite (it opens the SSH forward
both buckets need; image distribution dereferences `host.ApiClient` at
`ImageDistributor.cs:113`, which only materializes inside `HostPool.PreflightAsync` →
`InitializeAsync`, `HostPool.cs:560`), then two sequential buckets run concurrently:

```
preflight (shared prerequisite — opens SSH forwards)
   │
   ├─ Bucket A:  cleanup leftovers → game-data distribution   (VPS state)
   └─ Bucket B:  build images      → distribute images        (images)
```

## Blocker found by adversarial review — must fix first

**Setup-step progress events route to their phase by *inference*, not explicitly, and
the inference breaks when two phases share a category.** This makes naive
parallelization silently corrupt the setup UI.

- `SetupStepEvent` (`tests/JunimoServer.Tests/Schema/Events/SetupEvents.cs:42-54`)
  carries `(Category, StepName, Status, Details, CollectionName)` — **no phase name.**
- `ApplySetupStep` (`TestRunState.cs:722`) resolves the owning phase via
  `FindActivePhaseKey(category, collection)` (`:1960-1985`): tries a collection-exact
  match, else returns the **first** active phase whose category matches, else any.
- All four parent phases share `category == "Runner"` (`Program.cs:487`;
  `ImageDistributor.cs:41`, `GameDataDistributor.cs:48`, and the build sink) with
  `collection == null`. Run two "Runner" phases at once and every step from **both**
  distributors routes into whichever phase enumerates first in `_activePhases`.
  Nondeterministic; the live WebUI mirrors it via the JS twin
  `findActiveSetupPhase` (`tests/test-ui/src/composables/useTestStore.ts:1361-1372`).
  This is **logical mis-routing in the *state* path**, which `TestRunState._lock`
  serializes correctly (the routing *key* simply can't tell two concurrent "Runner"
  phases apart) — **but the lock does not cover the inner-renderer dispatch**, which
  Stage 2 must independently confirm is concurrency-safe (see "Inner-renderer dispatch
  thread-safety" below).

**Inner-renderer dispatch thread-safety (verified against source — the lock does NOT
cover this path).** `renderer.OnSetupStep` is `RendererDispatchGuard.OnSetupStep`
(`RendererDispatchGuard.cs:344-349`), which does two things: (1)
`ApplyState(..., () => _recorder.State.ApplySetupStep(e))` — this *is* serialized,
because `ApplySetupStep` takes `TestRunState._lock` internally; but (2)
`Guard(() => _inner.OnSetupStep(e))` dispatches to the inner renderer **outside any
lock** (`ApplyState` itself holds none — `:119-138`). So under two concurrent buckets the
inner renderer's `OnSetupStep` runs concurrently. Per-renderer verdict:
- **`CIRenderer`** — safe. The meaningful branches (`Completed`/`Failed`/`Warning`/
  `InProgress`) take `_writeLock` (`CIRenderer.cs:249-281`); only the `Started` spinner
  check is deliberately lock-free with a documented benign race.
- **`LLMRenderer`** — `WriteJson` does `_out.WriteLine(json); _out.Flush()` with no lock
  (`LLMRenderer.cs:290-295`), but `_out` defaults to `Console.Out` (a synchronized
  `TextWriter`), so each *line* is atomic. Concurrent buckets produce possibly-reordered
  JSONL, never corrupted lines — acceptable for line-delimited output whose consumer
  doesn't assume cross-producer ordering.
- **`WebRenderer`** — `OnSetupStep` is a no-op (`WebRenderer.cs:489`); the live data goes
  through `_broadcast?.Invoke(json)` in the guard. **Stage 2 must confirm the WebSocket
  broadcast callback is safe under concurrent invocation** before relying on it (not yet
  traced).

So the routing fix is necessary but **not sufficient on its own** — concurrent dispatch
to the inner renderer is a separate axis the structural fix doesn't address. It is
verified safe for CI and LLM; WebRenderer's broadcast path is the one open item.

Phase *start/complete* events already route by an explicit
`MakePhaseKey(category, phase, collection)` (`:1953-1958`). The xUnit **child** already
handles concurrent phases correctly — `ManagedServer`
(`tests/JunimoServer.Tests/Infrastructure/ManagedServer.cs`) emits each server's steps
with `collectionName: Key` (`ManagedServer.cs:780-784,793`), so the collection-prefix
branch of `FindActivePhaseKey` (`:1967`) disambiguates. Only the **parent's**
null-collection "Runner" phases fall into the ambiguous category-only fallback.

**Critical subtlety (caught in final review — corrected the original plan):** the child
opens its phase keyed by `_displayLabel` (`EmitPhaseStarted("Setup", _displayLabel, Key)`,
`ManagedServer.cs:779` → key `"{Key}:Setup:{_displayLabel}"`) but emits its steps
carrying only `collectionName: Key` (no phase). `FindActivePhaseKey` bridges that gap via
its collection-prefix match. So **the inference function cannot be deleted** — the child
genuinely relies on it. The fix is *additive*: add a **phase-exact-match branch that
takes precedence when `PhaseName` is present** (parent's case), and **keep the existing
collection/category inference** for callers that omit phase (child's case). The routing
key dimension differs per process — parent disambiguates by phase, child by collection —
and both must work. This is the "phase optional, route by best key available" design.

## Stage 1 — Structural fix: route setup steps by explicit phase name

Add an **optional** `PhaseName` to `SetupStepEvent` and make routing prefer a
phase-exact key when present, falling back to today's collection/category inference when
absent. `SetupStepEvent` is a **positional record** — to keep child callers untouched,
add `PhaseName` as the **last parameter with a `= null` default**, so the ~33
`SetupEventBus.EmitStep` child callers and other sites that don't pass it still compile.
Only the parent-side sites that need correct concurrent routing pass it.

**Edit surface (verified by grep, recounted):** `new SetupStepEvent(...)` appears at
**23** sites across **6** files — `Program.cs` (5), `ImageDistributor.cs` (7),
`GameDataDistributor.cs` (8), `RunnerCallbacks.cs` (1, `:276`),
`RendererBuildProgressSink.cs` (1), `SetupEventBus.cs` (1, `:82`). So **21 are direct
constructors** (the first four files) and **2 are choke points**
(`RendererBuildProgressSink.Step`, `SetupEventBus.EmitStep`) — *not* "20 direct + 3
choke", and the `IBuildProgressSink` interface contains **no** `new SetupStepEvent(`
(`SetupEventBusBuildProgressSink.Step` forwards to `SetupEventBus.EmitStep`, the choke
point already counted). `RunnerCallbacks.cs:276` is a **parent-process** direct site but
its category is `"Setup"` (a fixture phase), not `"Runner"`; it is single-phase, so it
needs no concurrent-routing change — leave it on inference like the orphans below.
`SetupEventBus.EmitStep` has **33 callers across 7 child-process files** — these stay
**unchanged** (they route correctly by collection today). Only the parent path is edited.

**Hops (each verified against source):**

1. **Record** — `SetupEvents.cs:42` add `[property: JsonPropertyName("phase")] string? PhaseName = null` as the last param. System.Text.Json round-trips it over the IPC pipe automatically (no `EventDispatcher`/`SetupPipeServer` parse edit — verify with one JSONL line).
2. **Routing (the actual fix)** — `TestRunState.ApplySetupStep` (`:722`): when `e.PhaseName != null`, look up `MakePhaseKey(e.Category, e.PhaseName, e.CollectionName)` directly (exact match, parent's case). When null, fall through to the existing `FindActivePhaseKey(e.Category, e.CollectionName)` (child's case). **`FindActivePhaseKey` is NOT deleted** — the child opens its phase by `_displayLabel` but emits steps carrying only `collectionName: Key` (`ManagedServer.cs:779,780`), and the collection-prefix branch (`:1967`) is what bridges that. Deleting it would silently drop every child setup step. The category-only fallback loop (`:1975-1981`) — the actual ambiguity source — is now only reached by callers that pass neither phase nor a disambiguating collection; the parent no longer hits it.
3. **Live WS `evt`** — `TestRunState.cs:805-814`: add `Phase = e.PhaseName` (the TS store's phase-exact branch needs it). `GetArtifactView` (`:1563-end`) builds test views from `_collections` and does **not** project setup steps — verified by reading the full body; no snapshot change needed.
4. **`IBuildProgressSink`** — the builder runs exactly one phase (`"Docker Images"`). Make both sinks carry the phase as instance state (captured in `PhaseStarted`, reused in `Step`) rather than threading a phase arg into all 13 `DockerImageBuilder.Step(...)` calls. Edit `RendererBuildProgressSink.cs:30-38` (parent — needs the phase) and, for symmetry/correctness if a future concurrent child build appears, `SetupEventBusBuildProgressSink` (`IBuildProgressSink.cs:34-38`): store `_currentPhase` set by `PhaseStarted`, pass it into the emitted event. `Step()`'s interface signature is unchanged.
5. **Parent direct constructors** — pass the in-scope phase literal at the concurrent-routing parent sites: `Program.cs` (5 sites: `:498,510,542,559,574`), `ImageDistributor.cs` (7: `:133,239,259,304,335,356,727`), `GameDataDistributor.cs` (**8**: `:135,145,156,186,227,258,279,736`). The phase name is a constant already in or adjacent to each (`ImageDistributor.SetupPhase` exists at `:42`; `GameDataDistributor` has **no** `SetupPhase` const today — add one, e.g. `"Game data distribution"`; `Program.cs` needs `const`s for `"Preflight"`/`"Cleanup leftovers"`). `RunnerCallbacks.cs:276` is excluded — it's `"Setup"`-category and single-phase (see edit-surface note above).
6. **Child callers untouched** — the 33 `SetupEventBus.EmitStep` sites (`ServerContainer`, `GameClientContainer`, `ManagedServer`, `WaitUntilGameReadyInContainer`, `DownloadValidationFixture`, `RunMetadata`) and `RunnerCallbacks.cs:276` omit `PhaseName` and keep collection-based routing. No edits, no risk to already-correct code.
7. **test-ui** — `tests/test-ui/src/types/events.ts:183` add optional `phase?: string`. `useTestStore.ts` setup_step handler (`:816-817`): when `event.phase` present, match `(category, phase, collection)` via the existing `makePhaseKey`; else keep `findActiveSetupPhase(category, collection)`. **`findActiveSetupPhase` is NOT deleted** (same child-bridging reason as the C# twin). Verify with `make build-test-ui` (vue-tsc, per `test-ui-build.md`).

**Why no exact-literal-match contract risk:** because routing falls back to inference
when phase is null, a parent site that forgets to pass `PhaseName` degrades to today's
behavior (best-effort inference) rather than dropping the step. Still, hoist the parent
phase literals to `const`s (`"Image distribution"`, `"Game data distribution"`, etc.)
so the `PhaseStarted` and step emits in the same file can't diverge — cheap insurance,
not load-bearing.

**Orphan steps** — `RunMetadata.cs:138` ("Run directory") and the two pre-`PhaseStarted`
Warning steps in `DockerImageBuilder.cs:85,106` emit with no open phase. They keep
working unchanged: they pass no `PhaseName`, so they hit the inference fallback exactly
as today. No regression, no per-orphan decision needed. (This is a direct benefit of the
phase-optional design over the original delete-the-fallback plan.)

## Stage 2 — Parallelize: preflight → fan-out(A, B)

Only after Stage 1 makes concurrent same-category phases safe. All edits in
`Program.cs`, replacing the three sequential `try` blocks (~532–752); preflight
(~489–530) is unchanged.

**Decisions (confirmed with user):**
- **Cancel sibling immediately** on first abort-class failure via a shared `CancellationTokenSource`. Both distributors thread `ct` into the in-flight stream copy — `ImageDistributor` `SaveImagesAsync(…, ct)` (`:383`) + `LoadImageAsync(…, ct)` (`:406`); `GameDataDistributor` `ExtractArchiveToContainerAsync` + retry `Task.Delay(…, ct)` — so cancellation interrupts a multi-GB transfer, not just between hosts. **Verified.**
- **Promote cleanup-leftovers to abort-on-fail** (today non-fatal warning, `Program.cs:594`). Both Bucket-A steps become uniformly fatal — no special warn-and-continue branch.

**Implementation:**
1. After preflight: `using var setupCts = new CancellationTokenSource();`
2. Two `async Task<SetupOutcome>` local functions (`SetupOutcome` = success | abort `(reason, message)` — **returned, not thrown**, so a deterministic precedence applies when both fail in-window):
   - **`RunVpsStateBucketAsync()`** (A): "Cleanup leftovers" phase → `SweepStaleResourcesAsync(hosts, setupCts.Token)`; any `result.Error != null` → `setupCts.Cancel()` + abort `("cleanup_leftovers", …)`. Then "Game data distribution" → `GameDataDistributor.DistributeAsync(hosts, renderer, setupCts.Token)`; failures → cancel + abort `("game_data_transfer", …)`.
   - **`RunImageBucketAsync()`** (B): `EnsureImagesExistAsync` (+ `SDVD_SKIP_BUILD=true` on success) → "Image distribution" → `ImageDistributor.DistributeAsync(hosts, renderer, setupCts.Token)`. Build fail → `("image_build", …)`; transfer fail → `("image_transfer", …)`. Cancel on either.
   - Each catches internally: `catch (OperationCanceledException) when (setupCts.IsCancellationRequested)` → cancellation-loss outcome with **null reason** (sibling owns the cause); `catch (Exception ex)` → `setupCts.Cancel()` + abort with `ScrubForLog(ex.Message)`.
3. `var outcomes = await Task.WhenAll(RunVpsStateBucketAsync(), RunImageBucketAsync());` — never throws (both catch internally).
4. **Single** abort site (collapses three): pick `outcomes.FirstOrDefault(o => o.AbortReason != null)`, image-bucket failure winning precedence, null-reason cancellation-losses deferring. On abort: `InfrastructureEventLog.Emit("run_aborted", …)` + `recorder.SetAbortReason(...)` + `recorder.WriteRunArtifacts()` + `await renderer.DisposeAsync()` + `return 2`. The benefit is a **single** `run_aborted` cause, a single `DisposeAsync`, and deterministic precedence when both buckets fail in-window — *not* protection against a `WriteRunArtifacts()` data race. `WriteRunArtifacts()` is already idempotent: `RunRecorder.WriteRunArtifacts` calls `_writer.WriteIfNotWritten(view)`, whose `_written` latch under lock swallows the second call, and the docstring states the `finally` and abort paths "can both call this without coordination" (`RunRecorder.cs:54-66`). So the single abort site is about *one clean abort decision*, not about serializing a writer that already self-guards.

> Setup-phase aborts keep the **synchronous early `return 2`** shape and do **not**
> route through `BeginAbort` (`Program.cs:115`) — that starts xUnit graceful-teardown +
> force-kill machinery, correct for an abort *during a test run* but wrong at setup time
> (no test run exists yet). Preserving the early return is deliberate.

**Internal ordering preserved (for least-change, NOT a volume-shadowing dependency):**
A = cleanup→game-data; B = build→distribute (`SDVD_SKIP_BUILD` so the child doesn't
rebuild, `:599,:622`). Only the two buckets run concurrently; each bucket's steps stay
sequential. **Correction to the original rationale:** the `Program.cs:534` comment
("sweep a leftover volume before populating a fresh one") and the docs line
(`remote-host-setup.md:70`, "game-data volume reset by `EmergencyCleanup`") describe a
dependency that **does not exist**. `EmergencyCleanup.SweepStaleResourcesAsync` removes
only `sdvd.test=true` resources (`EmergencyCleanup.cs:342-362`), and the game-data volume
is created **unlabeled** (`new VolumesCreateParameters { Name = _gameDataVolumeName }`, no
`Labels` — `GameDataDistributor.cs:356-357`), so cleanup never touches it. The docs line
is a pre-existing inaccuracy, out of scope here. The practical upshot is *favorable*:
because cleanup neither resets the game-data volume (Bucket A) nor the `sdvd/*` images
(Bucket B), the two buckets are **more** independent than the original plan argued — the
intra-bucket order is preserved only to minimize the diff, not to satisfy a data
dependency.

## Files

- `tests/JunimoServer.Tests/Schema/Events/SetupEvents.cs` — add optional `PhaseName = null` (last param) to `SetupStepEvent`.
- `tests/JunimoServer.TestRunner/Rendering/Web/TestRunState.cs` — phase-exact route when `PhaseName` present, **keep** `FindActivePhaseKey` as the no-phase fallback; add `Phase` to live `evt`.
- `tests/JunimoServer.TestRunner/Setup/RendererBuildProgressSink.cs` (+ `tests/JunimoServer.Tests/Helpers/IBuildProgressSink.cs`'s `SetupEventBusBuildProgressSink`) — carry phase as instance state.
- Parent direct-constructor sites only — `Program.cs` (5), `ImageDistributor.cs` (7), `GameDataDistributor.cs` (8): pass the in-scope phase const.
- `tests/test-ui/src/types/events.ts`, `tests/test-ui/src/composables/useTestStore.ts` — optional `phase` field, phase-exact route when present, **keep** `findActiveSetupPhase` fallback.
- `tests/JunimoServer.TestRunner/Program.cs` — Stage 2 fan-out.

**Untouched:** the 33 `SetupEventBus.EmitStep` child callers (`ManagedServer`,
`ServerContainer`, `GameClientContainer`, `WaitUntilGameReadyInContainer`,
`DownloadValidationFixture`, `RunMetadata`, `RunnerCallbacks`) — they route by
collection today and keep doing so. No `SetupEventBus.cs` signature change required (the
record's new param is optional; the `EmitStep` overload only needs a phase param if a
child caller ever wants phase-exact routing, which none do now).

No changes to `ImageDistributor`/`GameDataDistributor`/`HostPool`/`EmergencyCleanup`
*logic* — they already accept and thread `CancellationToken`. (They gain only the phase
const from Stage 1.)

## Risks / caveats

- **Stage 1 is the larger, riskier change** — ~23 sites, child + parent processes, the test-ui twin, and a new exact-literal-match contract. The phase-const hoist (mitigation above) is mandatory, not optional. This is real scope the parallelization *requires*; it is not optional polish.
- **Inner-renderer dispatch is a *second* concurrency axis the Stage 1 routing fix does not address.** `TestRunState._lock` serializes the *state* mutation, but `RendererDispatchGuard.Guard(() => _inner.OnSetupStep(e))` dispatches to the inner renderer outside that lock (`RendererDispatchGuard.cs:344-349`, `ApplyState` holds no lock `:119-138`). Verified safe for `CIRenderer` (its `Completed`/`Failed`/`Warning`/`InProgress` branches take `_writeLock`, `CIRenderer.cs:249-281`) and `LLMRenderer` (`_out` is `Console.Out`, a synchronized `TextWriter`, so each JSONL line is atomic; ordering across concurrent buckets may interleave but lines never corrupt). **Open item: `WebRenderer`'s `_broadcast` callback under concurrent invocation is not yet traced** — confirm it before relying on the WebSocket stream during concurrent setup.
- **Concurrent daemon + SSH-mux contention (unverified).** Buckets A+B hit the same remote daemon over the same forward at once: `docker load` (cap 3, `ImageDistributor.cs:36`) vs busybox create/start/tar-extract (cap 2, `GameDataDistributor.cs:46`). Caps were tuned for **sequential** phases; concurrently up to 5 streams/host. Per `minimize-exec-count-and-cut-unconsumed-diagnostic-execs.md`, transfer pressure degrades sharply under parallel load. **Must be measured on a real fresh VPS** — if it degrades, the fix is a *shared* per-host gate across both distributors, added only after confirming contention is real (don't preempt).
- **Bounded payoff.** Game-data skips on every re-run against a provisioned host (`GameDataDistributor.cs:152`); only first-provision of a fresh remote benefits. Weigh against Stage 1's cost.

## Verification

1. **Build:** `dotnet build tests/JunimoServer.TestRunner/JunimoServer.TestRunner.csproj` + `make build-test-ui` (vue-tsc) — both green. The positional-record change guarantees a build error at any un-updated emit site.
2. **Stage 1 regression (runtime gate, before Stage 2):** run the suite on the default single-local-host config and watch the setup tree. Every existing phase's steps must appear **under the correct phase** — Preflight, Cleanup, Docker Images, both distributions, and (in the child) per-server "Setup" phases. Confirm no step is missing (the dropped-step contract) and the orphan steps (`RunMetadata` "Run directory", the two builder Warnings) still appear wherever they were re-homed. Per `runtime-post-conditions-are-gates.md`, a green build does not prove correct routing — observe a real run.
3. **Stage 2 concurrency (the target):** fresh-VPS first-provision run; via WebUI / `infrastructure.jsonl` confirm "Game data distribution" and "Image distribution" progress events **interleave in time**, and each phase's steps stay correctly under it (not cross-contaminated — the Stage 1 gate). Also run the same concurrent setup in **CI/LLM renderer mode** (`make test-llm`) and confirm no torn/garbled setup lines (the inner-renderer concurrency axis from Risks); if WebRenderer is the target, confirm the live WS stream stays intact under concurrent buckets. Compare total setup wall-clock vs the sequential baseline; watch for transfer retries signalling daemon/SSH contention.
4. **Abort path:** force one bucket to fail (broken Dockerfile, or a remote out of disk) and confirm (a) exit code 2 + correct `run_aborted` cause (single emit — the single abort site, not the writer, guarantees this); (b) `summary.json`/`ctrf-report.json` written once (the writer self-guards via `_written` regardless, `RunRecorder.cs:54-66` — so this is a "no double `DisposeAsync`/`run_aborted`" check, not a writer-race check); (c) the sibling's in-flight transfer is cancelled promptly, not run to completion.
