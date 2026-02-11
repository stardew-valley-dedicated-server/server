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

::: tip
Consider a dedicated Steam account to avoid conflicts when playing while the server runs.
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

The server runs continuously (24/7) and uses resources even when no players are connected. For cloud hosting, factor in always-on costs.
