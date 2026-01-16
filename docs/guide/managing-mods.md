# Managing Mods

JunimoServer fully supports SMAPI mods, allowing you to customize your multiplayer experience with the same mods you use in single-player.

## Installing Mods

To add mods to your server, follow these steps:

**1. Create a Mods Directory**

First, create a directory on your host machine to store your mods:

```sh
mkdir mods
```

**2. Download Mods**

Download your desired SMAPI mods from sources like:

-   [Nexus Mods](https://www.nexusmods.com/stardewvalley)
-   [ModDrop](https://www.moddrop.com/stardew-valley)
-   [Stardew Valley Official Forum](https://forums.stardewvalley.net/index.php?forums/mods.10/)

Extract the mod folders into your `mods` directory.

**3. Configure Docker Volume**

Add a volume bind mount in your `docker-compose.yml` to make the mods available to the server:

```yml
services:
    server:
        volumes:
            - ./mods:/data/Mods/extra
```

**4. Restart the Server**

Restart your server to load the new mods:

```sh
docker compose down
docker compose up -d
```

::: tip Verifying Mods
Connect to the VNC interface and check the SMAPI console to verify that your mods loaded successfully. SMAPI will display a list of all loaded mods when the server starts.
:::

## Mod Compatibility

### Client-Server Synchronization

For most mods to work properly in multiplayer:

1. **Server-side mods** should be installed using the method above
2. **Client-side mods** need to be installed on each player's local game
3. **Content mods** (that add items, NPCs, etc.) should be installed on **both** server and client

::: warning
All players connecting to the server should have the same content mods installed to avoid synchronization issues.
:::

### Known Compatibility Issues

There are currently no known compatibility issues with JunimoServer. However, if you encounter problems with specific mods, please [report a bug](https://github.com/stardew-valley-dedicated-server/server/issues/new?assignees=&labels=bug%2C+needs-verification%2C+incompatible%20mod&projects=&template=bug_report.md).

## Troubleshooting

### Mod Not Loading

If a mod doesn't appear to be loading:

1. **Check SMAPI console** - Look for error messages in the VNC interface
2. **Verify mod location** - Ensure mods are in the correct directory structure
3. **Check dependencies** - Some mods require other mods to be installed first
4. **Update mods** - Make sure mods are compatible with your Stardew Valley version

### Performance Issues

If you experience performance problems after adding mods:

-   Some mods are more resource-intensive than others
-   Try adding mods one at a time to identify problematic ones
-   Consider increasing server resources (CPU/RAM) if running many mods

## Best Practices

-   **Keep mods updated** - Check for mod updates regularly
-   **Read mod descriptions** - Understand what each mod does and its requirements
-   **Backup before adding mods** - Always backup your save files before installing new mods
-   **Test with friends** - Ensure all players can connect after adding new mods

## Next Steps

-   [Upgrading](/guide/upgrading) - Learn how to update your server to the latest version
