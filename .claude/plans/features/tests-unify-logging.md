# Unify the test-harness logging substrate

## The user-visible problem

`make test-web` runs almost silently on the terminal. Setup phases (preflight, image build, image transfer, game-data distribution) only surface in the WebUI; the terminal shows `[WebUI] Server started at http://...` and nothing else until tests start failing. If the browser tab is closed or the WebSocket hasn't connected yet, the operator has no live signal at all.

`make test` (CIRenderer) and `make test-llm` (LLMRenderer) print rich live output. The difference isn't accidental — it's the symptom of a deeper structural problem: **there is no single core logging substrate**. Each renderer is fed by a different mix of emit paths, and parent-side diagnostics bypass the renderer entirely.

## What's actually happening (verified by grep)

There are **three parallel emit vocabularies** for what should be one stream of diagnostic events, and only one of them reaches the WebUI:

| Vocabulary | Producer | Sink | Reaches CIRenderer? | Reaches WebUI? |
|---|---|---|---|---|
| `SetupEventBus.Emit*` (typed records over named pipe) | child + parent (via direct `renderer.OnX`) | `renderer.OnSetup*` / `OnInstance*` / `OnTest*` | ✅ via `_out`/`_err` | ✅ via WebSocket |
| `InfrastructureEventLog.Emit` (envelope JSONL) | child + parent | `{runDir}/diagnostics/infrastructure*.jsonl` | ❌ disk-only | ❌ disk-only |
| `Console.Error.WriteLine("[Module] …")` (raw stderr) | parent only — 27+ sites | terminal stderr directly | ✅ in CI (terminal user sees stderr) | ❌ WebUI never sees stderr |

**The fork is intentional and documented.** `tests/JunimoServer.Tests/Helpers/SetupEventBus.cs:18-26`:

> *"This bus does NOT persist events to disk… If a future reader needs to confirm an `EmitInstance*` fired from a completed run's artifacts, those events are not there. For events that must survive into the on-disk diagnostic log, use `InfrastructureEventLog.Emit` instead."*

So the two buses are **explicitly siblings, neither subordinate to the other**, and producers pick whichever one happens to fit their narrow purpose. There is no place where "every diagnostic" funnels through a single channel.

The renderer interface (`tests/JunimoServer.TestRunner/Rendering/ITestRenderer.cs`) already declares `OnDiagnostic(DiagnosticEvent)` and `OnError(ErrorEvent)`. Every renderer implements them:
- `CIRenderer.OnDiagnostic` writes formatted lines to `_out`/`_err` (file:line `CIRenderer.cs:503-545`)
- `LLMRenderer.OnDiagnostic` writes JSONL to stdout
- `WebRenderer.OnDiagnostic` enqueues a WS event (confirm the exact method body and line range in `WebRenderer.cs` before editing — the existing override may also do incidental parsing such as VNC-URL extraction)

**These methods are correct, complete, and almost no parent-side code calls them.** Parent-side producers write to `Console.Error.WriteLine` and `InfrastructureEventLog.Emit` instead — both of which bypass the renderer tree.

## Inventory: every bypass site

### Tier 1 — Parent process raw stderr writes that should be diagnostics or errors

These are operator-facing messages about runtime events. They reach a CI terminal user because stderr is inherited; they do **not** reach the WebUI under any mode.

