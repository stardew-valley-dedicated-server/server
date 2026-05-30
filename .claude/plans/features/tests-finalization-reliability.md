# Make E2E run finalization react fast & reliably — without sacrificing failure debug-data capture

## Context

A full E2E run wedged for ~8 minutes in the "running" state (console + Web UI never finalized, no `summary.json` written) after a single test failed. The failure itself was an unrelated flaky timeout (`PasswordProtectionTests.LobbyPlayer_SurvivesDayTransition_CanAuthenticateAfter`, a day-transition test that overran under suite-wide slot starvation); 90 passed, 1 failed, 9 were cancelled mid-flight. The recording `TaskCanceledException` warnings were benign teardown noise, not the cause.

The real defect is **slow, unbounded run finalization after an abort**. `runner.Run()` (`tests/JunimoServer.TestRunner/Program.cs:634`) blocks until xUnit raises `OnExecutionComplete`, which waits for every in-flight test's `DisposeAsync`; `summary.json` is written in the `finally` after it. So slow teardown wedges the whole run, and there is no run-level watchdog (the `dotnet test` session timeout that "used to work" was dropped when the custom runner replaced `dotnet test`).

**Design priority (user):** on failure, capturing debug data (the failing test's screenshots + finalized video) takes precedence over bailing fast — so do **not** route `StopOnFail` through the hard-kill `ForceExitNow` path (that's correct for a manual UI-Stop, which explicitly accepts losing in-flight recordings, but wrong for an automatic failure abort we want to debug). Make finalize **efficient and bounded**, never unbounded. `/ping` is out of scope (no orchestrator consumer; only a Docker `HEALTHCHECK` + manual debug endpoint, so a 200 from a frozen-game container is correct).

## Verified mechanism (adversarial pass — corrects earlier assumptions)

Reading the actual code changed the diagnosis. The wedge is **two sequential phases**, and the existing 300s cap is **not the effective bound it appears to be**:

1. **Phase 1 — inside `runner.Run()`**: only the _failing_ test blocks here. `TestLifecycle.FinalizeAsync:159-188` runs the artifact phase (screenshot `CollectAsync` **then** `FinalizeRecordingAsync`) when `CollectArtifactsInternal || isTestFailing`. For passing/cancelled "all"-mode in-flight tests, `FinalizeRecordingAsync` takes the **deferred** path (`TestArtifactCollector.cs:227 canDefer`, enqueues + `return`s immediately) — so they do **not** block `runner.Run()`. The failing test takes the **sync** path (`:273`) and awaits extraction. Its observed `artifactsMs` was **446s — larger than the 300s cap**, proving the cap did not bound the work (and that `artifactsMs` includes the screenshot `CollectAsync` at `:164`, not only recording).

2. **Phase 2 — broker `DisposeAsync` in the `finally`**: `TestResourceBroker.cs:1720` `await Task.WhenAll(_backgroundDisposeTasks)` with **no timeout**. This queue holds the deferred per-test extractions for all "all"-mode passing tests **and** background _server disposals_ from `TryEvictIdleServerForAsync` (`:42-44`). Each deferred extraction is capped at 300s and runs via `Task.Run`, but they **serialize** through `DockerHost.ExtractLimiter` (`DockerHost.cs:223`, size = `concurrentExtractions`), so wall-time stacks.

3. **Why 300s doesn't cap**: clip extraction is `_container.ExecAsync(cmd, ct)` (`ContainerRecorder.cs:1162`), gated by `await _extractLimiter.WaitAsync(ct)` (`:1159`). On cancellation, `ExecAsync` stops _awaiting_ but does **not** kill the in-container ffmpeg (unlike the ffmpeg-stop path at `:616` which does `kill -9`). Under a StopOnFail pile-up (≈9 extractions contending for a small limiter), tests serialize on `WaitAsync`; a per-extraction `CancelAfter` doesn't bound the _aggregate_. So "swap 300s for a smaller linked token" alone is **insufficient**.

**Effective levers that already exist:**

- **`ShutdownCoordinator.Token`** (`Helpers/ShutdownCoordinator.cs:33`) — cancelled on Ctrl+C / Docker-down only (not StopOnFail), documented as the signal to pass to "recording extraction, container stop... Normal runs are unbounded." Already `using`-imported in `TestArtifactCollector.cs`. Correct for the _catastrophic_ abort.
- **`DockerHost.ExtractLimiter.CancelPending()`** (`DockerHost.cs:388`) — already releases queued extraction waiters on host disconnect. The mechanism to unblock serialized `WaitAsync` already exists; it just isn't triggered on a normal abort/shutdown.
- **Per-clip budget** (`ContainerRecorder.cs:689-694`, `max(30, 5×durationSec)`) — already bounds a single extraction's _await_; the gap is aggregate/limiter serialization + in-container process not being killed.

## Change plan

The goal is **bounded** finalization that still captures the failing test's clip. Three targeted changes; net deletes the magic numbers and the unbounded wait, wires existing signals.

### 1. Link recording finalize to the catastrophic-abort signal + right-size the backstop

`TestArtifactCollector.FinalizeRecordingAsync` (`:237` deferred, `:273` sync): delete both `new CancellationTokenSource(TimeSpan.FromSeconds(300))`. Build the token as `CancellationTokenSource.CreateLinkedTokenSource(ShutdownCoordinator.Token)` + `CancelAfter(TestTimings.RecordingFinalizeBackstop)`. Effect: a Ctrl+C / Docker-down now cancels in-flight extraction _awaits_ immediately (the gap the agent flagged); a normal/StopOnFail run is bounded by the backstop. Replace the 300s magic number with a named `TestTimings.RecordingFinalizeBackstop`, sized per §4. Delete the stale "300s safety net / Docker.DotNet defaultTimeout is Infinite" comments (`:231-236`, `:270-272`).

