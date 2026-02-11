# REST API

HTTP API for monitoring and controlling your server programmatically.

## What You Can Do

- Get server status and player list
- Control in-game time
- Manage farmhand slots
- Read and modify settings
- Send and receive chat messages (WebSocket)

## Endpoints

| Category | Examples |
|----------|----------|
| Server | Status, health, rendering control |
| Players | Connected players, invite codes |
| Farmhands | Slot management |
| Settings | Read/write server configuration |
| Cabins | Cabin state and positions |
| Time | In-game time control |

## WebSocket

Real-time bidirectional communication at `/ws`. Used by the Discord bot for chat relay.

```javascript
const ws = new WebSocket("ws://localhost:8080/ws");

ws.onmessage = (event) => {
  const msg = JSON.parse(event.data);
  if (msg.type === "chat") {
    console.log(`${msg.payload.playerName}: ${msg.payload.message}`);
  }
};
```

## Use Cases

- Custom Discord bots
- Server monitoring dashboards
- Automation scripts
- External admin tools

## Full Reference

See [API Reference](/developers/api/introduction) for complete endpoint documentation.