**`tests/JunimoServer.TestRunner/Program.cs`** (11 sites):
- `:133` `[ArtifactWriter] abort-path write failed` — should be `OnError`
- `:302` `[ArtifactWriter] force-exit write failed` — should be `OnError`
- `:409` `[HostPool] Preflight failed: {message}` — fork pair with `InfrastructureEventLog.Emit("run_aborted", …)` at `:410` + `OnSetupPhaseCompleted(false, …)` at `:411`; the stderr line should not exist
- `:451` `[Cleanup] Stale-resource sweep failed` — fork pair with `OnSetupPhaseCompleted(false, …)` at `:452`
- `:483` `[ImageBuild] Parent-side build failed` — fork pair with `InfrastructureEventLog.Emit("run_aborted", …)` `:484` + `parentBuildProgress.PhaseCompleted(false, …)` `:485`
- `:505` `[ImageTransfer] host '{x}' failed` — per-host failure detail, should be `OnDiagnostic(Level=Error)`
- `:518` `[ImageTransfer] aborted` — fork pair with `InfrastructureEventLog.Emit("run_aborted", …)` `:519` + `OnSetupPhaseCompleted(false, …)` at `:520`
- `:543` `[GameData] host '{x}' failed` — same shape as `:505`
- `:556` `[GameData] aborted` — fork pair with `InfrastructureEventLog.Emit("run_aborted", …)` `:557` + `OnSetupPhaseCompleted(false, …)` at `:558`
- `:673` `[ArtifactWriter] finally-path write failed` — should be `OnError`
- `:694` `[Renderer] dispose failed` — must stay raw (the renderer is mid-disposal, so it can't dispatch to itself); treat as a Tier-4 lender-of-last-resort site, not a migration target

**`tests/JunimoServer.TestRunner/Distribution/ImageDistributor.cs`** (2 sites):
- `:209` `[ImageTransfer] {host}: {bytes} sent in {sec}s` — per-host progress, should be `OnDiagnostic(Level=Info, Source=Fixture)` AND/OR an `OnSetupStep(InProgress, details=…)`
- `:423` `[ImageTransfer] {host}: {detail}` — same shape

**`tests/JunimoServer.TestRunner/Distribution/GameDataDistributor.cs`** (2 sites):
- `:164` `[GameData] {host}: {bytes} sent in {sec}s` — same shape as ImageDistributor
- `:491` `[GameData] {host}: {detail}` — same shape

**`tests/JunimoServer.TestRunner/IPC/SetupPipeServer.cs`** (2 sites):
- `:108` `[SetupPipeServer] Unexpected error in read loop` — should be `OnError`
- `:122` `[SetupPipeServer] dispatch threw` — should be `OnError`

**`tests/JunimoServer.TestRunner/IPC/EventDispatcher.cs`** (1 site):
- `:106` passes `Console.Error.WriteLine` as a callback. Should pass a wrapper that funnels through `renderer.OnDiagnostic`.

### Tier 2 — WebRenderer's own `[WebUI] …` lines

**`tests/JunimoServer.TestRunner/Rendering/WebRenderer.cs`** (12 sites at `:157, :204, :235, :261, :262, :287, :292, :328, :472, :481, :515, :519`) and **`Rendering/Web/ReportGenerator.cs`** (8 sites) and **`Rendering/Web/TestRunArtifactWriter.cs`** (6 sites).

These are intra-renderer infrastructure messages (WS lifecycle, mock data writes, artifact write failures). They land on stderr only because the renderer can't dispatch to itself for these — they describe its own state.

**Decision: leave these as `Console.Error.WriteLine` for now**, but normalize their prefix to a single namespace `[Renderer.Web]` and route through a single internal `LogSelf(level, msg)` method so they're auditable. Routing them through `OnDiagnostic` would create re-entrancy: the WebRenderer's own broadcast loop announcing "client disconnected" would re-enqueue an event for that same client.

### Tier 3 — Child process stderr writes

**`tests/JunimoServer.Tests/Fixtures/TestSummaryFixture.cs`** (3 sites), **`Helpers/AsyncJsonlWriter.cs`** (1), **`Helpers/BackgroundTaskRunner.cs`** (1), **`Helpers/FlakinessTracker.cs`** (2), **`Helpers/InfrastructureEventLog.cs`** (6 internal-fault sites), **`Infrastructure/TestLog.cs`** (3 sites — `[Server]`, `[Client]`, `[Test]` formatters).

These run in the xUnit child. The CLAUDE.md prohibition is **stdout** in test assemblies; stderr is technically allowed. The premise here is that the child's stderr is **piped through xUnit and not displayed in any renderer mode** — landing in the xUnit IPC noise channel, effectively a black hole rather than "the terminal in CI mode." **Confirm this with a runtime smoke test before removing TestLog's stderr path** (add a unique marker via `TestLog`, run `make test`, and check whether the marker appears on the terminal or only in the xUnit IPC stream). If the marker does surface to the operator today, the stderr path is not a black hole and removing it is a regression.

**Decision: Tier 3 must go through `InfrastructureEventLog.Emit` (for disk) OR `SetupEventBus.EmitDiagnostic` (new, for live UI) — never raw stderr.** `TestLog.cs` is the highest-traffic case and the most misleading: it pretends to be a logger but writes to a sink no operator reads.

### Tier 4 — Already-correct paths to leave alone

- `InfrastructureEventLog`'s own internal fault `Console.Error.WriteLine` calls (when the JSONL writer itself fails). These are the lender of last resort and must stay raw — if the structured logger is broken, the error about it can't go through the structured logger.
- `OnSetupPhaseStarted/Completed/Step` calls from Program.cs to the renderer. These are correct usages of the existing typed event surface.

## The unification plan

Pick one substrate and make everyone use it. Two viable shapes:

### Option A — `renderer.OnDiagnostic` becomes the choke point (recommended)

The renderer interface is already the dispatch tree. Every emit becomes a `renderer.OnDiagnostic(new DiagnosticEvent(level, source, message))` or `OnError(...)`. Disk persistence becomes a *subscriber* to the renderer, not a sibling producer.

```
Producer → renderer.OnDiagnostic(evt)
                   ↓
            RendererBase.OnDiagnostic (new — base implementation)
                   ↓
        ┌──────────┴──────────┐
        ↓                     ↓
   Subclass renders      InfrastructureEventLog.Persist(evt)
   (CI/LLM/Web)          (called from the base, not by producers)
```

**Wins:**
- One call site per emit; no fork pairs.
- WebUI gets everything (it already wires `OnDiagnostic` to WS).
- Disk artifact stays — but is now downstream of the renderer, not parallel to it.
- `make test-web` terminal stays quiet *by design* (UI is the surface) and operators who want terminal output keep using `make test`. **No mode mirrors a sink the other mode owns.**

**Costs:**
- `InfrastructureEventLog.Initialize/ShutdownAsync/Emit/DrainAsync/ForwardRaw` public API stays, but `Emit` becomes a *fallback* used by producers that genuinely don't have a renderer handle (the IPC sink in the child before it connects; bootstrap errors in `Program.cs` before the renderer is constructed). Most call sites move to renderer.
- Child process emit path is trickier — it has no direct renderer reference. **Solution:** every `InfrastructureEventLog.Emit` in the child *also* gets sent over `SetupEventBus.EmitDiagnostic` (a new method) — both buses are kept for the child's two distinct needs (disk persistence vs. live UI). Producer-side this is still a single call: `EmitEvent(name, payload)` fans out to both.

### Option B — `InfrastructureEventLog` becomes the choke point

Every diagnostic goes to `InfrastructureEventLog.Emit`, and the renderer *subscribes* to the in-memory event stream.

**Why not B:** the event log is async-disk-first by design (`AsyncJsonlWriter` channel). Making the renderer subscribe means a second consumer of the same channel — and the channel is bounded/dropping, not multicast. We'd have to add a fan-out, at which point we're back to Option A's shape but inverted. Also: the renderer is the natural ordering boundary for live output; the disk writer is the natural durability boundary. Aligning the architecture to renderer-first matches the existing `OnDiagnostic`/`OnError` interface that's already there waiting.

**Go with Option A.**

## Concrete steps

Step count is deliberately small per step so each can be reviewed and verified independently. Each step is self-contained: the test suite still passes between steps, and the logging shape only changes incrementally.

### Step 1 — Add a base `OnDiagnostic` / `OnError` fan-out in `RendererBase`

Currently `OnDiagnostic` and `OnError` are `abstract` on `RendererBase`. Make them `virtual` with a default implementation that:
1. Persists the event to `InfrastructureEventLog` (parent-process), with a typed mapping from `DiagnosticEvent` → JSONL envelope.
2. Calls a new abstract `OnDiagnosticCore` / `OnErrorCore` that each subclass implements for the live display side.

Rename existing `OnDiagnostic` overrides in `CIRenderer`/`LLMRenderer`/`WebRenderer` to `OnDiagnosticCore`. Same for `OnError`. The dispatch contract from `RunnerCallbacks` and IPC stays unchanged.

**Edits:** `RendererBase.cs`, `CIRenderer.cs`, `LLMRenderer.cs`, `WebRenderer.cs`, `RendererDispatchGuard.cs`.

**Verification:** `dotnet build`, run `make test FILTER=PasswordTests.Login_Works` — diagnostics still print on terminal; `infrastructure.parent.jsonl` still gets entries from the renderer-side path.

### Step 2 — Migrate Tier 1 (`Program.cs` raw stderr → renderer)

Replace each of the 10 `Console.Error.WriteLine("[Module] …")` calls in `Program.cs` with `renderer.OnError(new ErrorEvent(...))` or `renderer.OnDiagnostic(new DiagnosticEvent(level: Error, source: Fixture, message: ...))`. Pick `OnError` for terminal failures (the run is aborting) and `OnDiagnostic(Error)` for per-host details inside a phase that may still proceed.

Critical: **collapse the scattered `InfrastructureEventLog.Emit("run_aborted", …)` emit *sites* into a single orchestration-layer emit** — but **keep the literal event name `run_aborted` unchanged** on both the WebSocket and disk paths. Do NOT rename the event; it has live consumers that the collapse must not break:

- `tests/test-ui/src/composables/useTestStore.ts:497` (`case 'run_aborted':`)
- `tests/test-ui/src/types/events.ts:75` (`event: 'run_aborted'`)
- `docs/developers/testing/e2e-testing.md:352`
- the event catalog at `tests/JunimoServer.Tests/Helpers/InfrastructureEventLog.cs:197`

The real setup-phase / abort-path `run_aborted` (and related `run_force_aborted`) emit sites to consolidate are:

- `Program.cs:112` — `BeginAbort` first-abort `run_aborted`
- `Program.cs:208` — `run_force_aborted` (second abort)
- `Program.cs:278` / `:279` — `ForceExitNow` `run_aborted` + `run_force_aborted`
- `Program.cs:410` — `run_aborted { cause = "host_preflight" }`
- `Program.cs:484` — `run_aborted { cause = "image_build" }`
- `Program.cs:506` — `run_aborted { cause = "image_transfer" }`
- `Program.cs:519` — `run_aborted { cause = "image_transfer_exception" }`
- `Program.cs:544` — `run_aborted { cause = "game_data_transfer" }`
- `Program.cs:557` — `run_aborted { cause = "game_data_transfer_exception" }`
- `Program.cs:640` — top-level catch `run_aborted { cause = "exception" }`

Consolidate these into one emit at the abort orchestration point (a single `EmitRunAborted(cause)` helper called from `BeginAbort`/`ForceExitNow` and the setup-phase exits), still emitting under the name `run_aborted` with the same `cause`/payload the consumers above expect. Account for all of the sites listed; do not drop any `cause`.

The renderer is constructed at `Program.cs:79-84` (wrapped at `:90`) — before all of the abort/setup-phase emit sites above — so every site can route through the renderer if desired. The `InfrastructureEventLog.Initialize` call at `Program.cs:56` and the `EmergencyCleanup.RegisterDrainable` block at `Program.cs:32-34` run before the renderer exists and must keep using `InfrastructureEventLog` directly for any early error.

**Edits:** `Program.cs` (10 stderr sites — see Step 3 — plus the `run_aborted`/`run_force_aborted` emit consolidation).

**Verification:** `make test-web`, kill SSH to a remote host before preflight finishes, confirm the failure surfaces in the WebUI as an error toast AND in the terminal of `make test`. Confirm `infrastructure.parent.jsonl` still contains the abort entry.

### Step 3 — Migrate Tier 1 (Distributors + IPC stderr → renderer)

`ImageDistributor` and `GameDataDistributor` already accept an `ITestRenderer` in their `DistributeAsync` signature (verified at `Program.cs:500` — `distributor.DistributeAsync(hostPool.Hosts, renderer)` — and `:538` — `gameDataDistributor.DistributeAsync(hostPool.Hosts, renderer)`). Wire their progress lines through `renderer.OnDiagnostic(Level=Info, Source=Fixture)` instead of `Console.Error.WriteLine`. They should *also* call `renderer.OnSetupStep(InProgress, details=...)` for the spinner-friendly summary line (CIRenderer already redraws spinners around `OnDiagnostic` writes — verified at `CIRenderer.cs:524-541`).

`SetupPipeServer` and `EventDispatcher` get an `ITestRenderer` constructor parameter (the former already has one — `SetupPipeServer.cs` accepts `renderer`, constructed at `Program.cs:361` as `new SetupPipeServer(renderer)`). Replace their 3 `Console.Error.WriteLine` sites with `renderer.OnError`.

**Edits:** `ImageDistributor.cs`, `GameDataDistributor.cs`, `SetupPipeServer.cs`, `EventDispatcher.cs`.

**Verification:** `make test-web` — image transfer progress lines appear as toasts/log entries in the UI in real time, not just as setup-step status.

### Step 4 — Migrate Tier 3 (child process)

**This step depends on Step 5** — it consumes `SetupEventBus.EmitDiagnostic`, which Step 5 adds. Do Step 5 first (or fold the two together); the "suite passes between steps" guarantee does not hold for this pair in the other order, because Step 4's rewrite would reference a method that does not yet exist.

`TestLog.cs` `[Server]`/`[Client]`/`[Test]` formatters — three call sites whose stderr destination Step 3's inventory flags for a smoke-test confirmation — become thin wrappers around `SetupEventBus.EmitDiagnostic(source, message)` (the method Step 5 adds). The `DateTime.UtcNow` prefix is dropped; the IPC envelope already carries a timestamp. Only remove the existing stderr write once the Tier-3 smoke test confirms it is not visible to the operator today.

`TestSummaryFixture`'s 3 `Console.Error.WriteLine` sites become `InfrastructureEventLog.Emit("fixture_dispose_failure", { … })` calls. These are dispose-time failures; they need disk persistence and they emit during teardown when the renderer pipe may already be closed.

`AsyncJsonlWriter`, `BackgroundTaskRunner`, `FlakinessTracker` raw-stderr sites — case-by-case. Each gets either: (a) a typed `InfrastructureEventLog.Emit` if disk persistence is the actual need, or (b) deletion if the message was never useful (some of these are "I caught an exception in a swallowed catch" diagnostics that should fail-fast or log structured).

`InfrastructureEventLog`'s own internal-fault sites stay raw — they ARE the lender of last resort.

**Edits:** `TestLog.cs`, `TestSummaryFixture.cs`, `AsyncJsonlWriter.cs`, `BackgroundTaskRunner.cs`, `FlakinessTracker.cs`.

**Verification:** Grep `tests/JunimoServer.Tests/` for `Console.Error.WriteLine` — only `InfrastructureEventLog.cs`'s 6 internal-fault sites should remain.

### Step 5 — Add the `SetupEventBus.EmitDiagnostic` producer facade

The child→renderer IPC dispatch route **already exists**: `EventDispatcher.cs:74-75` already maps `EventNames.Diagnostic` → `renderer.OnDiagnostic` and `EventNames.Error` → `renderer.OnError`. The only missing piece is the *producer-side* facade.

Add `SetupEventBus.EmitDiagnostic(LogSource source, LogLevel level, string message)` that builds a `DiagnosticEvent` and ships it over the named pipe under `EventNames.Diagnostic` — mirroring the existing `EmitTestAnnotation` facade at `SetupEventBus.cs:85-87`. No parent-side dispatch or `EventDispatcher`/`SetupPipeServer` change is needed; the route is already wired.

Update the `SetupEventBus.cs` header docstring: it is no longer "the bus that doesn't persist to disk" — it now carries diagnostics that the parent renderer fans out to both display and `InfrastructureEventLog` (via Step 1's base fan-out).

**Edits:** `SetupEventBus.cs` (add the `EmitDiagnostic` facade + update the header docstring).

**Verification:** Add a temporary `SetupEventBus.EmitDiagnostic(LogSource.Test, LogLevel.Info, "hello from child")` in a fixture, run `make test-web`, see it in the UI. Run `make test`, see it on terminal. Remove the temporary.

### Step 6 — Confirm a single parent-side persistence point (no double-persist)

After steps 1-5, both buses still exist:
- `SetupEventBus` — typed records over IPC, drives live display
- `InfrastructureEventLog` — JSONL envelopes to disk

A child diagnostic emitted via `SetupEventBus.EmitDiagnostic` (Step 5) arrives at the parent through `EventDispatcher.cs:74` → `renderer.OnDiagnostic` → the base `RendererBase.OnDiagnostic` (Step 1), which performs the **single** `InfrastructureEventLog.Persist`. That base call is the one and only parent-side persistence point.

**Do NOT add a second persist on IPC arrival.** Making `SetupEventBus.EmitDiagnostic` (or the `EventDispatcher` handler) *also* call `InfrastructureEventLog.Emit`/`Persist` for the child diagnostic would double-write every such event — once at the IPC-arrival site and once at the base fan-out. The base fan-out from Step 1 is the unifying property: every diagnostic, parent- or child-originated, reaches both the disk log and every renderer because it all funnels through `RendererBase.OnDiagnostic`.

Confirm the persist fires **exactly once**: `RendererDispatchGuard` (the wrapper constructed at `Program.cs:90`) must delegate `OnDiagnostic`/`OnError` to the inner renderer's base method — it must not override them in a way that bypasses or re-invokes the base `Persist`.

**Which jsonl file each producer writes to** (per `one-writer-per-artifact.md`): the parent's `InfrastructureEventLog` instance is initialized with `RunArtifactNames.ParentInfrastructureJsonl` (`Program.cs:56`) and writes `infrastructure.parent.jsonl`. The child has its own `InfrastructureEventLog` instance writing the canonical `infrastructure.jsonl`. Consequence of Step 4/5: a child producer that moves from a direct `InfrastructureEventLog.Emit` (child-local → `infrastructure.jsonl`) to `EmitDiagnostic` (IPC → parent → base `Persist` → `infrastructure.parent.jsonl`) has its event **relocated to a different file**. This is a deliberate move, not a duplication; state it explicitly for each producer so the next reader knows where to look. Producers that genuinely need the event in the canonical child-side `infrastructure.jsonl` should keep their direct `Emit` call instead of switching to `EmitDiagnostic`.

**Edits:** none required beyond Steps 1 and 5; this step is a verification that no second persist was introduced and that `RendererDispatchGuard` delegates to the base.

**Verification:** Diff `infrastructure.parent.jsonl` and `infrastructure.jsonl` before and after a run. The combined set across both files should be the union of parent-side and child-side diagnostics with **no duplicate** of any single event, plus the bus-specific operational events (`run_aborted`, `setup_ipc_read_deadline`, etc.) on the parent side and per-process internal events on the child side. A diagnostic appearing in BOTH files is the double-persist regression this step guards against.

### Step 7 — Tier 2 cleanup pass

WebRenderer's 12 `Console.Error.WriteLine("[WebUI] …")` sites, ReportGenerator's 8, TestRunArtifactWriter's 6. These all describe the renderer's own internal state. Two options:
- Wrap them in a single `private void LogSelf(LogLevel, string)` per file that writes to `Console.Error` with a consistent prefix and respects `NO_COLOR`.
- Move them through `OnDiagnostic` — but only after carefully checking re-entrancy. `OnDiagnostic` in `WebRenderer` enqueues a WS event; "client disconnected" announced to that client is harmless because it's broadcast to *all* clients, not the dead one. But "send failed for client X" emitted during a send loop iteration could amplify the loop. Audit each site for this.

Recommend: wrap in `LogSelf`, do not migrate to `OnDiagnostic`. These messages are about renderer plumbing and would be noise in the WebUI; CI users already see them on stderr; LLM users don't need them. The distinction is intentional and small.

**Edits:** `WebRenderer.cs`, `ReportGenerator.cs`, `TestRunArtifactWriter.cs`.

### Step 8 — Documentation

After 1-7, update:
- `tests/JunimoServer.TestRunner/Rendering/ITestRenderer.cs` interface docstring with the fan-out contract (`OnDiagnostic` → display + disk).
- `tests/JunimoServer.Tests/Helpers/InfrastructureEventLog.cs` header — its role is now "the on-disk subscriber to renderer.OnDiagnostic, plus the canonical home for structured envelope events that don't fit the display model (e.g. `http_request`, `poll_completed`, `wait_matched`)".
- `tests/JunimoServer.Tests/Helpers/SetupEventBus.cs` header — drop the "does NOT persist to disk" warning (no longer true after Step 5). (Already specified in Step 5; listed here only as part of the final doc pass.)
- `docs/developers/testing/test-failure-runbook.md` — if it references where to find logs, update to reflect the single substrate.

## Out of scope, named so they don't drift in

- **xUnit child stdout prohibition** — still applies, untouched. The child writes nothing to stdout; all diagnostics flow over IPC.
- **Container log streaming** (`SimpleContainerLogStreamer` → `container.log` files). The mod's `IMonitor` log is its own thing; the harness ingests `SDVD_EVENT` prefixes via `SimpleContainerLogStreamer.ForwardRaw` → `InfrastructureEventLog.ForwardRaw`. That path stays. **This plan's single choke point is for runner-host + child-test-process diagnostics only — it must NOT route or re-wrap the in-container `SDVD_EVENT` lines (the boot-phase plan's `mod_phase` events) through `renderer.OnDiagnostic`.** Those lines are already a live forwarded stream and already persisted by `ForwardRaw`; funneling them through the renderer's base fan-out would double-persist them to disk. Leave the `ForwardRaw` path untouched (`prefer-live-stream-over-disk-artifact.md`).
- **`summary.json` / `ctrf-report.json` writing path.** These are *artifacts*, not logs. They stay with `TestRunArtifactWriter` per `runner-side-artifact-writer.md`.
- **WebUI's display-side rendering of diagnostic events.** The infrastructure timeline already consumes `OnDiagnostic` events per `prefer-live-stream-over-disk-artifact.md`. After this work, more events reach it; the UI may want to add filters but that's a UI-side follow-up.
- **Mod-side `IMonitor` logging convention** (`mod/JunimoServer/`). Untouched — the mod logs to SMAPI; the harness picks up game-side container logs separately.

## Compatibility verification (per `plan-discipline.md`)

- **Modes:** All three (`make test` / `make test-llm` / `make test-web`) keep their existing terminal contracts. CIRenderer keeps printing diagnostics inline with spinners; LLMRenderer keeps printing JSONL; WebRenderer keeps the terminal mostly quiet — but the UI gains the parent-side stream that today it can't see.
- **LAN vs Steam transports:** No change. The logging substrate is harness-side; transport behavior is unaffected.
- **TPS:** Diagnostic events are not on the hot game-loop path. `OnDiagnostic` calls are off the game thread (parent process or child fixture context). No TPS impact.
- **Disk artifact compatibility:** `infrastructure.parent.jsonl` and `infrastructure.jsonl` schemas are preserved. The set of events grows (more producers feeding in); no field renames, no event-name renames (the `run_aborted` name in particular is kept — see Step 2), no removals. The only movement is the deliberate cross-file relocation of child diagnostics that switch from direct `Emit` to `EmitDiagnostic` (Step 6). Existing post-run tools (`jq` queries, the failure runbook) and the live UI's `run_aborted` handler keep working. New events get the same envelope shape.
- **Re-entrancy:** Step 7 explicitly avoids piping renderer-internal log lines through `OnDiagnostic`. The base fan-out in Step 1 dispatches to `*Core` and `InfrastructureEventLog.Persist` — neither calls back into `OnDiagnostic`, so no recursion.
- **Ordering:** `RendererBase.OnDiagnostic` calls `Persist` first then `*Core` (or vice versa — pick one and document). On abort, `InfrastructureEventLog` is drained via the `EmergencyCleanup.RegisterDrainable` registration at `Program.cs:32-34`, and the parent log is also drained in `ForceExitNow` (`Program.cs:294`) and closed in the outer finally (`Program.cs:700`, `InfrastructureEventLog.ShutdownAsync`). Display-side has no drain — but live-display ordering during abort isn't load-bearing.
- **`drain-before-consume-disposal.md`:** The IPC pipe drain already happens in the outer finally (`Program.cs:657`, `await setupPipe.DrainAsync(...)`) before renderer dispose. New `EmitDiagnostic` events flow over the same pipe; the existing drain covers them. No new drain needed.

## Post-conditions (these are gates per `runtime-post-conditions-are-gates.md`)

After this work lands, run `make test-web` and visually confirm:

1. The terminal of `make test-web` continues to show only `[Renderer.Web] …` lines plus exit prompt. Setup phase failures still produce a terminal line via `OnError` (which CIRenderer is for, but WebRenderer's `OnErrorCore` should still print a single-line summary to stderr so terminal-only operators see *something* on hard failure).
2. The WebUI's infrastructure timeline shows preflight, image build, image transfer, and game-data distribution progress lines that today are absent.
3. `infrastructure.parent.jsonl` after a successful run contains every diagnostic event that was visible in the WebUI (modulo the renderer-internal `[Renderer.Web]` lines which stay on stderr).
4. `make test FILTER=...` against the same scenario produces the same diagnostic detail as today — no regression in CI mode.
5. Grep `tests/JunimoServer.TestRunner/ tests/JunimoServer.Tests/` for `Console.Error.WriteLine` and verify the remaining hits are exactly: (a) `InfrastructureEventLog.cs` internal-fault sites, (b) WebRenderer/ReportGenerator/TestRunArtifactWriter `[Renderer.Web]` lines, (c) the `Program.cs:694` `[Renderer] dispose failed` line (renderer is mid-disposal, can't dispatch to itself), and (d) any pre-renderer bootstrap site in `Program.cs` (the `Initialize`/`RegisterDrainable` block at `:32-56` runs before the renderer exists). Every other site is gone.
6. The literal event name `run_aborted` is unchanged on both the WebSocket and disk paths; its consumers (`useTestStore.ts:497`, `events.ts:75`, `e2e-testing.md:352`, `InfrastructureEventLog.cs:197`) still resolve, and `run_aborted` appears once per aborted run.
7. No diagnostic appears in BOTH `infrastructure.parent.jsonl` and `infrastructure.jsonl` (the single-persist guarantee of Step 6).
8. The in-container `SDVD_EVENT` / `mod_phase` lines forwarded by `SimpleContainerLogStreamer.ForwardRaw` are NOT re-persisted by the choke point.

## Why this fits the user's framing

> "logging should be mostly the same for all modes, and web-ui is sitting on top of that reusable/core-ingrained logging"

The renderer interface IS that core substrate. Every diagnostic enters at `renderer.OnDiagnostic` and fans out to (live display per-mode) + (disk persistence). The WebUI's WebSocket dispatch is one of the three subscribers — not a parallel stack, not a special sink. CIRenderer and LLMRenderer are the other two subscribers; they consume the same events with mode-appropriate formatting. The "core" is the dispatch tree; modes are views.
