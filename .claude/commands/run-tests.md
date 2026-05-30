# Running E2E Tests

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

`dotnet test` directly is no longer supported — the test assembly fails fast
with a clear error message if invoked outside the custom runner.

## Debugging Loop (LLM-Optimized)

When tests fail, follow this sequence:

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
| `Isolation` | `SharedClass` (default), `SharedAssembly`, `PerTest` |
| `Clients` | Number of client slots needed (0 for API-only) |
| `Password` | Server password (null = no password) |
| `KeepConnected` | Keep client connected across tests in class |
| `Exclusive` | Drain other tests before running (use sparingly) |

## Common Issues

### StopOnFail cascade
One failure kills the run. Check `summary.json` — the FIRST failure is the real one. Later failures are usually `TaskCanceledException` from cancellation.

### Server poisoned
`ServerContainer` error detection matches `\b(ERROR|FATAL)\b` in game logs. If a mod logs ERROR for benign cases, it poisons the server. Check `infrastructure.jsonl` for `server_poisoned` events with the reason.

### Capacity deadlock
If tests hang, check `infrastructure.jsonl` for `capacity_acquired`/`capacity_released` balance. Common cause: KeepConnected session holds capacity while another class waits for it.
