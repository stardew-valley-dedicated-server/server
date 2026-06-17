# Wire up the Mac M5 as a remote E2E test runner (local LAN + CI)

## Context

The goal is to use a local Apple-Silicon Mac (M5, Docker Desktop) as a **remote Docker
resource** for the E2E test suite — both from the developer's own machine on the home LAN,
and from CI — to offload the heavy Docker workload. **The Mac is a pure resource only: a
Docker daemon reached over SSH, exactly like the VPS today. The coordinator
(JunimoServer.TestRunner) always runs externally and orchestrates; the Mac never
orchestrates.** This is a firm constraint.

Key finding: the multi-host SSH infrastructure already supports this. `SDVD_DOCKER_HOSTS`
accepts `ssh://` endpoints and a macOS `socketPath` override, `TunnelManager` opens the SSH
ControlMaster + daemon-socket forward, `ImageDistributor` streams images over that tunnel,
and `e2e-testing.md:425` already documents the Rosetta-2 requirement for the amd64 game
images. `.env.test.example:72` even ships `ssh://julian@mac.lan` as a sample. So the
**local-LAN case is setup + config — no code.**

The only genuinely new problem is **CI reachability**: a GitHub-hosted Ubuntu coordinator
cannot make an *inbound* SSH connection to a Mac behind home NAT. Chosen solution:
**Tailscale** — both the ephemeral GitHub runner and the Mac join the developer's tailnet,
and the runner SSHes to the Mac's stable tailnet hostname. No router port-forwarding, no
public internet exposure, WireGuard-encrypted (direct peer-to-peer via NAT hole-punching;
no Mac inbound port is ever opened — both sides dial outbound). The topology is otherwise
**unchanged from the VPS model** — coordinator on `ubuntu-latest`, Mac as an `ssh://` host.
The per-call WAN latency (container startup is dominated by many small Docker API
round-trips, `e2e-testing.md:439-443`) makes CI runs slower than the VPS; this trade-off is
**accepted**.

**Lock-in is shallow and intentionally documented as escapable.** Only the network-reach
layer touches Tailscale (one workflow step + the tailnet hostname in `SDVD_DOCKER_HOSTS`);
the test harness is vendor-agnostic SSH-over-IP. The data plane is open WireGuard. If we
ever want off hosted Tailscale, the no-rearchitecture exit is **Headscale** (open-source
self-hostable control server, same official clients via `--login-server`) — noted as a
future option, **not built now**.

## Part 1 — Local-LAN use (no code; setup + config)

On the **Mac**:
1. Docker Desktop installed; **enable Rosetta** (Settings → General → "Use Rosetta for
   x86_64/amd64 emulation"). The images are amd64-pinned (`Makefile:59,82`, `linux-x64`
   .NET runtime, `linux64` ffmpeg, `linux_amd64` go2rtc); QEMU is ~85% slower and
   unsupported for .NET (`e2e-testing.md:425`).
2. Enable Remote Login (System Settings → Sharing → Remote Login) for SSH.
3. Install the coordinator's public key into the Mac SSH user's `~/.ssh/authorized_keys`
   (existing recipe, `e2e-testing.md:380-417`, substituting the Mac user).
4. Daemon socket path: Docker Desktop on macOS is per-user `~/.docker/run/docker.sock` (the
   documented `socketPath` override).

On the **dev machine** (coordinator), add a Mac entry to `SDVD_DOCKER_HOSTS` in `.env.test`
— the shape already shown in `.env.test.example:72`:
```jsonc
{"id": "mac", "endpoint": "ssh://julian@mac.lan", "sshKey": "~/.ssh/sdvd_runner",
 "socketPath": "~/.docker/run/docker.sock", "serverSlots": 2, "clientSlots": 4, "gpu": false}
```
Then `make test FILTER=<oneClass>` and watch `diagnostics/infrastructure.jsonl` for
`ssh_master_ready` + `host_id=mac`. Troubleshooting is documented at
`remote-host-setup.md:87-101`. **No source changes for this part.**

## Part 2 — CI use (GitHub coordinator → Mac over Tailscale, Mac stays a pure SSH resource)

