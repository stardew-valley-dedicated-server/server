# Mod Support

JunimoServer supports SMAPI mods.

## Installing Mods

### 1. Create a Mods Directory

Create a directory on your host machine to store mods:

```sh
mkdir mods
```

### 2. Download Mods

Download SMAPI mods from sources like:

- [Nexus Mods](https://www.nexusmods.com/stardewvalley)
- [ModDrop](https://www.moddrop.com/stardew-valley)
- [Stardew Valley Official Forum](https://forums.stardewvalley.net/index.php?forums/mods.10/)

Extract mod folders into your `mods` directory.

### 3. Configure Docker Volume

Add a volume bind mount in `docker-compose.yml`:

```yaml
services:
    server:
        volumes:
            - ./mods:/data/Mods/extra
```

### 4. Restart the Server

```sh
docker compose down
docker compose up -d
```

::: tip Verify Mods Loaded
Connect to VNC and check the SMAPI console. SMAPI lists all loaded mods at startup.
:::

## Mod Types

| Type | Install On | Example |
|------|-----------|---------|
| **Server-only** | Server | JunimoServer core, automation mods |
| **Content mods** | Server AND clients | New items, NPCs, maps |
| **Client-only** | Client only | UI improvements, client utilities |

::: warning Content Mods
Content mods that add items, NPCs, or maps must be installed on both the server and all connecting clients. Mismatched mods cause sync issues. Share your mod list with players.
:::

## Finding Compatible Mods

When choosing mods for your server:

- Look for "multiplayer compatible" in mod descriptions
- Check mod comments/forums for multiplayer feedback
- Test with a small group before adding to production
- Prefer actively maintained mods

## Troubleshooting

### Mod Not Loading

1. **Check SMAPI console** — Look for error messages in VNC
2. **Verify mod location** — Ensure correct directory structure
3. **Check dependencies** — Some mods require other mods
4. **Update mods** — Ensure compatibility with your game version

### "Missing assembly" Errors

The mod is missing a dependency. Check the mod's page for required mods.

### Players Missing Items/NPCs

Content mod mismatch. Ensure all players have:

- Same mods installed
- Same mod versions
- Same configuration (if applicable)

### Performance Issues

If performance degrades after adding mods:

- Add mods one at a time to identify problems
- Check mod resource usage requirements
- Consider increasing server resources
- Remove unused mods

## Best Practices

- Keep mods updated
- Read mod descriptions for requirements and compatibility
- Backup saves before adding mods
- Test new mods before rolling out to all players
- Keep a list of installed mods and versions

## Directory Structure

After setup, your mod structure should look like:

```
your-server/
├── docker-compose.yml
├── .env
└── mods/
    ├── SomeContentMod/
    │   └── manifest.json
    └── AnotherMod/
        └── manifest.json
```

The server mounts these at `/data/Mods/extra` and SMAPI loads them automatically.

