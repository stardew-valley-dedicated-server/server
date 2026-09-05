---
description: Install JunimoServer with Docker Compose — download the configuration, set your Steam credentials, and start your Stardew Valley dedicated server.
---

# Installation

For development, see [Building from Source](/developers/advanced/building-from-source).

## 1. Download Configuration

```sh
mkdir junimoserver && cd junimoserver
curl -O https://raw.githubusercontent.com/stardew-valley-dedicated-server/server/master/docker-compose.yml
curl -O https://raw.githubusercontent.com/stardew-valley-dedicated-server/server/master/.env.example
mv .env.example .env
```

## 2. Configure

Edit `.env`:

```sh
STEAM_USERNAME="your_steam_username"
STEAM_PASSWORD="your_steam_password"
VNC_PASSWORD="your_secure_password"
```

On Linux, also set the user the game runs as to your own, so the server's files on your computer
belong to you (not needed on Windows and macOS):

```sh
printf 'USER_ID=%s\nGROUP_ID=%s\n' "$(id -u)" "$(id -g)" >> .env
```

## 3. Pull Images

Download the pre-built Docker images:

```sh
docker compose pull
```

## 4. First-Time Setup

Authenticate with Steam:

```sh
docker compose run --rm -it steam-auth setup
```

Follow the prompts for Steam Guard (email code, mobile app, or QR code). Setup also downloads the game files right away. If you skip it and Steam can log in without prompts (a saved session or `STEAM_REFRESH_TOKEN`), the server downloads them itself on first start and reports a startup phase (downloading, then starting) until the game is up.

## 5. Start the Server

```sh
docker compose up -d
```

::: info .local-container Directory
On first startup, a `.local-container/` directory is created next to your `docker-compose.yml`. This contains your `server-settings.json` and is how settings are persisted on your host machine. See [Server Settings](/admins/configuration/server-settings) to customize.
:::

## 6. Get Invite Code & Connect

Get your invite code:

```sh
docker compose exec server attach-cli
# Type: info
```

Then connect with your game, just like joining any multiplayer server:

1. Launch Stardew Valley
2. Click **Co-op** → **Enter Invite Code**
3. Paste the invite code
4. Play!

::: tip No VNC Needed
You don't need VNC to play or manage the server. The CLI and in-game commands handle everything. VNC is only for advanced debugging.
:::

## Basic Commands

```sh
docker compose up -d       # Start
docker compose down        # Stop
docker compose logs -f     # View logs
docker compose restart     # Restart
docker compose ps          # Status
```