### Topology
The `e2e` job stays on `ubuntu-latest` and stays the coordinator — **no change to who
orchestrates**. The only additions: the runner joins Tailscale, then reaches the Mac's
Docker daemon over SSH exactly as it reaches the VPS today. `SDVD_DOCKER_HOSTS` for CI is a
single-host fleet whose `endpoint` is the Mac's tailnet hostname.

### Mac provisioning (operator, outside the repo)
- Dedicated unprivileged SSH user (e.g. `ghrunner`) in the `staff`/docker-capable context
  with access to the Docker Desktop socket; install the runner's public key in its
  `authorized_keys`.
- Docker Desktop (+ Rosetta enabled); run `make setup` once as that user to seed the local
  `server_game-data` / `server_steam-session` volumes (the coordinator's `ImageDistributor`
  + `GameDataDistributor` then stream/skip as needed — same as the VPS).
- Install Tailscale on the Mac; bring it up with a stable hostname (e.g. `mac-m5`). Consider
  Tailscale ACLs that allow SSH (port 22) from the CI runner's tagged identity only.

### GitHub settings
- New environment `test-mac` (clone of `test-vps`) with:
  - `SDVD_DOCKER_HOSTS` = single remote host, key **inline** (CI pattern), endpoint = the
    Mac's tailnet hostname:
    ```jsonc
    [{"id":"mac","endpoint":"ssh://ghrunner@mac-m5.<tailnet>.ts.net",
      "socketPath":"~/.docker/run/docker.sock","serverSlots":2,"clientSlots":4,"gpu":false,
      "sshKey":"-----BEGIN OPENSSH PRIVATE KEY-----\n…\n-----END…-----\n"}]
    ```
  - `STEAM_ACCOUNTS` (size ≥ `1 + clientSlots` so the host is Steam-capable per the slicing
    rule, `remote-host-setup.md:38-48`), R2 credentials.
  - Vars `R2_BUCKET` / `R2_PUBLIC_BASE_URL`.
  - **`TS_OAUTH_CLIENT_ID` / `TS_OAUTH_SECRET`** (or an ephemeral auth key) for the runner to
    join the tailnet.
- `fork-pr` environment approval (already wired, `:240-247`) is **even more important** here:
  the inline SSH key + tailnet access reaching fork code = control of the Mac's Docker
  daemon (root-equivalent on that host). Required reviewers = maintainers only. Reaffirm the
  header ban (`:30-35`) on `pull_request_target` / `push` / `merge_group` triggers.

### `.github/workflows/e2e-tests.yml` edits
| Location | Change |
|---|---|
| `e2e.environment` (`:263`) | `test-vps` → `test-mac` |
| New step in `e2e`, **before** "Trust VPS host key" (`:439`) and before the suite step | "Connect to Tailscale" — use the official `tailscale/github-action`, authenticating with the `TS_OAUTH_*` secrets and `--ephemeral` so the node deregisters after the job. This must run before any SSH to the Mac (the ssh-keyscan and the in-process SSH master both need the tailnet route up). Follow with a `tailscale ping <mac-host>` assertion that the path is **`direct`** (not `via DERP`) so a relayed fallback — which would add a latency hop — fails the run loudly rather than silently degrading. |
| "Enable containerd image store" step (`:321-342`) | **Unchanged** — the coordinator daemon (Ubuntu runner) still does `docker save`; the Mac is a remote host receiving the stream, so the compressed-OCI transfer optimization still applies. |
| "Trust VPS host key" ssh-keyscan (`:439-458`) | **Unchanged in logic** — it already parses `.endpoint` generically (`sed 's#^ssh://##; s#^[^@]*@##'`), so a tailnet hostname keyscans fine once Tailscale is up. (Optionally rename the step "Trust remote host key".) |
| `e2e.name` (`:250`) / header doc (`:1-39`) | Update prose: the remote host can be the VPS or the Tailscale-reached Mac; the SSH/coordinator model is identical. |
| `gate` / `authorize` jobs | Unchanged. |

All other steps work unchanged — the Mac is just another SSH Docker host. The SSH
ControlMaster, daemon-socket forward, image distribution, game-data distribution, abort
sweep, and host-disconnect handling are the **same code paths the VPS uses today**.

### Concurrency & lifecycle (no change)
- The job-level concurrency singleton (`:257-260`) still serializes runs against the one
  physical Mac. Keep at job level (`:63-67` warning stands).
