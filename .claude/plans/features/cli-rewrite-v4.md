# Plan: Unified CLI/TUI Setup & Management Tool (`sdvd`) — v4

## Scope (this plan)

This plan ships **Phase 1 (Setup Wizard + Core CLI)** and **Phase 2 (Operations)** only. Both are local-only and do not depend on missing server-side endpoints.

**Phases 3 (TUI Dashboard) and 4 (Migration / Diagnose / Config) are deferred** — they depend on server-side endpoints that do not exist today (`GET /version`, `GET /save-status`, `POST /pause`, `POST /backup`, `GET /logs` SSE, `POST /diagnose/{nat,ping,speed}`). Their sections are kept below as design notes but are NOT in scope for the implementation that lands from this plan. Each missing endpoint is a concrete TODO blocker — see "Deferred phases — server API gaps" below.

## Context

Users currently set up JunimoServer through a manual multi-step process: downloading files from GitHub, hand-editing `.env`, running Docker commands, and reading troubleshooting docs when things go wrong. The in-container CLI (tmux-based split-pane) provides basic log viewing and command input but requires `docker exec` to access and can't help with host-level operations like setup, backup, or upgrades.

**Goal:** Build a single cross-platform CLI/TUI tool that serves as the unified interface for the entire server lifecycle — from first-time setup through daily management — replacing the need to manually run Docker commands or attach to in-container sessions.

**Key insight from exploration:** A React/Ink TUI prototype already exists at `tools/.playground/cli/` (runs under `tsx`). The HTTP REST API (`mod/JunimoServer/Services/Api/ApiService.cs`) already exposes comprehensive remote control (status, players, rendering, time, admin, farmhands, cabins, WebSocket chat). The `netdebug` tool provides network diagnostics.

**Runtime swap from prototype**: the prototype uses `tsx` as its TypeScript runtime. This plan picks `bun` for distribution (faster startup, `bun build --compile` for static binaries). Prototype code that lands in Phase 3 will need a small runtime port — not a rewrite, but not a copy-paste either.

---

## Architectural Assumptions & Constraints

Before defining features, this section answers foundational questions about the environment this tool operates in. Every answer is backed by evidence from the codebase.

### Deployment Topology

**Supported environments** (per README + `docs/admins/quick-start/prerequisites.md`):
- Local machine (Windows/macOS with Docker Desktop, Linux with Docker Engine)
- VPS / dedicated server (production, Linux recommended)
- Cloud providers (AWS, Azure, DigitalOcean — mentioned in CI/CD docs)

**Network model:** Game traffic uses Steam SDR relays (NAT-traversal, no port forwarding) by default. Administrative interfaces (API port 8080, VNC port 5800) are bound to `0.0.0.0` — exposed on all interfaces with no access controls beyond an optional API key.

**Reverse proxy:** Not documented, not supported out of the box. No TLS termination exists at the application level. The API server uses raw `HttpListener` binding to `http://+:{port}/`. Users who expose the API to the internet are currently unprotected unless they set `API_KEY` and handle TLS themselves.

**Implication for the CLI:** The tool must work across all these topologies. When running locally, it can shell out to Docker. When connecting to a remote server, it can only use the HTTP API. The tool must **never assume Docker is local**.

### Security Model

**This is an admin tool, not a public interface.** It manages the server — start/stop, backup, config changes, player management. It should never be exposed to untrusted users.

**Current API security state** (from `ApiService.cs`):

| Feature | Status | Detail |
|---------|--------|--------|
| Authentication | Optional | `API_KEY` env var; Bearer token. Disabled if unset. |
| Rate limiting | None | Unlimited requests, no brute-force protection |
| TLS/HTTPS | None | Plaintext HTTP only (`http://+:{port}/`) |
| CORS | Wide open | `Access-Control-Allow-Origin: *` |
| WebSocket auth | Yes | 10s timeout, same API key, `ws://` not `wss://` |

**In-game password system** is separate and well-hardened (constant-time comparison, max attempts, auth timeout, player sandboxing). That's player-facing, not admin-facing.

