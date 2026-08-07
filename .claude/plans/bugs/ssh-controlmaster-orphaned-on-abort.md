# Fix: SSH ControlMaster teardown is neither terminal nor reachable — reap it reliably

## Scope

The `-f`-forked `ssh -M` ControlMaster (`TunnelManager`) is the only long-lived OS process the harness
creates that can outlive the run. Three independent defects let it survive; this plan fixes each at its
own layer. Local-only fleets spawn no master (`HostPool.cs:487-488`) and are unaffected.

**Observed:** a master from a 2026-08-01 run was still connected to the remote host 5 days later,
holding an open `-E` handle into that run's (since-deleted) artifact directory.

## Three defects

### D1 — Teardown is not terminal, so even a clean run can leak

`ExitMasterAsync` (`TunnelManager.cs:1380-1405`) runs `ssh -O exit` bounded by `perCancelTimeout`
(**2 s**, from `Program.cs:1071` and `DisposeAsync` at `:1448`), records the exit code, and then
**unconditionally unlinks the socket** — with no PID-kill fallback:

```
(exit, stderr) = await RunSshOpAsync(psi, perCancelTimeout, …);   // may time out / fail
TryDeleteFile(master.ControlPath);                                 // runs regardless
```

The codebase already knows `-O exit` is not reliable: *"a wedged master survives `-O exit` — its mux is
the broken part"* (`TunnelManager.cs:641`), which is exactly why `TryRespawnMasterAsync` follows it with
`TryKillMasterProcess`. `ExitMasterAsync` never got the same treatment. So a wedged master — or merely a
busy one exceeding the 2 s budget — survives a **clean** run, and unlinking the socket strips the only
remaining handle, leaving a PID-only orphan.

This is the defect that makes the leak a general reliability bug rather than an abort-path bug.

### D2 — Teardown is unreachable from every abort path

| Step | Evidence |
|---|---|
| Master is spawned `-M -N -f` → detached, not a child of the coordinator | `TunnelManager.cs:252-254`; the comment at `:244-249` says it outright: *"Don't try to track the master via this handle … Reach the master only via ControlPath"* |
| `KillTestChildren()` reaches only the **xUnit child's** process tree | `Program.cs:417-427` |
| The only reaper is `DrainAsync` → `ExitMasterAsync` | `TunnelManager.cs:1269-1302` |
| …reachable **only** from the graceful unwind | `Program.cs:1071`; `await using` at `Program.cs:75` → `DisposeAsync` (`:1446-1449`) |
| Every abort path calls `Environment.Exit`, which does not unwind the stack | Ctrl+C `Program.cs:253`; abort `Program.cs:413` |
| `Environment.Exit` runs `AppDomain.ProcessExit` → `EmergencyCleanup.RunAll()` — but **`TunnelManager` is registered there nowhere** | `EmergencyCleanup.cs:373-377`; only drainable is `infrastructure-event-log` (`Program.cs:41`, `TestResourceBroker.cs:401`); `Register` actions are containers, game clients, summary fixture |

**Ctrl+C on any run that used a remote host leaks the master. No crash required.**

**Ordering constraint (why the obvious fix is wrong).** `RunAll()` runs drainables → actions →
`BulkCleanupLabeledResources()` (`EmergencyCleanup.cs:130-180`), and that bulk sweep removes remote
containers **through the tunnel** — it enumerates per-host Docker clients and applies a 3 s per-call
timeout to `ssh://` hosts precisely because *"a hung SSH master must not block process exit"*
(`EmergencyCleanup.cs:193-195`, `:206-211`). Registering tunnel teardown as a *drainable* would run it
**first** and strand every remote container: an ssh leak traded for a container leak. Tunnel teardown
must run **last**.

### D3 — Nothing recovers an orphan across runs, and the existing sweep makes it worse

`CleanupStaleControlSockets` (`TunnelManager.cs:1786-1827`, called at `HostPool.cs:497` with
`maxAge = 1h`) deletes stale `sdvd-test-ssh-*` **files** and never touches a process. Because
`ComputeControlPath` hashes `hostId|runId|pid` (`:1762-1778`), a leaked master's path is unique to its
run, so no later run adopts it — and once the sweep unlinks the socket, `ssh -O exit` can no longer
reach the orphan at all. **The existing hygiene converts a reapable orphan into a PID-only one.**

No in-process hook covers `kill -9`, a BSOD, or a power cut, so a cross-run reaper is required regardless.

### Not a backstop: `ControlPersist=10m`

`TunnelManager.cs:260` sets `ControlPersist=10m`, but OpenSSH's `set_control_persist_exit_time()`
**cancels** the timer (sets it to `0`) whenever `channel_still_open()` is true — it does not pause it.
`channel_still_open()` (`channels.c`) counts `SSH_CHANNEL_OPEN` as open while `PORT_LISTENER` and
`MUX_LISTENER` fall through to `continue`. Idle `-L` listeners do not hold the master; **one established
forwarded connection that never closes pins it forever**.

