# Test Failure Debugging Runbook

When tests fail, follow this exact sequence. Do NOT skip steps or guess at root causes:

1. **`make test-summary`**: read `summary.json`. Identify the FIRST failure — sort `failures[]` by `failedAt` (later ones are usually cancellation cascade from StopOnFail). Note the `failureCategory` (assertion/timeout/infrastructure/crash) and `reproCommand`, and check `infrastructureErrors` — a `host_disconnected` entry there names a host outage's cause (including the SSH master's own death line). `summary.json` is always written, even on aborted runs (`"aborted": true` + `abortReason`).
2. **`make test-events TEST=Class.Method`**: filter `infrastructure.jsonl` for events attributed to the failing test (jq filter on `test.displayName`). Look at timing between events, which `phase` failed (connect, test body, artifacts, cleanup), and any error details.
3. **`make test-container-log CONTAINER=server-0`** (or `client-0`, `steam-auth-shared`, `steam-auth-per-N`): full lifecycle log for the container. Use the test window timestamps from `make test-events` output to slice context around the failure.
4. **`make test-infra-log`**: if the failure is infrastructure-related (server poisoned, capacity deadlock, timeout waiting for server), check `diagnostics/infrastructure.jsonl` for resource lifecycle events (server create/evict/poison, capacity acquire/release, HTTP requests, session lifecycle) around the failure timestamp.
5. **Read the actual test code and mod code** before proposing a fix. Cross-reference the error with source.
6. **`make test-flaky`**: check if this test has failed before across runs. Flaky tests need different fixes than consistent failures.
7. **Remote-host / SSH tunnel failures** (only when `failureCategory` is `infrastructure` AND a host is remote — see `host_id` in events): the SSH tunnel is a silent failure domain, so a dead tunnel surfaces downstream as a generic timeout/`host_disconnected`. To find the SSH-level cause:
   - Grep `diagnostics/infrastructure.jsonl` for `host_disconnected`, `ssh_master_log`, `ssh_master_exited`, `tunnel_forward_failed` (`make test-infra-log`, or grep the file directly — `ssh_master_log` is emitted at teardown on the coordinator, so it is not attributed to a single test and won't appear under `make test-events`).
   - A `host_disconnected` whose `reason` names a transport fault and which carries an `sshMasterLogTail` is the smoking gun for a mid-run drop; the tail holds ssh's own death line (e.g. `Timeout, server not responding.`).
   - Read `diagnostics/ssh-master-{host}.log` for the master's full `-E` error log. An **empty** master log on a poison is expected for an abrupt RST drop (the reset is caught by the exception classifier instead, not the log) — not itself a bug.
   - Healthy sequence: `ssh_preflight` → `ssh_master_ready` → `tunnel_forward_opened` (×N) → … → `ssh_master_exited` (clean `exitCode 0`). A non-zero `exitCode`/`stderr` on `ssh_master_exited` or `tunnel_forward_closed` means a teardown step itself failed.

Output locations: `TestResults/latest.txt` points to the current run directory. All artifacts are under `TestResults/runs/{timestamp}_{sha}/`.

## Known benign log lines

Ignore these when triaging — they are not signals of test failure:

- **`Timer:       time has moved backwards!`** in any `container.log`. Emitted by TigerVNC (`common/rfb/Timer.cxx`, `Timer::getNextTimeout`) when the container's wall clock jumps backwards by more than 1 second. Common on virtualized hosts (Docker Desktop on Mac/Windows, WSL2) where the VM clock is periodically resynced from the host. TigerVNC self-corrects (`dueTime = now`); the game, the mod, and the test infrastructure are unaffected. Cross-worker event correlation uses `run_ms`, so wall-clock jumps inside a single container do not affect ordering either. Do not add in-container time-sync daemons to suppress this.
