# Cross-Platform Multiplayer

Steam and GOG players can connect to the same server.

## How It Works

The server generates two invite codes:

| Platform | Code Format | Connection Method |
|----------|-------------|-------------------|
| Steam | Starts with "S" (e.g., `S123456789`) | Steam Datagram Relay |
| GOG | Starts with "G" (e.g., `G1234567890ABCDEF`) | Galaxy P2P |

Share the appropriate code with each player based on their platform. When both are available, Steam is recommended for better reliability.

## Connection Reliability

| Platform | Success Rate | Notes |
|----------|--------------|-------|
| Steam | ~99% | Traffic routes through Valve's relay network |
| GOG | ~50% | P2P connection, depends on NAT compatibility |

Steam connections are more reliable because Valve's relay handles NAT traversal. GOG uses peer-to-peer which can fail with certain router configurations.

## Getting Invite Codes

```sh
docker compose exec server attach-cli
# Type: info
```

Or use `!invitecode` in-game chat.

## Mixed Platform Farms

Players from both platforms play together on the same farm. There are no gameplay differences between Steam and GOG players once connected.
