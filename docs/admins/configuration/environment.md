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
| `VNC_PORT` | TCP port for VNC web interface | `5800` |
| `STEAM_AUTH_PORT` | Port for steam-auth service HTTP API | `3001` |
| `API_PORT` | Port for the HTTP REST API | `8080` |
| `API_ENABLED` | Enable HTTP API for external tools | `true` |
| `DISABLE_RENDERING` | Disable VNC rendering for performance | `true` |
| `VERBOSE_LOGGING` | Override verbose logging setting | - |

## Security Variables

| Variable | Description | Default |
|----------|-------------|---------|
| `STEAM_REFRESH_TOKEN` | Pre-existing refresh token (for CI/automation) | - |
| `SERVER_PASSWORD` | Server password for player authentication | (empty = disabled) |
| `MAX_LOGIN_ATTEMPTS` | Failed login attempts before kick | `3` |
| `AUTH_TIMEOUT_SECONDS` | Seconds before unauthenticated players are kicked | `120` |

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

# ===== Ports =====
GAME_PORT=24642
VNC_PORT=5800
API_PORT=8080

# ===== Performance =====
DISABLE_RENDERING=true

# ===== Password Protection (optional) =====
# SERVER_PASSWORD=your_server_password
# MAX_LOGIN_ATTEMPTS=3
# AUTH_TIMEOUT_SECONDS=120

# ===== Discord (optional) =====
# DISCORD_BOT_TOKEN=your_bot_token
# DISCORD_BOT_NICKNAME=My Stardew Server
# DISCORD_CHAT_CHANNEL_ID=123456789012345678

# ===== CI/Automation (optional) =====
# STEAM_REFRESH_TOKEN=your_refresh_token
```

## Variable Details

### DISABLE_RENDERING

When `true`, the game skips rendering frames to the display:

- Game still runs normally
- VNC still works for input
- CPU usage significantly reduced

Set to `false` only if you need to debug visual issues.

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

The internal container ports remain unchanged — only the host mapping changes.

## Next Steps

- [Server Settings](/admins/configuration/server-settings) — Game and server behavior
- [Discord Integration](/admins/configuration/discord) — Bot setup
- [Networking](/admins/operations/networking) — Port forwarding and troubleshooting
