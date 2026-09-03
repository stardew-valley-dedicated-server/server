# Test Failure Debugging Runbook

When tests fail, follow this exact sequence. Do NOT skip steps or guess at root causes:

1. **`make test-summary`**: read `summary.json`. Identify the FIRST failure — sort `failures[]` by `failedAt` (later ones are usually cancellation cascade from StopOnFail). Note the `failureCategory` (assertion/timeout/infrastructure/crash) and `reproCommand`, and check `infrastructureErrors` — a `host_disconnected` entry there names a host outage's cause (including the SSH master's own death line). `summary.json` is always written, even on aborted runs (`"aborted": true` + `abortReason`).
2. **`make test-events TEST=Class.Method`**: filter `infrastructure.jsonl` for events attributed to the failing test (jq filter on `test.displayName`). Look at timing between events, which `phase` failed (connect, test body, artifacts, cleanup), and any error details.
3. **`make test-container-log CONTAINER=server-0`** (or `client-0`, `steam-auth-shared`, `steam-auth-per-N`): full lifecycle log for the container. Use the test window timestamps from `make test-events` output to slice context around the failure.
4. **`make test-infra-log`**: if the failure is infrastructure-related (server poisoned, capacity deadlock, timeout waiting for server), check `diagnostics/infrastructure.jsonl` for resource lifecycle events (server create/evict/poison, capacity acquire/release, HTTP requests, session lifecycle) around the failure timestamp.
5. **Read the actual test code and mod code** before proposing a fix. Cross-reference the error with source.
6. **`make test-flaky`**: check if this test has failed before across runs. Flaky tests need different fixes than consistent failures.
7. **Remote-host / SSH tunnel failures** (only when `failureCategory` is `infrastructure` AND a host is remote — see `host_id` in events): the SSH tunnel is a silent failure domain, so a dead tunnel surfaces downstream as a generic timeout/`host_disconnected`. To find the SSH-level cause:
   - Grep `diagnostics/infrastructure.jsonl` for `host_disconnected`, `ssh_master_log`, `ssh_master_exited`, `tunnel_forward_failed` (`make test-infra-log`, or grep the file directly — `ssh_master_log` is emitted at teardown on the coordinator, so it is not attributed to a single test and won't appear under `make test-events`). Also grep `infrastructure.parent.jsonl` for `ssh_master_unhealthy_owner` — a **mid-run** hit (its `cause` names what tripped: a failed mux check, a data-path wedge caught by the canary, or in-place forward reopens failing repeatedly) means the shared ControlMaster was replaced; the follow-up `ssh_master_respawn_attempt` (`termination`, `exitCode`, `killOutcome` of the old master — see the tunnel-stall section below) and `tunnel_forward_reopened` / `tunnel_forward_reopen_failed` events tell whether the daemon-socket forward came back on its original port. A teardown-window hit (after `summary.json`'s timestamp) is a benign shutdown race, not a mid-run outage.
   - A `host_disconnected` whose `reason` names a transport fault and which carries an `sshMasterLogTail` is the smoking gun for a mid-run drop; the tail holds ssh's own death line (e.g. `Timeout, server not responding.`).
   - Read `diagnostics/ssh-master-{host}.log` for the master's full `-E` error log. An **empty** master log on a poison is expected for an abrupt RST drop (the reset is caught by the exception classifier instead, not the log) — not itself a bug.
   - Healthy sequence: `ssh_preflight` → `ssh_master_ready` → `tunnel_forward_opened` (×N) → … → `ssh_master_exited` (clean `exitCode 0`). A non-zero `exitCode`/`stderr` on `ssh_master_exited` or `tunnel_forward_closed` means a teardown step itself failed.

Output locations: `TestResults/latest.txt` points to the current run directory. All artifacts are under `TestResults/runs/{timestamp}_{sha}/`.

## Tunnel stalls (every stream through one remote host stops at once)

