# Steam Authentication Architecture

The steam-auth service runs in a separate container to isolate Steam credentials from the game server. It handles:

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

1. **Credential Isolation**: Steam username and password never leave the steam-auth container
2. **Token Security**: Refresh tokens remain private in a dedicated volume
3. **Minimal Exposure**: Only ephemeral encrypted app tickets are exposed to the game server
4. **Shared Game Data**: Game files are downloaded once and shared via volume

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

### Lobbies are forced Public

An invite code is just the lobby's raw ID handed to `JoinLobby`. Stardew's default
visibility is **FriendsOnly** (`Game1.options.serverPrivacy`), which restricts joins to
friends of the host account — but a dedicated server's host account is not friended by its
players, so the default would block invite-code joins for everyone. The mod therefore forces
**both** lobbies Public regardless of the game-chosen value:

| Transport | Site |
|-----------|------|
| Steam lobby | `AuthService.SetSteamLobbyPrivacy` (hardcodes `"public"`) |
| Galaxy (GoG) lobby | `ServerOptimizerOverrides.CreateLobby_Prefix` (forces `ServerPrivacy.Public`) |

This is why a JunimoServer lobby is always Public and can appear in the Steam/GOG lobby
list. The two sites must stay in sync; changing one alone splits the transports' privacy.

## Auth vs Transport: What the Sidecar Replaces (and What It Can't)

The sidecar fully replaces the Steam client for **authentication** — but Steam network
**sessions** are a separate layer, and the distinction decides which flows can run headless.

An `encryptedAppTicket` is a static, signed identity proof designed to be shown to third
parties. GOG's backend accepts it as a login (`GalaxyInstance.User().SignInSteam(ticket)`), so
every Galaxy-side feature runs headless with sidecar tickets. Steam's own relay network (SDR)
is different: relays only carry traffic for endpoints holding live session certificates, and
Valve opens such sessions through exactly two doors:

| Door | API family | Headless? | Identity granted |
|------|-----------|-----------|------------------|
| User session | `SteamAPI.Init()` → attaches to a running Steam client process | No | The account's personal Steam64 (`7656…`) |
| GameServer session | `SteamGameServer.Init()` + `LogOnAnonymous()` | Yes (by design, for dedicated servers) | An ephemeral gameserver ID (`90…`), no account involved |

There is no API that turns a ticket into a Steam network session — deliberately, since
otherwise any headless process could impersonate a live user endpoint (the unforgeability the
mod's farmhand-ownership gate relies on).

**Consequences in this codebase:**

- The **server** never needs a user session: SDR *listening* uses the GameServer door
  (`SteamGameServerNetworkingSockets`, `Callback.CreateGameServer` —
  `SteamGameServerNetServer.cs`), the Steam lobby is managed by the sidecar's SteamKit2
  account session (GameServer mode has no `SteamMatchmaking`), and Galaxy hosting uses a
  sidecar ticket. Steam credentials exist for the ticket/lobby plumbing, not for SDR.
- A **vanilla client dialing SDR** is the one operation in the whole system gated on the user
  door. The decompiled chain (`decompiled/sdv-1.6.15-24356/StardewValley/SDKs/Steam/`):
  `SteamHelper.cs:60` `SteamAPI.Init()` (attach to the Steam client; the app ticket itself is
  then requested from the logged-on user session) → `SteamNetClient.cs:204`
  `SteamMatchmaking.JoinLobby` → `:163` `SteamMatchmaking.GetLobbyGameServer` (resolves the
  host's gameserver ID) → `:131` `SteamNetworkingSockets.ConnectP2P` — all user-mode APIs
  with no GameServer-mode counterpart for a *player* connection.
- **Test clients** therefore authenticate fully (real account, real ticket, real Galaxy
  logon — same chain as the server) but transport over Galaxy P2P: the test-client mod
  redirects Hybrid (S-prefix invite) joins to `GalaxyNetClient`
  (`tests/test-client/Auth/ClientAuthService.cs`, `CreateClient_Prefix`). Client-side SDR is
  never dialed in the harness; the server's SDR listener runs but is only exercised in
  production. A GameServer-door dialer for the test client (real SDR traffic under a
  gameserver identity) is planned in
  `.claude/plans/features/tests-sdr-client-transport.md`.

### Which identity a joining player presents (per door)

The door a *player* joins through decides the transport identity their connection carries —
and therefore which identity the farmhand-ownership map records:

| Join path | Transport | Identity the server sees |
|-----------|-----------|--------------------------|
| Friends list / S-prefix invite code | Steam SDR (`SN_…` connection id) | The account's Steam64 (`7656…`) |
| G-prefix invite code | Galaxy P2P (`GN_…` connection id) | The account's GOG Galaxy uint64 |
| Direct IP | Lidgren LAN (`L_…` connection id) | None |

The same Steam account therefore presents two unrelated ids depending on the door: the
Steam64 and the Galaxy pseudo-id share no id space and cannot be correlated server-side
(client-declared `userID` stamps are Galaxy-space on *both* platform transports, never the
Steam64). A player who owns a farmhand through one door and rejoins through the other is
rejected by the ownership gate; the operator fix is `farmhand rebind` with the id from the
new door's connect-log line. `ConnectionTransport` (mod) is the canonical parser for these
connection-id shapes.

## File Filtering

The download process skips unnecessary files to reduce download size:

- Large audio files (Wave Bank.xwb ~370MB) — always stripped (the server runs silent)
- Non-English language files (~50MB) — stripped by default; opt back in per language
  with `STEAM_KEEP_LANGUAGES` (see below)
- Other assets not needed for dedicated server operation

This reduces the download from ~1.5GB to ~600MB.

After filtering, `Content/ContentHashes.json` is pruned to list only the files that
were actually downloaded. The game checks this manifest (not the filesystem) to decide
whether an asset exists, so leaving a stripped file listed would make the game attempt
to load a missing `.xnb` and throw `ContentLoadException`. Pruning keeps the manifest
honest so the game's built-in localized→English font fallback works.

### Keeping a language's fonts

By default every localized font/content file is stripped and the server falls back to
the English font, so non-Latin chat (e.g. Cyrillic, CJK) renders as empty boxes on
clients. To keep a language's fonts in the download — so that language renders
correctly — set `STEAM_KEEP_LANGUAGES` to a comma-separated list of language codes:

```bash
STEAM_KEEP_LANGUAGES=pt-BR,ru-RU
```

Valid codes: `de-DE`, `es-ES`, `fr-FR`, `hu-HU`, `it-IT`, `ja-JP`, `ko-KR`, `pt-BR`,
`ru-RU`, `tr-TR`, `zh-CN`, `th-TH`. CJK codes (`zh-CN`, `ja-JP`, `ko-KR`) also keep
their larger font families, so those add more to the image size than the others. An
unrecognized code is logged as a warning and ignored.

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
| `STEAM_KEEP_LANGUAGES` | Comma-separated language codes whose fonts to keep in the download (e.g. `pt-BR,ru-RU`). Default strips all localized content (English-only). | - |

### Game Server Container

| Variable | Description | Default |
|----------|-------------|---------|
| `STEAM_AUTH_URL` | URL of steam-auth service | http://steam-auth:3001 |