*Unverified:* which channel pinned the observed orphan. The likely candidate is a container log/stats
follow-stream to the remote daemon, but that master's `-E` log lived in a worktree that has since been
deleted. The OpenSSH rule is verified from source; the specific channel is not.

## Fixes

### F1 — Make master teardown terminal (fixes D1, and is the primitive F2/F3 reuse)

Extract the two-tier teardown that `TryRespawnMasterAsync` already performs into one method both callers
use — `-O exit` first, **PID kill as the guarantee**, unlink last:

1. `ssh -O exit`, bounded.
2. If the master is still alive (or `-O exit` failed/timed out) → `TryKillMasterProcess`.
3. Unlink the socket **only after** the process is confirmed gone; if it survives, unlinking loses the
   handle, so keep the socket and emit the failure loudly.

Reuse the existing three-guard identity check in `TryKillMasterProcess` (`TunnelManager.cs:747-804`:
process name `ssh`, start time within 120 s of `SpawnedAtUtc`, binary path matches `_sshPath`) so a
recycled PID is never killed.

Step 3 inverts today's unconditional `TryDeleteFile`. That delete exists to stop the next run tripping
the *"ControlSocket … already exists, disabling multiplexing"* trap (`TunnelManager.cs:130-135`) — with
F3's reaper running at preflight, a retained socket is now an asset (a live handle to reap through)
rather than debris, and the per-run path uniqueness means it can never collide with the next run anyway.

### F2 — Make teardown reachable from abort paths (fixes D2)

Add a third phase to `EmergencyCleanup` that runs **after** the bulk sweep, so transport teardown cannot
strand the cleanup that depends on it. `RunAll()`'s ordering becomes a stated contract:

1. Drainables — flush sinks to disk
2. Actions — per-resource cleanup (containers, game clients)
3. Bulk sweep — labeled Docker resources (**needs the tunnel**)
4. **Finalizers — transport teardown** ← new

```
public static void RegisterFinalizer(string name, Action finalize)
```

Snapshot-and-clear like the existing registries so `RunAll()` stays idempotent. The finalizer calls F1's
primitive; because F1 leads with a PID kill when `-O exit` is unavailable, it needs no subprocess in the
common case.

**Two different budgets — do not conflate them:**

| Trigger | Budget | Implication |
|---|---|---|
| Ctrl+C handler calls `RunAll()` directly (`Program.cs:252`) | No hard cap (the handler already waits 15 s for graceful drain at `:240-242`) | `-O exit` then kill is affordable |
| `AppDomain.ProcessExit` → `RunAll()` (`EmergencyCleanup.cs:373-377`) | **~2 s total** (`LocalGameClientProvider.cs:431-432`) | Must be PID-kill-first; bound the whole phase, not per host |

Bound the finalizer phase **in aggregate**, not per item — a 3-host fleet must not blow the ProcessExit
budget.

### F3 — Cross-run orphan reaper backed by a journal (fixes D3)

This mirrors an established in-tree pattern: Docker resources are labeled `sdvd.run-id` and reaped at the
*next* run's startup by `EmergencyCleanup.SweepStaleResourcesAsync` (`Program.cs:732-736`). SSH masters
get the same treatment.

- **Write** `sdvd-test-ssh-registry-<coordinatorPid>.json` in the temp dir when a master is registered —
  `{hostId, controlPath, masterPid, spawnedAtUtc, sshPath}` per entry, plus the coordinator's own
  `processStartTimeUtc`. `RegisterHostMasterAsync` is the single write point: it is the *only* place
  `Owned = true` is set (`:218`) and both creation paths funnel through it — preflight (`HostPool.cs:516`)
  and respawn (`TunnelManager.cs:714`) — so a respawned master updates the journal for free.
- **Delete** it once every master is confirmed gone.
- **Reap at preflight**: for each journal whose coordinator is dead, reap the listed masters via F1's
  primitive, then delete the journal.

**Liveness check must be PID + start time.** Testing only "is the coordinator PID alive?" fails open under
PID recycling — a recycled PID reads as alive and the orphan is never reaped. Compare the recorded
`processStartTimeUtc` too.

**Concurrency.** Preflight may register hosts concurrently; the journal write needs a lock plus
write-temp-then-rename so a torn file can never be parsed.

**Sibling safety.** The reaper must never touch a live sibling coordinator's master — coordinator
PID+start-time liveness is the gate, mirroring why the bulk Docker sweep is run-id-scoped
(`EmergencyCleanup.cs:182-186`).

