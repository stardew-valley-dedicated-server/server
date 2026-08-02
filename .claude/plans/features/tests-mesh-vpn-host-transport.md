# Plan: Replace SSH multiplexing with mesh-VPN direct transport for remote hosts

## Status

Direction decided (2026-08-01): migrate. Not started. Open decisions at the bottom
need a call before Phase 1.

## Context

Remote Docker hosts are reached through one `ssh -M` ControlMaster per host
(`tests/JunimoServer.Tests/Infrastructure/TunnelManager.cs`): the daemon socket and
every server/client API port ride `-L` forwards as channels multiplexed over a single
TCP session, terminated by a single Cygwin `ssh.exe` (Git for Windows; the Windows
OpenSSH port is rejected at preflight, `SshBinaryResolver`). Local hosts bypass all of
this — the coordinator dials `127.0.0.1:mappedPort` directly (`TunnelManager.OpenAsync`
pass-through).

Run `2026-08-01T18-27-56Z_3348fc9` demonstrated the architecture's failure class: the
master wedged half-alive at peak concurrent-connection count (established channels kept
flowing; every NEW connection was accepted into the kernel backlog and never serviced).
Hang-mode faults are invisible to the transport classifier (built for fail-fast
refused/RST), so all three live servers were poisoned as "unhealthy" while perfectly
fine, Docker API access died, and the stall watchdog aborted the run (104 passed → 55
canceled). A recovery layer now exists (owner-side data-path canary, old-master kill,
same-port forward re-open, watchdog probe-timeout heal — added 2026-08-01), but it is
repair machinery for a layer whose entire job is simulating what a network provides
natively. The inner-most trigger (suspected: Cygwin poll/select emulation degrading at
peak fd count) is not provable from run artifacts and lives in a binary we don't
control.

Goal (explicit requirement): **one transport model everywhere** — local dev, the Mac,
CI — no remote-only special path. A mesh VPN achieves that by *deleting* the second
path rather than adding a third: meshed hosts are dialed `host-ip:mappedPort`, i.e.
the existing local code path plus an IP. (Precisely: the local daemon keeps its
npipe/unix endpoint — `DockerEndpointConfig`'s npipe timeout handling stays.
"One model" means the remote-only *forwarding layer* ceases to exist, not that every
host literally shares one endpoint scheme.)

## Why mesh over fixing SSH in place

- **Wedge class becomes structurally impossible** — no userspace listener/accept path
  in the middle; per-packet, connection-agnostic forwarding.
- **Truthful failures** — connect refused/timeout means the container/daemon/host is
  actually down. The forward-scoped fault ontology (classifier branch, healing handler,
  reopen/respawn/canary, master monitor) stops existing instead of being maintained.
- **Performance on the test-critical path** — coordinator↔container traffic is
  poll/long-poll heavy (`/wait/*`, 5s health probes, snapshot reads). Today each fresh
  connection pays an SSH channel-open through the mux, and all traffic shares one TCP
  stream (head-of-line: an image push competes with every probe). Mesh gives each
  connection its own flow.
- **Throughput** — the 654 MB image push measured ~13 MB/s through the master
  (single-threaded userspace crypto ceiling); direct TCP over LAN/WireGuard should be
  several-fold faster.
- **Net-negative diff** — see deletion inventory below (~2k+ lines whose only purpose
  is remote-host plumbing).

**Alternative considered — SSH.NET (in-process forwarding):** kills the Cygwin/mux
layer and the parent/child owner-asymmetry, zero host-side setup change, drop-in
behind `TunnelManager`'s interface. Rejected as the endgame because it keeps the
multiplexing architecture (single-session SPOF, channel churn, HOL) and therefore the
entire heal machinery alive forever. Remains the fallback if the mesh is vetoed
operationally.

## Relationship to existing plans