**Implications for the CLI:**
- The CLI must **require `API_KEY`** when connecting to any server (refuse to connect without it).
- Remote mode should **warn loudly** if connecting over plaintext HTTP to a non-localhost address.
- The CLI itself adds no new attack surface — it's a client consuming an existing API.
- TLS is the server's responsibility (via reverse proxy). The CLI should document this but can't enforce it.
- Server-side rate limiting, CORS restrictions, and TLS are not part of this plan. They belong in a separate server-hardening plan if and when they're prioritized.

### Remote Connections

**Yes, remote connections are expected and are a primary use case.** The TUI dashboard connects via HTTP API + WebSocket, meaning it works:
- On the same machine as Docker (local)
- From a different machine on the LAN
- Over the internet (if API port is exposed, ideally behind reverse proxy + TLS)
- Via SSH tunnel (`ssh -L 8080:localhost:8080 user@server`, then `sdvd dashboard`)

**TLS is not mandatory but strongly recommended for non-localhost connections.** The CLI should:
- Accept `--url` for remote connections (e.g., `https://my-server.com:8080`)
- Support both `http://` and `https://` URLs
- Warn when using `http://` with a non-localhost/non-private-IP URL
- Document SSH tunneling as the recommended approach when TLS isn't available

### Versioning Contract

**Single version number** for the entire project. Managed by release-please with semantic versioning. All three Docker images (`sdvd/server`, `sdvd/steam-service`, `sdvd/discord-bot`) share the same version tag (verified: `docker-compose.yml:4,49,72` all use `${IMAGE_VERSION:-latest}`). The CLI tracks this same version.

**CLI ↔ Server compatibility:** No formal compatibility matrix exists today, no `GET /version` endpoint, and no deployed alternate-version CLIs. A version-compatibility check is therefore unbacked scaffolding (`.claude/rules/universal/simplest-solution.md`) — leave it out of this plan. If multi-version operation becomes a real concern later, add the check then with a real consumer.

**The CLI does not manage multiple server versions simultaneously.** It targets one installation directory with one `docker-compose.yml` and one `.env`. Running multiple instances requires separate directories with separate CLIs (or at minimum separate `--cwd` flags).

**IMAGE_VERSION** lives in `.env` (not in `docker-compose.yml`). The compose file references `${IMAGE_VERSION:-latest}`. The CLI's `sdvd upgrade` modifies `.env` to change the version.

### Compose File Authority

**The `docker-compose.yml` is the source of truth for service definitions.** It is:
- Downloaded from GitHub (static, not generated)
- Not intended for user modification — all customization via `.env`
- Version-agnostic — the same compose file works across versions (image tag comes from `.env`)

The CLI should:
- Download the compose file during `sdvd setup` (pinned to a specific release tag)
- Detect compose file version via a comment header or hash
- Offer to update it during `sdvd upgrade` if a newer version exists
- Never modify the compose file's service definitions — only stamp metadata

### Docker Compatibility Target

**Primary target: Docker Engine + Compose V2 on Linux** (production recommendation from docs).

| Runtime | Support Level | Notes |
|---------|--------------|-------|
| Docker Engine (Linux) | Full | Primary target, all features |
| Docker Desktop (Win/Mac) | Full | Same API, different socket paths |
| Podman | Unsupported | `SYS_TIME` cap may fail in rootless; `stdin_open`+`tty` may behave differently; not tested |
| Rootless Docker | Unknown | Not tested, `SYS_TIME` may require `--privileged` |

The CLI should:
- Detect the runtime via `docker info --format json` and report it in `sdvd check`
- Warn (not block) if Podman or rootless Docker is detected
- Not attempt Podman-specific workarounds — that's a server-side concern

### Backup Consistency

**The game saves once per in-game day** (end-of-day, during the shipping menu). The mod auto-clicks through the shipping menu (`AlwaysOn.cs`). SMAPI creates a zip backup after each save (`/data/Stardew/save-backups/`).

**There is no save lock for external tools.** The mod checks `Game1.game1.IsSaving` internally (e.g., to block farmhand deletion during save) but doesn't expose a lock to external processes.

**Can the game pause?** Yes — automatically when no players are online (`Game1.netWorldState.Value.IsPaused`). There is no API endpoint to manually pause/unpause, and `POST /time` only sets the clock, it doesn't freeze progression.

