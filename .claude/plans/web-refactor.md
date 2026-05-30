# Web Cleanup & API Unification Plan

## Goal

Replace the AsyncAPI/WebSocket-based communication in `/web` with the server's HTTP API as the single source of truth. Keep WebSocket/AsyncAPI artifacts for future reference.

---

## Part 1: Server — Add map/player position HTTP endpoint

**Why:** The HTTP API's `/players` only returns `{id, name, isOnline}`. The Map page needs position data that currently only flows via WebSocket from `MapService.cs`.

**File:** `server/mod/JunimoServer/Services/Api/ApiService.cs`

- Add a new `GET /map/players` endpoint (separate from `/players` since map data is conceptually different — pixel positions, hair rendering data, etc.)
- Define a `MapPlayerInfo` DTO with fields: `id`, `name`, `location`, `tileX`, `tileY`, `mapPixelX`, `mapPixelY`, `hairColor`, `hairOffset`
- Implement `HandleGetMapPlayers()` by adapting the position-extraction logic from `MapService.SyncFarmerPositions()` (`server/mod/JunimoServer/Services/Map/MapService.cs:94-160`)
- Wrap game state access in try-catch (thread-safety pattern already used in the existing endpoints)
- Add `[ApiEndpoint]` and `[ApiResponse]` attributes so it appears in OpenAPI spec

**Note:** This reuses the same `Game1.getOnlineFarmers()` + `WorldMapManager.GetPositionData()` approach that MapService uses, but as a request-response call instead of a periodic broadcast.

---

## Part 2: Web — Set up API proxy to game server

**Why:** The web app needs to reach the game server's HTTP API. A server-side proxy avoids CORS issues and keeps the game server URL out of the browser.

**File:** `web/nuxt.config.ts`

- Add `runtimeConfig` with `gameServerUrl` (default: `http://server:8080`, overridable via `NUXT_GAME_SERVER_URL` env var)
- Add Nitro `routeRules` to proxy `/api/game/**` to the game server:
    ```
    /api/game/status     → http://server:8080/status
    /api/game/players    → http://server:8080/players
    /api/game/map/players → http://server:8080/map/players
    /api/game/farmhands  → http://server:8080/farmhands
    /api/game/invite-code → http://server:8080/invite-code
    /api/game/health     → http://server:8080/health
    /api/game/rendering  → http://server:8080/rendering
    /api/game/time       → http://server:8080/time
    ```

**File:** `web/.env` / `web/.env.example`

- Add `NUXT_GAME_SERVER_URL=http://server:8080` (Docker default; local dev can override to `http://localhost:8080`)

---

## Part 3: Web — Create typed API composable

**Why:** Provide a clean, typed interface for components to call the game server API. Replaces the WebSocket client as the primary data layer.

**New file:** `web/composables/useGameApi.ts`

- Export functions wrapping `useFetch` for each endpoint:
    - `useServerStatus()` — `GET /api/game/status`
    - `usePlayersOnline()` — `GET /api/game/players`
    - `useMapPlayers()` — `GET /api/game/map/players` (with optional polling interval via `useIntervalFn` from `@vueuse/core`)
    - `useFarmhands()` — `GET /api/game/farmhands`
    - `useInviteCode()` — `GET /api/game/invite-code`
- Each returns the standard `useFetch` result (`data`, `pending`, `error`, `refresh`)
- TypeScript interfaces for response shapes, matching the server DTOs

---

## Part 4: Web — Update Map component to use HTTP

**Files:**

- `web/components/map/Map.vue`
- `web/pages/game/map.vue`

**Changes:**

- Remove `nuxtApp.$ws.on('MapMessage', ...)` and `nuxtApp.$ws.on('ChatMessage', ...)` handlers
- Replace with `useMapPlayers()` composable from Part 3, polling on a reasonable interval (e.g. 2 seconds)
- Chat functionality: disable for now (it was WebSocket-only and not a core feature). Can be re-enabled when real-time transport is revisited.

---

## Part 5: Web — Decouple WebSocket pipeline

**Why:** Stop the WebSocket client from loading and connecting, but keep all files in the repo for future reference.

**Move to `web/_reference/websocket/`:**

- `web/plugins/websocket-client.client.ts` (auto-loaded by Nuxt — must be moved out of `plugins/`)
- `web/server/api/websocket.ts` (the Nitro WebSocket handler)
- `web/composables/useWebSocketClientCodegen.ts` (wrapper for generated client)

**Leave in place (already outside the critical path):**

- `web/asyncapi.yaml` — spec file, useful as documentation
- `web/.output/ws/` — generated code, not imported by anything once plugin is moved
- `web/scripts/api-generate.sh` — generation script
- `/asyncapi-generator-template-ts` and `/asyncapi-generator-template-cs` — templates

**Config cleanup in `web/nuxt.config.ts`:**

- Remove `nitro.experimental.websocket: true` (no longer needed)
- Remove `vite.build.rollupOptions.external` entry for asyncapi template (no longer relevant)

---

## Part 6: Web — Remove `$ws` type augmentation

**File:** Check for any `declare module` or type augmentation that exposes `$ws` on the Nuxt app instance. Remove it so TypeScript doesn't expect the WebSocket client to exist.

---

## Critical Files to Read Before Implementation

| File                                                    | Why                                      |
| ------------------------------------------------------- | ---------------------------------------- |
| `server/mod/JunimoServer/Services/Api/ApiService.cs`    | Add new endpoint here                    |
| `server/mod/JunimoServer/Services/Map/MapService.cs`    | Copy position extraction logic from here |
| `server/mod/JunimoServer/Services/Map/MapUtil.cs`       | Utility for tile normalization           |
| `server/mod/JunimoServer/Services/Api/ApiAttributes.cs` | Attribute usage for new endpoint         |
| `web/nuxt.config.ts`                                    | Add proxy config                         |
| `web/components/map/Map.vue`                            | Replace WS with HTTP                     |
| `web/pages/game/map.vue`                                | May also have WS references              |
| `web/plugins/websocket-client.client.ts`                | Understand before archiving              |
| `web/composables/useWebSocketClientCodegen.ts`          | Understand before archiving              |

---

## Execution Order

1. **Part 1** (server endpoint) — independent, can be done first
2. **Part 2** (proxy config) — independent of Part 1 at code level
3. **Part 3** (composable) — depends on knowing the proxy paths from Part 2
4. **Part 4** (update Map) — depends on Part 3
5. **Part 5 + 6** (decouple WS) — do last, after HTTP path is working