- [`tests-local-ci-runners.md`](tests-local-ci-runners.md) already puts the GitHub
  runner and the Mac on a tailnet, but keeps SSH as the data plane over it
  ("vendor-agnostic SSH-over-IP"). This plan supersedes that data plane: once the
  runner is on the mesh anyway, the harness dials `tcp://` directly and the SSH hop
  disappears. Its enrollment design (ephemeral auth keys, Headscale as the
  no-rearchitecture exit) carries over unchanged.
- [`tests-live-web-ui-remote-access.md`](tests-live-web-ui-remote-access.md) chose
  reverse-SSH riding the ControlMaster as the carrier. Post-migration that carrier no
  longer exists — and the mesh solves the same problem more directly (open
  `http://<runner-mesh-ip>:<webPort>` from any mesh device). Revise that plan when this
  one lands.
- [`../investigate-cut-test-wallclock-burst-fleet.md`](../investigate-cut-test-wallclock-burst-fleet.md)
  (burst VPS fleet, spin-up-per-run): its follow-up Terraform build must provision
  hosts as mesh nodes — cloud-init joins the tailnet with an ephemeral auth key and
  exposes dockerd on the tailnet interface, instead of installing an ssh key. Same
  enrollment mechanism as the CI runner. If the fleet lands before this plan, it lands
  on `ssh://` and migrates with everything else.
- [`tests-parallelize-remote-setup.md`](tests-parallelize-remote-setup.md) survives
  structurally (its fan-out is transport-agnostic; "preflight opens the SSH forwards"
  shrinks to "preflight materializes `host.ApiClient`"), but its payoff shrinks as
  mesh throughput rises — re-measure the overlap win before implementing it.

## Sub-choice: Tailscale vs raw WireGuard

Tailscale (with the Headscale exit already documented in `tests-local-ci-runners.md`)
for fleet coherence: ephemeral CI runners need enrollment-on-demand (auth keys), which
raw WireGuard's static peer configs handle poorly. Raw WireGuard stays viable only in a
fixed-fleet world with no ephemeral runners. Decide explicitly before Phase 1.

## Migration

**Phase 0 — root-cause evidence (optional, decide explicitly):** a
`tools/.playground/` stress rig (same ssh.exe `-M` + `-L` forwards, ramp concurrent
connections + churn with a fresh-connection canary) to pin the wedge threshold, plus
master telemetry (HandleCount/thread/TCP-count sampling, per-respawn-generation `-E`
log suffix). Settles *why* the master wedges. Becomes moot once the layer is deleted —
worth an afternoon only if we want the mechanism documented or the migration stalls.

**Phase 1 — Mac pilot (one small harness change + setup):**
1. Mac + dev coordinator join the tailnet.
2. Expose OrbStack's dockerd on TCP bound to the tailnet interface (needs a spike:
   native OrbStack config vs the socat-sidecar pattern publishing the socket on a
   mesh-scoped port).
3. Audit port bindings: Testcontainers publishes on `0.0.0.0` by default — verify
   nothing (daemon, containers, harness URL construction) assumes loopback for this
   host.
4. **Re-key "remote host" off ssh-field presence.** Today remoteness IS
   `SshDestination` non-empty: `ImageDistributor.cs:102,172` and
   `GameDataDistributor.cs:98` skip hosts without one, so a `tcp://` host would be
   treated as local and silently never receive images or game data. Introduce an
   explicit `DockerHost.IsRemoteDaemon` (derived from endpoint kind — not from ssh
   fields) and route both distributors through it. Sweep the other
   `SshDestination`-presence gates while there: the tunnel/heal call sites correctly
   no-op on null, but any future gate meaning "this daemon is another machine" must
   use the new predicate.
5. **Redaction audit.** `ReportRedactor.cs:51,87` and the secrets list
   (`Program.cs:1191-1200`) mask ssh destinations in published CI reports; decide
   whether tailnet addresses inside `tcp://` endpoints get equivalent masking, and add
   the pattern if so.
6. Flip the host's `SDVD_DOCKER_HOSTS` entry from `ssh://…` (+ `socketPath`) to
   `tcp://<tailnet-address>:<port>`. The transport layer then follows the existing
   local/direct path — `TunnelManager` never engages for it.