**Filesystem quiescing:** Not available. Docker volumes can be read while the container writes to them. The `saves` volume is an ext4 filesystem inside a Docker volume — no snapshot or COW mechanism.

**Implications for `sdvd backup`:**
- **Approach (only mode shipped):** Stop the container (`docker compose stop server`), tar the volume, restart. Guarantees consistency.
- **No `--live` mode.** A live tar carries a known partial-save risk during the ~2-second save window. Per `.claude/rules/universal/holistic-or-explicit-todo.md`, shipping a knowingly-flawed mode behind a flag with a "use at your own risk" disclaimer hides the gap; we don't ship it. SMAPI's own per-day zip backup (`/data/Stardew/save-backups/`) already covers the "I forgot to stop the server" case.
- **Prerequisite for ever adding a live mode:** add `GET /save-status` (idle / saving / just-saved) so the CLI can wait for a clean window. Until that endpoint exists, there's no safe way to live-backup.

### Dashboard Network Resilience (Phase 3 — deferred)

**What happens when the network drops during a dashboard session?**

The TUI connects via HTTP polling (status) + WebSocket (real-time events). On disconnect:
- Status bar should show "Disconnected" with elapsed time.
- Log view freezes (last known state).
- Command input rejects with clear feedback — surface the failure to the operator immediately rather than retrying silently. Per `.claude/rules/universal/retry-is-evidence-of-root-cause.md`, the dashboard is an admin tool typically run on LAN or over an SSH tunnel; an exponential reconnect-backoff loop would be retry-as-feature for a connection that, when broken, the operator wants to know about.
- Manual reconnect: a single keybind (`r`) attempts one immediate reconnection, with no automatic backoff.
- After reconnection: re-fetch full status, resume WebSocket stream.

The WebSocket already has a 30s cleanup interval server-side (`mod/JunimoServer/Services/Api/ApiService.cs:1385-1386`). The client sends pings every 10s to detect drops quickly.

### CLI Identity: Local Manager + Remote Admin Client

**Both.** The tool has two distinct operational modes:

| | Local mode (no `--url`) | Remote mode (`--url` provided) |
|---|---|---|
| **Docker access** | Yes — shells out to `docker compose` | No — Docker is on a different machine |
| **File access** | Yes — reads `.env`, writes `.sdvd/`, modifies settings | No — can only interact via API |
| **Available commands** | All (setup, backup, restore, upgrade, dashboard, diagnose) | Dashboard, status, diagnose (API-only checks), config get |
| **Backup** | Yes — stop container + tar volume | No — requires `POST /backup` (deferred) |
| **Setup** | Yes — full wizard | No — setup requires Docker access |
| **Upgrade** | Yes — pull + restart | No — would need remote Docker access |

Commands that require Docker should **fail gracefully in remote mode** with a clear message: "This command requires local Docker access. Connect via SSH or run the CLI on the server host."

---

## Tech Stack

| Component | Choice | Rationale |
|-----------|--------|-----------|
| Language | TypeScript | Already in ecosystem (discord-bot, docs), best CLI library support |
| Runtime | Bun | Already used by discord-bot, fast startup, `bun build --compile` for binaries |
| CLI framework | `commander` | Lightweight, mature, standard subcommand pattern |
| Wizard prompts | `@clack/prompts` | Modern, beautiful (used by create-svelte/astro), works with Bun |
| TUI framework | `ink` (React for terminals) | Prototype already exists at `tools/.playground/cli/`, component model ideal for dashboard |
| API client | `fetch` (built-in) | Bun has native fetch, no extra deps needed |
| Distribution | npm (`npx @junimoserver/cli`) + GitHub Release binaries | npx for convenience, binaries for users without Node.js |

**Location:** `tools/cli/` (follows `tools/discord-bot/` Bun/TypeScript pattern). Note: `tools/steam-service/` is C# net10.0, not Bun — it is not a precedent for this stack.

---

## Feature Phases

### Phase 1: Setup Wizard + Core CLI

