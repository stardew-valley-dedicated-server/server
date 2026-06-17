# Plan: True-live remote access to the E2E test-UI during an in-progress CI run

## Context

There is no way to watch the test-UI of a running E2E CI job from outside. The
coordinator (`JunimoServer.TestRunner`) runs on an **ephemeral GitHub `ubuntu-latest`
runner** (`.github/workflows/e2e-tests.yml:313`) and in CI it uses `CIRenderer`
(streaming logs only). The Vue SPA + WebSocket server (`WebRenderer`) exists but is
never started in CI, and even locally it binds to `127.0.0.1:0` — loopback only. The
only post-run view is the offline R2-hosted report (`e2e-tests.yml:590-659`).

The goal is **true live** access (a real WebSocket stream, not polling/near-live),
preferably **without a third-party service**. The VPS the harness reaches over SSH is
**publicly addressable**, which makes reverse-SSH the preferred carrier.

**Why a tunnel at all:** the runner is an ephemeral VM with no inbound network path, and
the WebRenderer binds to `127.0.0.1`. Something must carry the loopback server outward.
There are two viable carriers:

### Option A — Reverse SSH to the public VPS (NO third party) — chosen

`ssh -R <vps>:<port>:127.0.0.1:<webPort>` rides the **SSH ControlMaster the harness
already opens to the VPS** for every run (`TunnelManager.SpawnMasterAsync`, `RegisterHostMasterAsync`).
A developer opens `http://<vps-host>:<port>/?t=<token>`.

- **Reuses the existing trusted channel.** The runner already holds the
  `SDVD_DOCKER_HOSTS` key and keeps a live master to the VPS. The reverse forward adds a
  *direction* to that channel — no new secret, no new connection. The only genuinely new
  exposure is a public listening port on the VPS, which the token gate covers.
- **Small, pattern-matching code.** `TunnelManager` already adds forwards via
  `ssh -O forward -L …` over the master (`OpenForwardOnMasterAsync:429-454`) and cancels
  via `-O cancel -L …` (`CancelForwardAsync:549-583`). A reverse forward mirrors these
  with `-R` — ~40 lines, no new SSH process model.
- **Traffic never leaves the project's own infrastructure** — the privacy win over a
  third-party edge.
- **Operator prerequisites (one-time):** `GatewayPorts yes` (or `clientspecified`) in the
  VPS sshd so the remote bind reaches a public interface (OpenSSH man page: a remote
  `bind_address` "will only succeed if the server's `GatewayPorts` option is enabled"),
  plus the chosen port open in the firewall.
- **One claim to verify empirically at build time (~2 min):** that `ssh -O forward`
  accepts `-R` over the control socket on the installed OpenSSH. The man page lists
  `forward`/`cancel` as `-O` commands and confirms `-R`/`-KR` via the `~C` escape, but
  does **not** explicitly state `-O forward` takes `-R`. **Guaranteed fallback** if it's
  rejected: spawn a dedicated `ssh -N -R …` process (the same shape as the master spawn,
  `SpawnMasterAsync:166-201`), tracked and killed on teardown.

### Option B — Cloudflare quick-tunnel (ONE third party, already a dependency)

`cloudflared tunnel --url http://127.0.0.1:<port>` dials **outbound** from the runner to
the loopback server, needs **no inbound, no account, no secret**, and returns an ephemeral
`*.trycloudflare.com` URL that dies with the run. Cloudflare already hosts the offline R2
report, so it's no *new* vendor.

- **Zero operator setup** — no sshd change, no firewall port, no public bind to manage.
- **Downsides:** un-redacted traffic transits Cloudflare's edge (TLS-terminated there — a
  real privacy delta vs. Option A); the service is unofficial/rate-limited/best-effort;
  a `cloudflared` binary is downloaded per run; the hostname is random and scraped from
  its log; background-process lifecycle to manage.

