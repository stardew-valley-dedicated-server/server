# Test harness reference

Component catalog and IPC reference for the E2E test harness. For the operator-facing run/debug story see [E2E Testing](/developers/testing/e2e-testing). Contributor pitfalls live at [.claude/rules/test-broker-invariants.md](https://github.com/stardew-valley-dedicated-server/server/blob/master/.claude/rules/test-broker-invariants.md) and [.claude/rules/docker-test-resources.md](https://github.com/stardew-valley-dedicated-server/server/blob/master/.claude/rules/docker-test-resources.md).

## Key components

- **TestBase** — unified base for all E2E tests; handles server acquisition, client leasing, and cleanup.
- **TestResourceBroker** — assembly-scoped singleton; manages server lifecycle, pre-warming, and health monitoring.
- **ServerContainer** — manages the server + steam-auth container pair.
- **ClientPool** — ref-counted game-client leasing with a global creation limiter.
- **ManagedServer** — wraps `ServerContainer` with ref counting, exclusive access, and a health watchdog.
- **ClientCapacity** — priority-queue semaphore for test scheduling.
- **PersistentSession** — `KeepConnected` sessions hold a client + capacity for the entire test class duration.
- **GameTestClient** — high-level client automation (connect, chat, navigate).
- **ServerApiClient** — HTTP API wrapper for server control.

## Observability

Artifacts live under `TestResults/runs/{timestamp}_{sha}/`. `TestResults/latest.txt` points to the current run.

- **Per-run**: `run-metadata.json` (RunMetadata, written by the test child), `infrastructure.jsonl` (InfrastructureEventLog — sole writer), `flakiness.jsonl` (FlakinessTracker — appended at run end before flakiness IPC emit so the current run is included), `summary.json` and `ctrf-report.json` (TestRunArtifactWriter, runner-side).
- **Per-test**: screenshots, recordings, `server.log` on failure (container logs via `SaveLogsToFileAsync`). Per-test events are reconstructed by filtering `infrastructure.jsonl` on `test.displayName` — see `make test-events TEST=Class.Method`.
- **HTTP tracing**: `TracingHandler` logs every `ServerApiClient` call to `infrastructure.jsonl`.
- **Health watchdog**: `ManagedServer` emits `health.slow_response` (>3s), `health.check_failed`, `health.check_error`, `health.poison`.
- **`/health` endpoint**: returns `lastTickMs`, `pendingActions`, `gameAvailable`. Status is `"degraded"` when the game thread has stalled for more than 5s.
- **LLMRenderer + WebRenderer**: both consume the same setup-pipe event stream and project from a shared `TestRunState` model. `summary.json` / `ctrf-report.json` are projections of that model — no parallel per-test list.

### Recording alignment debugging

Where to start when cross-clip alignment regresses (recorder invariants are catalogued in `.claude/rules/recorder-anchor-first-frame.md`):

1. **All clips off by >100ms in the same direction** → check the burn-in (`%{pts}`) value at a known event vs the event's wall-clock — if the burn-in shows the event correctly but UI scrub doesn't, the orchestrator's `timelineOffsetSec` / `actualFirstFramePts` flow is broken. If the burn-in is also wrong, the recorder's PTS stream is wrong (check Invariant 3).
2. **Clips drift relative to each other** → check `recording_started` events' `phaseLockTargetEpoch` and `phaseLockOvershootMs`. Same target across recorders + healthy overshoot means phase-lock is working; widely different targets means recorders started in different 1/fps windows; healthy `phaseLockOvershootMs` in this image is ~100-200ms (dominated by x11grab open + libx264 init latency on top of sleep precision); investigate when consistently >300ms.
3. **Anchor value looks impossible** (e.g. ~49254s instead of ~1.78e9s) → ffprobe-of-source-TS regression somewhere (Invariant 4).
4. **Clips have wrong duration / missing tail frames** → check pass-2 `-copyts` hasn't been re-added (Invariant 6), or `-tune zerolatency` hasn't been removed (Invariant 1).
5. **Clips show pre-test filler instead of the test body** → Invariant 1 again, almost always.
6. **`recording_clip_failed` with `stage="ffmpeg_failed"` and `sizeBytes=0`** → could be a true ffmpeg crash OR the active-segment wait timing out before pass-1 had data to read. The shell emits a `WAIT_RESULT:fin=...,phase=...,alive=...` line to stderr that disambiguates (`phase=timeout/fsz=...` = active-segment wait timed out before the file grew). Look in the recorder's logged stderr (the test's container log; `LogStderr` filter keeps the last 10 meaningful lines).