A Wi‑Fi or ssh-mux stall shows as every `container.log` of a host ending within the same second, Docker stats stopping, and an in-flight API request timing out or ending prematurely. The evidence lives on the coordinator only — a macOS `sshd` logs no session open/close and the Mac's unified log holds no session-level record, so do not look there. Read, in order:

1. **`diagnostics/infrastructure.parent.jsonl`** (runner-side events, not attributed to a test):
   - `ssh_master_canary_stall` — first wedged `/_ping` canary through the host's daemon-socket forward; `canary` carries the connect/write/read timings and which deadline hit.
   - `ssh_master_canary_recovered` — the stall ended by itself; `stallMs` is its measured length and `wedgedPolls` how many 10 s polls it spanned. A run full of these and no `ssh_master_wedge_observed` was a stall the harness rode out.
   - `ssh_master_wedge_observed` — the streak reached the action threshold. Captured **before** any action: `canaries` (every poll of the streak), `muxCheck` (`ssh -O check` exit + stderr), `tcp` (coordinator TCP state toward the host: `Get-NetTCPConnection` + `netstat -s` retransmit counters on Windows, `ss -ti` on Linux; `error` names why sampling failed), `reachability` (a fresh TCP connect to the host's SSH port — `port`, resolved via `ssh -G` — with its latency) and `masterLogTail`. `reachability.result: connected` with wedged canaries points at the mux; a `timeout` there points at the path.
   - `ssh_master_respawn_attempt` — the action: `incidentId`, `cause`, `termination` (`exit_ok` / `killed` / `socket_gone` / `unconfirmed`), `exitCode`, `exitStderr`, `killOutcome`, `elapsedMs`, `terminatedAtUtc`, and `masterLogArchivePath` — the old master's `-E` log, archived per pid so the replacement master's log starts empty. Then `ssh_master_respawned` (`alive`) or `ssh_master_respawn_failed`.
2. **`diagnostics/transport-state.{hostId}.json`** — the runner's latest action on that host as one document (`incidentId`, `cause`, `termination`, `outcome`, `actionStartedAtUtc` … `windowEndUtc`). A test failure between `actionStartedAtUtc` and `windowEndUtc` on that host was caused by the respawn, not by the test.
3. **`diagnostics/infrastructure.jsonl`** (child-side):
   - `container_log_stream_gap` / `container_stats_stream_gap` — a stream delivered again after a silent gap (`gapStartUtc`, `gapEndUtc`, `gapMs`). Sort by `gapStartUtc`: every stream of the host starting a gap at the same instant is the stall's start; one stream alone is a quiet container. A stream with no gap-end event never resumed.
   - `container_log_stream_reconnected` — a container log reader re-opened its stream after a transport loss (`outageStartUtc`, `reconnectedAtUtc`, `gapMs`, `openFailures`, `lastLineTimestamp`, `incidentId` when a runner action covered the outage). One per container after a master respawn is the healthy shape.
   - `container_log_stream_ended` — why a container log reader stopped, emitted on every exit: `reason` is `container_exited` (inspect confirmed not running or gone), `open_failures_exhausted` (no re-open before the budget deadline; `detail` names the budget source and `incidentId` the covering action), `cancelled` (drain/dispose/shutdown), `docker_down` (daemon 500) or `line_handler_faulted` (the per-line callback threw — a sink/forwarding fault, not a transport loss; `faultType`/`faultMessage` name it). Carries `faultType`/`faultMessage`/`faultChain`, `lastLineTimestamp`, `linesEmitted`, `reconnects`, `outageMs`. A `container.log` ending before the run did with no `container_log_stream_ended` for it is a reader bug.
   - `forward_heal_attempt` — one per heal cycle of a request's `ForwardHealingHandler` (`attempt`, `port`, `faultChain`, `classification`, `healMs`, `outcome`). Alternating ports across consecutive attempts means two handlers re-opened the same forward in turns.
4. **`diagnostics/ssh-master-{host}.log`** (current master) and `ssh-master-{host}.pid{N}-{hhmmss}.log` (archived masters): ssh's own death line (`Timeout, server not responding.`) when the master saw the stall as a keepalive timeout; empty when it did not.
5. **`diagnostics/test-process-stderr.log`** — every `TestLog` line of the xUnit child (`[Server]` / `[Client]` / `[Test]` prefixes), including the lease/reuse and "marking dead" lines that exist nowhere else.

### Finishing coverage when every full run dies to a tunnel stall

When consecutive full runs keep ending in a stall cascade (a `ssh_master_wedge_observed` followed within a minute by "connection refused (localhost:NNNNN)" failures and mass cancellation), run the never-passed remainder instead of a fifth full run:

1. Take the union of `status == "passed"` test names across the runs' `ctrf-report.json` files; the complement is the set still unexercised.
2. Run that set with `make test FILTER="ClassA|ClassB|Method_C"` (`|` joins independent substring patterns) — a shorter run finishes before the tunnel wedges.

**Confirm with the user before doing this.** Every failure being folded away must first be tied to a causal transport window on its own host: its `failedAt` falls between a `ssh_master_wedge_observed` (or `ssh_master_canary_stall`) and the matching `ssh_master_respawned` / `tunnel_forward_reopened`, or inside a `host_daemon_forward_healed` heal cycle, for the same `host_id`. Healthy lifecycle events (`ssh_master_ready`, `ssh_master_exited` at teardown) do not count. A failure without such a window is a real failure and the runbook's normal path applies; a real regression that happens to sit next to a stall would otherwise be laundered into "canceled, re-run later".

## Stalled / wedged runs (`aborted: true`, tests "Not executed", 0 failed)

A run aborted by the stall watchdog (`abortReason: "child-stall-watchdog"`, `notDispatched > 0`) has no failing test to start from — something is blocked while holding a lease. Diagnose the *waiter*, not a test:

1. **`summary.json`**: note `notDispatched` and which tests never ran — their shared server config is usually the blocked resource.
2. **`diagnostics/infrastructure.jsonl`**: find `wait` events with `"phase":"started"` and no matching `"phase":"completed"` — those are the hung waiters. The `run_stall_watchdog_tripped` event carries `outstandingLeases`.
3. **Pool-accounting tells**: `steam_account_pool_insufficient` with `kind:"server"` means a second steam server config forked and its prestart is starving (see `.claude/rules/test-broker-invariants.md`); `steam_pool_lease_wait_started` with `availableInBag:0` means steam-client scarcity; `client_returned`/`client_acquired` events reconstruct who held which client when.
4. **`diagnostics/test-process-stderr.log`**: the lease-request/reuse `TestLog` lines (`Lease requested`, `client-N reused (steam=…)`, `… marking dead`) are only there and on the console — `client_returned` events land in `infrastructure.jsonl`, the requests/reuses do not.
5. **A dead Steam client is self-healing**: `client-N disconnect failed, marking dead: A task was canceled.` retires the pool's only Steam-bearing client, but dead containers don't count toward the cap and the next Steam lease recreates one. A run that still starves on Steam leases after that line means the discount/recreate path regressed.

## Known benign log lines

Ignore these when triaging — they are not signals of test failure:

- **`Timer:       time has moved backwards!`** in any `container.log`. Emitted by TigerVNC (`common/rfb/Timer.cxx`, `Timer::getNextTimeout`) when the container's wall clock jumps backwards by more than 1 second. Common on virtualized hosts (Docker Desktop on Mac/Windows, WSL2) where the VM clock is periodically resynced from the host. TigerVNC self-corrects (`dueTime = now`); the game, the mod, and the test infrastructure are unaffected. Cross-worker event correlation uses `run_ms`, so wall-clock jumps inside a single container do not affect ordering either. Do not add in-container time-sync daemons to suppress this.
