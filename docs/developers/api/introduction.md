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

## WebSocket

The server also provides a WebSocket endpoint at `/ws` for real-time bidirectional communication, primarily used for chat relay with the Discord bot.

### Connection

```
ws://localhost:8080/ws
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
| `ping` | Heartbeat | None |
| `chat_send` | Send chat message to game | `{ "author": "Name", "message": "Hello" }` |

#### Server → Client

| Type | Description | Payload |
|------|-------------|---------|
| `pong` | Heartbeat response | None |
| `chat` | Player chat message from game | `{ "playerName": "Name", "message": "Hello", "timestamp": "ISO8601" }` |

### Example

```javascript
const ws = new WebSocket("ws://localhost:8080/ws");

// Send heartbeat
ws.send(JSON.stringify({ type: "ping" }));

// Send chat to game
ws.send(JSON.stringify({
  type: "chat_send",
  payload: { author: "Discord User", message: "Hello from Discord!" }
}));

// Receive game chat
ws.onmessage = (event) => {
  const msg = JSON.parse(event.data);
  if (msg.type === "chat") {
    console.log(`${msg.payload.playerName}: ${msg.payload.message}`);
  }
};
```

## Use Cases

Common integrations:

- **Discord bots** — Relay chat and display server status
- **Monitoring tools** — Track server health and player activity
- **Web dashboards** — Display server information on websites
- **Automation scripts** — Control server behavior programmatically