- Abort path unchanged: the coordinator (on the runner) sweeps the Mac's containers by
  `sdvd.run-id` over SSH on cancel, exactly as for the VPS.
- The `120 min` job cap (`:273`) stays — its "wedged SSH master" rationale still applies, and
  WAN latency makes a generous cap more important.

### Coexistence of local-dev and CI on the same Mac
Both use the Mac purely as a remote SSH daemon. Container/network/steam-auth names are
per-coordinator-process GUIDs (`SharedSteamAuth.cs:108`), so name collisions are impossible.
The two real hazards on a **shared daemon** are (a) the fixed-name shared volumes
`server_game-data` / `server_steam-session` (`ServerContainerOptions.cs:19,24`) and (b) the
startup sweep `SweepStaleResourcesAsync`, which deletes by the broad `sdvd.test=true` label
(`EmergencyCleanup.cs:296-298`) — so a CI run starting mid local-run on the same daemon would
rip out the local run's containers. Mitigation = **don't run a local `make test` against the
Mac while a CI run is active** (the concurrency singleton already serializes CI-vs-CI; this
is the human discipline for CI-vs-local). Disjoint `STEAM_ACCOUNTS` between the two
`SDVD_DOCKER_HOSTS` configs avoids the Steam single-login conflict. Document this.

## Files to modify
- `.github/workflows/e2e-tests.yml` — add the Tailscale connect step; switch the `e2e`
  environment to `test-mac`; touch up the keyscan step name + header prose.
- `.env.test` (dev's machine, local, gitignored) — add the `mac` host entry for Part 1.
- `docs/developers/testing/remote-host-setup.md` / `e2e-testing.md` — document the Tailscale
  CI path and the shared-daemon coexistence caveat (the existing remote-host SSH recipe
  already covers the Mac's daemon/SSH/Rosetta setup).
- **No mod/runner C# source changes** — the remote-SSH-host code path is exactly the VPS one.

## Verification (end-to-end)
1. **Part 1 (local LAN):** with the `mac` entry in `.env.test`, run
   `make test FILTER=<oneClass>`; confirm `infrastructure.jsonl` shows `host_id=mac`,
   `ssh_master_ready`, and a pass. Confirm Rosetta (not QEMU) is engaged on the Mac.
2. **Part 2 (CI), staged by trust level:**
   - `workflow_dispatch` from master (trusted) — confirm the Tailscale step connects, the
     keyscan resolves the tailnet host, the SSH master comes up, images stream to the Mac,
     and the suite runs with `host_id=mac`.
   - A same-repo PR `/run-tests-e2e <filter>` comment.
   - Only with eyes-on review: a fork PR through the `fork-pr` approval.

## Open questions / risks
- **WAN latency (accepted):** CI runs against the home Mac are slower than the VPS due to
  per-Docker-API-call round-trips over Tailscale (`e2e-testing.md:439-443`). Accepted
  trade-off; keep the workflow a non-required, non-merge-coupled check (already the rule).
- **Tailscale on ephemeral runners:** verify the official `tailscale/github-action` with
  `--ephemeral` and OAuth/tag auth; ensure the node deregisters after the job so the tailnet
  doesn't accumulate dead nodes. (Verify the exact action ref + auth flow during implementation.)
- **DERP fallback:** if the GitHub runner ↔ home-Mac path can't hole-punch (strict NAT),
  Tailscale relays via DERP (extra hop, slower). The `tailscale ping`-must-be-`direct`
  assertion catches this; if it trips, enabling UPnP/NAT-PMP on the home router usually
  restores a direct path.
- **Vendor lock-in (mitigated):** only the reach layer depends on hosted Tailscale; documented
  exit is Headscale (self-hosted, same clients) with no harness changes. Not built now.
- **Rosetta perf/stability on M5** — confirm Rosetta is engaged; QEMU is unsupported for .NET.
- **Mac availability** — a home Mac is less available than a VPS; never make this a required
  check.
- **Security:** inline SSH key + tailnet access reaching an approved fork PR = control of the
  Mac's Docker daemon. The `fork-pr` maintainer-approval gate is the load-bearing control;
  use burner Steam accounts and rotate the key if a bad fork is ever approved.
