---
description: What you need before installing JunimoServer — Docker Engine 20+ with Compose V2, a Steam copy of Stardew Valley, system requirements, and an NTP-synced host clock.
---

# Prerequisites

## Docker

Docker Engine 20+ with Compose V2.

```sh
docker --version
docker compose version
```

::: warning Compose V2 Required
Use `docker compose` (V2), not `docker-compose` (V1). [Install Docker](https://docs.docker.com/get-docker/)
:::

| Platform | Method |
|----------|--------|
| Linux | Docker Engine + Compose plugin (recommended for production) |
| Windows/macOS | Docker Desktop |

## Steam Account

A Steam account that owns Stardew Valley. Credentials are only used locally to download game files.

::: warning Use a Dedicated Steam Account
**Do not use the same Steam account for both the server and your game client.** Steam only allows one active session per account. If the same account is used for the server and a player's client, Steam will log the server out, causing "Connection Failed" for all subsequent reconnect attempts — even with a valid, unchanged invite code.

Use a separate Steam account (with its own copy of Stardew Valley) for the server.
:::

## Hardware

| Resource | Minimum | Recommended |
|----------|---------|-------------|
| CPU | Dual-core | Quad-core |
| RAM | 2 GB | 4 GB |
| Disk | 1 GB | 2+ GB |

::: info Ballpark Estimates
These are approximate figures based on testing. Actual requirements vary depending on player count, mods, and farm complexity. Monitor resource usage and adjust as needed.
:::

The server runs continuously and uses resources even when no players are connected. For cloud hosting, factor in always-on costs.

## System Clock

The server uses the host's clock. A wrong clock drops players who join with an invite code about
30 seconds after they join. Clocks sync automatically on every current OS, so normally nothing is
required. On Linux, `timedatectl status` should report `System clock synchronized: yes`. Docker
Desktop can fall behind after the computer wakes from sleep; restarting it fixes that.
