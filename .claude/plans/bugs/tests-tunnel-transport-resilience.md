# Remote-host transport: stop healing by guesswork, make every failure observable

**Status:** validation
**Priority:** 3 (high)
**GitHub Issue(s):** none
**Area:** tests
**Related:** [`tests-transport-fault-typed-classification.md`](tests-transport-fault-typed-classification.md); [`tests-log-stream-reader-stops-silently.md`](tests-log-stream-reader-stops-silently.md); [`tests-stats-collector-never-reconnects.md`](tests-stats-collector-never-reconnects.md); [`tests-api-forward-heal-ping-pong.md`](tests-api-forward-heal-ping-pong.md); [`tests-transport-incident-diagnostics.md`](tests-transport-incident-diagnostics.md)
**Observed:** run `2026-08-25T03-54-45Z_c4f041c`, host `mac` on Wi‑Fi: one stall at 04:09:36 UTC cost 1 failed + 103 cancelled tests
**Next step:** decide the transport shape (direct-published API ports on the test host, daemon socket as the only tunneled resource) before the siblings implement
**Notes:** parent architecture plan; each sibling in Related is independently shippable

Sibling plans (each independently shippable, all cite this one for context):

- `tests-transport-fault-typed-classification.md`
- `tests-log-stream-reader-stops-silently.md`
- `tests-stats-collector-never-reconnects.md`
- `tests-api-forward-heal-ping-pong.md`
- `tests-transport-incident-diagnostics.md`

## Symptom

Run `2026-08-25T03-54-45Z_c4f041c`, host `mac`, Wi‑Fi only. At 04:09:36 UTC every stream
through the host's single SSH ControlMaster stalled at the
same instant: all ten `container.log`s end within 90 ms, Docker stats stop, and a
`/newgame` response the server had already produced never arrived. Twenty seconds later
`TunnelManager.EnsureOwnedMasterHealthyAsync` saw two wedged canary probes and ran
`ssh -O exit` on a master that was still alive (`oldMasterKill=not_needed`,
`oldMasterGone=true`). That severed the in-flight request:
`HttpIOException: The response ended prematurely` → test failed, 103 tests cancelled,
container logs and stats lost for the rest of the run.

What stalled the master is not recorded anywhere. Candidates: a Wi‑Fi transmit stall on
the Mac (its WLAN driver logs `Datapath timeout on en0` all day; RSSI −63…−66) or the ssh
mux wedging under load. macOS `sshd` logs no session open/close; the Mac holds no evidence.
**The host stays on Wi‑Fi; the harness must be resilient to periodic multi-second stalls.**

## Root cause

The transport layer is a stack of reactive heals, each added after an incident, each
inferring the state of the layer below and swallowing what it cannot handle:

canary streak → master respawn → `ServerContainer.ReopenApiForwardAsync` →
`ForwardHealingHandler` request retry → `ManagedServer` watchdog heal → host poison.

Measured in `tests/` (`.cs`): 88 empty or comment-only `catch` blocks, 215 bare
`catch`/`catch (Exception)`, 58 "best-effort" comments, 32 files with retry/streak
loops, 42 boolean-only outcome fields in events (`healed = …`, `gone = …`).

Consequences seen in this run:

- `TransportFaultClassifier.LooksLikeBrokenConnection` matches exception *message
  substrings*; `ResponseEnded` matched nothing, so the fault the harness itself caused was
  treated as application-level and not retried.
- `ContainerLogStreamReader` returns silently on EOF or after three open failures; the
  reason goes to a stdout callback, never to disk.
- `ContainerStatsCollector` opens the Docker stats stream once inside `catch { }`; the
  game-stats URL is a string captured at `Register` and is stale after any forward reopen.
- Two `ForwardHealingHandler` instances (the test's request and the watchdog's probe)
  re-opened the same forward 17 times in 52 s, each disposing the other's fresh port.
- The one shared mux is a single point of failure with a blast radius of the whole host,
  and the only remedy (kill it) is itself the most destructive action available.

## Fix

### Principles (apply to every sibling plan)

1. **Fail loud or record why — never both silent and graceful.** A catch block either
   rethrows, fails the run, or emits a structured event carrying `exceptionType`,
   `message`, `reason`, and the decision taken. Empty catches are forbidden in `tests/`.
2. **Events carry observations and reasons, not booleans.** `gone: true` becomes
   `termination: exit_ok | killed | socket_gone | unconfirmed`, plus `exitCode`,
   `elapsedMs`, and the triggering exception.
3. **Classify by typed signals only** (`SocketErrorCode`, `HttpRequestError`,
   exception type). Unknown → `forwardScoped: false` plus an emitted
   `transport_fault_unclassified` event; never host-scoped (host-scoped poisons the host,
   and an unclassified application exception must not cascade) and never silent.
4. **Ask owned state before inferring.** The harness owns the forward and the master; a
   "is my listener bound / does `-O check` answer / did I just kill it" check outranks any
   exception-shape heuristic.