**New files:**
- `tools/cli/package.json`
- `tools/cli/tsconfig.json`
- `tools/cli/src/index.ts` — Entry point, command routing, mode detection (local vs remote)
- `tools/cli/src/commands/setup.ts` — Full guided setup wizard (local only)
- `tools/cli/src/commands/check.ts` — Pre-flight checks
- `tools/cli/src/commands/status.ts` — Server status (via API)
- `tools/cli/src/commands/logs.ts` — Log streaming wrapper
- `tools/cli/src/lib/docker.ts` — Docker CLI wrapper (spawn `docker compose` commands)
- `tools/cli/src/lib/env.ts` — `.env` parser/writer/validator
- `tools/cli/src/lib/api-client.ts` — HTTP API client for server
- `tools/cli/src/lib/checks.ts` — Check system (individual check implementations)
- `tools/cli/src/lib/config.ts` — `.sdvd/config.json` state management
- `tools/cli/src/lib/ui.ts` — Shared formatting utilities

**Mode detection (`sdvd` with no args):**
1. If `--url` is passed → remote mode, skip to API health check → dashboard
2. If no installation detected locally → setup wizard
3. If installation found but critical checks fail → wizard with diagnostics
4. If server healthy → dashboard
5. If server not running but ready → dashboard (with "Start" option)

**`sdvd setup` flow (local only, interactive wizard using @clack/prompts):**
1. Prerequisites check — Docker version, Compose V2, disk space, port conflicts
2. .env generation — Prompt for STEAM_USERNAME, STEAM_PASSWORD, VNC_PASSWORD; optional SERVER_PASSWORD, API_KEY (auto-generate), ports (only if conflicts)
3. Download `docker-compose.yml` from GitHub (pinned to release tag matching CLI version)
4. `docker compose pull` with progress spinner
5. `docker compose run --rm -it steam-auth setup` with TTY passthrough for Steam Guard
6. `docker compose up -d`
7. Health poll until server responds on API
8. Display invite code, connection instructions, and summary

**`sdvd check` — granular pre-flight check system**

Mode is flag-based: no `--url` = local mode (all checks), `--url` present = remote mode (API-only checks).

**Local-only checks** (require Docker on the same machine, skipped in remote mode):

| Check | Severity | What it validates |
|-------|----------|-------------------|
| DockerVersion | Critical | Docker Engine >= 20.x installed and running |
| ComposeVersion | Critical | Docker Compose V2 available |
| DockerRuntime | Info | Reports engine type (Docker Engine, Docker Desktop, Podman — warn on Podman) |
| DiskSpace | Critical | Minimum 2GB free on Docker data root |
| PortAvailability | Critical | Configured ports not in use by other processes |
| EnvConfig | Critical | `.env` exists with required variables set |

**Universal checks** (work via API, run in both modes):

| Check | Severity | What it validates |
|-------|----------|-------------------|
| ApiHealth | Critical | Server API responding (GET /health) |
| ApiAuth | Critical | API key is set and accepted (refuse to connect without it) |
| SteamAuth | Critical | Steam auth service healthy |
| GameFiles | Critical | Server started successfully (implies game files present) |
| TimeSyncDrift | Warning | Container time vs NTP, warn if >30s drift (GOG Galaxy auth) |
| SteamTokenAge | Warning | Estimated token age vs 200-day expiry, warn if <30 days remaining |
| TlsWarning | Warning | Warn if connecting via `http://` to a non-localhost/non-private-IP address |

Each check returns pass/warn/fail with actionable troubleshooting text.

**`sdvd status` — server status via API:**
- Server info (GET /status — players, invite code, farm info, game time, rendering)
- Container states (via `docker compose ps --format json` in local mode, omitted in remote)
- Steam token age estimate (from `.sdvd/config.json` in local mode)
- System resources (via `docker stats --no-stream --format json` in local mode)

**`sdvd logs [--server|--steam|--discord] [-f] [--level error|warn] [--search PATTERN]`** (local only — requires Docker)
- Service filtering: `--server`, `--steam`, `--discord`
- Level filtering: `--level error` (errors only), `--level warn` (warnings+)
- Text search: `--search "pattern"` for grep-style filtering
- Color-coded output (errors red, warnings yellow)

**State directory (`.sdvd/` alongside docker-compose.yml):**
```
.sdvd/
  config.json    # install timestamp, server version, token expiry estimate, API URL
  backups/       # backup archives
```

### Phase 2: Operations

