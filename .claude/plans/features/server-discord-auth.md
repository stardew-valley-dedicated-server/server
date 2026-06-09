# Discord-Based Authentication Implementation Plan

> **Related GitHub Issue:** [#43 - Discord integration](https://github.com/stardew-valley-dedicated-server/server/issues/43)

## Overview

Implement Discord-based authentication where players link their Steam/GOG account via Discord OAuth, and are automatically authenticated when joining the game server.

## Configuration

**Auth Mode** (`AUTH_MODE` env var):
- `password` — password only (existing behavior).
- `discord` — Discord whitelist; players not registered in Discord are rejected.

**`both` mode is intentionally not supported.** A "try Discord first, fall back to password" mode is order-dependent fallback masking unclear authority semantics — per `.claude/rules/universal/retry-is-evidence-of-root-cause.md`, the right answer to "which method authoritatively admits this player?" is to pick one, not chain them. Operators who want a mixed roster register some players via Discord and configure others with the existing per-server password — but at the *server* level, `AUTH_MODE` is single-valued.

## Authentication Flow

```
ONE-TIME SETUP (Discord OAuth)
┌──────────────────────────────────────────────────────────────┐
│ 1. User runs /register in Discord                            │
│ 2. Bot sends OAuth link (requests 'connections' scope)       │
│ 3. User clicks link → authorizes on Discord                  │
│ 4. Bot receives callback → reads Steam/GOG from connections  │
│ 5. Bot stores: { discordId, steamId, gogId }                 │
│ 6. User receives confirmation in Discord                     │
└──────────────────────────────────────────────────────────────┘

EVERY GAME JOIN (Async, mirrors password lobby-staging)
┌──────────────────────────────────────────────────────────────┐
│ 1. Player connects to Stardew server                         │
│ 2. Harmony prefix on `checkFarmhandRequest` captures the     │
│    platform ID and spawn info, then stages the player in     │
│    the lobby (mirrors the existing                            │
│    `_unauthenticatedPlayers` flow used by password auth).    │
│ 3. Background task: server calls Discord bot                 │
│    `GET /auth/verify?platformId=X` (timeout-bounded, runs    │
│    OFF the game tick thread).                                │
│ 4. Bot responds: { valid: true, discordUsername: "Name" }    │
│ 5. On success → admit from lobby to Farm (existing path).    │
│    On failure / timeout / `valid: false` → kick from lobby   │
│    with a clear chat message.                                │
└──────────────────────────────────────────────────────────────┘
```

**Why async lobby-staging, not synchronous prefix.** The Harmony prefix runs on the server's network/tick thread (`mod/JunimoServer/Services/PasswordProtection/PasswordProtectionService.cs:132`). A 5-second `DiscordAuthTimeoutMs` HTTP round-trip on that thread stalls every other player's network for the duration. The existing password flow already solves this: stage the player, verify async, admit-or-kick. Reuse that pattern — do not block in the prefix.

**Platform ID extraction note.** Steam IDs are available directly on `userId` passed to `checkFarmhandRequest` (`PasswordProtectionService.cs:132`). For Galaxy/GOG, `userId` arrives as `""` until the Galaxy server is late-added during auth (see commit `e174208` "fix(auth): resolve n/a invite code by late-adding Galaxy server on auth"). Before this plan ships, **resolve**: does the Discord registration flow accept GOG IDs at all, and if so, where in the join sequence do we have a stable Galaxy ID to look up? Concrete TODO blocker — do not begin implementation until answered.

---

## Part 1: Discord Bot Changes

**Preserve existing bot features.** `tools/discord-bot/src/index.ts` already implements chat-relay (game ↔ Discord channel) and dynamic-nickname (commit `a3822b6`). The OAuth/registration code lands as **additions** to the existing module structure — not a complete rewrite. Refactor in place: extract the existing presence/chat code into `src/bot/relay/` if needed, then add `src/bot/commands/`, `src/api/`, etc. alongside.

### New Project Structure

```
tools/discord-bot/
├── src/
│   ├── index.ts              # Main entry: starts bot + relay + HTTP servers
│   ├── config.ts             # Environment configuration
│   ├── bot/
│   │   ├── commands/
│   │   │   ├── register.ts   # /register slash command (initiates OAuth)
│   │   │   ├── unregister.ts # /unregister slash command
│   │   │   └── status.ts     # /status - check own registration
│   │   └── deploy-commands.ts
│   ├── api/
│   │   ├── server.ts         # Hono HTTP server
│   │   └── routes/
│   │       ├── auth.ts       # /auth/verify endpoint (game server calls)
│   │       └── oauth.ts      # /oauth/callback (Discord OAuth callback)
│   ├── services/
│   │   └── discord-oauth.ts  # OAuth token exchange & connections fetch
│   └── storage/
│       ├── index.ts          # Storage interface
│       └── json-storage.ts   # JSON file persistence
├── data/
│   └── registrations.json    # Persistent storage
└── package.json
```

> **Related follow-up:** [`discord-bot-mcp-module.md`](./discord-bot-mcp-module.md) proposes
> exposing the bot as a first-class MCP server (`src/mcp/`). It is **deferred** and gated on a
> build-vs-adopt decision, but if both land it should reuse *this* restructure (shared `Client`,
> token, and the `src/bot/` ↔ `src/api/` split) rather than re-refactoring the single file again.

### New Dependencies

```json
{
  "dependencies": {
    "discord.js": "^14.25.1",
    "hono": "^4.0.0"
  }
}
```

### Discord OAuth Setup

Requires a Discord Application with OAuth2 configured:
- **Scopes**: `identify`, `connections`
- **Redirect URI**: `http://<bot-host>:8081/oauth/callback`

### Slash Commands

**`/register`**
- Checks if user has required role (if configured)
- Generates state token, stores pending registration
- Returns ephemeral message with OAuth link
- Link: `https://discord.com/oauth2/authorize?client_id=X&scope=identify+connections&state=Y&redirect_uri=Z`

**`/unregister`**
- Removes user's registration
- Confirms deletion

**`/status`**
- Shows user's current registration status
- Displays linked Steam/GOG ID if registered

### HTTP API: split into public and internal listeners

**Two HTTP servers on two ports.** The OAuth callback must be reachable by every player's browser; the verify/list endpoints are protected only by a shared secret. Putting both on the same port reduces "network isolation" to "one secret protects everything," and risks the secret-protected endpoints being indexed/scanned via the same public hostname.

| Listener | Bind | Exposed in `docker-compose.yml`? | Endpoints | Auth |
|----------|------|----------------------------------|-----------|------|
| **Public** (`PORT_OAUTH`, default `8081`) | `0.0.0.0` | yes (port-mapped) | `/health`, `/oauth/callback` | none (callback is unauth by design; Discord signs the `state` token) |
| **Internal** (`PORT_INTERNAL`, default `8082`) | `0.0.0.0` inside the compose network | no (no `ports:` entry) | `/auth/verify?platformId=X`, `/auth/list` | Bearer `BOT_API_SECRET` |

The internal listener is reachable from the `server` container via the docker-compose service DNS (`http://discord-bot:8082`) but not from outside the compose network. Update `docker-compose.yml` `discord-bot` service to bind only `8081` in `ports:` and leave `8082` as an unpublished internal port.

### OAuth Callback Flow

1. User clicks OAuth link from `/register`
2. Discord redirects to `/oauth/callback?code=X&state=Y`
3. Bot exchanges code for access token
4. Bot fetches user connections via `GET /users/@me/connections`
5. Bot extracts Steam/GOG IDs from connections
6. Bot stores registration and DMs user confirmation

### Storage Format (registrations.json)

```json
[
  {
    "discordId": "123456789",
    "discordUsername": "User#1234",
    "steamId": "76561198012345678",
    "gogId": null,
    "registeredAt": "2024-01-15T10:30:00Z"
  }
]
```

**Atomic writes.** Slash-command registrations can race (two `/register` flows finishing simultaneously). The storage layer must write via temp-file + `rename` so partial writes never appear in `registrations.json`. Document this in `json-storage.ts`.

---

## Part 2: C# Mod Changes

### New Environment Variables (Env.cs)

`mod/JunimoServer/Env.cs` currently has only `ServerPassword`, `MaxLoginAttempts`, `AuthTimeoutSeconds` (verified). All env vars below are introduced by this plan and exempt from `verify-documented-config-is-consumed.md` provided the consumer (`DiscordAuthService`) lands in the same change.

```csharp
/// <summary>
/// Authentication mode: "password" or "discord". `both` is intentionally not supported.
/// </summary>
public static readonly string AuthMode =
    Environment.GetEnvironmentVariable("AUTH_MODE") ?? "password";

public static readonly string DiscordAuthApiUrl =
    Environment.GetEnvironmentVariable("DISCORD_AUTH_API_URL") ?? "http://discord-bot:8082";

public static readonly string DiscordAuthApiSecret =
    Environment.GetEnvironmentVariable("DISCORD_AUTH_API_SECRET") ?? "";

public static readonly int DiscordAuthTimeoutMs =
    Int32.Parse(Environment.GetEnvironmentVariable("DISCORD_AUTH_TIMEOUT_MS") ?? "5000");

public static bool IsDiscordAuthEnabled => AuthMode == "discord";
public static bool IsPasswordAuthEnabled => AuthMode == "password";
```

(`DISCORD_AUTH_API_URL` defaults to the internal port `8082`, not the public OAuth port `8081`.)

### New Service: DiscordAuthService.cs

Location: `mod/JunimoServer/Services/DiscordAuth/DiscordAuthService.cs`

Responsibilities:
- HTTP client to call Discord bot API
- `VerifyPlatformId(string platformId)` method
- Returns `DiscordAuthResult` with success/failure and Discord user info

### Modified: PasswordProtectionService.cs

`CheckFarmhandRequest_Prefix` is at `mod/JunimoServer/Services/PasswordProtection/PasswordProtectionService.cs:132` (verified). `OnFarmhandRequest(NetFarmerRoot farmer)` is at `:289`. The existing `_unauthenticatedPlayers` lobby-staging flow is the model — reuse it for Discord verification.

Changes needed:
1. Inject `DiscordAuthService` via constructor.
2. `CheckFarmhandRequest_Prefix` (`:132`) already captures `userId`/`connectionId`/`farmer`; route the platform ID into `_unauthenticatedPlayers` alongside the spawn info.
3. **Do NOT block in the prefix.** Stage the player exactly as the password flow does today.
4. In `OnFarmhandRequest` (`:289`) or a follow-on tick handler: when `AUTH_MODE=discord`, fire-and-forget a background `DiscordAuthService.VerifyPlatformId(platformId)` call. On success, transition the player out of the lobby. On failure or timeout, kick with a clear chat message.
5. When `AUTH_MODE=password`, behavior is unchanged — Discord code path is dead.

### Modified: AuthenticationState.cs

`mod/JunimoServer/Services/PasswordProtection/AuthenticationState.cs` currently has none of the fields below (verified). Add to `PlayerAuthData`:

```csharp
public enum AuthMethod { None, Password, Discord }

public string PlatformId { get; set; }
public string DiscordId { get; set; }
public string DiscordUsername { get; set; }
public AuthMethod Method { get; set; } = AuthMethod.None;
```

---

## Part 3: Docker/Configuration

### docker-compose.yml changes

```yaml
discord-bot:
  volumes:
    - discord-bot-data:/app/data
  ports:
    - "${BOT_OAUTH_PORT:-8081}:8081"  # Public OAuth callback ONLY
  # Note: port 8082 (internal verify/list API) is intentionally NOT mapped.
  # It is reachable only from other containers in this compose network.
  environment:
    DISCORD_BOT_TOKEN: "${DISCORD_BOT_TOKEN}"
    API_URL: "http://server:8080"
    DISCORD_CLIENT_ID: "${DISCORD_CLIENT_ID}"
    DISCORD_CLIENT_SECRET: "${DISCORD_CLIENT_SECRET}"
    DISCORD_GUILD_ID: "${DISCORD_GUILD_ID:-}"
    DISCORD_REQUIRED_ROLE_ID: "${DISCORD_REQUIRED_ROLE_ID:-}"
    BOT_API_PORT_OAUTH: "8081"
    BOT_API_PORT_INTERNAL: "8082"
    BOT_API_SECRET: "${DISCORD_AUTH_API_SECRET:-}"
    BOT_PUBLIC_URL: "${BOT_PUBLIC_URL:-http://localhost:8081}"

server:
  environment:
    AUTH_MODE: "${AUTH_MODE:-password}"
    DISCORD_AUTH_API_URL: "http://discord-bot:8082"
    DISCORD_AUTH_API_SECRET: "${DISCORD_AUTH_API_SECRET:-}"

volumes:
  discord-bot-data:
```

### .env.example additions

```bash
########################################
#         Authentication Mode          #
########################################
# "password" = password only (default)
# "discord"  = Discord only (whitelist)
# `both` is intentionally not supported — pick one authority.
AUTH_MODE=password

########################################
#       Discord OAuth (for AUTH_MODE=discord or both)
########################################
# Discord Application credentials (from Discord Developer Portal)
DISCORD_CLIENT_ID=""
DISCORD_CLIENT_SECRET=""

# Discord server (guild) ID
DISCORD_GUILD_ID=""

# Optional: Require specific role to register
DISCORD_REQUIRED_ROLE_ID=""

# Public URL for OAuth callback (must be accessible to users)
# e.g., https://your-server.com:8081 or http://localhost:8081
BOT_PUBLIC_URL=http://localhost:8081

# Shared secret for game server ↔ bot API communication
# Generate with: openssl rand -hex 32
DISCORD_AUTH_API_SECRET=""
```

---

## Part 4: Security

1. **API Authentication**: Shared secret (`BOT_API_SECRET`) as Bearer token on the internal listener (`8082`).
2. **Network Isolation**: Public OAuth callback (`8081`) is the only port exposed externally; internal verify/list API (`8082`) is only reachable inside the compose network.
3. **Input Validation**: Steam ID must be 17 digits.
4. **Duplicate Prevention**: One platform ID per Discord account.
5. **Atomic registration writes**: `registrations.json` written via temp-file + rename to survive concurrent slash-command bursts.

---

## Files to Modify

| File | Changes |
|------|---------|
| `tools/discord-bot/src/index.ts` | Extend in place (NOT a complete rewrite): preserve existing chat-relay and dynamic-nickname features (commit `a3822b6`); start the new public+internal HTTP listeners + slash commands alongside |
| `tools/discord-bot/package.json` | Add `hono` dependency |
| `mod/JunimoServer/Env.cs` | Add AUTH_MODE and Discord auth env vars |
| `mod/JunimoServer/Services/PasswordProtection/PasswordProtectionService.cs` | Integrate Discord auth check, capture platformId |
| `mod/JunimoServer/Services/PasswordProtection/AuthenticationState.cs` | Add Discord-related fields, AuthMethod enum |
| `docker-compose.yml` | Add volume, expose OAuth port, add env vars |
| `.env.example` | Document AUTH_MODE and OAuth variables |

## New Files to Create

| File | Purpose |
|------|---------|
| `mod/JunimoServer/Services/DiscordAuth/DiscordAuthService.cs` | HTTP client to verify platform IDs with bot |
| `tools/discord-bot/src/config.ts` | Environment configuration |
| `tools/discord-bot/src/bot/commands/register.ts` | /register command (initiates OAuth) |
| `tools/discord-bot/src/bot/commands/unregister.ts` | /unregister command |
| `tools/discord-bot/src/bot/commands/status.ts` | /status command (check registration) |
| `tools/discord-bot/src/api/server.ts` | Hono HTTP server |
| `tools/discord-bot/src/api/routes/auth.ts` | /auth/verify endpoint |
| `tools/discord-bot/src/api/routes/oauth.ts` | /oauth/callback endpoint |
| `tools/discord-bot/src/services/discord-oauth.ts` | OAuth token exchange & connections fetch |
| `tools/discord-bot/src/storage/index.ts` | Storage interface |
| `tools/discord-bot/src/storage/json-storage.ts` | JSON file persistence |

---

## Prerequisites

Before implementing, ensure you have:
1. A Discord Application created in the [Discord Developer Portal](https://discord.com/developers/applications)
2. OAuth2 configured with redirect URI pointing to your bot's public URL
3. Bot invited to your Discord server with appropriate permissions
