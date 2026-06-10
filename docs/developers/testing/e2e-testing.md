# E2E Testing

End-to-end tests run Stardew Valley server and client containers, connecting them together to verify real game behavior.

## Architecture

```
                    Docker Network
  ┌──────────────────────────────────────────────┐
  │                                              │
  │  ┌─────────────┐  ┌──────────┐  ┌─────────┐ │
  │  │ steam-auth  │  │  server   │  │  client  │ │
  │  │ (game files │──│ VNC:5800  │──│ VNC:5800 │ │
  │  │  + session) │  │ API:8080  │  │ API:5123 │ │
  │  └─────────────┘  │ UDP:24642│  └─────────┘ │
  │                    └──────────┘              │
  └──────────────────────────────────────────────┘
```

**steam-auth** provides game files and Steam session management. **server** runs the dedicated Stardew Valley server with SMAPI mods. **client** runs a game client with the JunimoTestClient mod for automation.

All containers use the `jlesage/baseimage-gui` base image which provides X11 + VNC (noVNC on port 5800).

## Quick Start

### Prerequisites

1. Run `make setup` to download game files (requires Steam credentials in `.env`)
2. Docker with BuildKit support

### Testcontainers (Automated)

Tests use [Testcontainers](https://dotnet.testcontainers.org/) to manage containers programmatically:

```bash
# Run all E2E tests (CI mode)
make test

# Run specific tests
make test FILTER=PasswordProtection

# Run with web UI
make test-web

# With host game client (local Stardew Valley + Steam, instead of Docker)
make test SDVD_HOST_CLIENT=true
```

## Visual Observation

The base image provides noVNC (a web-based VNC viewer) on port 5800. No additional streaming infrastructure is needed.

### During Automated Tests

When tests run via Testcontainers, VNC ports are dynamically mapped. The fixture logs the URLs:

```
[Setup] Server VNC: http://localhost:32768
```

In web UI mode (`make test-web`), VNC links appear as clickable badges in the status bar.

### During Compose Environment

Fixed port mappings (configurable via env vars):

| Service | Default Port | Env Var |
|---------|-------------|---------|
| Server VNC | 5800 | `E2E_VNC_PORT` |
| Client VNC | 5801 | `E2E_CLIENT_VNC_PORT` |
| Server API | 8080 | `E2E_API_PORT` |
| Client API | 5123 | `E2E_CLIENT_API_PORT` |
| Game (UDP) | 24642 | `E2E_GAME_PORT` |

## Screenshot Capture

Screenshots are captured from the running game's backbuffer via the in-game API (the server's screenshot endpoint for the server, the test-client's `CaptureScreenshot` for the connected client). The captured image matches what the VNC viewer shows.

### Modes

Set via `SDVD_TEST_SCREENSHOTS` environment variable:

| Value | Behavior |
|-------|----------|
| `none` | No screenshots |
| `done` (default) | One screenshot at the end of each test (server, plus the connected client if any) |
| `all` | The end-of-test screenshot, plus checkpoint screenshots during connection (`after_connect`, `after_join`, `after_auth`) |

The end-of-test screenshot is labelled `result` when the test passed and `failure` when it failed. Checkpoint screenshots (`all` mode only) are sequence-prefixed, e.g. `01_after_connect.png`.

### Output Location

Screenshots are written to the per-test artifact directory:

```
TestResults/runs/{timestamp}_{sha}/tests/{Class}.{Method}/screenshots/
  result.png            # Server backbuffer (failure.png if the test failed)
  client_result.png     # Connected client (client_failure.png on failure)
  01_after_connect.png  # Checkpoint screenshots, all mode only
```

## Video Recording

Video recording captures full motion video of server and client containers during test execution using ffmpeg inside each container.

### How It Works

Both server and client containers record their X11 display continuously from startup. When a test completes, per-test video clips are extracted from the running recording using ffmpeg stream copy (near-instant, zero CPU). In `failure` mode, passing tests skip per-test clip extraction entirely (no ffmpeg cost on the per-test path); only failing tests pay the extraction cost. Note: the per-container `full_recording.mp4` files (under `containers/{server,client}-N/`) are produced at container disposal in both `failure` and `all` modes — they capture the full container lifetime and are useful for cross-test debugging. Set `SDVD_TEST_RECORDING=none` to skip recording entirely.

### Modes

Set via `SDVD_TEST_RECORDING` environment variable:

| Value | Behavior |
|-------|----------|
| `none` (default) | No recording |
| `failure` | Record all tests, keep only failed test recordings |
| `all` | Record and keep all test recordings |

### Output Location

Per-test clips are stored in the test artifact directory:

```
TestResults/runs/{timestamp}_{sha}/tests/{Class}.{Method}/
  server_recording.mp4       # Server container video for this test
  client_recording.mp4       # Primary client video
  client_2_recording.mp4     # Additional client (if multiple)
```

### Configuration

| Variable | Default | Description |
|----------|---------|-------------|
| `SDVD_TEST_RECORDING` | `none` | Recording mode: none/failure/all |
| `SDVD_TEST_RECORDING_SERVER` | `true` | Record server containers (requires `SERVER_FPS > 0`) |
| `SDVD_TEST_RECORDING_CLIENT` | `true` | Record client containers (requires `CLIENT_FPS > 0`) |
| `SERVER_FPS` | `0` | Server draw/recording fps; 0 disables rendering and recording |
| `CLIENT_FPS` | `0` | Client draw/recording fps; 0 disables rendering and recording |

### Performance

- CPU overhead: <5% per container (ffmpeg ultrafast preset)
- Disk usage: ~1-3 MB/min per recording at default settings (CRF28)
- Clip extraction: ~100ms per clip (stream copy, no re-encoding)

## Configuration Reference

### Environment Variables

| Variable | Default | Description |
|----------|---------|-------------|
| `SDVD_IMAGE_TAG` | `local` | Docker image tag for server/client |
| `SDVD_HOST_CLIENT` | `false` | Use local Stardew Valley process instead of Docker containers (requires Steam) |
| `SDVD_VOLUME_PREFIX` | `server` | Docker volume name prefix |
| `SDVD_TEST_SCREENSHOTS` | `done` | Screenshot capture mode: none/done/all |
| `SDVD_TEST_RECORDING` | `none` | Video recording mode: none/failure/all |
| `NO_COLOR` | unset | Set (to any value) to disable colored output |
| `SDVD_SKIP_BUILD` | `false` | Skip automatic image builds |

### Compose Environment Variables

| Variable | Default | Description |
|----------|---------|-------------|
| `E2E_VNC_PORT` | `5800` | Server VNC host port |
| `E2E_API_PORT` | `8080` | Server API host port |
| `E2E_GAME_PORT` | `24642` | Server game UDP host port |
| `E2E_CLIENT_VNC_PORT` | `5801` | Client VNC host port |
| `E2E_CLIENT_API_PORT` | `5123` | Client API host port |
| `IMAGE_VERSION` | `local` | Image tag for compose services |

## CI Usage

The E2E suite runs from `.github/workflows/e2e-tests.yml` (the coordinator runs on
the GitHub runner; the game containers run on a remote VPS over SSH). That workflow
is the source of truth for CI invocation. There are three ways to launch it, all
manual and **maintainer-gated** — it is never an automatic merge gate (an external
VPS being down must not block the queue):

- **Actions tab → "Run workflow"** (`workflow_dispatch`) — runs the full suite from a
  trusted branch; the optional `filter` input narrows it to a class/method. The optional
  `pr` input instead resolves that PR's HEAD and tests it, posting the same sticky comment
  as the comment path; fork PRs pause at the `fork-pr` approval (same as the comment path),
  and an empty `pr` runs the selected branch / master exactly as before.
- **`/run-tests-e2e [filter]` PR comment** — runs against the PR's
  HEAD commit and posts results back to the PR (see below). `filter` is an xUnit
  class/method substring; omit it for the full suite.
- **The "🔁 Re-run E2E tests" checkbox** in the bot's results comment — ticking it
  re-runs with the same filter (a click-in-the-PR control; it fires `issue_comment:
  edited`). The box auto-resets and shows "(requested)" until fresh results arrive.

**Authorization.** Only repo maintainers (write/admin permission — checked via the
repo-permission API, not `author_association`) can trigger a run from a PR; anyone
else gets a 👎 and a "not authorized" reply, and no test job runs. **Fork PRs**
additionally pause at a `fork-pr` Environment approval before the secret-bearing job
runs (a maintainer clicks "approve"); same-repo PRs run without a prompt.

**Single runner / queueing.** There is one physical VPS runner, so all runs share a
global concurrency singleton: a new trigger **queues** behind an active run (it does
not abort it). To deliberately preempt the in-flight run, a maintainer cancels it from
the **Actions tab**; the cancelled run still writes its summary and reports "⚪ aborted".

Results surface in several places, all produced from artifacts the runner emits:

- **PR sticky comment** (comment/checkbox or `pr`-dispatch path) — a single bot comment,
  updated in place each run, with the pass/fail headline, the hosted report link, the re-run
  checkbox, and a collapsed **"Run history"** table that is appended to every run
  (older rows kept) so the PR retains a full per-run trail for debugging.
- **Job Summary tab** — the [CTRF reporter action](https://github.com/ctrf-io/github-test-reporter)
  renders `runs/{id}/ctrf-report.json` into a pass/fail + failed-tests table.
- **`e2e-web-report` artifact** — a self-contained offline bundle of the test-UI
  SPA with the run snapshot inlined and screenshots/videos copied alongside.
  Download it and open `report/index.html` over `file://`; screenshots and videos
  play without a running server.
- **Hosted report URL (optional)** — when Cloudflare R2 is configured (see below),
  the bundle is published to `…/e2e/{branch}/{run-id}/index.html` and linked from the
  Job Summary and the PR sticky, so the interactive report (with playing videos) can
  be opened directly and shared. Per-branch history is kept; an R2 lifecycle rule
  expires reports after 30 days.

Under `CI` (or with the `--report` flag locally), the runner assembles that
bundle into `TestResults/runs/{id}/report/` after the run — it requires the
test-UI SPA to be built first (the workflow runs `bun run build` in
`tests/test-ui`). Locally, `make test-web-report FILTER=<name>` does the same
build-then-report in one step.

### Hosted report (Cloudflare R2)

The hosted-report URL is **optional**: with none of the `R2_*` secrets set, the
publish step skips cleanly and the run stays green — the `e2e-web-report` artifact
is always the fallback. GitHub Pages is deliberately not used (its ~1 GB limit is
smaller than a single recording-heavy run, and the docs already own the Pages site).
R2 gives 10 GB free with zero egress fees, so videos stream without cost.

The bucket is **public-read, token-write**: reads are world-readable (anyone with the
link opens the report), but uploads require the write token (only CI has it). The
published snapshot is redacted (IPs, Steam credentials, player names, keys masked — see
"Redaction" below), but because the bucket is public, treat it as a hard rule that
**secrets must never be logged in the first place**.

One-time setup (Cloudflare dashboard, outside this repo):

1. Create an R2 bucket (e.g. `sdvd-test-artifacts`).
2. Enable **public read** access (managed `r2.dev` subdomain or a custom domain); record
   the public base URL. Do not grant public write — writes use the API token (step 4).
3. Add an **object-lifecycle rule** on the bucket to expire objects after 30 days
   (scope it to the `e2e/` prefix). This is the retention mechanism — R2 deletes old
   reports server-side, so CI does no pruning.
4. Create an R2 API token scoped to **Object Read & Write** → Access Key ID + Secret.
   This token is the only write path; keep it in the `test-vps` environment, never public.
5. In the **`test-vps`** GitHub Environment, add the credentials as **secrets** and the
   bucket name + public URL as **variables**:

   | Secret | Value |
   |--------|-------|
   | `R2_ACCOUNT_ID` | Cloudflare account ID (the `<id>` in the S3 endpoint) |
   | `R2_ACCESS_KEY_ID` | R2 API token access key |
   | `R2_SECRET_ACCESS_KEY` | R2 API token secret |

   | Variable | Value |
   |----------|-------|
   | `R2_BUCKET` | Bucket name (e.g. `sdvd-test-artifacts`) |
   | `R2_PUBLIC_BASE_URL` | Public base URL of the bucket (no trailing slash) |

   The bucket name and URL are **variables, not secrets** — they're public by design, and
   both contain a hyphen. GitHub's secret masker over-masks the `-` substring across the
   entire log when these are secrets (turning every `-` into `***`). Keep them as variables.

The publish step runs `aws s3 sync` against R2's S3-compatible endpoint
(`https://{R2_ACCOUNT_ID}.r2.cloudflarestorage.com`). It is wrapped in
`continue-on-error: true` with internal config/credential guards, so a missing or
broken R2 never fails the test run.

### Redaction

The published report inlines captured server logs, so it is redacted in two layers
(defense-in-depth) before reaching the public URL:

- **At the mod source** — `ChatRedaction.MaskIp`/`MaskValue` mask the server's IP banner,
  the public-IP log line, the Steam SDR ID, invite codes, and player names where the mod
  logs them. Values are masked in place (`46.38.238.188` → `***.***.***.188`,
  `secret` → `s***t`) so the diagnostic shape survives.
- **At the report boundary** — `ReportRedactor.Scrub` runs over the whole snapshot before
  publishing: it masks values the runner knows exactly (the VPS host/IP from
  `SDVD_DOCKER_HOSTS`, Steam credentials from `STEAM_ACCOUNTS`) plus high-confidence
  patterns (private-key blocks, `/run/secrets`, `password=`/`token=` assignments,
  `user@host`). It deliberately does **not** mask bare IPs by regex (a version string like
  `Unix 6.1.0.17` is indistinguishable from an IP) — those are caught at the mod source
  and by the known-value set. If redaction ever yields invalid JSON, the report is not
  published (fail-closed).

## Troubleshooting

### Game files missing

```
Waiting for game files at /data/game...
```

Run `make setup` first. The steam-auth service downloads game files to a shared Docker volume.

### Steam authentication failures

```
Steam-auth container failed to start within 60 seconds
```

Check `.env` for valid `STEAM_REFRESH_TOKEN` or `STEAM_USERNAME`/`STEAM_PASSWORD`. Refresh tokens expire periodically.

### Container startup timeout

```
Game client failed to start within 120s
```

The client container has a hard startup timeout (the runner waits for the in-container `/health` endpoint up to `GameClientOptions.StartupTimeout`). Check container logs for SMAPI initialization errors.

### Display blanking during long tests

The containers disable X screensaver and DPMS power management automatically. If the display still blanks, verify that `xset` commands are running in `startapp.sh`.

### Docker build fails with "parent snapshot does not exist"

```
ERROR: failed to prepare extraction snapshot "...": parent snapshot ... does not exist: not found
```

This is a known containerd snapshotter bug ([moby/buildkit#6521](https://github.com/moby/buildkit/issues/6521), [containerd/containerd#10835](https://github.com/containerd/containerd/issues/10835)) that became widespread when Docker Desktop defaulted to the containerd image store (Docker 29+). BuildKit's cache holds references to layer snapshots that containerd's GC has already removed, causing the export/unpack phase to fail even though the build itself succeeds.

**Workaround:** Clear the stale cache when it bites:
```bash
docker builder prune -f
```

If it keeps recurring, disable the containerd image store in Docker Desktop: **Settings → General → uncheck "Use containerd for pulling and storing images"**, then restart Docker Desktop. That switches back to the classic storage driver, which lacks the sync issue and sidesteps the bug for good. The cost: if you run **multi-host** tests (a remote host in `SDVD_DOCKER_HOSTS`), the classic store makes `docker save` emit uncompressed `layer.tar` (~2.5× larger), so each fresh image transfer to a remote host roughly doubles (see [Image distribution](#image-distribution)). Single-host local runs are unaffected — nothing is saved or transferred.

### Stale test containers

Previous test runs that crashed may leave containers behind. The test fixture automatically cleans up stale resources labeled with `sdvd.test=true`. To manually clean:

```bash
docker ps -a --filter label=sdvd.test=true
docker rm -f $(docker ps -aq --filter label=sdvd.test=true)
docker network rm $(docker network ls --filter label=sdvd.test=true -q)
docker volume rm $(docker volume ls --filter label=sdvd.test=true -q)
```

## Multi-Host Execution

The E2E runner is a single coordinator process that places test containers on one or more Docker hosts. Each host is a Docker daemon, addressed by an endpoint URL — typically `ssh://user@machine` for remotes and the OS-default named pipe / Unix socket for the local one. There is no separate worker process; the coordinator drives every daemon over the Docker API.

### When to add hosts

The architectural payoff is **adding capacity** without giving up the broker's per-test reuse cache. A test's server and its clients always run on the same host (no cross-host container traffic), and the broker prefers reuse-on-host first, only falling back to creating a fresh server when no matching one is already running on the chosen host. Adding a host roughly multiplies effective concurrency without doubling startup work.

If pure local-machine speedup is the only goal, raising `host0`'s slot counts in `SDVD_DOCKER_HOSTS` is the simpler path.

### Configuration

`SDVD_DOCKER_HOSTS` is the canonical setting — a JSON array of host entries. Required fields per entry: `id` (unique), `serverSlots`, `clientSlots`. Optional: `endpoint` (omit for the local default daemon, or `ssh://user@host`) and `sshKey` (private-key path, `~` is expanded).

```ini
SDVD_DOCKER_HOSTS='[
  {"id": "local", "serverSlots": 3, "clientSlots": 6},
  {"id": "vps",   "endpoint": "ssh://sdvd-runner@10.0.0.2",
                  "sshKey": "~/.ssh/sdvd_runner",
                  "serverSlots": 2, "clientSlots": 4},
  {"id": "mac",   "endpoint": "ssh://julian@mac.lan",
                  "serverSlots": 2, "clientSlots": 4}
]'
```

When `sshKey` is set, the runner passes `-i {expandedPath} -o IdentitiesOnly=yes` to every `ssh` invocation for that host. Omitting `sshKey` falls back to standard OpenSSH resolution (`~/.ssh/config` `IdentityFile`, ssh-agent).

`SDVD_DOCKER_HOSTS` is required. The runner throws fast at startup if it's unset.

### Coordinator host requirements

The coordinator host (the machine that runs `make test`) needs an SSH binary that supports ControlMaster fd-passing when any remote host is configured. The runner relies on `ssh -O forward` against a per-host `ControlMaster` socket.

- **Linux and macOS**: the system `ssh` (upstream OpenSSH) works as-is.
- **Windows**: requires Git for Windows' Cygwin-built ssh at `C:\Program Files\Git\usr\bin\ssh.exe`. The Microsoft port at `C:\Windows\System32\OpenSSH\ssh.exe` is **rejected at preflight** because its named-pipe transport doesn't carry the `sendmsg()` ancillary data the multiplex layer needs (tracking issue [PowerShell/Win32-OpenSSH#1328](https://github.com/PowerShell/Win32-OpenSSH/issues/1328)). Git for Windows is already a project prerequisite for the `bash`-based Makefile targets, so most Windows dev setups already have it installed.

Override the resolved binary by setting `SDVD_SSH_PATH=/path/to/ssh` if you need a non-default location. The banner check (reads `ssh -V` from stderr) fires regardless of how the binary was selected; an `OpenSSH_for_Windows` banner aborts preflight with a diagnostic message naming the rejected binary.

### Remote-host setup

Provision a dedicated user on each remote host:

1. **Create the user** (no sudo, no password login):
   ```bash
   sudo useradd -m -s /bin/bash sdvd-runner
   sudo usermod -aG docker sdvd-runner
   ```
   The `docker` group is effectively root on that host -- treat the host accordingly.

2. **Generate a fresh keypair on the coordinator** (separate from any personal SSH key so it can be rotated independently):
   ```bash
   ssh-keygen -t ed25519 -f ~/.ssh/sdvd_runner -C "sdvd-runner@<coordinator>"
   ```

3. **Install the public key** in `/home/sdvd-runner/.ssh/authorized_keys` on the host:
   ```bash
   sudo -u sdvd-runner mkdir -m 700 -p /home/sdvd-runner/.ssh
   sudo -u sdvd-runner tee -a /home/sdvd-runner/.ssh/authorized_keys < sdvd_runner.pub
   sudo -u sdvd-runner chmod 600 /home/sdvd-runner/.ssh/authorized_keys
   ```

4. **Verify** the runner can reach the daemon:
   ```bash
   ssh -i ~/.ssh/sdvd_runner -l sdvd-runner <host> docker info
   ```
   Must succeed without password prompt and report Docker ≥ 20.10.

5. **Reference the host in `SDVD_DOCKER_HOSTS`** with the matching `endpoint` and `sshKey`:
   ```json
   {"id": "vps", "endpoint": "ssh://sdvd-runner@<host>", "sshKey": "~/.ssh/sdvd_runner",
    "serverSlots": 2, "clientSlots": 4}
   ```
   `sshKey` can be omitted to fall back to `~/.ssh/config` + ssh-agent.

A remote host also needs:

- A working Docker daemon (`docker info` returns version >= 20.10).
- Inbound SSH from the coordinator. No reverse tunneling, no public IP requirement on the coordinator.
- At least 2 GB free for transferred Docker images.

On Apple Silicon (arm64) hosts, **enable Rosetta** in Docker Desktop → Settings → General → "Use Rosetta for x86_64/amd64 emulation". The game-server images are amd64; without Rosetta, Docker falls back to QEMU which is ~85% slower (5× worse than Rosetta) and is officially unsupported for .NET runtimes.

### Image distribution

Test images are built once on the coordinator and shipped to remote hosts via `docker save | ssh docker load`, with a content-hash skip when the daemon already holds matching digests. Transfers run with a concurrency cap of 3 across hosts so a many-host fleet doesn't thrash a single upload pipe. When `docker save | ssh docker load` stops scaling for your fleet, the swap point is `ImageDistributor.cs` only — the next step is a coordinator-hosted Docker registry.

Transfer size is governed by the **coordinator's** image store, not the remote's: `docker save` emits compressed OCI blobs under the containerd image store but uncompressed `layer.tar` (~2.5× larger) under the classic overlay2 store. Enabling the containerd store on the coordinator roughly halves what each host has to receive on a fresh transfer (see the trade-off under [Disable the containerd image store](#docker-build-fails-with-parent-snapshot-does-not-exist)).

### Tunneling

For each remote host, the coordinator pre-creates an SSH `ControlMaster` (`ssh -M -N -f -o ControlMaster=auto -o ControlPath=… -o ControlPersist=10m`). For each container start, the coordinator opens a per-port `ssh -O forward -L localhost:{coordinatorPort}:127.0.0.1:{mappedPort} {sshDest}` against that master with `ExitOnForwardFailure=yes`. URL construction always goes through `TunnelManager.OpenAsync` so the same code path works on local and remote hosts — local hosts return the mapped port unchanged.

Tunnels close on container dispose via `ssh -O cancel`. Coordinator shutdown drains all forwards in parallel (bounded per-cancel timeout) before sending `ssh -O exit` to each master.

### Remote-host cold-start floor

Cold container `startupMs` on a remote host is roughly +12–15 s vs the local daemon (e.g. ~28 s local vs ~42 s on a 10-vCPU EPYC VPS for the server image). This is **not** the remote host being slow at running the container — measured directly via `docker run` on each side, the same image reaches its `/health` endpoint within ~0.5 s of the local time. The gap is harness overhead distributed across many small Docker API calls during `Testcontainers.StartAsync` (container create, settings tar-injection, network/port inspect, and ~1 Hz `docker exec curl` wait-strategy probes), each paying SSH round-trip on top of the local cost. The remaining contribution is single-thread CPU clock — most cheap VPS slices clock under the typical developer laptop, but inside container start that adds <1 s.

If you're sizing a remote host or interpreting `infrastructure.jsonl`, treat the +12–15 s as a structural floor rather than a regression to chase. The fix path is consolidating wait-strategy round-trips, tracked alongside the broader Docker-API round-trip reduction work — not provisioning a faster VPS.

### Host disconnect

When a Docker.DotNet call against a remote host throws a transport-class exception (HTTP timeout, broken pipe, SSH auth failure), the host is poisoned for the rest of the run. Future placements skip it; the active test fails with status `host_disconnected` (distinct from cancel/timeout/test-fail). A `KeepConnected` session pinned to a class on a poisoned host fails the rest of that class with `host_disconnected` (N-test cascade). This is by design — re-placing the class on a different host would defeat the no-auto-retry rule and silently mask the disconnect.

If every host is poisoned, the run aborts with a non-zero exit code and a final `run_aborted` event naming the disconnect reasons per host.

### Output

A run produces one run directory under `TestResults/runs/{timestamp}_{sha}/`:

```
run-metadata.json       # coordinator writes
summary.json            # passed/failed/canceled counts + degradation
ctrf-report.json        # CTRF format
diagnostics/
  infrastructure.jsonl  # structured event stream; host_id field on host-fanned
                        #   events (container_started/stopped/oom_killed,
                        #   docker_preflight, image_transfer_*, capacity_*).
flakiness.jsonl
tests/
  {Class}.{Method}/     # per-test artifacts (screenshots, recordings)
```

`make test-summary`, `make test-events`, `make test-infra-log`, and `make test-flaky` work unchanged.
`make test-events` filters `infrastructure.jsonl` by `test.displayName` (jq); per-test events live there alongside the global stream.

### Troubleshooting

- **Run aborts at preflight.** A `docker_preflight_failed` event in `diagnostics/infrastructure.jsonl` names the host and the underlying SDK error. Common causes: stale `ControlMaster` socket (rerun `ssh -O exit <host>` and try again), unreachable SSH endpoint, Docker daemon not running on the remote, or a missing `docker` group on the SSH user.
- **Image transfer fails.** SSH-pipe transfer has no resume-from-byte-N. Improve the link or pre-load images on the host via `docker load` and let the digest-skip handle subsequent runs.
- **Test fails with `host_disconnected`.** The host's daemon stopped responding mid-run. The full reason is in the `host_disconnected` event in `infrastructure.jsonl`. Subsequent tests on that host are skipped for the rest of the run; rerun after fixing the host.