### F4 — Fix the sweep, correct the stale comments

- `CleanupStaleControlSockets` becomes reap-then-unlink, never unlink-first. It is largely subsumed by
  F3; retain the bare-socket glob as the fallback for sockets whose journal was lost.
- `Program.cs:248`, `:310-311`, `:420` describe *"per-container `ssh -N -L` forwards"* as grandchildren
  killed by `Kill(entireProcessTree: true)`. Forwards are `ssh -O forward` **mux calls against the
  master** (`TunnelManager.cs:14`, `:25-26`, `:936-937`) and spawn no persistent process. The comments
  assert an ssh cleanup that does not exist — the master is the only ssh process, and it is the one
  nothing kills.
- `TunnelManager.cs:260` — state what `ControlPersist` actually does (cancelled by any open channel, so
  opportunistic, not a safety net) so nobody re-relies on it.

## Implementation order

1. **F1** first — it is the primitive F2 and F3 both call. Landing it alone already fixes clean-run leaks.
2. **F2** — depends on F1's primitive existing.
3. **F3** — depends on F1; independently testable via a killed coordinator.
4. **F4** — comment/sweep cleanup, no dependency.

**Cross-step check.** F1 step 3 stops unlinking a socket whose master survived, which changes the
precondition F3's reaper relies on (a retained socket now means *"orphan still reachable"* rather than
*"debris"*) and removes the reason `RegisterHostMasterAsync`'s specific delete existed. Land F1 and F3
together, or F1's retained sockets accumulate with no reaper to clear them.

## Verification (runtime gates — static review does not satisfy these)

Per `runtime-post-conditions-are-gates.md`, run each against a remote-host `SDVD_DOCKER_HOSTS`:

1. **Ctrl+C mid-run** → no `sdvd-test-ssh-*` in temp, no surviving `ssh` holding the control path, **and
   remote containers still removed** (proves the finalizer ordering did not strand the bulk sweep).
2. **`taskkill /F` the coordinator** (no hooks run at all) → the next run's preflight reaps the orphan and
   logs it.
3. **Wedged master on a clean run** — the D1 case. Force it by `SIGSTOP`/suspending the master process
   before shutdown, then confirm teardown still ends with no live process and no orphaned socket.
4. **Clean run** → unchanged behaviour, no double-drain error, journal deleted.
5. **Concurrent sibling run** → the reaper leaves the live sibling's master alone.

Assert on process/socket state directly, not on the absence of a log line
(`passing-test-isnt-proof-the-scenario-ran.md`).

## Verified while planning — no action needed

- **The xUnit child never owns a master.** `Owned = true` is set only in `RegisterHostMasterAsync`
  (`:218`), whose only callers are `HostPool.PreflightAsync` (`:516`, parent) and `TryRespawnMasterAsync`
  (`:714`, which refuses on `!Owned`). The child adopts read-only entries and `DrainAsync` only `-O exit`s
  owned ones (`:1298`). No child-side reaper is needed.

## Non-goals

- Replacing the ControlMaster transport (e.g. the mesh-VPN migration in
  `.claude/plans/features/tests-mesh-vpn-host-transport.md`) — that removes the master entirely, but this
  bug needs fixing on the current transport regardless.
- Making forwards survive a master restart; `TryRespawnMasterAsync` already covers that.

## Open decisions

1. **`RegisterFinalizer` phase, or just call teardown directly at the end of `RunAll()`?**
   `EmergencyCleanup` already imports `JunimoServer.Tests.Infrastructure` (`:3`), so a hardcoded call is
   available and is strictly less code (`simplest-solution.md`). The registry keeps `RunAll`'s ordering
   declarative and lets the parent register only when the fleet actually has a remote host — but it is a
   new mechanism for one consumer. **Recommendation: registry**, because the phase ordering is the thing
   we most need to keep explicit and greppable. Your call.
2. **Keep `ControlPersist=10m`?** It still helps the zero-channel case and costs nothing, but it is not a
   safety net. Keep with a corrected comment, or drop it as misleading?
3. **Should the emergency path spend budget folding the `-E` master log tail** into diagnostics
   (`ReadMasterLogTail`, `TunnelManager.cs:1419-1443`)? It is the only record of a silent-timeout drop,
   but it costs file I/O inside the ~2 s `ProcessExit` window.
4. **Shared `/tmp` on Linux** — the sweep currently swallows `UnauthorizedAccessException` for other
   users' sockets (`:1819-1821`). Should the reaper stay same-user-only (safe), or attempt cross-user
   reaping (needs privileges, risks killing another user's live run)?
5. **Reaper cadence** — preflight-only, or also on the parent's existing 10 s master-health monitor
   (`Program.cs:491-513`)? Preflight-only leaks for at most the duration of one run.