**Recommendation: Option A (reverse-SSH).** It satisfies the no-third-party preference,
keeps un-redacted test data inside the project's own infrastructure, reuses the
already-trusted SSH channel, and is small code. Its costs are a one-time sshd
`GatewayPorts` + firewall change and a build-time check of the `-O forward -R` support
(with a guaranteed fallback). Option B is the right choice only to avoid touching the VPS
sshd at all and accept third-party transit — documented in full at the end.

**Common to both:** the runner-side changes (run WebRenderer in CI via `--live`, fixed
`SDVD_WEB_PORT`, `SDVD_WEB_TOKEN` gate) are **identical**. Only the carrier + workflow
wiring differ. **All access is opt-in** behind a new `workflow_dispatch` boolean — normal
runs stay byte-for-byte unchanged.

---

## Verified facts (load-bearing)

- **Renderer selection** is a 3-way switch — `OutputModeDetector.Detect` maps `--llm` /
  `--web`, else `CI` (`OutputMode.cs:36-45`); renderer constructed once at
  `Program.cs:80-85`.
- **State mutation is renderer-independent.** `RendererDispatchGuard` applies every event
  to `recorder.State` *before* dispatching to the inner renderer; the live broadcast
  callback is wired only when `baseRenderer is WebRenderer` (`Program.cs:87-91`). So
  running `WebRenderer` in CI serves correct state, and the existing abort-path already
  pushes `run_aborted` over the WS via `UnwrapRenderer(renderer) is WebRenderer`
  (`Program.cs:144-159`).
- **WebRenderer bind** is hardcoded `http://127.0.0.1:0` at one site (`WebRenderer.cs:86`);
  port is printed to stderr after `StartAsync` (`WebRenderer.cs:234`).
- **No CORS / no HostFiltering / no Origin check** in the WebRenderer pipeline
  (`WebRenderer.cs:87-224`); `/ws` checks only `IsWebSocketRequest` (`:147`). A request
  arriving via a forwarded port under any Host header is served — and equally, **there is
  no auth today**.
- **The SSH ControlMaster to the VPS is registered during preflight, not at startup.**
  `HostPool.PreflightAsync` (`:388`) calls `tunnels.RegisterHostMasterAsync` per remote host
  (`:422`), invoked from `Program.cs:402` — which is **after** `renderer.InitializeAsync`
  (`:223`) and `OpenBrowser` (`:382`). `TunnelManager` then adds/cancels forwards over the
  master (`OpenForwardOnMasterAsync:429-454`, `CancelForwardAsync:549-583`); a reverse
  forward is the same pattern with `-R`. **Consequence: the reverse forward must be opened
  after `:402`, not after WebRenderer init.** `GatewayPorts yes` on the VPS sshd is required
  for the remote bind to reach a public interface (OpenSSH man page).
- **Live endpoints are UN-redacted.** `/api/state` (`WebRenderer.cs:103`) and the `/ws`
  snapshot (`:163-164`) serve raw `_state.ToSnapshotJson()`; `/artifacts/{**path}`
  (`:116-142`) serves raw screenshots/videos/logs. `ReportRedactor.Scrub` runs **only**
  in the offline `ReportGenerator` (`ReportGenerator.cs:106`), never on the live server.
- **Opt-in toggle** slots into the existing `workflow_dispatch.inputs` block
  (`e2e-tests.yml:42-48`). Live viewing only makes sense from an Actions-tab dispatch
  (where a human is watching), so gating on `workflow_dispatch` input is sufficient.

---

## Changes

### 1. Runner: add a `--live` flag (run WebRenderer in CI)

**`tests/JunimoServer.TestRunner/OutputMode.cs`** — one line in `Detect`, before the
`--web` check, so CI can enter Web mode without implying a local browser-open:

```csharp
if (args.Contains("--live", StringComparer.OrdinalIgnoreCase))
    return OutputMode.Web;
```

This reuses Web mode wholesale. Browser-open is already CI-guarded inside
`WebRenderer.OpenBrowser`, and the end-of-run keypress-hold is CI-guarded in
`Program.cs` — a CI Web run serves the UI and tears down cleanly. No composite renderer
is needed (it would add hand-wired `UnwrapRenderer` hops per
`runner-ui-pipeline-plumbing.md`); the only loss is the streaming CI log during a live
run, acceptable for an opt-in/rare session where the watcher is on the web UI.

