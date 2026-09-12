# Upgrading

## Quick Upgrade

Run this from your server directory:

::: code-group

```sh [Linux / macOS]
curl -fsSL https://docs.junimoserver.com/install.sh | bash
```

```powershell [Windows]
irm https://docs.junimoserver.com/install.ps1 | iex
```

:::

It pulls the image for your configured `IMAGE_VERSION` (stable, preview, or pinned), installs the `docker-compose.yml` that matches it, and restarts. `.env`, saves, and settings are untouched.

On an interactive run it asks two things: which channel to use (press Enter to keep your current one, or type `preview` or `stable` to switch, see [Using Preview Builds](#using-preview-builds)), then whether to restart to apply the update. Answer no and the update is staged (image pulled, `docker-compose.yml` updated) but not applied until you run `docker compose up -d`. A run with no terminal (CI, cron) or with `NO_TTY=1` set skips both questions: it keeps your current channel and restarts automatically.

To upgrade by hand, run `docker compose pull && docker compose up -d --remove-orphans` after replacing `docker-compose.yml` with the version matching your image. The script above fetches that file automatically; to get it yourself, use the one linked by the startup warning below.

A **docker-compose.yml does not match this image** warning at startup links the matching file.

## Customizing docker-compose

Updates replace `docker-compose.yml`, so put your own changes in a `docker-compose.override.yml` next to it (Compose merges it automatically). Extra mods, for example:

```yaml
services:
  server:
    volumes:
      - ./mods:/data/Mods/extra
```

Settings such as ports and passwords go in `.env`; see [Environment Variables](/admins/configuration/environment).

## Upgrade Notes

### Updates replace `docker-compose.yml`

The update command replaces `docker-compose.yml` with the version matching the new image, saving the previous file as a timestamped `.bak` first. It's a generated file, so keep your own changes (extra mod mounts, port changes) in a `docker-compose.override.yml` instead; Compose merges both and your changes survive every update. See [Customizing docker-compose](#customizing-docker-compose).

### Empty `VNC_PASSWORD` disables VNC instead of aborting startup

Earlier versions exited at startup whenever `VNC_PASSWORD` was empty. Now an empty password keeps the VNC web interface and VNC port unreachable from outside the container, and the server starts normally; set a password to enable them. Startup still aborts when `API_KEY` is empty with the API enabled. Set `ALLOW_INSECURE_SETUP=true` on closed networks to start anyway and to leave VNC reachable without a password. See [`ALLOW_INSECURE_SETUP`](/admins/configuration/environment#allow-insecure-setup).

### The game runs as a non-root user

The game runs as UID/GID `1000` inside the container unless `USER_ID`/`GROUP_ID` are set in `.env`.
The first start after upgrading updates the ownership of existing files to match. On Linux, set the
values to your own user, or `0` on rootless Docker, before that start (see
[Host / Permissions](/admins/configuration/environment#host-permissions)).

## Using Preview Builds

Preview builds contain the latest changes from the `master` branch. Use these when:

- The latest stable release has known issues
- You want to test new features before official release

### Enable Preview Builds

Set the image version in your `.env` file:

```sh
IMAGE_VERSION=preview
```

Then run the [Quick Upgrade](#quick-upgrade) command. It pulls the preview image and installs the matching `docker-compose.yml` automatically.

::: warning
Preview builds may contain experimental features or bugs. Back up your saves before switching.
:::

### Return to Stable

Remove or comment out the `IMAGE_VERSION` line in `.env` (defaults to `latest`):

```sh
# IMAGE_VERSION=preview
```

Then run the [Quick Upgrade](#quick-upgrade) command again.

## Updating Game Files

To download the latest Stardew Valley game files or SMAPI updates:

**1. Stop the server**

```sh
docker compose down
```

**2. Remove the game volume**

```sh
docker volume rm server_game-data
```

::: info Volume Names
Docker prefixes volume names with your project directory name. Run `docker volume ls` to see actual names.
:::

::: warning Save Data is Safe
This only removes game files. Your save data in the `saves` volume is **not** affected.
:::

**3. Restart the server**

```sh
docker compose up -d
```

The server automatically downloads the latest game files on startup.

## Version Checking

Check your versions via CLI:

```sh
docker compose exec server attach-cli
# Type: info
```

Or look at the SMAPI console output in VNC when the server starts.

## Upgrade Checklist

Before upgrading:

- [ ] **Backup saves**: Always backup the `saves` volume before major updates
- [ ] **Check mod compatibility**: Verify mods work with new game versions
- [ ] **Read release notes**: Check the [changelog](/community/changelog) for breaking changes
- [ ] **Test first**: For production servers, test in development first

## Rollback

If you need to revert to a previous version:

**1. Specify version in `.env`**

```sh
IMAGE_VERSION=1.0.0
```

Replace `1.0.0` with your desired version from [Docker Hub tags](https://hub.docker.com/r/sdvd/server/tags).

**2. Pull and restart**

```sh
docker compose pull
docker compose down
docker compose up -d
```

## Troubleshooting

### Server Won't Start After Upgrade

1. Check logs: `docker compose logs -f`
2. Verify `.env` configuration is valid
3. Ensure Steam credentials are correct
4. Try removing and recreating the game volume

### Mods Not Working After Update

1. Check SMAPI console for mod errors
2. Update mods to latest versions
3. Remove incompatible mods temporarily
4. Check mod pages for version compatibility

## For Local Build Users

If building from source:

```sh
git pull origin master
git submodule update --recursive
make up
```

See [Building from Source](/developers/advanced/building-from-source) for details.

