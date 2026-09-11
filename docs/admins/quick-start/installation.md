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

When you run it, the script asks which release channel to use (preview or stable), then offers to **sign in to Steam and start the server for you**: answer yes, enter your Steam login when prompted, and it downloads the game, starts the server, and opens the console. If you accept, you're done. Jump to step 5 below to connect your game.

Prefer to do it yourself, or ran the script non-interactively? Follow the steps below. Don't edit `docker-compose.yml` (updates overwrite it); for ports or extra mods use a `docker-compose.override.yml` next to it (see [Upgrading](/admins/operations/upgrading#customizing-docker-compose)).

::: details Set it up by hand instead
Create the folder and download both files yourself:

```sh
mkdir junimoserver && cd junimoserver
curl -fsSL -o docker-compose.yml https://raw.githubusercontent.com/stardew-valley-dedicated-server/server/master/docker-compose.yml
curl -fsSL -o .env https://raw.githubusercontent.com/stardew-valley-dedicated-server/server/master/.env.example
```

Then set `IMAGE_VERSION` in `.env` to pick a channel (defaults to `latest`), and set a strong `API_KEY` (`openssl rand -hex 32`). Without it the server refuses to start unless you set `ALLOW_INSECURE_SETUP=true`.
:::

## 2. Configure (optional)

By default there's nothing to edit. The install script already put a strong random `API_KEY` in `.env`, and VNC stays off. Open `.env` only to change optional settings:

- **`VNC_PASSWORD`**: set it to expose the VNC web GUI (max 8 characters). Left empty, VNC stays disabled.
- You don't need to put Steam credentials in `.env`; you enter them in the next step.

::: warning The API key controls your server
The generated `API_KEY` grants full control through the HTTP API. Keep your `.env` private.
:::

See [Environment Variables](/admins/configuration/environment) for every available setting.

## 3. First-Time Setup

The guided setup runs this for you. To do it by hand, authenticate with Steam:

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
Steam login tokens last about 200 days. steam-auth automatically renews saved-session tokens and, during the last 14 days before expiry, warns daily in its logs if one still needs attention. Re-run `docker compose run --rm -it steam-auth setup` before yours expires to keep the server authenticated. A token supplied via `STEAM_REFRESH_TOKEN` is not renewed; mint a new one with `setup` and `export-token` instead.
:::

## 4. Start the Server

The guided setup starts it for you. To start manually:

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
