# Steam Authentication

This guide covers the Steam authentication architecture used by JunimoServer for multiplayer functionality.

## Overview

The Steam authentication service is a separate container that isolates Steam credentials from the main game server. It handles:

- Steam authentication and session management
- Game file downloads from Steam depots
- Encrypted app ticket generation for GOG Galaxy cross-platform multiplayer

```
┌─────────────────────────────────────┐
│   Game Server Container             │
│                                     │
│  ┌──────────────────────────────┐  │
│  │  AuthService.cs              │  │
│  │  (SteamAppTicketFetcherHttp) │  │
│  └──────────┬───────────────────┘  │
│             │ HTTP GET              │
│             │ /steam/app-ticket     │
└─────────────┼──────────────────────┘
              │
              ▼
┌─────────────────────────────────────┐
│   Steam Auth Container              │
│                                     │
│  ┌──────────────────────────────┐  │
│  │  HTTP Server (port 3001)     │  │
│  │  /health, /steam/app-ticket  │  │
│  └──────────┬───────────────────┘  │
│             │                       │
│  ┌──────────▼───────────────────┐  │
│  │  SteamKit2                   │  │
│  │  (login, 2FA, downloads)     │  │
│  └──────────────────────────────┘  │
│                                     │
│  Volumes:                           │
│  - steam-session (refresh tokens)  │
│  - game-data (shared with server)  │
└─────────────────────────────────────┘
```

## Security Benefits

1. **Credential Isolation** - Steam username and password never leave the steam-auth container
2. **Token Security** - Refresh tokens remain private in a dedicated volume
3. **Minimal Exposure** - Only ephemeral encrypted app tickets are exposed to the game server
4. **Shared Game Data** - Game files are downloaded once and shared via volume

## First-Time Setup

Run the interactive setup to authenticate with Steam and download the game:

```bash
# Interactive setup (prompts for credentials and 2FA)
make setup

# Or using docker compose directly
docker compose run --rm -it steam-auth setup
```

The setup process will:
1. Prompt for Steam credentials (or use environment variables)
2. Handle Steam Guard authentication (email code, mobile app, or QR code)
3. Save refresh token to the `steam-session` volume
4. Download game files to the `game-data` volume

## Normal Operation

After setup, start the server normally:

```bash
# Start both steam-auth and server containers
docker compose up -d

# Or with make
make up
```

The steam-auth container will:
- Auto-login using the saved refresh token
- Provide app tickets on demand via HTTP API
- Share game files with the server container

## Environment Variables

### Steam Auth Container

| Variable | Description | Default |
|----------|-------------|---------|
| `STEAM_USERNAME` | Steam account username | (prompted) |
| `STEAM_PASSWORD` | Steam account password | (prompted) |
| `STEAM_REFRESH_TOKEN` | Pre-existing refresh token (for CI) | - |
| `PORT` | HTTP server port | 3001 |
| `SESSION_DIR` | Token storage directory | /data/steam-session |
| `GAME_DIR` | Game files directory | /data/game |
| `FORCE_REDOWNLOAD` | Set to "1" to re-download all files | - |

### Game Server Container

| Variable | Description | Default |
|----------|-------------|---------|
| `STEAM_AUTH_URL` | URL of steam-auth service | http://steam-auth:3001 |

## Authentication Methods

### Interactive Login

The setup command supports multiple authentication methods:

1. **Username & Password** - Traditional login with Steam Guard support
2. **QR Code** - Scan with Steam Mobile App (no password needed)

### Steam Guard Support

All Steam Guard methods are supported:

- **Email Code** - Enter the code sent to your email
- **Mobile App Code** - Enter the code from Steam Mobile App
- **Mobile App Approval** - Approve the login notification in the app

### Token Persistence

Refresh tokens are automatically saved to `/data/steam-session/session-{username}.json` and reused on container restart. You only need to handle 2FA once, not on every restart.

::: tip Token Expiry
The steam-auth service displays token expiry information on login. Steam refresh tokens typically last 200 days.
:::

## Available Commands

The steam-auth service supports several commands:

| Command | Description |
|---------|-------------|
| `setup` | Interactive login + download game (first-time setup) |
| `login` | Interactive login only, saves session |
| `download` | Download/update game files (uses saved session) |
| `ticket` | Output encrypted app ticket to stdout |
| `export-token` | Export saved refresh token for CI use |
| `serve` | Run HTTP API for runtime ticket requests (default) |

Example usage:

```bash
# Re-download game files after update
docker compose run --rm steam-auth download

# Export token for CI pipelines
docker compose run --rm steam-auth export-token > token.json

# Get a ticket manually
docker compose run --rm steam-auth ticket
```

## API Endpoints

The steam-auth HTTP API exposes:

### GET /health

Health check endpoint.

```json
{
  "status": "ok",
  "logged_in": true,
  "timestamp": "2026-01-16T12:00:00.000Z"
}
```

### GET /steam/app-ticket

Get an encrypted app ticket for GOG Galaxy authentication.

```json
{
  "app_ticket": "base64-encoded-ticket",
  "steam_id": "76561198012345678"
}
```

## CI/CD Usage

For automated builds, you can use a refresh token instead of interactive login:

```bash
# Export token after local setup
docker compose run steam-auth export-token > token.json

# Use in CI (set as secret)
STEAM_REFRESH_TOKEN=xxx STEAM_USERNAME=user docker compose run steam-auth download
```

The build process uses Docker secrets to pass credentials securely:

```bash
make build  # Uses STEAM_USERNAME, STEAM_PASSWORD, or STEAM_REFRESH_TOKEN from .env
```

## Troubleshooting

### Steam auth not starting

```bash
# Check logs
docker compose logs steam-auth

# Verify container health
docker compose ps
```

### Game server can't reach steam-auth

```bash
# Verify steam-auth is healthy
curl http://localhost:3001/health

# Check from inside server container
docker compose exec server wget -qO- http://steam-auth:3001/health
```

### Session expired or invalid

```bash
# Re-run setup to get a new token
docker compose run --rm -it steam-auth setup
```

### Token persistence not working

```bash
# Check volume
docker volume inspect server_steam-session

# Verify files exist
docker compose exec steam-auth ls -la /data/steam-session/
```

### Download stuck or failing

```bash
# Force re-download
FORCE_REDOWNLOAD=1 docker compose run steam-auth download

# Check available disk space
docker compose exec steam-auth df -h /data/game
```

## Architecture Details

### How Invite Codes Work

1. Game server starts hosting via GOG Galaxy SDK
2. Galaxy creates a lobby and requests an encrypted app ticket
3. Game server's `AuthService` fetches ticket from steam-auth via HTTP
4. Steam-auth uses SteamKit2 to get an encrypted app ticket from Steam
5. Ticket is returned and used to generate an invite code with "G" prefix

### File Filtering

The download process skips unnecessary files to reduce download size:

- Large audio files (Wave Bank.xwb ~370MB)
- Non-English language files (~50MB)
- Other assets not needed for dedicated server operation

This reduces the download from ~1.5GB to ~600MB.
