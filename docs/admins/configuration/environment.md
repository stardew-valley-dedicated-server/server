# Environment Variables

Environment variables in the `.env` file control Docker infrastructure, credentials, and networking. They are **not** used for game settings (use `server-settings.json` for those).

## Required Variables

These must be set for the server to function:

| Variable | Description |
|----------|-------------|
| `STEAM_USERNAME` | Steam account username |
| `STEAM_PASSWORD` | Steam account password |
| `VNC_PASSWORD` | Password for VNC web interface |

## Runtime Variables

| Variable | Description | Default |
|----------|-------------|---------|
| `GAME_PORT` | UDP port for multiplayer connections | `24642` |
| `QUERY_PORT` | UDP port for Steam query protocol | `27015` |
| `VNC_PORT` | TCP port for VNC web interface | `5800` |
| `API_PORT` | Port for the HTTP REST API | `8080` |
| `API_ENABLED` | Enable HTTP API for external tools | `true` |
| `SERVER_FPS` | Render rate: `0` = rendering disabled, `N > 0` = render at N fps | `0` |
| `VERBOSE_LOGGING` | Override verbose logging setting | - |

## Security Variables

| Variable | Description | Default |
|----------|-------------|---------|
| `STEAM_REFRESH_TOKEN` | Pre-existing refresh token (for CI/automation) | - |
| `SERVER_PASSWORD` | Server password for player authentication | (empty = disabled) |
| `MAX_LOGIN_ATTEMPTS` | Failed login attempts before kick | `3` |
| `AUTH_TIMEOUT_SECONDS` | Seconds before unauthenticated players are kicked | `120` |
| `API_KEY` | API key for authenticating write requests | (empty = disabled) |
| `ALLOW_INSECURE_SETUP` | Allow startup when `VNC_PASSWORD` or `API_KEY` is empty | `false` |

## Host / Permissions

| Variable | Description | Default |
|----------|-------------|---------|
| `USER_ID` | User ID (UID) the game runs as inside the container | `1000` |
| `GROUP_ID` | Group ID (GID) of that user | `1000` |

The game runs as a normal user inside the container rather than as root, so a misbehaving mod cannot
touch anything outside the game's own files. The container starts as root only long enough to
prepare the server's files and assign their ownership.

On Linux, files written to `.local-container/settings` and `diagnostics` are owned by this UID/GID
on the host as well, so set them to your own user and group to access those files normally.

- **Windows and macOS:** no configuration required. Docker Desktop handles file ownership for you.
- **Linux:** set both values to your user and group IDs. If both commands print `1000`, the defaults
  are already correct.

  ```sh
  id -u   # USER_ID
  id -g   # GROUP_ID
  ```

- **Rootless Docker:** set both to `0`. In rootless Docker, UID/GID `0` inside the container maps to
  your host user.
- **Root:** setting both to `0` on regular Docker runs the game as root inside the container. Not
  needed; on Linux the two folders above are then owned by root on the host.

You can change the values later. On the next start, the container updates the ownership of the
server's files to match.

## Discord Integration

| Variable | Description | Default |
|----------|-------------|---------|
| `DISCORD_BOT_TOKEN` | Discord bot token | - |
| `DISCORD_BOT_NICKNAME` | Custom bot nickname | (farm name) |
| `DISCORD_CHAT_CHANNEL_ID` | Channel ID for chat relay | - |
| `STATUS_DASHBOARD_CHANNEL_ID` | Channel ID for the status dashboard | - |
| `STATUS_DASHBOARD_REFRESH_RATE` | Dashboard update interval in seconds | `30` |

See [Discord Integration](/admins/configuration/discord) for setup instructions.

## Example .env File

```sh
# ===== Required =====
STEAM_USERNAME=your_steam_username
STEAM_PASSWORD=your_steam_password
VNC_PASSWORD=your_secure_password

# ===== Ports (uncomment to change from defaults) =====
# GAME_PORT=24642
# QUERY_PORT=27015
# VNC_PORT=5800
# API_PORT=8080

# ===== Performance (uncomment to change from defaults) =====
# SERVER_FPS=10

# ===== Security (optional) =====
# SERVER_PASSWORD=your_server_password
# API_KEY=your_api_key

# ===== Discord (optional) =====
# DISCORD_BOT_TOKEN=your_bot_token
# DISCORD_CHAT_CHANNEL_ID=123456789012345678
# STATUS_DASHBOARD_CHANNEL_ID=123456789012345678

# ===== CI/Automation (optional) =====
# STEAM_REFRESH_TOKEN=your_refresh_token
```