**New files:**
- `tools/cli/src/commands/backup.ts`
- `tools/cli/src/commands/restore.ts`
- `tools/cli/src/commands/upgrade.ts`
- `tools/cli/src/commands/steam.ts` — `sdvd steam status`, `sdvd steam renew`
- `tools/cli/src/lib/backup-manager.ts`

**`sdvd backup [--all]`** (local only — requires Docker):
- Stop server → tar saves volume + settings → restart. Guarantees consistency.
- `--all`: Also backup `steam-session` volume.
- No `--live` mode (see "Backup Consistency" above).
- Format: tar.gz with metadata JSON (timestamp, server version, CLI version, volume list), stored in `.sdvd/backups/`.
- Uses `docker run --rm -v volume:/data alpine tar czf ...` (cross-platform — runs in Linux container regardless of host OS).

**`sdvd restore <backup-file>`** (local only):
- Validate backup metadata (version compatibility check)
- Stop server, restore volumes, restart
- Confirmation prompt before destructive operation

**`sdvd upgrade [--version X.Y.Z]`** (local only):
- Auto-backup before upgrade (stop-and-backup)
- Update `IMAGE_VERSION` in `.env`
- Check if `docker-compose.yml` needs updating (compare hash against release)
- `docker compose pull && docker compose down && docker compose up -d`
- Health check after restart
- If unhealthy, offer rollback (restore pre-upgrade backup, revert `IMAGE_VERSION`)

**`sdvd steam status`:**
- Token age from `.sdvd/config.json` or container inspection
- Estimated expiry (200 days from auth)
- Warning if approaching expiry

**`sdvd steam renew`** (local only):
- Re-run `docker compose run --rm -it steam-auth setup` with TTY passthrough

### Phase 3: TUI Dashboard (DEFERRED — depends on missing server endpoints)

> **NOT in scope for this plan.** Phase 3 requires `GET /logs` (SSE or WebSocket log stream) for remote-mode log streaming and `GET /version` for the version handshake. Neither exists. The remainder of this section is preserved as design notes for the follow-up plan.

**New files:**
- `tools/cli/src/commands/dashboard.ts` — Entry point for TUI mode
- `tools/cli/src/tui/App.tsx` — Root Ink component
- `tools/cli/src/tui/components/LogView.tsx` — Scrollable log panel (evolve from playground prototype)
- `tools/cli/src/tui/components/StatusBar.tsx` — Server status, players, resources
- `tools/cli/src/tui/components/CommandInput.tsx` — Command input with history
- `tools/cli/src/tui/components/PlayerList.tsx` — Connected players panel
- `tools/cli/src/tui/components/ConnectionStatus.tsx` — Connected/disconnected/reconnecting indicator
- `tools/cli/src/tui/hooks/useServerStatus.ts` — Polling hook for GET /status
- `tools/cli/src/tui/hooks/useWebSocket.ts` — WebSocket hook with auto-reconnect
- `tools/cli/src/tui/hooks/useLogs.ts` — Log streaming hook

**`sdvd dashboard [--url URL] [--api-key KEY]`:**

Works in both local and remote mode. Connects via HTTP API + WebSocket.

**Layout:**
```
┌─────────────────────────────────────────────────┐
│ SDVD Dashboard  │ Farm: Junimo │ Players: 3/10  │
│ Spring 15 Y2    │ ▶ Running    │ Invite: G-xxx  │
├─────────────────────────────────────────────────┤
│                                                 │
│  [Server Log Output - scrollable]               │
│  ...                                            │
│  [12:34] Player1 joined                         │
│  [12:35] Day saved                              │
│                                                 │
├─────────────────────────────────────────────────┤
│ > command input here                            │
└─────────────────────────────────────────────────┘
```

**Features:**
- Real-time log streaming. **Pick one source.** Per `.claude/rules/universal/prefer-live-stream-over-disk-artifact.md`, two pipelines (local `docker compose logs -f` vs remote WebSocket) hidden behind one UI fragment the implementation. Resolve when un-deferring Phase 3: either (a) wait for `GET /logs` SSE so both modes use the same WebSocket source, or (b) drop remote-mode log streaming until the endpoint exists. Do not ship the dual-source design.
- Log filtering: cycle through All / Errors / Warnings+ / Search pattern (Ctrl+F)
- Command input → sent via WebSocket (preferred) or HTTP API fallback
- Status bar with live player count, season/day, invite code
- Keybinds: Ctrl+C exit, Tab cycle focus, PageUp/Down scroll, Ctrl+R toggle rendering, Ctrl+F search logs
- Works remotely: `sdvd dashboard --url http://my-server:8080 --api-key abc123`

