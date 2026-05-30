# Upgrading

## Quick Upgrade

For most users with Docker images:

```sh
docker compose pull
docker compose down
docker compose up -d
```

Your save files and configuration are stored in Docker volumes and will be preserved.

## Upgrade Notes

### Empty `VNC_PASSWORD` no longer aborts startup unconditionally

Earlier versions exited at startup whenever `VNC_PASSWORD` was empty. The current release surfaces it as a warning and aborts only when an insecure setup is detected (empty `VNC_PASSWORD`, or empty `API_KEY` with the API enabled). Set `ALLOW_INSECURE_SETUP=true` on closed networks to keep the warnings but skip the abort. See [`ALLOW_INSECURE_SETUP`](/admins/configuration/environment#allow-insecure-setup).

## Using Preview Builds

Preview builds contain the latest changes from the `master` branch. Use these when:

- The latest stable release has known issues
- You want to test new features before official release

### Enable Preview Builds

Set the image version in your `.env` file:

```sh
IMAGE_VERSION=preview
```

Then pull and restart:

```sh
docker compose pull
docker compose down
docker compose up -d
```

::: warning
Preview builds may contain experimental features or bugs. Back up your saves before switching.
:::

### Return to Stable

Remove or comment out the `IMAGE_VERSION` line in `.env` (defaults to `latest`):

```sh
# IMAGE_VERSION=preview
```

Then pull and restart as above.

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

Replace `1.0.0` with your desired version from [GitHub Releases](https://github.com/stardew-valley-dedicated-server/server/releases).

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

