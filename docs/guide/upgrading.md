# Upgrading

Keep your JunimoServer up to date to get the latest features, bug fixes, and game updates.

## For Docker Release Users

If you're using the pre-built Docker images, upgrading JunimoServer is straightforward:

**1. Pull the Latest Image**

```sh
docker compose pull
```

**2. Restart the Server**

```sh
docker compose down
docker compose up -d
```

::: tip
Your save files and configuration are stored in Docker volumes, so they'll be preserved during the upgrade.
:::

### For Local Build Users

If you're building from source:

**1. Pull the Latest Code**

```sh
git pull origin master
git submodule update --recursive
```

**2. Rebuild and Restart**

```sh
make up
```

## Updating Game Files (Stardew Valley & SMAPI)

To download the latest Stardew Valley game files or SMAPI updates:

**1. Stop the Server**

```sh
docker compose down
```

**2. Remove the Game Volume**

```sh
docker volume rm server_game-data
```

::: info Volume Names
Docker prefixes volume names with your project directory name. If your directory is called `server`, volumes will be named `server_game-data`, `server_saves`, etc. Run `docker volume ls` to see your actual volume names.
:::

::: warning
This only removes game files. Your save data is stored in a separate `saves` volume and will **not** be affected.
:::

**3. Restart the Server**

```sh
docker compose up -d
```

The server will automatically download the latest Stardew Valley and SMAPI files on startup.

## Version Checking

To check your Stardew Valley and SMAPI versions, connect to the VNC interface or use the CLI (`make cli`) and look at the SMAPI console output when the server starts.

## Upgrade Checklist

Before upgrading, consider:

- **Backup your saves** - Always backup the `saves` volume before major updates
- **Check mod compatibility** - If you use mods, verify they're compatible with new game versions
- **Read release notes** - Check the [changelog](/community/changelog) for breaking changes
- **Test in development** - For production servers, test upgrades in a development environment first

## Troubleshooting

### Server Won't Start After Upgrade

1. Check the logs: `docker compose logs -f`
2. Verify your `.env` configuration is still valid
3. Ensure Steam credentials are correct (needed for game downloads)
4. Try removing and recreating the game volume

### Mods Not Working After Update

1. Check SMAPI console for mod errors
2. Update your mods to the latest versions
3. Remove incompatible mods temporarily
4. Check mod pages for Stardew Valley version compatibility

## Rollback

If you need to rollback to a previous version:

**1. Specify Version in docker-compose.yml**

```yml
services:
    server:
        image: sdvd/server:1.0.0
```

Replace `1.0.0` with your desired version.

**2. Restart**

```sh
docker compose down
docker compose up -d
```

::: info
Check [GitHub Releases](https://github.com/stardew-valley-dedicated-server/server/releases) for available versions.
:::

## Next Steps

- [Advanced Topics](/guide/advanced-topics) - Learn about decompiling and advanced server customization
