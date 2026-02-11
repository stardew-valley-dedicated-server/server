# REST API

The dedicated server exposes an HTTP REST API on port 8080 for monitoring and controlling your server programmatically.

Use the sidebar to browse available endpoints.

## Endpoint Overview

The API provides endpoints for:

- **Server** — Status, health checks, rendering control
- **Players** — Connected players and invite codes
- **Farmhands** — Farmhand slot management
- **Settings** — Server configuration from `server-settings.json`
- **Cabins** — Cabin state and positions
- **Time** — In-game time control

## Configuration

The API is enabled by default. Configure via environment variables:

| Variable | Description | Default |
|----------|-------------|---------|
| `API_ENABLED` | Enable/disable the API | `true` |
| `API_PORT` | Port for the API server | `8080` |
| `API_KEY` | API key for write endpoints | (empty = no auth) |

## Authentication

When `API_KEY` is set, all endpoints require authentication via the `Authorization` header:

```
Authorization: Bearer <your-api-key>
```

### Public Endpoints

These endpoints remain accessible without authentication:

| Endpoint | Purpose |
|----------|---------|
| `/health` | Health checks for monitoring tools |
| `/docs` | API documentation UI |
| `/swagger/v1/swagger.json` | OpenAPI specification |

### Example

```sh
# Without auth (will fail if API_KEY is set)
curl "http://localhost:8080/status"

# With auth
curl "http://localhost:8080/status" \
  -H "Authorization: Bearer your-api-key"
```

::: tip Generate a Secure Key
```sh
bun -e "console.log(require('crypto').randomBytes(32).toString('base64url'))"
```
:::

## WebSocket

The server also provides a WebSocket endpoint at `/ws` for real-time bidirectional communication, primarily used for chat relay with the Discord bot.

### Connection

```
ws://localhost:8080/ws
```

### Authentication

When `API_KEY` is set, WebSocket clients must authenticate within 10 seconds of connecting:

```javascript
const ws = new WebSocket("ws://localhost:8080/ws");

ws.onopen = () => {
  // Send auth message immediately after connecting
  ws.send(JSON.stringify({
    type: "auth",
    payload: { token: "your-api-key" }
  }));
};

ws.onmessage = (event) => {
  const msg = JSON.parse(event.data);

  if (msg.type === "auth_success") {
    console.log("Authenticated!");
    // Now you can send/receive messages
  }

  if (msg.type === "auth_failed") {
    console.error("Auth failed:", msg.error);
    // Connection will be closed by server
  }
};
```

### Message Format

All messages are JSON with a `type` field and optional `payload`:

```json
{
  "type": "message_type",
  "payload": { ... }
}
```

### Message Types

#### Client → Server

| Type | Description | Payload |
|------|-------------|---------|
| `auth` | Authenticate (required first if `API_KEY` set) | `{ "token": "api-key" }` |
| `ping` | Heartbeat | None |
| `chat_send` | Send chat message to game | `{ "author": "Name", "message": "Hello" }` |

#### Server → Client

| Type | Description | Payload |
|------|-------------|---------|
| `auth_success` | Authentication successful | None |
| `auth_failed` | Authentication failed | `{ "error": "reason" }` |
| `pong` | Heartbeat response | None |
| `chat` | Player chat message from game | `{ "playerName": "Name", "message": "Hello", "timestamp": "ISO8601" }` |

### Example

```javascript
const ws = new WebSocket("ws://localhost:8080/ws");

ws.onopen = () => {
  // Authenticate first (if API_KEY is set)
  ws.send(JSON.stringify({
    type: "auth",
    payload: { token: "your-api-key" }
  }));
};

ws.onmessage = (event) => {
  const msg = JSON.parse(event.data);

  if (msg.type === "auth_success") {
    // Start heartbeat after auth
    setInterval(() => {
      ws.send(JSON.stringify({ type: "ping" }));
    }, 30000);
  }

  if (msg.type === "chat") {
    console.log(`${msg.payload.playerName}: ${msg.payload.message}`);
  }
};

// Send chat to game (after authenticated)
ws.send(JSON.stringify({
  type: "chat_send",
  payload: { author: "Discord User", message: "Hello from Discord!" }
}));
```

## POST Endpoint Parameters

POST endpoints accept parameters via query string. All write endpoints require authentication when `API_KEY` is set.

### POST /rendering

Toggle rendering on or off.

```sh
# Enable rendering
curl -X POST "http://localhost:8080/rendering?enabled=true" \
  -H "Authorization: Bearer $API_KEY"

# Disable rendering
curl -X POST "http://localhost:8080/rendering?enabled=false" \
  -H "Authorization: Bearer $API_KEY"
```

### POST /time

Set the game time of day.

```sh
# Set time to noon (1200)
curl -X POST "http://localhost:8080/time?value=1200" \
  -H "Authorization: Bearer $API_KEY"

# Set time to 6 PM (1800)
curl -X POST "http://localhost:8080/time?value=1800" \
  -H "Authorization: Bearer $API_KEY"
```

Valid range: 600 (6 AM) to 2600 (2 AM next day).

### POST /roles/admin

Grant admin role to a player.

```sh
curl -X POST "http://localhost:8080/roles/admin?name=PlayerName" \
  -H "Authorization: Bearer $API_KEY"
```

### DELETE /farmhands

Delete a farmhand by name. The farmhand must be offline.

```sh
curl -X DELETE "http://localhost:8080/farmhands?name=FarmhandName" \
  -H "Authorization: Bearer $API_KEY"
```

## Use Cases

- Discord bots (chat relay, server status)
- Monitoring tools
- Web dashboards
- Automation scripts
