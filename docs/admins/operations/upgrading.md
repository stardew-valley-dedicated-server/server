# Upgrading

Keep your JunimoServer up to date to get the latest features, bug fixes, and game updates.

## Quick Upgrade

For most users with Docker images:

```sh
docker compose pull
docker compose down
docker compose up -d
```

Your save files and configuration are stored in Docker volumes and will be preserved.

## Using Preview Builds

Preview builds contain the latest changes from the `master` branch. Use these when:

- The latest stable release has known issues
- You want to test new features before official release

### Enable Preview Builds

**1. Update `docker-compose.yml`**

Change image tags from `latest` to `preview`:

```yaml
services:
    server:
        image: sdvd/server:preview  # [!code highlight]
    steam-service:
        image: sdvd/steam-service:preview  # [!code highlight]
    discord-bot:
        image: sdvd/discord-bot:preview  # [!code highlight]
```

**2. Pull and restart**

```sh
docker compose pull
docker compose down
docker compose up -d
```

::: warning
Preview builds may contain experimental features or bugs. Back up your saves before switching.
:::

### Return to Stable

Change `preview` back to `latest` and repeat the pull/restart steps.

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

- [ ] **Backup saves** — Always backup the `saves` volume before major updates
- [ ] **Check mod compatibility** — Verify mods work with new game versions
- [ ] **Read release notes** — Check the [changelog](/community/changelog) for breaking changes
- [ ] **Test first** — For production servers, test in development first

## Rollback

If you need to revert to a previous version:

**1. Specify version in docker-compose.yml**

```yaml
services:
    server:
        image: sdvd/server:1.0.0
```

Replace `1.0.0` with your desired version from [GitHub Releases](https://github.com/stardew-valley-dedicated-server/server/releases).

**2. Restart**

```sh
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

## Next Steps

- [Troubleshooting](/admins/troubleshooting) — Common issues and solutions
- [Changelog](/community/changelog) — Release history
