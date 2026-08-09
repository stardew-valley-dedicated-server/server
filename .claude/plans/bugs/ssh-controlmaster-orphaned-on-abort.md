# Fix: SSH ControlMaster teardown is neither terminal nor reachable — reap it reliably

## Scope

The `-f`-forked `ssh -M` ControlMaster (`TunnelManager`) is the only long-lived OS process the harness
creates that can outlive the run. Three independent defects let it survive; this plan fixes each at its
own layer. Local-only fleets spawn no master (`HostPool.cs:487-488`) and are unaffected.

**Observed:** a master from a 2026-08-01 run was still connected to the remote host 5 days later, with no
`ControlPath` socket left in the temp dir. That run's artifacts lived in a since-deleted worktree, so the
specific path it took is unrecoverable — either D2 (an abort that skipped the parent's `finally`, which
emits no `ssh_master_exited` at all) or the `maxAge = 1h` sweep unlinking the socket of an
already-orphaned process. D2 is the likelier of the two; see the D1 evidence note below.

**Observed firing #2 (2026-08-08, full artifacts retained)** — run `2026-08-08T22-57-14Z_b216eb1`
(`TestResults/runs/` in the `base-image-debian-13` worktree), the first wedged-master incident captured
end-to-end. It hardens two of this plan's premises and adds one new defect surface:

