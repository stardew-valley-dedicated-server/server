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

## Discord Integration

| Variable | Description | Default |
|----------|-------------|---------|
| `DISCORD_BOT_TOKEN` | Discord bot token | - |
| `DISCORD_BOT_NICKNAME` | Custom bot nickname | (farm name) |
| `DISCORD_CHAT_CHANNEL_ID` | Channel ID for chat relay | - |

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

When set, all API endpoints require an `Authorization: Bearer <api-key>` header (except `/health` and `/docs`).

Generate a secure key:

```sh
openssl rand -base64 32
```

::: tip When do I need this?
Currently, only the Discord bot uses the API. If you:
- **Use the Discord bot**: Set the same `API_KEY` for both server and bot
- **Don't use the Discord bot**: You can skip `API_KEY` if the API port (8080) is not exposed externally
- **Build custom integrations** (web dashboard, monitoring, etc.): Set `API_KEY` and include it in your requests
:::

::: warning
Without `API_KEY`, anyone with network access to port 8080 can read server data and control your server. Always set this if the API is accessible from untrusted networks.
:::

### ALLOW_INSECURE_SETUP

By default the server aborts startup when `VNC_PASSWORD` is empty or (with `API_ENABLED=true`) `API_KEY` is empty — leaving the VNC interface or HTTP API exposed without authentication. Set `ALLOW_INSECURE_SETUP=true` to override the abort on closed networks where the affected ports are not reachable from untrusted clients.

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

The internal container ports remain unchanged; only the host mapping changes.

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

