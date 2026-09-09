---
description: Install JunimoServer with Docker Compose. Download the config, set your Steam credentials, and start your Stardew Valley dedicated server.
---

# Installation

Before you start, make sure you have [Docker and a Steam account that owns Stardew Valley](/admins/quick-start/prerequisites). To build the server from source instead, see [Building from Source](/developers/advanced/building-from-source).

## 1. Download

Create a new server folder with the config files, then enter it:

::: code-group

```sh [Linux / macOS]
curl -fsSL https://docs.junimoserver.com/install.sh | bash
cd junimoserver
```

```powershell [Windows]
irm https://docs.junimoserver.com/install.ps1 | iex
cd junimoserver
```

:::

You configure the server through `.env` in the next step. Don't edit `docker-compose.yml` yourself, because updates overwrite it. To change a port or add extra mods, put those changes in a `docker-compose.override.yml` next to it, and Compose applies both files together. See [Upgrading](/admins/operations/upgrading#customizing-docker-compose) for how this works.

::: details Set it up by hand instead
Create the folder and download both files yourself:

```sh
mkdir junimoserver && cd junimoserver
curl -fsSL -o docker-compose.yml https://github.com/stardew-valley-dedicated-server/server/releases/latest/download/docker-compose.yml
curl -fsSL -o .env https://github.com/stardew-valley-dedicated-server/server/releases/latest/download/.env.example
```
:::

## 2. Configure

Open `.env` and set your Steam login and two passwords. The server won't start until both passwords are set:

```sh
STEAM_USERNAME="your_steam_username"
STEAM_PASSWORD="your_steam_password"
VNC_PASSWORD="a_password_for_the_web_admin_page"
API_KEY="a_long_random_secret"
```

`VNC_PASSWORD` protects the web admin page. `API_KEY` protects the HTTP API. Generate a strong key with `openssl rand -base64 32`. If you only run on a trusted local network and don't want passwords, set `ALLOW_INSECURE_SETUP=true` instead.

::: warning The API key controls your server
Anyone who has your API key can fully control the server through the HTTP API. Keep it secret and treat it like a password.
:::

See [Environment Variables](/admins/configuration/environment) for every available setting.

## 3. First-Time Setup

Authenticate with Steam:

```sh
docker compose run --rm -it steam-auth setup
```

Follow the prompts for Steam Guard:

| Method | How it works |
|--------|--------------|
| Email code | Enter the code from your email |
| Mobile app | Enter the code or approve the notification |
| QR code | Scan with the Steam app (no password needed) |

This also downloads the game files. You can skip this step if Steam can log in without prompts, for example with a saved session or `STEAM_REFRESH_TOKEN`. In that case the server downloads the files itself the first time it starts.

::: tip Steam tokens expire
Steam login tokens last about 200 days. steam-auth asks Steam to renew the token automatically and, during the last 14 days before expiry, warns daily in its logs if it still needs attention. Re-run `docker compose run --rm -it steam-auth setup` before yours expires to keep the server authenticated.
:::

## 4. Start the Server

```sh
docker compose up -d
```

Check it came up:

```sh
docker compose ps
docker compose logs -f
```

`server` and `steam-auth` should show `Up`. If you haven't set up Discord, `discord-bot` shows as exited. That's normal. Once the server is ready, it prints a startup banner in the logs: a box bordered with `*` showing the server version and network status. That's your signal it's up.

::: info Where your settings live
A `.local-container/` folder appears next to your `docker-compose.yml` on first startup. It holds your `server-settings.json`, so your settings persist on your machine. See [Server Settings](/admins/configuration/server-settings) to change them.
:::

## 5. Get Invite Code & Connect

Open the server console:

```sh
docker compose exec server attach-cli
```

Type `info` to see your invite code, then connect with your game just like joining any multiplayer server:

1. Launch Stardew Valley
2. Click **Co-op** → **Enter Invite Code**
3. Paste the invite code
4. Play!

::: tip You don't need VNC
The CLI and in-game commands handle everything. VNC is only for advanced debugging.
:::

That's it. Your server is running. Manage it with [Console & Chat Commands](/admins/operations/commands), keep it up to date with [Upgrading](/admins/operations/upgrading), and if something goes wrong, see [Troubleshooting](/admins/troubleshooting).
