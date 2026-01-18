# Prerequisites

Before installing JunimoServer, ensure your system meets the following requirements.

## Docker

JunimoServer runs in Docker containers. You need Docker Engine version 20 or higher with Compose V2.

### Verify Installation

Check if Docker is installed and running:

```sh
docker --version
docker compose version
```

::: warning Docker Compose V2
JunimoServer uses `docker compose` (V2), not the older `docker-compose` (V1). If you only have V1, upgrade Docker Desktop or install the Compose plugin.
:::

If Docker is not installed, follow the official [Docker installation guide](https://docs.docker.com/get-docker/).

## Steam Account

You need a Steam account that owns Stardew Valley. The credentials are used only to download the game files and are never transmitted outside your local environment.

::: tip Dedicated Account
Consider using a separate Steam account for the server. This avoids conflicts if you want to play while the server is running.
:::

## Next Steps

Once you have Docker installed and running, proceed to [Installation](/getting-started/installation).