**Network resilience:**
- Connection status indicator: Connected / Disconnected (Xs) / Reconnecting...
- On disconnect: status bar shows elapsed disconnect time, log view freezes, command input rejects with feedback
- Auto-reconnect with exponential backoff (1s → 2s → 4s → 8s → max 30s)
- Client-side ping every 10s to detect drops quickly (server cleanup is 30s)
- On reconnect: re-fetch full status, resume WebSocket stream
- After prolonged disconnect (5 min configurable): exit with error rather than silently hanging

**Reuses from playground prototype (`tools/.playground/cli/`):**
- Log scrolling with auto-scroll behavior (scroll lock when user scrolls up)
- Line buffering and incremental reading approach
- Input/output pane separation pattern

### Phase 4: Migration, Import & Advanced (DEFERRED — depends on missing server endpoints)

> **NOT in scope for this plan.** `sdvd diagnose --url` requires `POST /diagnose/{nat,ping,speed}` which doesn't exist; `sdvd config get` for remote requires `GET /settings` (verify before un-deferring). The local-only subcommands (`import-save`, `migrate`, `discord-setup`) could ship in Phase 1+2 if there's a real consumer pulling for them — flag for re-scoping before implementation.

**New files:**
- `tools/cli/src/commands/import-save.ts`
- `tools/cli/src/commands/migrate.ts`
- `tools/cli/src/commands/diagnose.ts`
- `tools/cli/src/commands/discord-setup.ts`
- `tools/cli/src/commands/config.ts` — View/edit server-settings.json

**`sdvd import-save <path>`** (local only):
- Validate save directory structure (must contain `SaveGameInfo`, `FarmName_ID` file)
- Stop server, copy into saves volume, restart
- Verify save appears

**`sdvd migrate`** (local only):
- Detect current config format version vs latest
- Apply .env variable renames/additions
- Apply docker-compose.yml structural changes
- Show diff before applying

**`sdvd diagnose`:**

Interactive diagnostics menu. Runs all applicable checks first (respecting local/remote mode), then offers individual deep-dive tools:

```
  Diagnostics

  ✓ API Health         ✓ Steam Auth       ✓ Game Files
  ✓ Docker 27.4.0      ✓ Compose V2       ! Token: 158 days remaining
  ✓ Disk: 45GB free    ✓ Ports clear      ✓ Time sync OK

  Select diagnostic:
  > NAT Discovery (hairpinning, mapping, filtering)
    Ping Test (latency to auth.gog.com)
    Speed Test (download via Cloudflare)
    Steam Auth Details
    Container Resources (CPU, memory, disk I/O)
    Back
```

- In local mode: runs `netdebug nat/ping/speed` inside the container via `docker exec`
- In remote mode: shows only API-reachable checks; deep-dive tools (NAT/ping/speed) require `POST /diagnose/{nat,ping,speed}` — see "Deferred phases — server API gaps".

**`sdvd discord-setup`** (local only):
- Guided wizard: bot token, channel ID, nickname
- Test token validity via Discord API before saving
- Update .env with Discord settings
- Restart discord-bot service

**`sdvd config [get|set|edit]`:**
- `sdvd config get` — pretty-print current settings (via API `GET /settings` in both modes)
- `sdvd config set Game.FarmType 2` — update individual setting (local only — modifies bind-mounted file)
- `sdvd config edit` — open in $EDITOR (local only)

---

## Command Reference: Local vs Remote