### 2. Runner: honor a fixed port via `SDVD_WEB_PORT`

**`tests/JunimoServer.TestRunner/Rendering/WebRenderer.cs:86`** — replace the hardcoded
`:0` so the reverse forward targets a known port:

```csharp
var portEnv = Environment.GetEnvironmentVariable("SDVD_WEB_PORT");
builder.Configuration["Urls"] =
    int.TryParse(portEnv, out var p) && p > 0 ? $"http://127.0.0.1:{p}" : "http://127.0.0.1:0";
```

Unset (every local/dev run) keeps `:0`. The `[WebUI] Server started at` line still prints
the actual port.

### 3. Runner: token-gate the live endpoints (security — required)

The live server is un-redacted (see verified facts). A public URL must not be "anyone who
sees the link." Add minimal auth, **default-off** so dev runs are untouched. Use a
**cookie-exchange gate**, *not* per-request query-param plumbing:

- Read `SDVD_WEB_TOKEN` once in `WebRenderer.InitializeAsync`. When set, insert one
  `app.Use(...)` middleware **right after `builder.Build()`, before `UseWebSockets`
  (`WebRenderer.cs:~87-89`)** so it runs for *every* request including the `/ws` upgrade.
  Logic: if the request carries a valid auth **cookie**, pass through. Else if `?t=<token>`
  matches, set an **HttpOnly, SameSite=Lax cookie** and pass through (this is the one-time
  hand-off from the URL the watcher pastes). Else **404** (not 401 — don't advertise).
  When `SDVD_WEB_TOKEN` is unset, the middleware isn't added — today's behavior exactly.
- **Why a cookie, not `?t=` on every request:** authenticated requests come from **≥8 call
  sites** — `useWebSocket.ts:38` (`/ws`), plus `fetch` in `useTestStore.ts` (`/api/state`,
  `/api/command`, `summary.json`), `useScreenshotCache.ts:27`, `useLogFile.ts:49`,
  `SyncedVideos.vue:905`, `VncFrame.vue:55`. The browser sends a same-origin cookie on all
  of them **automatically, including the WebSocket upgrade** — so the SPA needs **zero
  changes** (no `useTestStore.ts` edit, no risk of a missed call site 404ing behind the
  gate). The `WebSocket` constructor can't set request headers anyway, so cookie/query are
  the only WS-auth options; the cookie covers it cleanly.

This turns "anyone with the URL" into "URL **and** token." The token is generated per run
in the workflow and only written to the run's step summary (visible to repo collaborators on
an already maintainer-gated workflow). No test-ui source change is required — but still run
`make build-test-ui` if any SPA file is touched (`test-ui-build.md`).

### 4. Runner: open a reverse SSH forward to the VPS, print the public URL

**`tests/JunimoServer.Tests/Infrastructure/TunnelManager.cs`** — add a reverse-forward
method mirroring the existing `-L` pair:

- `OpenReverseAsync(hostId, sshDestination, sshKeyPath, vpsBindPort, localWebPort, ct)` —
  runs `ssh -O forward -R 0.0.0.0:<vpsBindPort>:127.0.0.1:<localWebPort> <dest>` over the
  host's existing ControlMaster (same `ResolveMasterOrThrow` + `master.ControlPath` path as
  `OpenForwardOnMasterAsync:429-454`). Teardown mirrors `CancelForwardAsync:549-583` with
  `-O cancel -R …`. Return a lease like `ForwardLease` (or extend it with an `isReverse`
  flag) so disposal cancels the forward.