**Phase 2 — bake-in:** run the suite on direct transport for a sustained period.
Measure before/after: image-distribution wall-clock, `/wait/*` latencies, suite
duration. Watch for the mesh gotcha classes (MTU blackholes → large payloads stall
while small requests work; DERP relay fallback on LAN → visible in `tailscale status`;
wintun/AV friction on the Windows coordinator).

**Phase 3 — CI:** fold into `tests-local-ci-runners.md` implementation — the runner
joins the tailnet per that plan and uses the `tcp://` endpoint, not `ssh://`.

**Phase 4 — deletion (single commit, net-negative):** remove the SSH layer and its
repair machinery once no host entry uses `ssh://`.

## Deletion inventory (Phase 4)

- `TunnelManager.cs` (~2k lines: masters, mux ops, forwards, heal/respawn/canary,
  drain) and `SshBinaryResolver.cs`.
- `ForwardHealingHandler.cs`; `ServerApiClient`/`GameTestClient` heal wiring.
- `TransportFaultClassifier` forward-scoped branch. Post-mesh semantics collapse to
  one rule — do NOT port the corroborate logic: a **daemon-level** fault (can't reach
  the `tcp://` endpoint) is host-scoped → host poison; a **single-container** fault
  (refused/timeout on one container's port while the daemon answers) means that
  container is actually down → server-scoped, handled by the existing health-watchdog /
  server-poison path, never a host poison. `ConnectionRefused`/`TimedOut` move from
  "forward-scoped, corroborate first" to this daemon-vs-container split.
- `ManagedServer` heal seams (`TryHealForwardScopedFaultAsync`, probe-timeout heal);
  `ServerContainer`/`GameClientContainer` `ReopenApiForwardAsync`/`HealApiForwardAsync`.
- `Program.cs` master-health monitor; `SDVD_SSH_HOST_MASTERS` / `SDVD_HOST_TUNNELS`
  env handoffs; ssh-master `-E` log plumbing.
- Docs/rules: runbook step 7 (SSH tunnel triage), `InfrastructureEventLog` catalog SSH
  section, `remote-host-setup.md`, SSH mentions in `docker-test-resources.md` /
  `test-broker-invariants.md`; revise `tests-live-web-ui-remote-access.md`.

## Security requirements

dockerd is root-equivalent: bind it to the tailnet interface only, restrict reachers
via tailnet ACLs to the coordinator nodes, and decide TLS-on-mesh (defense in depth)
during the pilot. Container ports become mesh-reachable — acceptable for this fleet,
but the ACL must say so deliberately. The CI threat model is unchanged in shape from
`tests-local-ci-runners.md`: tailnet membership now stands where the inline ssh key
stood (either grants daemon root-equivalence), so that plan's fork-PR
maintainer-approval gate remains the load-bearing control; only the secret changes
(`TS_OAUTH_*` ephemeral enrollment instead of an inline private key).

## Success criteria

- Full suite green on direct transport across a sustained bake-in, including runs with
  image rebuild (exercises bulk transfer alongside test traffic).
- Measured image-distribution improvement (baseline ~50s / ~13 MB/s).
- Zero transport-heal events (the event types no longer exist) and no new
  transport-class flakes in `flakiness.jsonl`.
- Phase 4 lands as a net-negative diff with runbook/docs updated.

## Open decisions

- Tailscale vs Headscale-from-day-1 vs raw WireGuard.
- TLS on dockerd within the mesh, or plain TCP + ACLs.
- Addressing in `SDVD_DOCKER_HOSTS`: tailnet IP (no DNS dependency in the harness) vs
  MagicDNS hostname (survives IP reassignment, more readable in configs/reports).
  Interacts with the redaction decision (Phase 1 step 5).
- Run Phase 0 (root-cause rig) at all, or let the deletion make the question moot.
- Timing relative to the current branch/PR load.
