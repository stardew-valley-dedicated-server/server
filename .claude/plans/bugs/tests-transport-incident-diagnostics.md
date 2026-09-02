# A tunnel stall leaves no record of what stalled

**Status:** ready-to-implement
**Priority:** 2 (medium)
**GitHub Issue(s):** none
**Area:** tests
**Related:** [`tests-tunnel-transport-resilience.md`](tests-tunnel-transport-resilience.md)
**Observed:** run `2026-08-25T03-54-45Z_c4f041c`, master declared wedged and terminated with no observation record; seen once
**Next step:** implement; ships first among the transport siblings so the next stall is explained
**Notes:** the runner-published `diagnostics/transport-state.json` built here is a prerequisite for the classification and log-reader siblings

## Symptom

Run `2026-08-25T03-54-45Z_c4f041c`: the master was declared wedged and terminated, and
the artifacts hold only decisions (`ssh_master_unhealthy_owner cause=datapath_wedged`,
`oldMasterGone=true`), not observations. The master's `-E` log at `LogLevel=INFO` was
empty (clean `-O exit` logs nothing). `ForwardHealingHandler` emits no event per attempt.
`WaitForMasterGoneAsync` reports a boolean. Coordinator-side TCP state was never sampled.
Runner stdout (`TestLog`) is the only sink for several failure lines and is not persisted.

## Fix

- **Detection record.** When the canary wedges (before any action), emit
  `ssh_master_wedge_observed` with: canary result per poll (connect ms / write ms / read
  ms / timeout), streak count, `ssh -O check` output, coordinator TCP snapshot for the
  master's connection (`Get-NetTCPConnection` state + bytes in flight via `netstat -s`
  retransmit delta on Windows; `ss -ti` on Linux), and a reachability probe
  (`Test-NetConnection <host> -Port 22` / TCP connect ms).
- **Action record.** `ssh_master_respawn_attempt` carries `termination`
  (`exit_ok | killed | socket_gone | unconfirmed`), `exitCode`, `-O exit` stderr,
  `elapsedMs`, and the observed process end time. `TerminateMasterCoreAsync` already
  returns `ExitCode`/`ExitStderr`/`KillOutcome` (`MasterTeardownOutcome`); this is
  plumbing, not new measurement. The same record is written to
  `diagnostics/transport-state.json` for the xUnit child (resilience plan, principle 7).
- **Master log.** Spawn the master with `LogLevel=VERBOSE` (channel open/close and
  keepalive lines) and rotate per master pid so a respawn does not append into the
  previous master's file; attach the tail to the detection record.
- **Per-attempt heal events.** `ForwardHealingHandler` emits `forward_heal_attempt`
  (attempt index, trigger exception chain, port, elapsed, outcome).
- **Stall duration log.** Every stream (log, stats, canary) records gap start/end so
  stall durations on the Wi‑Fi host are measurable; the resilience plan tunes thresholds
  against this data.
- **Persist `TestLog`.** `TestLog` writes to **stderr** (`TestLog.cs`) from the xUnit
  child, not runner stdout; the runner captures the child's stderr into
  `diagnostics/test-process-stderr.log` in the run dir in addition to the console.
- **Runbook.** Add a "tunnel stall" section to
  `docs/developers/testing/test-failure-runbook.md` naming these events and the fact that
  macOS `sshd` and the Mac's unified log hold no session-level evidence.

## Verification

- `kill -STOP` the Mac's `sshd-session` for the master for 20 s (above the current
  ≥10–17 s kill threshold): `infrastructure.jsonl` contains `ssh_master_wedge_observed`
  with TCP and probe fields filled, followed by an action record with `termination` and
  `exitCode`. A 5 s `STOP` yields the wedge observation only and no action record.
- `diagnostics/test-process-stderr.log` exists and contains the `TestLog` lines of the run.