Playground reproducer scripts in `tools/.playground/recording-validator/` are the fastest way to isolate any of these without spinning up the full E2E.

### Disk-write ownership

| Artifact | Producer | When |
|---|---|---|
| `run-metadata.json` | `RunMetadata.WriteRunMetadata` (test child) | Once at fixture init, after server demand computation |
| `flakiness.jsonl` | `FlakinessTracker.RecordRun` (test child) | At run end inside `TestSummaryFixture.FinalizeRun`. Must run before `ComputeFlakiness` IPC emit so the current run appears in its own window |
| `summary.json`, `ctrf-report.json`, `latest.txt` | `TestRunArtifactWriter` (runner-side) | On `OnRunFinished`. On abnormal child exit, the runner writes a partial `summary.json` with `aborted: true` from the in-memory `TestRunState` — same crash coverage that the old in-child `EmergencyFlush` provided |
| `infrastructure.jsonl` | Test child, written continuously | Per-event |

## Cleanup architecture

Every container, network, and volume the harness creates carries the label `sdvd.test=true` (containers and networks also carry `sdvd.run-id={runId}`). All cleanup is keyed off these labels.

- **Per-resource registration**: each `ServerContainer`, `GameClientContainer`, `SharedSteamAuth`, and `TestNetworkManager`-built network calls `EmergencyCleanup.Register(key, () => …)` at create time and `Unregister(key)` on graceful dispose. Registered callbacks force-remove the resource via Docker.DotNet (per-host `host.ApiClient`) on process exit (`AppDomain.ProcessExit` / `AssemblyLoadContext.Default.Unloading`).
- **Startup sweep**: `EmergencyCleanup.SweepStaleResourcesAsync` runs once at the start of every test process. It walks `HostPool.Instance.Hosts` and bulk-removes anything labeled `sdvd.test=true` on each daemon (containers, networks, volumes). This catches anything the previous run leaked.

**Testcontainers' Ryuk reaper is disabled** (`TestcontainersSettings.ResourceReaperEnabled = false` in `ModuleInit.cs`). Two structural reasons:

1. Ryuk dials its container's mapped port from the test process. For `ssh://` Docker endpoints, Testcontainers' `DockerContainer.Hostname` throws "endpoint not supported"; the dial loop times out after 60s with `ResourceReaperException("Initialization has been cancelled")`.
2. Multi-host runs need one reaper per Docker daemon. Testcontainers' reaper is a process-global singleton; per-host instantiation would require private API. `HostPool` is multi-host by design.

**Trade-off — the kill-9 gap**: graceful exits (Ctrl+C, normal completion, unhandled exceptions) run the registered cleanup callbacks. A test process killed with `kill -9` / OOM / BSOD / power loss leaves containers running on each host until the next `make test` sweeps them by label. Ryuk would close that window to ~10 seconds; the in-tree sweep closes it at the next run. Acceptable for the current single-user / personal-VPS workflow.

## Test runner output modes

The custom runner (`tests/JunimoServer.TestRunner/`) is the only supported entry point and offers three modes:

- **CI** (`make test`) — streaming human-readable output.
- **LLM** (`make test-llm`) — structured JSONL to stdout, optimized for AI debugging.
- **Web** (`make test-web`) — Vue UI with VNC grid (`tests/test-ui/`).

The test assembly fails fast with a clear error if invoked outside the runner (e.g. via plain `dotnet test`); use `make test FILTER=…` instead.

## Setup-phase IPC

