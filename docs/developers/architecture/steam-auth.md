# Steam Authentication Architecture

This document covers the technical architecture of JunimoServer's Steam authentication system.

## Overview

The Steam authentication service is a separate container that isolates Steam credentials from the main game server. It handles:

- Steam authentication and session management
- Game file downloads from Steam depots
- Encrypted app ticket generation for GOG Galaxy cross-platform multiplayer

## Architecture Diagram

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

1. **Credential Isolation** — Steam username and password never leave the steam-auth container
2. **Token Security** — Refresh tokens remain private in a dedicated volume
3. **Minimal Exposure** — Only ephemeral encrypted app tickets are exposed to the game server
4. **Shared Game Data** — Game files are downloaded once and shared via volume

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

## Token Persistence

Refresh tokens are saved to `/data/steam-session/session-{username}.json` and reused on container restart. Steam tokens typically last 200 days.

## How Invite Codes Work

1. Game server starts hosting via GOG Galaxy SDK
2. Galaxy creates a lobby and requests an encrypted app ticket
3. Game server's `AuthService` fetches ticket from steam-auth via HTTP
4. Steam-auth uses SteamKit2 to get an encrypted app ticket from Steam
5. Ticket is returned and used to generate an invite code with "G" prefix

## File Filtering

The download process skips unnecessary files to reduce download size:

- Large audio files (Wave Bank.xwb ~370MB)
- Non-English language files (~50MB)
- Other assets not needed for dedicated server operation

This reduces the download from ~1.5GB to ~600MB.

## CI/CD Usage

For automated builds, use a refresh token instead of interactive login:

```bash
# Export token after local setup
docker compose run steam-auth export-token > token.json

# Use in CI (set as secret)
STEAM_REFRESH_TOKEN=xxx STEAM_USERNAME=user docker compose run steam-auth download
```

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

## Related Documentation

- [First Server Setup](/admins/quick-start/first-setup) — Initial authentication setup
- [Troubleshooting](/admins/troubleshooting) — Common auth issues
- [CI/CD Pipelines](/developers/contributing/ci-cd) — Automated deployment
