# Container log reader stops silently on transport loss

**Status:** ready-to-implement
**Priority:** 2 (medium)
**GitHub Issue(s):** none
**Area:** tests
**Related:** [`tests-tunnel-transport-resilience.md`](tests-tunnel-transport-resilience.md); [`tests-transport-incident-diagnostics.md`](tests-transport-incident-diagnostics.md)
**Observed:** run `2026-08-25T03-54-45Z_c4f041c`, all ten `container.log`s end at 04:09:36 UTC; seen once
**Next step:** implement; the time-based open-failure budget depends on the runner-published `diagnostics/transport-state.json` from the diagnostics plan

## Symptom

Run `2026-08-25T03-54-45Z_c4f041c`: all ten `container.log`s end at 04:09:36 UTC (the
tunnel stall); the run continued to 04:13. `ContainerLogStreamReader.RunAsync` has two
exits that leave no on-disk trace:

- `PumpAsync` returns cleanly after data was read → treated as "container exited" → return.
  A killed ssh master EOFs the stream exactly the same way.
- Three consecutive open failures → return; the reason goes only to the diagnostic callback
  (`TestLog` → runner stdout).

Which exit fired is unknown, and the runbook's step 3 (container log) was blind for the
failure window.

## Fix

- On clean EOF, inspect the container (`Containers.InspectContainerAsync`): if it is still
  running, treat EOF as a transport loss and reconnect with the `Since` cursor; only a
  confirmed non-running container ends the reader. The inspect goes through the same
  daemon-socket forward that just died, so an inspect *failure* is a transport loss, never
  "container exited". Reconnect can succeed only because the runner restores the
  daemon-socket forward at the same port after a respawn
  (`TunnelManager.TryRespawnMasterAsync` → `ReopenRegisteredForwardsAsync`) — a stated
  dependency.
- The `docker_down` exit currently matches `ex.Message.Contains("InternalServerError")`
  — the substring classification the classification sibling removes. Replace with the
  typed `DockerApiException.StatusCode` check.
- Every exit emits `container_log_stream_ended` with `reason` (`container_exited`,
  `open_failures_exhausted`, `cancelled`, `docker_down`), the last exception chain, the
  cursor, and `linesEmitted`.
- Every reconnect emits `container_log_stream_reconnected` with the gap duration.
- Open-failure budget is time-based, not a count of three one-second attempts. The
  reader runs in the xUnit child and the master supervisor in the runner, so the window
  and incident id come from the runner-published `diagnostics/transport-state.json`
  (resilience plan, principle 7), with a fixed default when the file is absent. A stall
  longer than the budget ends the reader with `open_failures_exhausted` and the incident
  id.

## Verification

- Forced master kill: every `container.log` shows a gap and resumes; `infrastructure.jsonl`
  holds one `container_log_stream_reconnected` per container.
- Stopping a container yields `container_log_stream_ended reason=container_exited`.