## Variable Details

### SERVER_FPS

Controls the rate at which the server draws graphics to its own display. **This does not affect players** — they always see the game normally on their own screens.

| Value | What happens |
|-------|--------------|
| `0` (default) | Rendering disabled. The server installs a null display device and suppresses draws; VNC shows a "Rendering Disabled" notice. **The server works normally** — players connect and play as usual — and it uses less CPU. |
| `N > 0` | Server draws at up to N frames per second. VNC shows the game display. Useful for debugging visual issues; a low value like `10` keeps CPU cost modest. |

::: info Why is `0` the default?
A dedicated server doesn't need to display anything. It just processes game logic and sends updates to players. Skipping the graphics rendering saves significant CPU resources.
:::

::: tip Players are not affected
When you connect to the server with your game client, you see the game on *your* screen rendered by *your* computer. `SERVER_FPS` only affects the server's own display (viewed via VNC).
:::

::: tip Changing the rate at runtime
You don't have to restart to enable rendering for debugging. Both of these take effect immediately:

- HTTP: `POST /rendering?fps=10` (use `fps=0` to disable again).
- Console: `docker compose exec server attach-cli`, then `rendering 10` (or `rendering 0`, `rendering status`).
:::

::: tip Driving the game over VNC
While rendering is off (`SERVER_FPS=0`), VNC input is fully suppressed — there is nothing to see, so input has no meaning. While rendering is on, the VNC view is input-blocked except **F9** (toggle host automation) and **F10** (toggle visibility). Press **F9** to drop automation and gain full keyboard/mouse control; press it again to re-arm the guard.
:::

### STEAM_REFRESH_TOKEN

Alternative to username/password for automated environments. Export after initial setup:

```sh
docker compose run --rm steam-auth export-token
```

See [Steam Authentication](/developers/architecture/steam-auth) for CI/CD usage.

### SERVER_PASSWORD

When set, players must authenticate with `!login <password>` before they can play. Leave empty to disable password protection.

See [Password Protection](/features/password-protection/) for full documentation.

### API_ENABLED

When `true`, the REST API is available for external tools and monitoring. See [REST API](/developers/api/introduction) for endpoints.

### API_KEY

When set, API endpoints require an `Authorization: Bearer <api-key>` header. The [API reference](/developers/api/introduction) marks which endpoints are public.

Generate a secure key:

```sh
openssl rand -base64 32
```

::: tip Who needs the key
- **Discord bot**: set the same `API_KEY` for the server and the bot.
- **`diagnostics` command**: reads the key from the container environment.
- **Your own integrations**: send the `Authorization` header.
:::

### ALLOW_INSECURE_SETUP

The server refuses to start without `VNC_PASSWORD`, or without `API_KEY` while `API_ENABLED=true`. Set `ALLOW_INSECURE_SETUP=true` to start anyway. Only do this on networks where the VNC and API ports are not reachable by untrusted clients.

## Port Summary

| Port | Protocol | Purpose | Expose Externally? |
|------|----------|---------|-------------------|
| 24642 | UDP | Game (Steam SDR) | No (relay handles NAT) |
| 27015 | UDP | Steam query | No (relay handles NAT) |
| 5800 | TCP | VNC web interface | Only for remote access |
| 8080 | TCP | REST API | Only for external tools |
| 3001 | TCP | Steam auth (internal) | No |

## Changing Ports

To avoid port conflicts, you can change the host-side mappings in `.env`:

```sh
VNC_PORT=5801
API_PORT=8081
```

The VNC container port stays fixed; only its host mapping changes. `API_PORT` changes the API port both on the host and inside the container, so the server, its health check, and the Discord bot all follow it.

## Advanced Variables

These are rarely needed but available for advanced use cases:

| Variable | Description | Default |
|----------|-------------|---------|
| `HEALTH_CHECK_SECONDS` | Interval for internal health checks | `300` |
| `ENABLE_MOD_INCOMPATIBLE_OPTIMIZATIONS` | Enable performance optimizations that may break some mods | `true` |
| `FORCE_NEW_DEBUG_GAME` | Force creation of a new debug game on startup | `false` |

::: warning
These variables are for advanced users. Changing them may cause unexpected behavior.
:::

