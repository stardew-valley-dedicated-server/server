# Test setup review — findings & remediation plan

## Context

The `tests/` tree was reviewed for soundness, best-practice, maintainability, and usability. The request framed it as "a first implementation of tests" — that framing is wrong and is corrected below, because it changes what work is appropriate.

**What `tests/` actually is** (verified via `git log`, file counts, LOC):

- **~47,000 LOC across 4 sub-projects** supporting **85 test methods in 21 classes**.
- Developed continuously **2026-01-26 → 2026-05-30** (~4 months), with explicit "overhaul"/"rebuild" commits (`6f0128d test infrastructure overhaul`, `103251a rebuild E2E test harness`).
- **26 documented invariant-rules** in `.claude/rules/`, each anchored to a real bug that was hit and engineered around.

| Sub-project               | Purpose                                        | LOC     | Files           |
| ------------------------- | ---------------------------------------------- | ------- | --------------- |
| `JunimoServer.Tests`      | E2E tests + resource broker + helpers          | ~32,200 | 135 `.cs`       |
| `JunimoServer.TestRunner` | Custom xUnit v3 runner host (CI/LLM/Web modes) | ~7,900  | 33 `.cs`        |
| `test-client`             | SMAPI mod for in-game automation (HTTP API)    | ~6,500  | 29 `.cs`        |
| `test-ui`                 | Vue 3 + TS live monitoring dashboard           | ~11,800 | 56 `.ts`/`.vue` |

**Verdict up front:** the harness is **sound and well-engineered — no architectural rewrite is warranted.** The complexity is mostly justified by a genuinely hard problem (orchestrating real game containers across multiple Docker hosts over SSH). What exists is ordinary large-codebase maintainability debt: a few god-files, one self-admittedly "aspirational" abstraction boundary, and thin unit-test coverage on the UI. This document records the findings (including subagent claims I overruled after verification) and then lays out a prioritized, low-risk remediation backlog.

**Intended outcome:** reduce bus-factor risk and onboarding cost without changing any test behavior or harness semantics.

---

## Part 1 — Findings assessment

### 1.1 Done well (keep as-is — do not "simplify")