- **Build-time check (do first, ~2 min):** confirm `ssh -O forward -R` is accepted by the
  installed OpenSSH (the man page lists `-O forward`/`cancel` and confirms `-R` via the
  `~C` escape, but doesn't explicitly state `-O forward` takes `-R`). If rejected,
  implement the **fallback**: spawn a dedicated `ssh -N -R …` child (same shape as
  `SpawnMasterAsync:166-201`), track its PID, kill on teardown. Either way the call site
  and signature are unchanged.

**`tests/JunimoServer.TestRunner/Program.cs`** — open the reverse forward at the **right
point in the sequence**. The WebRenderer port is known after `InitializeAsync` (`:223`),
but the SSH ControlMaster to the VPS does **not exist until `PreflightAsync` runs
(`:402`)** — calling `OpenReverseAsync` any earlier throws `ResolveMasterOrThrow` ("No SSH
ControlMaster registered"). So, when `--live` is set, **after the preflight try/catch
completes successfully (after `:408`), and outside the 30s `preflightCts`** (so a slow
forward doesn't consume or get cancelled by the preflight budget): read the VPS public host
+ bind port from env (`SDVD_LIVE_VPS_HOST`, `SDVD_LIVE_VPS_PORT`), call `OpenReverseAsync`
over `HostPool.Instance.First` (the VPS already in `SDVD_DOCKER_HOSTS` — no second SSH
connection), and print `[WebUI] LIVE: http://<SDVD_LIVE_VPS_HOST>:<port>/?t=<token>` to
stderr so the workflow can lift it into the step summary. This is still early — preflight
precedes the long test phase — so the URL is live before tests start. Dispose the lease in
the run's outer finally (alongside renderer disposal); `TunnelManager.DisposeAsync`'s
existing drain (`:637-640`, cancels all forwards + exits masters) is the backstop on the
abort path, where `BeginAbort` doesn't touch the lease directly.

**Post-run linger (required — the UI otherwise dies at completion).** The existing
keypress-hold that keeps Kestrel alive after a run is explicitly `!IsCIEnvironment()`
(`Program.cs:698-700`), so in CI the WebRenderer is disposed the instant the last test
finishes — a watcher loses the final state immediately, and anyone opening the URL just
after completion gets nothing. Under `--live`, add a **bounded** hold before
`renderer.DisposeAsync()` (`:708`). Note `WaitForKeypressOrShutdownAsync` (`WebRenderer.cs:306`)
takes **no timeout** — so add a small `WaitForShutdownOrTimeoutAsync(TimeSpan)` that does
`Task.WhenAny(_shutdownSignal.Task, Task.Delay(hold))` (reusing the existing
`_shutdownSignal`/`SignalShutdown` at `:321`), and call it with `SDVD_LIVE_HOLD_SECONDS`
(default ~120s). Bounded so a forgotten session can't wedge the concurrency-singleton runner
(the 120-min job cap is the backstop, but the hold should be short). Skip the hold when not
`--live` (today's behavior).

### 5. Workflow: opt-in input + publish the live URL

**`.github/workflows/e2e-tests.yml`**

- Add a `live` boolean to `workflow_dispatch.inputs` (`:42-48`):

  ```yaml
  live:
    description: 'Open a live SSH tunnel to the test UI via the VPS (maintainer-only; un-redacted state).'
    type: boolean
    required: false
    default: false
  ```

- On **"Run E2E suite"** (`:500-515`), only when `inputs.live == true`: set env
  `SDVD_WEB_PORT=41999`, `SDVD_WEB_TOKEN=$(openssl rand -hex 16)`,
  `SDVD_LIVE_VPS_HOST` (the VPS public hostname — a new `vars.LIVE_VPS_HOST`, public by
  design, *not* a secret to avoid the `-`-masking trap the R2 vars comment documents at
  `:584-589`), `SDVD_LIVE_VPS_PORT=41999`, `SDVD_LIVE_HOLD_SECONDS=120` (post-run linger),
  and pass `--live` (built into a shell arg the same way the existing `--filter` is
  conditionally appended — don't interpolate into the command line).
- **Surface the URL *during* the run — the runner must write it itself.** The `Run E2E
  suite` step doesn't return until the whole suite finishes, so a *later* workflow step
  can't lift the URL mid-run (steps are sequential). Instead, the runner appends the
  "🔴 LIVE test session" block (URL + un-redacted-state warning) **directly to
  `$GITHUB_STEP_SUMMARY`** (an env var the step inherits, pointing at a file; appending to
  it updates the live Summary tab) right after `OpenReverseAsync` succeeds. Since that's
  just after preflight — before the long test phase — the watcher sees the URL in the
  Summary tab while the run is live. The `[WebUI] LIVE: …` stderr line stays for terminal
  users. (When `$GITHUB_STEP_SUMMARY` is unset, e.g. local, just print to stderr.)

No `cloudflared` install, no background process to track in the workflow — the forward's
lifetime is the runner process's lifetime, torn down in its finally. The "Build test-UI
SPA" step already runs in this job, so `WebRenderer` serves the real built SPA.

**Operator prerequisites (one-time, documented in the PR):** `GatewayPorts yes` (or
`clientspecified`) in the VPS sshd_config; port `41999` open in the VPS firewall; a
`LIVE_VPS_HOST` repo/environment variable set to the VPS's public hostname.

**Pinned port — stable URL.** Both the runner bind (`SDVD_WEB_PORT`) and the VPS remote
bind (`SDVD_LIVE_VPS_PORT`) are the same fixed `41999`, so the live URL is identical every
run — `http://<LIVE_VPS_HOST>:41999/?t=<token>` (only the per-run token rotates). A single
pinned port is safe because the E2E job is a concurrency singleton (`e2e-tests.yml`
`group: e2e-tests-singleton`) — at most one run binds it at a time. Keep the SSH-tunnel
infrastructure ports (the existing `-L` forwards) on their dynamic loopback scheme; this
fixed port is only the live-view bind.

---

## Critical files

| File | Change |
|---|---|
| `tests/JunimoServer.TestRunner/OutputMode.cs` | `--live` → `Web` (one line, `Detect`) |
| `tests/JunimoServer.TestRunner/Rendering/WebRenderer.cs` | `SDVD_WEB_PORT` bind (`:86`); cookie-exchange token middleware before `UseWebSockets` (`:~87-89`); `WaitForShutdownOrTimeoutAsync` for the linger |
| `tests/JunimoServer.Tests/Infrastructure/TunnelManager.cs` | `OpenReverseAsync` (+ `-O cancel -R` teardown / `ssh -N -R` fallback), mirroring `OpenForwardOnMasterAsync:429-454` + `CancelForwardAsync:549-583`; reverse `ForwardLease` |
| `tests/JunimoServer.TestRunner/Program.cs` | when `--live`: open reverse forward **after `PreflightAsync` (`:402`)**, write `[WebUI] LIVE: …` to `$GITHUB_STEP_SUMMARY`, bounded post-run hold before disposal (`:708`), dispose lease in finally |
| `.github/workflows/e2e-tests.yml` | `live` input (`:42-48`); `SDVD_WEB_PORT`/`SDVD_WEB_TOKEN`/`SDVD_LIVE_VPS_HOST`/`SDVD_LIVE_VPS_PORT`/`SDVD_LIVE_HOLD_SECONDS` + `--live` on the run step |

**No `tests/test-ui/` change** — the cookie gate means the SPA forwards auth automatically;
all ≥8 fetch/WS call sites work unmodified.

Reused as-is: the per-host `ssh -M` ControlMaster + `-O forward`/`cancel` pattern
(`TunnelManager`), `RendererDispatchGuard` state mutation + WS broadcast (`Program.cs:87-91`),
abort-path WS push (`Program.cs:144-159`), CI-guarded browser-open (`WebRenderer.cs:251`), the
existing conditional-arg pattern for `--filter`, and the SPA's snapshot+event hydration.

---

## Security note (carry into the PR description)

The live `/api/state`, `/ws`, and `/artifacts/*` serve **un-redacted** state — raw
screenshots, container logs, diagnostics, and possibly the VPS host and Steam-credential
error text that the offline report masks (`ReportRedactor` runs only in
`ReportGenerator.cs:106`). With reverse-SSH the data **never leaves the project's own
infrastructure** (runner → VPS only), so the exposure is a public *listening port* on the
VPS, not third-party transit. The token gate (change 3) is the mitigation for that port; the
workflow is maintainer-gated (`e2e-tests.yml` gate job) and the feature is opt-in. Do
**not** ship the live path without the token gate. Plain HTTP over the VPS port sends the
token in the clear — acceptable for an opt-in maintainer session, but note it; TLS would
need a cert on the VPS (out of scope for v1).

---

## Verification (end-to-end)

1. **Build gates.** `dotnet build ./tests/JunimoServer.TestRunner`. (No test-ui change, so
   `make build-test-ui` is only needed if an SPA file was touched.)
2. **Local live run, no token (back-compat).** `SDVD_WEB_PORT=41999 make test-web` (or
   `dotnet run -- --live`); confirm the UI loads at `127.0.0.1:41999`, the WS streams live,
   and **no** auth is required when `SDVD_WEB_TOKEN` is unset (middleware not added).
3. **Local token gate (cookie exchange).** Re-run with `SDVD_WEB_TOKEN=abc123`; confirm a
   request with **no cookie and no `?t=`** 404s (`/api/state`, `/ws`, `/artifacts/*`); that
   loading `http://127.0.0.1:41999/?t=abc123` sets the cookie and the **full SPA then works
   unmodified** — live WS, screenshots, videos, logs (the ≥8 call sites) all 200 on the
   cookie; and that a wrong `?t=` still 404s.
4. **`-O forward -R` support check (do early).** Against the registered master, run
   `ssh -O forward -R 0.0.0.0:41999:127.0.0.1:41999 <dest>` and confirm exit 0; then
   `ssh -O cancel -R …`. If the installed OpenSSH rejects `-R` over `-O forward`, switch
   `OpenReverseAsync` to the `ssh -N -R` child fallback before wiring the rest.
5. **Local reverse-forward smoke.** With the WebRenderer up on `41999` and a reachable test
   host that has `GatewayPorts yes`, open the reverse forward and load
   `http://<host>:41999/?t=abc123` from another network; confirm the live WS stream renders
   and the un-tokened URL 404s.
6. **CI dispatch.** Trigger the workflow from the Actions tab with `live = true` against a
   short `filter`; confirm: (a) the **Summary tab** shows the
   `http://<LIVE_VPS_HOST>:41999/?t=…` URL *during* the run (written by the runner, not a
   later step), (b) it renders the live UI with WS updates, (c) the final state stays
   viewable for the `SDVD_LIVE_HOLD_SECONDS` window after the last test, then (d) the
   forward is gone (port no longer accepts). Trigger once with `live = false` (and via
   `schedule`/`issue_comment`) to confirm the live path is skipped and the run is unchanged
   (`CIRenderer`, `:0` bind, no post-run hold).

---

## Alternative (one third party, zero VPS setup): Cloudflare quick-tunnel

If touching the VPS sshd (`GatewayPorts`) / firewall is undesirable, swap changes 4–5 for a
`cloudflared` quick-tunnel. Runner changes 1–3 are **identical**; `TunnelManager` and
`Program.cs` are untouched (no reverse forward).

- **Workflow:** before "Run E2E suite" (gated `inputs.live == true`): install the
  `cloudflared` linux-amd64 binary; generate `SDVD_WEB_TOKEN`; launch
  `cloudflared tunnel --no-autoupdate --url http://127.0.0.1:41999` in the background; poll
  its log ≤30s for the `https://<sub>.trycloudflare.com` URL; publish `<url>?t=<token>` to
  `$GITHUB_STEP_SUMMARY` (with the un-redacted warning). Set `SDVD_WEB_PORT=41999`,
  `SDVD_WEB_TOKEN`, `--live` on the run step. After the run: `kill "$CLOUDFLARED_PID"`.
- **Trade-off vs. reverse-SSH:** zero VPS/operator setup and HTTPS for free, but
  un-redacted traffic transits Cloudflare's edge, the service is unofficial / rate-limited /
  best-effort, a binary is downloaded per run, and there's a background process + URL-scrape
  to manage. Reach for this only to avoid the one-time `GatewayPorts` + firewall change.