- **Mid-run wedge, not just leak-on-abort.** After 67 passed tests, every container log stream and
  Docker API connection to the `mac` host died simultaneously (all seven server logs end 23:06:47–57Z);
  6 in-flight tests failed as `SocketException` timeouts and the run aborted at 23:25 via
  `child-stall-watchdog`. The master's `-E` log (`diagnostics/ssh-master-mac.log`) is an unbroken loop
  of `channel_post_mux_listener: accept: Resource temporarily unavailable` plus
  `mux_master_process_open_fwd … bind 127.0.0.1:64629 … Address already in use` — the wedged master
  still *held* the daemon-forward listener (netstat: LISTEN owned by the master's PID, hours later)
  while refusing to serve it, so every same-port restore attempt self-collided. This is exactly the
  state the respawn comment describes ("a wedged master survives `-O exit` — its mux is the broken
  part"), and **the `TryRespawnMasterAsync` hard-reset demonstrably never recovered it** — the same
  master process was still alive 40+ minutes after the abort. F1's kill-as-guarantee is not just for
  teardown; the mid-run respawn path needs the same verification that the old process is actually gone.
- **The parent survived its own abort as a zombie.** The coordinator (`JunimoServer.TestRunner`) wrote
  the aborted summary at 23:25 and then never exited — found alive with its wedged master 40+ minutes
  later, holding both the forward port and the `bin/` DLL locks (which fail the next `make test` build
  with MSB3027; second such zombie in two days). So there is an abort path that reaches neither the
  parent's `finally` nor `Environment.Exit` at all — likely wedged in post-abort cleanup over the dead
  tunnel. D2's table assumes every abort path at least reaches `Environment.Exit`; this one didn't.
  Worth a targeted look at what the parent blocks on after `child-stall-watchdog` (a dump of the zombie
  next time, before killing it).

## Three defects

### D1 — Teardown is not terminal, so even a clean run can leak (latent; no observed firing)

`ExitMasterAsync` (`TunnelManager.cs:1380-1405`) runs `ssh -O exit` bounded by `perCancelTimeout`
(**2 s**, from `Program.cs:1071` and `DisposeAsync` at `:1448`), records the exit code, and then
**unconditionally unlinks the socket** — with no PID-kill fallback:

```
(exit, stderr) = await RunSshOpAsync(psi, perCancelTimeout, …);   // may time out / fail
TryDeleteFile(master.ControlPath);                                 // runs regardless
```

The codebase already knows `-O exit` is unreliable: *"a wedged master survives `-O exit` — its mux is the
broken part"* (`TunnelManager.cs:641`), which is why `TryRespawnMasterAsync` follows it with
`TryKillMasterProcess`. `ExitMasterAsync` never got the same treatment.

So a wedged master — or merely a busy one exceeding the 2 s budget — survives a **fully clean run**, and
the unlink then strips the only remaining handle, leaving a PID-only orphan.

**Evidence check: this gap is real in code but has not been observed firing.** Every retained
`ssh_master_exited` event across the run artifacts (8 runs, 2026-07-11/12) reports `exitCode: 0` with
`durationMs` between 67 and 127 — `-O exit` succeeding an order of magnitude inside the 2 s budget. So D1
is a latent robustness gap, not a demonstrated cause. Fix it because it is cheap and the failure is
silent-and-permanent when it does fire, not because it is the current bleeding wound — that is D2.

### D2 — Teardown is unreachable from the abort paths that skip the parent's `finally`

The master is unreachable by every process-tree mechanism:

| Fact | Evidence |
|---|---|
| Spawned `-M -N -f` → detached, not a child of the coordinator | `TunnelManager.cs:252-254`; the comment at `:244-249` says it outright: *"Don't try to track the master via this handle … Reach the master only via ControlPath"* |
| `KillTestChildren()` reaches only the **xUnit child's** process tree | `Program.cs:417-427` |
| The only reaper is `DrainAsync` → `ExitMasterAsync`, called from the parent's `finally` | `TunnelManager.cs:1269-1302`; `Program.cs:1071`; `await using` at `:75` → `DisposeAsync` (`:1446-1449`) |
| `Environment.Exit` does not unwind the stack, so that `finally` never runs | — |
| It *does* run `AppDomain.ProcessExit` → `EmergencyCleanup.RunAll()` — but **`TunnelManager` is registered there nowhere** | `EmergencyCleanup.cs:373-377`; only drainable is `infrastructure-event-log` (`Program.cs:41`, `TestResourceBroker.cs:401`); `Register` actions are containers, game clients, summary fixture |

**Which paths actually leak.** Not every abort. `ShutdownCoordinator.SignalGracefulComplete()` fires at
`Program.cs:1129` — *after* `tunnelManager.DrainAsync` at `:1071` — so a **first Ctrl+C whose graceful
drain finishes inside the 15 s window does tear the master down** (subject to D1). The leaking paths are
those that reach `Environment.Exit` without the parent's `finally` having completed:

| Path | Site | Why it skips teardown |
|---|---|---|
| **UI Stop** | `:284-294` | Documented "nuke" button — explicitly bypasses the graceful chain |
| **Second Ctrl+C** | `:261-272` | *"Skips the graceful window — the operator asked twice"* |
| **First Ctrl+C, drain stalls > 15 s** | `:240-242` | `WaitForGraceful` times out, then `RunAll()` + `Exit(130)` |
| **Force-exit / early aborts** | `:411-414` | `finally { Environment.Exit(130); }` |
| **Hard kill, crash, power loss** | — | No managed code runs at all (D3's domain) |

### D3 — Nothing recovers an orphan across runs, and the existing sweep makes it worse

`CleanupStaleControlSockets` (`TunnelManager.cs:1786-1827`, called at `HostPool.cs:497` with
`maxAge = 1h`) deletes stale `sdvd-test-ssh-*` **files** and never touches a process. Because
`ComputeControlPath` hashes `hostId|runId|pid` (`:1762-1778`), a leaked master's path is unique to its
run, so no later run adopts it — and once the sweep unlinks the socket, `ssh -O exit` can no longer reach
the orphan at all. **The existing hygiene converts a reapable orphan into a PID-only one** — the same
end state D1 produces.

No in-process hook covers a hard kill, a BSOD, or a power cut, so a cross-run reaper is required
regardless of D1/D2.

### Not a backstop: `ControlPersist=10m`

`TunnelManager.cs:260` sets `ControlPersist=10m`, but OpenSSH's `set_control_persist_exit_time()`
**cancels** the timer (sets it to `0`) whenever `channel_still_open()` is true — it does not pause it.
`channel_still_open()` (`channels.c`) counts `SSH_CHANNEL_OPEN` as open while `PORT_LISTENER` and
`MUX_LISTENER` fall through to `continue`. Idle `-L` listeners do not hold the master; **one established
forwarded connection that never closes pins it forever**.

*Unverified:* which channel pinned the observed orphan. The likely candidate is a container log/stats
follow-stream to the remote daemon, but that master's `-E` log lived in a worktree since deleted. The
OpenSSH rule is verified from source; the specific channel is not.

## Fixes

### F1 — Make master teardown terminal (fixes D1; the primitive F2/F3 reuse)

Extract the two-tier teardown `TryRespawnMasterAsync` already performs into one method both callers use —
`-O exit` first, **PID kill as the guarantee**, unlink last:

1. `ssh -O exit`, bounded.
2. If the master is still alive (or `-O exit` failed/timed out) → `TryKillMasterProcess`.
3. Unlink the socket **only after** the process is confirmed gone. If it survives, keep the socket (it is
   the only remaining handle) and emit the failure loudly.

Reuse the existing three-guard identity check in `TryKillMasterProcess` (`TunnelManager.cs:747-804`:
process name `ssh`, start time within 120 s of `SpawnedAtUtc`, binary path matches `_sshPath`) so a
recycled PID is never killed.

**Do not remove `RegisterHostMasterAsync`'s specific delete** (`TunnelManager.cs:130-135`). A respawn
runs in the *same* process with the same `hostId|runId|pid`, so it recomputes the **identical**
ControlPath — that delete is what stops the respawn tripping the *"ControlSocket … already exists,
disabling multiplexing"* trap. It is unrelated to cross-run collisions, which the runId term already
rules out.

### F2 — Make teardown reachable from the abort paths (fixes D2)

Call F1's teardown primitive as the **last statement** of `EmergencyCleanup.RunAll()`, after
`BulkCleanupLabeledResources()`. No new registry — `EmergencyCleanup` already imports
`JunimoServer.Tests.Infrastructure` (`:3`), and a single hardcoded call is the whole fix.

`RunAll()`'s ordering:

1. Drainables — flush sinks to disk
2. Actions — per-resource cleanup (containers, game clients)
3. Bulk sweep — labeled Docker resources (**needs the tunnel**)
4. **Transport teardown** ← new, and must stay last

**Why it must be last** — this is the comment the call site carries. `BulkCleanupLabeledResources`
removes remote containers *through the tunnel*: it enumerates per-host Docker clients and applies a 3 s
per-call timeout to `ssh://` hosts precisely because *"a hung SSH master must not block process exit"*
(`EmergencyCleanup.cs:193-195`, `:206-211`). Tearing the transport down any earlier strands every remote
container — an ssh leak traded for a container leak.

Teardown must no-op cleanly when no master exists (local-only fleets, or a second `RunAll()`), so
`RunAll()` stays idempotent.

**Two different budgets — do not conflate them:**

| Trigger | Budget | Implication |
|---|---|---|
| Ctrl+C / UI Stop call `RunAll()` directly (`:252`, `:270`) | No hard cap | `-O exit` then kill is affordable |
| `AppDomain.ProcessExit` → `RunAll()` (`EmergencyCleanup.cs:373-377`) | ~2 s total | PID-kill-first; bound the whole phase, not per host |

The ~2 s figure is an inherited in-tree assumption (`LocalGameClientProvider.cs:431-432`), not
independently verified here. PID-kill-first is the right design either way, so nothing rests on it —
but confirm before treating it as a hard number.

### F3 — Cross-run orphan reaper backed by a journal (fixes D3)

This mirrors an established in-tree pattern: Docker resources are labeled `sdvd.run-id` and reaped at the
*next* run's startup by `EmergencyCleanup.SweepStaleResourcesAsync` (`Program.cs:732-736`). SSH masters
get the same treatment.

- **Write** a journal in the temp dir when a master is registered — `{hostId, controlPath, masterPid,
  spawnedAtUtc, sshPath}` per entry, plus the coordinator's own PID and `processStartTimeUtc`.
  `RegisterHostMasterAsync` is the single write point: it is the *only* place `Owned = true` is set
  (`:218`) and both creation paths funnel through it — preflight (`HostPool.cs:516`) and respawn
  (`TunnelManager.cs:714`) — so a respawned master updates the journal for free.
- **Delete** it once every master is confirmed gone.
- **Reap at preflight**: for each journal whose coordinator is dead, reap the listed masters via F1's
  primitive, then delete the journal.

**Name it outside the `sdvd-test-ssh-*` namespace** (e.g. `sdvd-ssh-journal-<pid>.json`).
`CleanupStaleControlSockets` globs `sdvd-test-ssh-*` and deletes by age (`:1793`, `:1811-1817`) — a
journal under that prefix would be deleted by the very sweep it exists to replace.

**Liveness must be PID + start time.** Testing only "is the coordinator PID alive?" fails *open* under PID
recycling — a recycled PID reads as alive and the orphan is never reaped.

**Concurrency.** Preflight may register hosts concurrently; the journal write needs a lock plus
write-temp-then-rename so a torn file can never be parsed.

**Sibling safety.** The reaper must never touch a live sibling coordinator's master — coordinator
PID+start-time liveness is the gate, mirroring why the bulk Docker sweep is run-id-scoped
(`EmergencyCleanup.cs:182-186`).

### F4 — Fix the sweep, correct the stale comments

- `CleanupStaleControlSockets` becomes reap-then-unlink, never unlink-first. Largely subsumed by F3;
  retain the bare-socket glob as the fallback for sockets whose journal was lost.
- `Program.cs:248`, `:310-311`, `:420` describe *"per-container `ssh -N -L` forwards"* as grandchildren
  killed by `Kill(entireProcessTree: true)`. Forwards are `ssh -O forward` **mux calls against the
  master** (`TunnelManager.cs:14`, `:25-26`, `:936-937`) and spawn no persistent process. The comments
  assert an ssh cleanup that does not exist — the master is the only ssh process, and it is the one
  nothing kills.
- `TunnelManager.cs:260` — state what `ControlPersist` actually does (cancelled by any open channel, so
  opportunistic, not a safety net) so nobody re-relies on it.

## Implementation order

1. **F1** — the primitive F2 and F3 both call. Cheap, but latent (see the D1 evidence note): sequence it
   first because F2/F3 depend on it, not because it is the urgent one.
2. **F2** — the defect with real exposure today (UI Stop is a first-class button, not an edge case).
3. **F3** — depends on F1; independently testable via a killed coordinator.
4. **F4** — comment/sweep cleanup, no dependency.

**Cross-step check.** F1 step 3 stops unlinking a socket whose master survived, so a retained socket
changes meaning from *"debris"* to *"orphan still reachable"*. F3's reaper is what consumes that new
meaning — land F1 and F3 together, or retained sockets accumulate with nothing to clear them. F1 does
**not** affect `RegisterHostMasterAsync`'s same-process respawn delete (see F1).

## Verification (runtime gates — static review does not satisfy these)

Per `runtime-post-conditions-are-gates.md`, run each against a remote-host `SDVD_DOCKER_HOSTS`:

1. **UI Stop mid-run** (the most direct D2 path) → no `sdvd-test-ssh-*` in temp, no surviving `ssh`
   holding the control path, **and remote containers still removed** (proves the finalizer ordering did
   not strand the bulk sweep).
2. **Second Ctrl+C** → same assertions.
3. **`taskkill /F` the coordinator** (no managed code runs) → the next run's preflight reaps the orphan
   and logs it.
4. **Wedged master on a clean run** — the D1 case, and the one that needs a deliberate repro. There is no
   `SIGSTOP` on Windows; suspend the Cygwin `ssh` process via a native suspend tool, or inject a test-only
   fault that makes `-O exit` return non-zero. Confirm teardown still ends with no live process and no
   orphaned socket. *Pick the mechanism before implementing — this gate is the plan's weakest link.*
5. **Clean run** → unchanged behaviour, no double-drain error, journal deleted.
6. **Concurrent sibling run** → the reaper leaves the live sibling's master alone.

Assert on process/socket state directly, not on the absence of a log line
(`passing-test-isnt-proof-the-scenario-ran.md`).

## Verified while planning — no action needed

- **The xUnit child never owns a master.** `Owned = true` is set only in `RegisterHostMasterAsync`
  (`:218`), whose only callers are `HostPool.PreflightAsync` (`:516`, parent) and `TryRespawnMasterAsync`
  (`:714`, which refuses on `!Owned`). The child adopts read-only entries and `DrainAsync` only `-O exit`s
  owned ones (`:1298`). No child-side reaper is needed.
- **A first Ctrl+C with a healthy drain already tears the master down** — `SignalGracefulComplete()`
  (`:1129`) fires after `DrainAsync` (`:1071`). D2 is narrower than "every abort"; D1 still applies here.

## Non-goals

- Replacing the ControlMaster transport (the mesh-VPN migration in
  `.claude/plans/features/tests-mesh-vpn-host-transport.md` removes the master entirely) — this bug needs
  fixing on the current transport regardless.
- Making forwards survive a master restart; `TryRespawnMasterAsync` already covers that.

## Open decisions

1. **Keep `ControlPersist=10m`?** It still helps the zero-channel case and costs nothing, but it is not a
   safety net. Keep with a corrected comment, or drop it as misleading?
2. **Should the emergency path spend budget folding the `-E` master log tail** into diagnostics
   (`ReadMasterLogTail`, `TunnelManager.cs:1419-1443`)? It is the only record of a silent-timeout drop,
   but it costs file I/O inside the `ProcessExit` window.
3. **Shared `/tmp` on Linux** — the sweep currently swallows `UnauthorizedAccessException` for other
   users' sockets (`:1819-1821`). Should the reaper stay same-user-only (safe), or attempt cross-user
   reaping (needs privileges, risks killing another user's live run)?
4. **Reaper cadence** — preflight-only, or also on the parent's existing 10 s master-health monitor
   (`Program.cs:491-513`)? Preflight-only leaks for at most the duration of one run.

*Decided: teardown is a direct call at the end of `RunAll()`, no new registry (F2).*