| Strength                                                                                                                         | Evidence                                                                                                                                               |
| -------------------------------------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------ |
| Test bodies read as scenario narratives; ~1 line boilerplate per test; resource needs declared via `[TestServer(...)]` attribute | `tests/JunimoServer.Tests/*Tests.cs`; `Infrastructure/TestServerAttribute.cs`                                                                          |
| Timeouts centralized — no magic-number `Task.Delay`/`Thread.Sleep` in test bodies                                                | `Helpers/TestTimings.cs`                                                                                                                               |
| The one teardown delay is FPS-derived and load-bearing for video frame capture (not a flake mask)                                | `Infrastructure/Fixture/TestLifecycle.cs:174`; documented in `.claude/rules/test-timing.md`                                                            |
| Polling attributes "when did condition change" to producer clock, not observer                                                   | `Helpers/PollingHelper.cs`, `Helpers/WaitTrace.cs`                                                                                                     |
| `ExecutionContext.SuppressFlow()` applied consistently to long-lived background tasks                                            | `Infrastructure/TestResourceBroker.cs`, `Containers/ServerContainer.cs`, `Infrastructure/ManagedServer.cs`; per `.claude/rules/asynclocal-pitfalls.md` |
| Three-strike renderer fault isolation (UI bug can't abort a run)                                                                 | `TestRunner/Rendering/RendererDispatchGuard.cs`                                                                                                        |
| Single-writer-per-artifact discipline enforced                                                                                   | per `.claude/rules/one-writer-per-artifact.md`, `runner-side-artifact-writer.md`                                                                       |
| `ExecuteOnGameThread` cross-thread handshake (atomic 3-state, tick-based timeout, AsyncLocal rebind)                             | `test-client/ModEntry.cs:892-964`                                                                                                                      |
| Assertion quality: specific messages, diagnostics logged before asserts so failures are self-contained                           | e.g. `CabinConcurrencyTests.cs:68-72`                                                                                                                  |

### 1.2 Verified maintainability debt (actionable — see Part 2)

| ID      | Finding                                                                                                                                                                                                                                                                                                                                                                    | Evidence                                                                                                                                      | Severity                    |
| ------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | --------------------------------------------------------------------------------------------------------------------------------------------- | --------------------------- |
| **F1**  | **Broker god-file.** `TestResourceBroker.cs` is 1,899 LOC; `AcquireSharedCoreAsync` interleaves reservation + on-demand creation + capacity release/reacquire + eviction polling + exclusive-gate coordination + post-AddRef re-check in ~200 lines. Correct, but high-risk to modify.                                                                                     | `Infrastructure/TestResourceBroker.cs:744-943`                                                                                                | Maintainability             |
| **F2**  | **TestBase callback surface.** ~30 `*Internal` members exist only so `Fixture/` helpers can call _back_ into `TestBase`. Code itself states the "helpers don't call back" rule "is aspirational."                                                                                                                                                                          | `Infrastructure/TestBase.cs:358-505` (esp. comment at `:366-369`)                                                                             | Maintainability             |
| **F3**  | **TestRunState god-file.** 1,879 LOC mixing per-event `Apply*` state mutation with `GetArtifactView` projection. A new field must be added in 3 places (Apply / projection / writer) with no compile-time link.                                                                                                                                                            | `TestRunner/Rendering/Web/TestRunState.cs`                                                                                                    | Maintainability             |
| **F4**  | **Under-documented broker invariants in-code.** Eviction policy and exclusive-gate coordination are correct but rely on the reader already knowing the rules; no broker-local architecture note. Eviction (`TryEvictIdleServerForAsync`) is referenced but its invariants ("never evicts RefCount>0", "freed slot immediately reacquired") aren't stated at the call site. | `Infrastructure/TestResourceBroker.cs`, `ManagedServer.cs:314-474`                                                                            | Maintainability             |
| **F5**  | **test-ui god-composable.** `useTestStore.ts` is 1,338 LOC routing 20+ event types through one switch (`:346-997`); handlers not independently testable.                                                                                                                                                                                                                   | `test-ui/src/composables/useTestStore.ts`                                                                                                     | Maintainability             |
| **F6**  | **Zero test-ui unit tests** over ~11,800 LOC of TS. `vue-tsc` (via `make build-test-ui`) is the only safety net.                                                                                                                                                                                                                                                           | `find tests/test-ui/src -name '*.spec.ts' -o -name '*.test.ts'` → 0                                                                           | Coverage                    |
| **F7**  | **test-client reflection drift risk.** ~58 reflection sites across 9 files reflect into game internals; on a game-version field/method rename they fail **silently** (caught + default), masking incompatibility. No startup probe, no version check.                                                                                                                      | `test-client/Util/ReflectionHelper.cs`, `GameControl/MenuDetector.cs:101-234`, `GameTweaks/GodTool.cs:48-77`, `GameControl/CoopController.cs` | Maintainability / fragility |
| **F8**  | **Menu navigation timing fragility.** Menus are set then state-checked without confirming the transition committed; `FarmhandMenu` readiness is 5 inline flags with implicit sequencing; no retry on menu construction.                                                                                                                                                    | `test-client/GameControl/MenuNavigator.cs:41-66`, `ModEntry.cs:552-626`                                                                       | Fragility                   |
| **F9**  | **`any` count higher than ideal.** 61 `any`/`as any` across 9 test-ui files (mostly Chart.js/zoom/iframe wrapping, which is pragmatic; not all).                                                                                                                                                                                                                           | `grep -rn ':\s*any\b\|as any\b' tests/test-ui/src` → 61                                                                                       | Minor                       |
| **F10** | **No in-code rationale for the custom runner.** The decision to forbid `dotnet test` (it can't host the multi-host orchestration) is documented in `docs/` but not discoverable from the runner code, inviting a future "why not just `dotnet test`?" regression attempt.                                                                                                  | `TestRunner/Program.cs` (no header note)                                                                                                      | Minor                       |

### 1.3 Subagent claims overruled after verification

Recorded so the reasoning isn't re-litigated. Per `.claude/rules/adversarial-review-split-findings.md` (don't collapse valid sub-findings with weak framing) and `verify-claims.md`.

| Claim (from review agents)                                                          | Ruling                        | Why                                                                                                                                                                                                                                                                                                                                                                                                                                                                                       |
| ----------------------------------------------------------------------------------- | ----------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| "Custom runner is over-engineered; add a `dotnet test` fallback."                   | **Rejected (false premise).** | Runner is an `Exe` host using `xunit.v3.runner.utility` _programmatically_ because it orchestrates multi-host SSH preflight + image distribution + game-data distribution _around_ execution (`Program.cs:399, 499, 538`). `dotnet test` is a discovery/run host and structurally cannot do this. **Kept** the valid sub-findings: event-plumbing per-field tax is real (F3 + `.claude/rules/runner-ui-pipeline-plumbing.md`); renderers share some duplicated stack-trace/counter logic. |
| "Distribution code (~1,000 LOC) is probably dead weight for a single-host project." | **Rejected.**                 | Multi-host is a real, documented capacity-scaling workflow (local + VPS + Mac, GPU opt-in) in `.env.test.example`, with measured cold-start floors in `docs/developers/testing/e2e-testing.md`. Keep it.                                                                                                                                                                                                                                                                                  |
| "40 `any` in test-ui, all pragmatic."                                               | **Corrected.**                | Actually **61** across 9 files (F9). Still mostly chart-library pragmatism; the number was wrong.                                                                                                                                                                                                                                                                                                                                                                                         |
| Broker "reservation atomicity gap" / "steam-ticket asymmetry" are bugs.             | **Downgraded.**               | Agent itself conceded "not a current bug" and self-healing (retry loop / ticket re-emit). Worth a defensive comment (F4), not a fix.                                                                                                                                                                                                                                                                                                                                                      |
| KeepConnected + within-class parallelization is fragile.                            | **Downgraded to non-issue.**  | Serialization is enforced by the turn-lock in `PersistentSessionCoordinator`; works in practice. No change.                                                                                                                                                                                                                                                                                                                                                                               |

---

## Part 2 — Remediation backlog (prioritized)

Ordering = value/risk. Every item is **behavior-preserving** unless noted. Do them as independent PRs.

### Priority 1 — Documentation & comments (no code-behavior change, highest value/effort)

**P1-a. Broker `ARCHITECTURE.md` (addresses F4, F1, F3).**

- New file: `tests/JunimoServer.Tests/Infrastructure/ARCHITECTURE.md`.
- Content: the broker/pool/capacity object graph; the three eviction invariants ("never evicts RefCount>0", "freed slot immediately reacquired for the new server", "racy WaitingCount read is intentional"); the exclusive-gate coordination (TCS + semaphore + class-waiter count); poison-recovery callback flow.
- Source the invariants from `.claude/rules/test-broker-invariants.md` (do not duplicate verbatim — link to it; per `.claude/rules/event-catalog-no-inline-enums.md` philosophy of not drifting copies).
- Add a one-line pointer comment at the top of `AcquireSharedCoreAsync` (`TestResourceBroker.cs:744`) and `AddRefAndAcquireExclusiveAsync` (`ManagedServer.cs:314`) → `see ARCHITECTURE.md`.

**P1-b. Defensive concurrency comments (addresses F4 downgraded items).**

- At the reservation→AddRef window (`ServerPool.cs` `TryReserveBest` / `TestResourceBroker.cs:~833` `Contains`+poison check): state "reservation is a best-effort load-balancing hint, not a commitment; server may be evicted between reserve and AddRef → retry path at the `continue`."
- At `ClientPool` steam-ticket return paths (`DiscardClient`/`ReturnClient`): comment "the `_steamAvailable` ticket count must stay in lockstep with steam-bearing clients in `_available`; release the lock only after both the bag mutation and the semaphore Release."
- No logic change.

**P1-c. Runner rationale header (addresses F10).**

- Add a comment block at the top of `TestRunner/Program.cs` and a `tests/JunimoServer.TestRunner/README.md`: why this exists instead of `dotnet test` (programmatic xUnit host wrapping multi-host preflight + image/game-data distribution + live IPC rendering), and the three output modes. Link to `docs/developers/testing/test-harness.md`.

### Priority 2 — Behavior-preserving refactors (mechanical, reviewable)

**P2-a. Extract broker creation/eviction orchestration (addresses F1).**

- New class `Infrastructure/ServerCreationOrchestrator.cs` (or similar) owning: `ServerQueue` resolution, on-demand creation dedup, capacity release/reacquire during creation waits, eviction polling.
- `TestResourceBroker` delegates to it; `AcquireSharedCoreAsync` shrinks to the acquire/AddRef/re-check spine.
- Pure extraction — move methods, no semantic change. Guard with full E2E run (Part 3).
- **Risk note:** this is the highest-risk refactor in the plan because it touches the most invariant-dense code. Treat `.claude/rules/test-broker-invariants.md` as a pre-flight checklist; do not "simplify" any DO-NOT rule while moving code.

**P2-b. Extract `useTestStore` event handlers (addresses F5, enables F6).**

- New file `test-ui/src/composables/useTestStore-handlers.ts` exporting one handler fn per event type.
- Replace the `switch` (`useTestStore.ts:346-997`) with a `Record<string, (event, ctx) => void>` dispatch map; unknown event → `log.warn`.
- Handlers receive an explicit context object (the reactive state + sub-composable setters) so they're unit-testable without a mounted component.
- Verify with `make build-test-ui` (runs `vue-tsc`).

**P2-c. (Optional, lower value) split `TestRunState` projection from mutation (addresses F3).**

- If P2-a/P2-b land cleanly and there's appetite: separate the `Apply*` mutation methods from `GetArtifactView` projection into two partial files or two classes, so the "add a field in 3 places" path is at least co-located per concern. Defer if time-boxed.

### Priority 3 — Test-ui unit tests (addresses F6; depends on P2-b)

**P3-a. Introduce vitest.**

- Add `vitest` to `tests/test-ui/package.json` devDeps; minimal `vitest.config.ts` reusing the vite config; add `make test-ui-unit` target (mirror the `bun`-based `build-test-ui` target).
- Keep separate from the C# E2E suite — this is UI-unit only.

**P3-b. Unit-test the extracted handlers + core utils.**

- Cover the `useTestStore-handlers.ts` handlers (feed a synthetic event, assert state mutation), and pure utils (`src/utils/` — format/status/chart helpers, whichever are pure).
- Goal is a smoke-level safety net, not 100% coverage.

### Priority 4 — test-client hardening (addresses F7, F8)

**P4-a. Reflection startup probe + version note (addresses F7).**

- Centralize the fragile reflection-path resolutions behind `Util/ReflectionHelper.cs` (or a new `Diagnostics/ReflectionProbe.cs`) and run a probe in `ModEntry.OnGameLaunched` that resolves each load-bearing field/method (`CoopMenu.currentTab`, `startSleep`, FarmhandMenu internals, GodTool patch targets).
- On any unresolved path: `Monitor.Log(..., LogLevel.Warn)` naming the path and the expected game version, and disable the dependent feature rather than failing silently mid-test.
- **Note:** `LogLevel.Error` in mod code is test poison (`\b(ERROR|FATAL)\b` triggers cancellation per `.claude/rules/debugging.md`) — use `Warn`, not `Error`.
- Decompiled reference for current field/method names: `decompiled/sdv-1.6.15-24356/`.

**P4-b. Menu-transition stability helper (addresses F8).**

- Add a helper (in `GameControl/MenuNavigator.cs`) that, after setting `Game1.activeClickableMenu`, confirms the transition committed on the game thread (bounded retry) before returning success — instead of set-and-assume.
- Extract the `FarmhandMenu` readiness check (`ModEntry.cs:552-626`) into a named helper with explicit states (loading / ready / failed) rather than 5 inline flags.
- **Caution:** this is a retry/stability layer in screen-scraping code; per `.claude/rules/retry-is-evidence-of-root-cause.md`, confirm each added retry papers over genuine UI-commit latency (provably outside our control) and not a missing await. Document the justification inline.

### Priority 5 — Minor (opportunistic)

**P5-a. `any` reduction (addresses F9).** Where a real type exists (not Chart.js plugin internals), replace `any`. Leave documented library-wrapping `any` with a one-line `// Chart.js plugin API is untyped` comment. Low priority.

---

## Part 3 — Verification

Each PR must pass these before merge. The harness has **no unit-test layer for the C# side** (`CLAUDE.md`: "E2E only") — correctness of infra refactors is verified by a real run + JSONL inspection, per `.claude/rules/runtime-post-conditions-are-gates.md`.

**Build gates (every PR):**

- C# changes: `dotnet build mod/JunimoServer/JunimoServer.csproj` and build the test projects.
- test-ui changes: `make build-test-ui` (runs `vue-tsc` — catches what plain vite build misses, per `.claude/rules/test-ui-build.md`).

**Behavior gates by area:**

- **P2-a (broker refactor):** run the full suite `make test`; confirm pass/fail counts and `queueDurationTotalMs` match a pre-refactor baseline run (no new deadlock/starvation). Inspect `TestResults/runs/{latest}/diagnostics/infrastructure.jsonl` for unexpected `server_poisoned` / `host_disconnected` / capacity-starvation events. Spot-check a KeepConnected class and an `[TestServer(Exclusive=true)]` class still serialize correctly.
- **P2-b / P3 (test-ui):** `make build-test-ui` + `make test-ui-unit`; load `make test-web` against a recorded run and confirm the live event stream still populates the test tree, instance stats history, and screenshots/recordings (no regression vs the WebSocket-first behavior in `.claude/rules/prefer-live-stream-over-disk-artifact.md`).
- **P4 (test-client):** rebuild via `make build-test-client`, then **verify the edit landed in the produced image** (`docker create` + `docker cp`) per `.claude/rules/verify-edit-landed-in-artifact.md` — the test-client Dockerfile does NOT `COPY docker/rootfs/`. Run `make test FILTER=<a menu-navigation-heavy class>` and confirm the probe logs at `Warn` (not `Error`) and menus still navigate. Confirm no `LogLevel.Error` was introduced.

**Documentation gates (P1):**

- Confirm every invariant cited in `ARCHITECTURE.md` / comments still matches the code it describes (open the file:line, don't trust the prose) — per `.claude/rules/runtime-post-conditions-are-gates.md` applied to docs.

---

## Out of scope (explicit non-goals)

- Replacing the custom runner with `dotnet test` — structurally impossible (Part 1.3).
- Removing multi-host / distribution code — it backs a real workflow (Part 1.3).
- Any change to the documented broker invariants in `.claude/rules/test-broker-invariants.md` — those are load-bearing.
- Adding a C# unit-test layer — the project is deliberately E2E-only (`CLAUDE.md`).
- Re-enabling Ryuk — structurally incompatible with `ssh://` + multi-host (`ModuleInit.cs`).