| Command | Local | Remote | Notes |
|---------|-------|--------|-------|
| `sdvd setup` | Yes | No | Requires Docker + file access |
| `sdvd check` | All checks | API checks only | |
| `sdvd status` | Full (Docker + API) | API only | |
| `sdvd logs` | Yes | No | Requires `docker compose logs` |
| `sdvd dashboard` | Yes | Yes | Logs via Docker locally, WebSocket remotely |
| `sdvd backup` | Yes | No | Requires Docker volume access |
| `sdvd restore` | Yes | No | Requires Docker volume access |
| `sdvd upgrade` | Yes | No | Requires Docker + file access |
| `sdvd steam status` | Yes | Partial | Token age only available locally |
| `sdvd steam renew` | Yes | No | Requires Docker TTY |
| `sdvd diagnose` | Full | API checks + limited | netdebug requires Docker |
| `sdvd import-save` | Yes | No | Requires Docker volume access |
| `sdvd migrate` | Yes | No | Requires file access |
| `sdvd discord-setup` | Yes | No | Requires file access |
| `sdvd config get` | Yes | Yes | Via API `GET /settings` |
| `sdvd config set` | Yes | No | Modifies local file |

---

## Key Technical Details

### Docker Interaction
- **Primary:** Spawn `docker compose` CLI commands (handles networking, volumes, orchestration)
- **TTY passthrough:** `child_process.spawn('docker', [...], { stdio: 'inherit' })` for Steam Guard
- **JSON parsing:** Use `--format json` flags on `docker compose ps`, `docker stats`, `docker volume ls`
- **No Docker API library needed** for Phase 1-2; consider `dockerode` only if JSON parsing proves fragile
- **Availability guard:** All Docker-dependent code paths must check `isLocalMode()` first and fail gracefully in remote mode

### API Client
- Built on native `fetch` (Bun built-in)
- **Requires** `Authorization: Bearer <api-key>` — refuses to connect without it
- WebSocket via native `WebSocket` class for dashboard real-time features
- Auto-discover API URL from `.env` (`API_PORT` → `http://localhost:{port}`) in local mode, or `--url` flag
- Warns if `--url` is `http://` targeting a non-localhost/non-private-IP address
- Supports both `http://` and `https://` URLs

### Installation Detection
The CLI detects existing installations by checking (in order):
1. `docker-compose.yml` in current directory containing `sdvd/server` image
2. `.sdvd/config.json` file
3. Docker containers named `sdvd-server`, `sdvd-steam-auth` (local mode only)
4. Docker volumes matching `*_saves`, `*_game-data` (local mode only)

### Cross-Platform Considerations
- Port checks: `net` module for TCP, `child_process` for UDP (`ss`/`netstat`)
- Path handling: `path.join()` everywhere, no hardcoded separators
- Volume backups: `docker run --rm -v volume:/data alpine tar czf ...` works on all platforms (runs in Linux container)
- Steam auth TTY: Works in cmd.exe, PowerShell, and most Unix terminals; may need warning for Git Bash on Windows

### Version Compatibility
- CLI embeds its own version (tracks project semver)
- On connection, queries server version via `GET /health` or `GET /status`
- Warns if major versions differ
- Does not hard-fail on minor mismatches (API is additive between minors)

---

## Project Structure

```
tools/cli/
  package.json
  tsconfig.json
  bun.lock
  src/
    index.ts                    # Entry + commander setup + mode detection
    commands/
      setup.ts                  # Phase 1: Setup wizard (local only)
      check.ts                  # Phase 1: Pre-flight checks
      status.ts                 # Phase 1: Server status
      logs.ts                   # Phase 1: Log streaming (local only)
      backup.ts                 # Phase 2: Backup (local only)
      restore.ts                # Phase 2: Restore (local only)
      upgrade.ts                # Phase 2: Upgrade (local only)
      steam.ts                  # Phase 2: Steam token mgmt
      dashboard.ts              # Phase 3: TUI entry point
      import-save.ts            # Phase 4: Save import (local only)
      migrate.ts                # Phase 4: Config migration (local only)
      diagnose.ts               # Phase 4: Diagnostics
      discord-setup.ts          # Phase 4: Discord wizard (local only)
      config.ts                 # Phase 4: Settings mgmt
    tui/                        # Phase 3
      App.tsx
      components/
        LogView.tsx
        StatusBar.tsx
        CommandInput.tsx
        PlayerList.tsx
        ConnectionStatus.tsx
      hooks/
        useServerStatus.ts
        useWebSocket.ts
        useLogs.ts
    lib/
      docker.ts                 # Docker CLI wrapper (guards local-only)
      env.ts                    # .env parser/writer
      api-client.ts             # HTTP API + WebSocket client
      checks.ts                 # Check implementations (local + universal)
      config.ts                 # .sdvd/config.json
      backup-manager.ts         # Phase 2
      mode.ts                   # Local vs remote mode detection
      ui.ts                     # Shared formatting
  scripts/
    build.ts                    # Compile standalone binaries
  tests/
    env.test.ts
    checks.test.ts
```

