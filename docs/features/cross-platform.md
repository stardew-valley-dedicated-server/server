# Cross-Platform Multiplayer

Steam and GOG players can connect to the same server.

## How It Works

One invite code serves both platforms. It starts with "S" (e.g., `S1234567890ABC`): a Steam client joins it through the Steam relay, a GOG client reads the same code as its Galaxy lobby and joins through Galaxy P2P.

The code appears a few seconds after the server starts, as soon as the Galaxy lobby exists; GOG players can join it right away, and Steam players a moment later, once the Steam relay is ready. The status page, the Discord bot, and the in-game `!invitecode` command show the code with its [connection status](/admins/operations/public-status#invite-code-status), for example `GOG ready · Steam connecting…` in that window.

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
