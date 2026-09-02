---
name: run-tests
description: Runs the E2E test suite via `make test`/`make test-llm` and walks the structured run output (summary, per-test failures, infrastructure context, flakiness history, screenshots). Use when the user asks to run, rerun, or debug E2E tests, or when a test run needs to be launched safely on a machine that may already have one active.
argument-hint: [optional FILTER=ClassName or focus hint]
allowed-tools: Bash, Read, Grep, Glob
---

# Running E2E Tests

## Before launching: one run per machine

Check for an already-active run before starting one — concurrent runner instances on the same
machine are unsupported and kill each other (a mid-run collision dies with `make ... Error 127`
and writes no `summary.json`, and it can take the other run down too):

```bash
docker ps --format '{{.Names}}' | grep -i sdvd   # active test containers
```

The container check can't see a run still building or pre-starting, so also check the runner
process. `make test` shells out to `dotnet run --project ./tests/JunimoServer.TestRunner`, and the
apphost only exists once the build finishes — so match on the command line, not just the process
name (PowerShell):

```powershell
Get-Process JunimoServer.TestRunner -ErrorAction SilentlyContinue
Get-CimInstance Win32_Process -Filter "Name='dotnet.exe'" |
  Where-Object CommandLine -match 'JunimoServer\.TestRunner'
```

No output from both means no run is active. If either returns anything (e.g. another agent
session's run), don't launch; coordinate with the user first.

## Quick Commands

```bash
# Run all tests with structured LLM output (recommended for AI-driven debugging)
make test-llm

# Run all tests with CI output (human-readable streaming)
make test

# Run specific test class
make test-llm FILTER=PasswordProtectionTests

# Run single test
make test-llm FILTER=Login_WithCorrectPassword
```

`dotnet test` directly is not supported — the test assembly fails fast
with a clear error message if invoked outside the custom runner.

## Debugging Loop (LLM-Optimized)

When tests fail, follow `docs/developers/testing/test-failure-runbook.md` exactly — it is the authoritative triage order, including container-log slicing and SSH-tunnel failures. The steps below are the quick reference for what each command shows:

### 1. Read the summary
```bash
make test-summary
```
Shows: pass/fail counts, failure classification (assertion/timeout/infrastructure/crash), error preview, repro command, server context.

### 2. Investigate specific failures
```bash
make test-events TEST=PasswordProtectionTests.Login_WithCorrectPassword
```
Shows per-test event stream: `test_started`, `server_acquired`, `connect_started`, `connect_completed`, `screenshot_captured`, `test_completed` with timing data.

### 3. Check infrastructure context
```bash
make test-infra-log
```
Shows resource lifecycle: server create/evict/poison, client capacity acquire/release, exclusive access, HTTP request traces, session lifecycle.

### 4. Check run metadata
```bash
make test-metadata
```
Shows git SHA, branch, .env.test config, runtime info, server demands discovered.

### 5. Check flakiness across runs
```bash
make test-flaky
```
Shows per-test pass/fail history across recent runs.

### 6. View screenshots
Screenshots are in `TestResults/runs/{run}/tests/{Class}.{Method}/screenshots/`.
The `test-events` output includes `screenshot_captured` events with paths.

## Test Output Structure

```
TestResults/
├── latest.txt                          # Points to most recent run directory
├── flakiness.jsonl                     # Cross-run flakiness data
└── runs/
    └── {timestamp}_{gitsha}/
        ├── run-metadata.json           # Git, env, runtime context
        ├── summary.json                # Pass/fail, failure classification
        ├── infrastructure.jsonl        # Resource lifecycle events
        ├── ctrf-report.json            # CTRF format report
        └── tests/
            └── {Class}.{Method}/
                └── screenshots/
                    ├── failure.png
                    └── 01_checkpoint.png
```

## Environment Variables

| Variable | Values | Default | Description |
|----------|--------|---------|-------------|
| `SDVD_DOCKER_HOSTS` | JSON array | required | Host definitions (`serverSlots`/`clientSlots` per host gate concurrency) |
| `SDVD_MAX_CONCURRENT_STARTS` | `1`-`N` | host's `serverSlots+clientSlots` | Per-host cap on concurrent `docker create+start`; per-host `concurrentStarts` JSON field overrides |
| `SDVD_TEST_SCREENSHOTS` | `none`/`done`/`all` | `done` | When to capture screenshots (done=on test completion) |
| `SDVD_SKIP_BUILD` | `true`/`false` | `false` | Skip Docker image rebuild |

## Test Infrastructure

All E2E tests extend `TestBase` and use `[TestServer(...)]` attribute:

| Attribute Property | Description |
|-------------------|-------------|
| `Isolation` | `SharedClass` (default), `SharedGroup`, `SharedAssembly`, `PerTest` |
| `SharedGroup` | Group name for `SharedGroup` isolation |
| `Clients` | Number of client slots needed (0 for API-only) |
| `Password` | Server password (null = no password) |
| `KeepConnected` | Keep client connected across tests in class |
| `Exclusive` | Drain other tests before running (use sparingly) |
| `DeferAcquisition` | Skip acquisition in `InitializeAsync`; the test calls `AcquireServerAsync()` itself (Theory tests whose server config depends on parameters) |

## Common Issues

### StopOnFail cascade
One failure kills the run. Check `summary.json` — the FIRST failure is the real one. Later failures are usually `TaskCanceledException` from cancellation.

### Server poisoned
`ServerContainer` error detection matches `\b(ERROR|FATAL)\b` in game logs. If a mod logs ERROR for benign cases, it poisons the server. Check `infrastructure.jsonl` for `server_poisoned` events with the reason.

### Capacity deadlock
If tests hang, check `infrastructure.jsonl` for `capacity_acquired`/`capacity_released` balance. Common cause: KeepConnected session holds capacity while another class waits for it.