---

## What This Replaces

| Current | Replaced By |
|---------|-------------|
| Manual .env editing | `sdvd setup` interactive wizard |
| `docker compose run --rm -it steam-auth setup` | `sdvd setup` (orchestrated step) / `sdvd steam renew` |
| `docker compose up -d` | `sdvd setup` (final step) |
| `docker compose exec server attach-cli` | `sdvd dashboard` (remote, no docker exec needed) |
| `docker compose logs -f` | `sdvd logs` or dashboard log panel |
| `docker compose pull && down && up -d` | `sdvd upgrade` (with auto-backup) |
| Manual tar/volume backup commands | `sdvd backup` / `sdvd restore` |
| Reading troubleshooting docs | `sdvd diagnose` |
| `docker compose exec server netdebug nat` | `sdvd diagnose` (integrated) |

---

## Deferred phases — server API gaps (TODO blockers)

Phases 3 and 4 depend on server endpoints that do not exist. Each is a concrete TODO blocker that must land before the corresponding CLI feature can ship. Per `.claude/rules/universal/holistic-or-explicit-todo.md`, these are tracked as named blockers, not "future work" hedges.

| Endpoint | Blocks | Why needed |
|----------|--------|-----------|
| `GET /version` | Phase 3 version handshake | CLI ↔ server compatibility check |
| `GET /save-status` | Future `--live` backup mode | Wait for clean window before tar |
| `POST /pause` | Future quiesced backup | Cleaner alternative to stop-restart |
| `POST /backup` | Phase 2 remote backup | `sdvd backup --url` requires server-side tar |
| `GET /logs` (SSE or WS) | Phase 3 dashboard remote-mode logs | Dual-source design rejected; need this endpoint to ship |
| `POST /diagnose/{nat,ping,speed}` | Phase 4 `sdvd diagnose --url` | netdebug currently `docker exec` only |

When any of these lands, un-defer the corresponding phase; until then it stays out of scope.

---

## Verification

### Phase 1 Testing
1. Run `sdvd check` on Windows, macOS, Linux — verify Docker/Compose detection, port checks
2. Run `sdvd check --url http://server:8080 --api-key KEY` — verify remote mode skips Docker checks
3. Run `sdvd setup` end-to-end — verify .env creation, Steam auth passthrough, server startup, invite code retrieval
4. Run `sdvd status` against a running server — verify API data display
5. Run `sdvd logs` — verify log streaming and filtering
6. Verify bare `sdvd` auto-detects mode correctly (no install → wizard, healthy → dashboard)

### Phase 2 Testing
1. `sdvd backup` → verify server stops, tar.gz created with correct contents, server restarts
2. `sdvd backup --live` → verify tar created without stopping (warn about consistency)
3. `sdvd restore` → verify volumes restored, server healthy
4. `sdvd upgrade` → verify auto-backup, image pull, .env update, restart, health check
5. `sdvd upgrade` with unhealthy result → verify rollback offer works

### Phase 3 Testing
1. `sdvd dashboard` locally — verify log streaming, command input, status bar
2. `sdvd dashboard --url http://remote:8080 --api-key KEY` — verify remote connectivity
3. Simulate network drop — verify disconnect indicator, auto-reconnect, state recovery
4. Prolonged disconnect (>5 min) — verify clean exit
5. Keyboard navigation, scrolling, resize handling

### Phase 4 Testing
1. `sdvd import-save` with valid/invalid save directories
2. `sdvd diagnose` locally — verify all checks + deep-dive menu
3. `sdvd diagnose --url` remotely — verify graceful degradation (no Docker tools)
4. `sdvd config get` locally and remotely — verify both work via API
5. `sdvd config set` remotely — verify clear error about local-only
