# Cross-Platform Multiplayer

Steam and GOG players can connect to the same server.

## How It Works

One invite code serves both platforms. It starts with "S" (e.g., `S1234567890ABC`): a Steam client joins it through the Steam Datagram Relay, a GOG client reads the same code as its Galaxy lobby and joins through Galaxy P2P.

The code becomes available a few seconds after the server starts, once the Steam lobby is published. Until then the status page, the Discord bot, and the in-game `!invitecode` command show that it is not yet available.

## Connection Reliability

| Platform | Success Rate | Notes |
|----------|--------------|-------|
| Steam | ~99% | Traffic routes through Valve's relay network |
| GOG | ~50% | P2P connection, depends on NAT compatibility |

Steam connections are more reliable because Valve's relay handles NAT traversal. GOG uses peer-to-peer which can fail with certain router configurations.

## Getting the Invite Code

```sh
docker compose exec server attach-cli
# Type: info
```

Or use `!invitecode` in-game chat.

## Mixed Platform Farms

Players from both platforms play together on the same farm. There are no gameplay differences between Steam and GOG players once connected.
