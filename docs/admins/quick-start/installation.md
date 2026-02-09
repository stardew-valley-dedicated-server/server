# Installation

Install JunimoServer using pre-built Docker images.

::: tip Building from Source?
See [Building from Source](/developers/advanced/building-from-source) for development.
:::

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

## 3. First-Time Setup

Authenticate with Steam and download game files:

```sh
docker compose run --rm -it steam-auth setup
```

Follow the prompts for Steam Guard (email code, mobile app, or QR code).

## 4. Start the Server

```sh
docker compose up -d
```

## 5. Access VNC

Open `http://localhost:5800` and enter your VNC password.

## Basic Commands

```sh
docker compose up -d       # Start
docker compose down        # Stop
docker compose logs -f     # View logs
docker compose restart     # Restart
docker compose ps          # Status
```

## Next Steps

- [First Setup](/admins/quick-start/first-setup) — Verify Steam authentication
- [Server Settings](/admins/configuration/server-settings) — Customize your server
- [Password Protection](/features/password-protection/) — Secure your farm