### 2. On shutdown, cancel queued extractions so the limiter can't serialize-wedge

The aggregate bottleneck is `_extractLimiter.WaitAsync` serialization during teardown. `ExtractLimiter.CancelPending()` already exists and is already called on host disconnect (`DockerHost.cs:388`). Call it as part of broker shutdown so the deferred-extraction drain can't sit behind a saturated limiter: in `TestResourceBroker.DisposeAsync`, before the `_backgroundDisposeTasks` drain, signal each host's `ExtractLimiter.CancelPending()` **only on the abort path** (gate on `ShutdownCoordinator.IsShuttingDown` — a clean green run must NOT cancel, so passing-test clips still extract fully). This reuses the existing cancel mechanism rather than adding one.

### 3. Bound the broker drain without abandoning server disposals

`TestResourceBroker.cs:1720`: the queue mixes recording extractions **and** container disposals, so a blanket `.WaitAsync(timeout)` could leak a container. Fix by **separating the two** at enqueue time — give background _server disposals_ and deferred _recording extractions_ distinct queues (or tag them) — then: `await Task.WhenAll(serverDisposalTasks)` stays unbounded (must finish to avoid leaks; already bounded by Docker stop-grace), while `await Task.WhenAll(recordingTasks).WaitAsync(TestTimings.RecordingDrainBudget)` is bounded (catch `TimeoutException`, log pending count, emit per-source recording-skip so the UI shows a placeholder). With §2 cancelling the limiter on abort, the recording drain collapses fast on a real abort and runs to completion on a clean run.

### 4. Size the backstop/budget from measured data, not guesses

`RecordingFinalizeBackstop` must be ≥ the realistic worst-case per-test clip extraction (mark→end window, parallel across the test's containers, each `max(30, 5×durationSec)`) so the **failing test's clip is never truncated** — that's the debug payload. `RecordingDrainBudget` covers the aggregate of deferred passing-test extractions. Both derived from observed `recording_per_test_clip` durations + extraction elapsed on a real `SDVD_TEST_RECORDING=all` run (see Verification), set well below the old 300s but comfortably above real clips. Add both to `TestTimings` (the established home), named and commented.

### Out of scope / deliberately unchanged

- `NotifyStopOnFail` (`:1622`) keeps cancelling `_runCts` + zeroing demand; we do **not** call `ForceExitNow`/`SignalShutdown` there (preserves debug capture).
- `ForceExitNow`, `/ping`, `/health` watchdog, `host_disconnected` — unchanged.
- **Not addressed (state honestly):** this plan does not kill the _in-container_ ffmpeg on extraction cancel (the `ExecAsync` await cancels but the process lingers until the container stops). Acceptable because the container is torn down immediately after; killing the in-container process is a deeper change with little benefit once the container is going away. Called out so the next reader knows it's a known, accepted limit, not an oversight.

## Critical files

- `tests/JunimoServer.Tests/Infrastructure/Fixture/TestArtifactCollector.cs` — delete two 300s CTSes (`:237`,`:273`)+comments; link `ShutdownCoordinator.Token` + `RecordingFinalizeBackstop`.
- `tests/JunimoServer.Tests/Infrastructure/TestResourceBroker.cs` — split `_backgroundDisposeTasks` into server-disposal vs recording queues (`:49`, `EnqueueBackgroundTask` `:60`, drain `:1720`); on-abort `ExtractLimiter.CancelPending()`; bound only the recording drain.
- `tests/JunimoServer.Tests/Helpers/TestTimings.cs` — add `RecordingFinalizeBackstop`, `RecordingDrainBudget` (sized per §4).
- (read-only refs) `tests/JunimoServer.Tests/Helpers/ShutdownCoordinator.cs`, `ContainerRecorder.cs` (limiter/exec), `DockerHost.cs:388` (`CancelPending`), `Fixture/TestLifecycle.cs:159-188`, `TestRunner/Program.cs`.

## Verification

Build: `dotnet build tests/JunimoServer.Tests/JunimoServer.Tests.csproj` clean.

**0. Sizing measurement (do first, §4):** from a green `SDVD_TEST_RECORDING=all` run, read `recording_per_test_clip` durations + extraction elapsed from `infrastructure.jsonl`; set `RecordingFinalizeBackstop`/`RecordingDrainBudget` above observed worst case, well under 300s.

**1. StopOnFail finalization (core gate)** — force a fast deterministic failure (temp `Assert.True(false)` in a quick server-only test), run with `SDVD_TEST_RECORDING=all` so several tests are in flight. **Expect:** run finalizes in a bounded window (failing-clip extraction + `RecordingDrainBudget`, not 8 min); `summary.json`+`ctrf-report.json` written; UI reaches terminal (failed) state. **Debug-capture preserved (the priority):** the failing test's per-test dir has its screenshot(s) **and a finalized `.mp4`** — confirm via its `recording_per_test_clip` event + the file on disk.

**2. Catastrophic-abort cut** — recording-heavy run, Ctrl+C mid-extraction. **Expect:** in-flight extraction _awaits_ abort within ~1s (`ShutdownCoordinator.Token` + `ExtractLimiter.CancelPending()`), not 300s; `summary.json` still written by the existing Ctrl+C path.

**3. Regression — clean green run** (`SDVD_TEST_RECORDING=all`). **Expect:** passing tests still produce full per-test clips (proves the linked token + the `IsShuttingDown`-gated `CancelPending` never truncate a clean run); `summary.json` by the outer `finally`; no `recording_finalize_deferred_failed` / recording-skip storm; no leaked containers (the server-disposal queue stays unbounded).

**4. Regression — UI Stop & Ctrl+C** behave as today (unchanged paths).
