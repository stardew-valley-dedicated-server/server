# Stats collector freezes after a transport loss

**Status:** ready-to-implement
**Priority:** 1 (low)
**GitHub Issue(s):** none
**Area:** tests
**Related:** [`tests-tunnel-transport-resilience.md`](tests-tunnel-transport-resilience.md); [`tests-api-forward-heal-ping-pong.md`](tests-api-forward-heal-ping-pong.md)
**Observed:** run `2026-08-25T03-54-45Z_c4f041c`, frozen `instance-stats.jsonl` rows from 04:09:40 UTC to run end; seen once
**Next step:** implement the stream supervisor and the live `BaseUrl` delegate; check the unexplained 3 s timeout in the diagnostics run

## Symptom

Run `2026-08-25T03-54-45Z_c4f041c`: from 04:09:40 UTC to run end every instance's
`instance-stats.jsonl` rows repeat the same values (`cpuPercent`, `memoryMb`, zero
network) and the emission cadence drops from 1 s to ~3 s.

The UI and `instance-stats.jsonl` showed stable numbers while nothing was being measured.

## Root cause

Two causes in `ContainerStatsCollector`:

- `StartStreamAsync` calls `GetContainerStatsAsync(Stream = true)` once inside
  `catch { }`; when the ssh master was killed the stream ended and was never reopened, so
  `entry.Latest` froze.
- The game-stats poll uses `ApiBaseUrl`, a string captured from `Server.BaseUrl` at
  `ManagedServer` `Register` time. After `ReopenApiForwardAsync` moved the port every poll
  hit the dead port. Why that cost the full 3 s `_statsHttp` timeout (stretching the
  emission loop to that cadence) is unexplained: a disposed forward's loopback port is
  unbound and refuses instantly. Either the stale listener survived `DisposeAsync` or the
  port was accepted-but-black-holed — check in the diagnostics run; it is evidence for
  the ping-pong plan's mechanism.

## Fix

- Stream supervisor: loop `GetContainerStatsAsync` with reconnect on any non-cancellation
  exit; emit `docker_stats_stream_ended` (reason, exception chain) and
  `docker_stats_stream_reconnected` (gap ms). Give up only when the container is not
  running, and say so.
- `ApiBaseUrl` becomes `Func<string?>` reading the container's live `BaseUrl` (the same
  delegate `CreateApiClient` uses). Both `Register` sites change: `ManagedServer`
  (server) and `ClientPool` (clients).
- A frozen `Latest` is never re-emitted as fresh: emission carries `sampleAgeMs`; the UI
  greys out samples older than the stream interval. Per `runner-ui-pipeline-plumbing.md`
  this is end-to-end: collector emit → runner event type → JSONL → `test-ui` types/store
  → component; touch every hop.
- Game-stats failures emit a reason per streak (`game_stats_poll_failed` with
  `exceptionType`, `baseUrl`), not a silent null.

## Verification

- Forced master kill: stats rows show a gap, then resume with changing values;
  `docker_stats_stream_reconnected` appears once per instance.
- Forward reopen: game-stats fields (`fps`, `tps`) keep arriving from the new port.