Setup-phase progress events flow from the xUnit child process to the parent renderer over a named pipe (JSONL wire format). The producer side is `tests/JunimoServer.Tests/Helpers/SetupEventBus.cs`; the consumer is `tests/JunimoServer.TestRunner/IPC/SetupPipeServer.cs`. Adding a field requires end-to-end plumbing — see [.claude/rules/runner-ui-pipeline-plumbing.md](https://github.com/stardew-valley-dedicated-server/server/blob/master/.claude/rules/runner-ui-pipeline-plumbing.md).

### Two parallel event streams

The runner consumes two independent event channels:

1. **xUnit-native** (`Xunit.SimpleRunner` → `RunnerCallbacks` → `ITestRenderer`): `OnTestStarting`, `OnTestPassed`, `OnTestFailed`, `OnTestSkipped`, `OnExecutionComplete`. Carries only what xUnit knows: name, duration, exception. **The runner cannot enrich these from child-side data — they live behind the IPC boundary.**
2. **Setup-pipe** (`SetupEventBus` → `SetupPipeServer` → `ITestRenderer`): everything else, including child-side enrichment that supplements xUnit-native events.

### Event catalog (setup-pipe)

| Event | Producer site | Carries | Notes |
|---|---|---|---|
| `setup_started` / `setup_step` / `setup_completed` | `SetupEventBus.EmitPhaseX` / `EmitStep` | Setup phase progress | |
| `test_running` | `SetupEventBus.EmitTestRunning` | Acquired-broker-lease transition | |
| `test_annotation` | `SetupEventBus.EmitTestAnnotation` (test body + co-located infrastructure producers) | displayName, level, source, message | Renderers filter on `level`/`source`. |
| `test_enrichment` | `SetupEventBus.EmitTestEnrichment` from `TestBase.DisposeAsync` | failureCategory, phase, errorPreview, reproCommand, server context, full lifecycle phase breakdown, optional `failureContext` (server state at failure point) | Correlates with xUnit-native `test_passed`/`failed` by displayName. May arrive slightly after the outcome event — same pattern as `screenshot` |
| `screenshot` | `SetupEventBus.EmitScreenshot` | Path + source (server/client) | Appended to `test.output[]`; runner also tracks latest as `LatestScreenshotPath` for CTRF attachment |
| `recording` | `SetupEventBus.EmitRecording` | Per-test video clip | |
| `instance_*` | `SetupEventBus.EmitInstanceX` | Lifecycle + stats | |
| `run_metadata` | `SetupEventBus.EmitRunMetadata` from `RunMetadata.WriteRunMetadata` | runId, runDir, git, env, runtime, server-config plan | The runner-side artifact writer uses `runDir` to know where to write `summary.json` |
| `flaky_tests` | `SetupEventBus.EmitFlakyTests` from `TestSummaryFixture.FinalizeRun` | Last-20-run flakiness array | Emitted after `FlakinessTracker.RecordRun` so the current run is included |

## Configuration

- `.env.test` / `.env.test.example` — test config (indexed Steam accounts, `SDVD_DOCKER_HOSTS` host definitions with per-host `serverSlots`/`clientSlots`, image tag).

### Internal environment variables

The runner sets these on child and container processes via `Environment.SetEnvironmentVariable` / Testcontainers `WithEnvironment`. They are **not operator knobs** — don't set them in `.env.test`.

| Variable | Set by | Carries |
|---|---|---|
| `SDVD_RUN_DIR` | runner → child | Shared run-artifact directory so child and parent write to the same root |
| `SDVD_RUN_START_MS` | distributed coordinator → worker | Epoch offset aligning worker clocks |
| `SDVD_SETUP_PIPE` | runner → child | Named-pipe name for the setup-phase IPC channel |
| `SDVD_HOST_TUNNELS` | runner → child | `{hostId → coordinatorPort}` map for SSH daemon-socket forwards |
| `SDVD_SSH_BINARY` | runner → child | Absolute path to the SSH binary resolved at preflight |
| `SDVD_SSH_HOST_MASTERS` | runner → child | `{hostId → ControlMaster socket info}` map for `ssh -O forward` |
| `SDVD_WORKER_TEST_COUNT` | distributed coordinator → worker | Expected test count for the worker's expected-count seed |
| `SDVD_TEST_FILTER` | runner → child | `make test FILTER=…` value, so prestart only provisions for dispatched tests |
| `SDVD_SKIP_BUILD` | runner → child | Set `true` after the parent-side image build so the child skips rebuilding |
| `SDVD_ENV` | container builder → container | Marks the container as a test environment (`test`) |
| `SDVD_TEST_STEAM_ACCOUNT_INDEX` | container builder → container | Slice-local Steam account index for the container |
