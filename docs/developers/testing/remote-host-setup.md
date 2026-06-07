# Remote-host setup

How to configure `SDVD_DOCKER_HOSTS` for multi-host runs and how `STEAM_ACCOUNTS` is sliced across the fleet.

## `SDVD_DOCKER_HOSTS` schema

`SDVD_DOCKER_HOSTS` is a JSON array of host entries. Each entry has:

| Field | Required | Description |
|---|---|---|
| `id` | yes | Unique identifier; used in event payloads (`host_id=…`) and container/network/cleanup names. |
| `serverSlots` | yes | Concurrent server containers this host can run. Combined with other hosts via Hamilton allocation. |
| `clientSlots` | yes | Concurrent client containers this host can run. Sets the upper bound on how many clients a server on this host can serve concurrently. |
| `endpoint` | no | `ssh://user@machine` for remote daemons. Omit for the local Docker daemon. |
| `sshKey` | no | Either a path to a private key (relative paths anchor at the project root, `~` is expanded), or inline key material (a `-----BEGIN…` block, written to a 0600 temp file — used in CI so the whole `SDVD_DOCKER_HOSTS` JSON, key included, can live in one secret). Omit to use `~/.ssh/config` + ssh-agent. |
| `socketPath` | no | Remote Unix socket path. Defaults to `/var/run/docker.sock` (the standard Docker location). Override for hosts where the daemon listens elsewhere — most commonly macOS Docker Desktop's per-user `~/.docker/run/docker.sock`. Ignored for local entries. |
| `gpu` | no | `true` if this host has an NVIDIA GPU + Container Toolkit. Defaults to `false`. Per-host so a fleet can mix GPU workstations and CPU-only VPSes. |
| `concurrentStarts` | no | Cap on simultaneous `docker create+start` calls against this daemon. When omitted, falls back to `SDVD_MAX_CONCURRENT_STARTS` (if set) and otherwise to this host's `serverSlots + clientSlots`. Independent across hosts. |

Example:

```json
[
  {"id": "local", "serverSlots": 3, "clientSlots": 6, "gpu": true},
  {"id": "vps",  "endpoint": "ssh://sdvd-runner@10.0.0.2", "sshKey": "~/.ssh/sdvd_runner",
                 "serverSlots": 2, "clientSlots": 4},
  {"id": "mac",  "endpoint": "ssh://dev@mac.local", "socketPath": "~/.docker/run/docker.sock",
                 "serverSlots": 1, "clientSlots": 2}
]
```

`SDVD_DOCKER_HOSTS` is required. The runner throws fast at startup if it's unset.

## `STEAM_ACCOUNTS` slicing

Each Steam-capable host runs its own `steam-auth` container on its own bridge network. Containers reach steam-auth via the alias `steam-auth` on the host-local bridge — the URL `http://steam-auth:3001` is identical everywhere; only the resolution target differs per host.

The global `STEAM_ACCOUNTS` array is sliced disjointly across hosts in declared order. The slicer walks each host and assigns it `min(1 + clientSlots, accountsRemaining)` accounts:

- 1 account is the host's *server* account (slice-local index 0).
- The remainder are *client* accounts (slice-local indices 1..k-1).
- A host that would receive only 1 account gets 0 instead — a 1-account slice can't serve any Steam test (needs server + ≥1 client). The next host in declared order picks up the unused account.

Slice-local indices are what the wire format carries (`?account=N`, `SDVD_TEST_STEAM_ACCOUNT_INDEX`); the global → slice-local shift lives entirely at the allocator boundary.

### Sizing rule

A host is **Steam-capable** iff its slice has ≥2 accounts. If a test requires Steam (`WithSteam=true`) and no host is capable, the run fails fast in `ServerConfigDiscovery.ValidateRequirements` before any container starts.

### Worked examples

| Hosts | `STEAM_ACCOUNTS` length | Slice sizes | Steam-capable hosts |
|---|---|---|---|
| 1 host (`local`, clientSlots=2) | 2 | local=2 | local |
| 2 hosts (clientSlots=1 each) | 4 | local=2, vps=2 | local, vps |
| 2 hosts (clientSlots=2 each) | 4 | local=3, vps=1 | local |
| 2 hosts (clientSlots=1 each) | 1 | local=0, vps=0 (1-account slice can't serve any test) | none |
| 2 hosts (clientSlots=2 each) | 0 | local=0, vps=0 | none |

To rebalance a lopsided allocation (e.g., the third row above), shrink `clientSlots` so each host's `1 + clientSlots` matches what your `STEAM_ACCOUNTS` array can support.

## Per-host resources

Each Steam-capable host materializes:

| Resource | Name pattern | Lifetime |
|---|---|---|
| steam-auth container | `sdvd-steam-auth-{hostId}-{runId}` | run |
| bridge network | `sdvd-test-shared-{hostId}-{runId}` | run |
| game-data volume | `server_game-data` (per-daemon) | persistent (cache; reset by `EmergencyCleanup.SweepStaleResourcesAsync`) |
| steam-session volume | `server_steam-session` (per-daemon) | persistent |
| emergency cleanup key | `SharedSteamAuth-{hostId}-{runId}` | run |

The same `runId` appears across all hosts on a single run; `hostId` distinguishes the per-host instances. Volumes are populated by `GameDataDistributor` on first use and reused across runs as a cache.

## Verifying capability prediction

Per-host slice decisions are emitted as `steam_account_slicing` events in `infrastructure.jsonl`:

```jsonc
{"event":"steam_account_slicing","host_id":"local","sliceSize":2,"globalIndices":[0,1],"isSteamCapable":true}
{"event":"steam_account_slicing","host_id":"vps","sliceSize":2,"globalIndices":[2,3],"isSteamCapable":true}
```

These events fire once per host at the top of pre-start, before any container starts. If a Steam test fails to place, grep these events first — they tell you exactly which hosts the slicer considered capable, and which globally-indexed accounts each got.

## Troubleshooting

**Symptom:** tests on a remote host time out, or a whole class fails with `host_disconnected`. The Docker daemon is reached over an SSH tunnel (`ssh -M` ControlMaster + `ssh -L` daemon-socket forward), so a tunnel that dies mid-run surfaces only as a downstream timeout unless you look at the SSH layer.

**Where to look** (all under `diagnostics/` in the run dir):

- `infrastructure.jsonl` — grep `host_disconnected` (poison reason + `sshMasterLogTail` when the cause was transport), `ssh_master_log` (the master's own death line, emitted at teardown when non-empty), `ssh_master_exited` / `tunnel_forward_closed` (non-zero `exitCode` = a teardown step failed).
- `ssh-master-{hostId}.log` — the master's full `-E` error log. Holds ssh's own message (e.g. `Timeout, server not responding.`) for a silent network drop. Empty for an abrupt RST drop (expected — that path is caught by the exception classifier, not the log).

**Common causes:**

- VPS rebooted or the `docker` daemon / `docker` group was restarted mid-run — the forward's remote end dies (TCP reset), classified directly as a transport fault.
- Network loss to the VPS — the master self-exits ~30s after the link goes quiet (`ServerAliveInterval=15` × `ServerAliveCountMax=2`); the `Timeout, server not responding.` line lands in the master log. A bare-timeout failure within that ~30s window may not poison until the next test re-probes (`ssh -O check`) after the master has self-exited — by design, see `test-broker-invariants.md`.
- SSH master never came up — look for `ssh_master_spawn_failed` / `ssh_master_check_failed` at preflight (bad key, host unreachable, wrong `socketPath`).