5. **One healer per resource.** A forward, a stream, a master each have exactly one
   supervisor that may re-establish it; every other consumer observes and retries against
   the supervisor's current state.
6. **Idempotency is explicit.** A transport retry of a non-idempotent request
   (`POST /newgame`) is either declared safe at the call site or not retried.
7. **Two processes, one truth.** The runner (`TestRunner/Program.cs`, 10 s poll →
   `TunnelManager.EnsureOwnedMasterHealthyAsync`) owns and kills the master; the xUnit
   child only adopts it and never performs a transport action. Every design that
   attributes a fault to an action (`LastTransportAction`), scopes a budget to "the
   supervisor's re-establish window", or stamps an incident id on a test therefore needs
   the runner to publish the current incident/action record to the child. Channel: a
   run-dir state file `diagnostics/transport-state.json` (incident id, action, timestamps,
   window end) written by the runner and read by the child on demand; env is startup-only
   and cannot carry it. Built here as a prerequisite for the classification and
   log-reader siblings.
8. **Exhausted transport = skip, not fail.** `ForwardHealingHandler` already converts an
   exhausted heal into `InfrastructureSkipException`; keep that. "Fail as
   `infrastructure`" below means the test is reported skipped with
   `failureCategory: infrastructure` and the incident id, not a red test.

### Transport shape

Keep the ssh mux (no LAN cable; Wi‑Fi is the path) but shrink what depends on it:

- **Direct-published API ports.** Server/client API ports are Docker-published on the
  Mac; the coordinator is on the same LAN. If OrbStack binds published ports on the host
  interface (verify with `lsof -iTCP:<mapped_port>` on the Mac), the coordinator reaches
  `mac.fritz.box:<mapped_port>` directly and the per-server `-L` forwards (the
  `ServerContainer` and `GameClientContainer.HealApiForwardAsync` paths), their reopen and
  heal machinery, and the watchdog heal are deleted. `ServerContainer.BaseUrl`
  (`http://localhost:{ApiPort}`) becomes host-aware. Wi‑Fi stalls then affect one
  request at a time, not one mux for everything. Decision needed: accept LAN-visible API
  ports on the test host (they are already reachable on the Mac's loopback and the LAN
  is private).
- **Daemon socket stays tunneled** but becomes the only tunneled resource.
- **Master loss is an incident, not a heal.** When the master must be replaced, record a
  structured incident (trigger observation, canary results, TCP state, action, exit code,
  duration) and re-establish once through one supervisor. In-flight tests fail with
  `failureCategory: infrastructure` carrying the incident id; streams re-establish
  through their supervisor or the run aborts with the incident as `abortReason`.
- **Stall tolerance before kill.** A Wi‑Fi stall of a few seconds must ride out: the
  canary wedge threshold and the in-place reopen escalation are tuned against measured
  stall durations (collect them via the diagnostics plan first), and killing a live master
  requires the daemon-socket forward itself to be unusable, not just slow.

### Enforcement

- A meta-test in `tests/JunimoServer.Tests` that fails on empty or comment-only `catch`
  bodies under `tests/`. It must match the multi-line form (`catch` / `{ /* best-effort
  */ }`, as in `ManagedServer.HealthWatchdogLoop`), not only `catch\s*\{\s*\}`. The
  ~82-site sweep that makes it pass is step 6 below; no sibling plan owns it otherwise.
- Event schema types in `Schema/Json` for transport events with required `reason`
  fields; `InfrastructureEventLog.Emit` sites for these events use the typed records.

### Order

1. `tests-transport-incident-diagnostics.md` — first, so the next stall is explained.
2. `tests-transport-fault-typed-classification.md`.
3. `tests-log-stream-reader-stops-silently.md`, `tests-stats-collector-never-reconnects.md`.
4. Direct-published-port verification; if it holds, `tests-api-forward-heal-ping-pong.md`
   becomes a deletion instead of a fix.
5. Master-loss incident model and stall-tolerance tuning, using data from step 1.
6. Empty-catch sweep + meta-test.

`tests-api-forward-heal-ping-pong.md` is held until step 1's per-attempt events confirm its
mechanism (its diagnosis does not fit the dedupe code; see that plan).

## Verification

- A forced stall (`kill -STOP` the Mac's `sshd-session` pid, then `-CONT`) rides out with
  no master kill and no test failure. Its length is the p99 of step 1's measured Wi‑Fi
  stalls, not a fixed 5 s: 5 s already rides out by construction (10 s poll × two
  consecutive `Wedged` canaries, each up to 2 s connect + 5 s I/O ⇒ a kill needs a
  ≥10–17 s stall), so it discriminates nothing.
- A forced master death (`kill -9` the ssh master) produces exactly one incident event
  with a reason, in-flight tests fail as `infrastructure` with that incident id, and every
  `container.log` and stats stream resumes within the supervisor's re-establish window.
- The empty-catch meta-test passes (single-line and multi-line comment-only bodies).
