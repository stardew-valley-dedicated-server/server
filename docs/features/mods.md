# Mod Support

JunimoServer supports SMAPI mods.

## Installing Mods

::: tip
[Back up your saves](/features/backup) before adding or changing mods.
:::

### 1. Create a Mods Directory

Create a directory on your host machine to store mods:

```sh
mkdir mods
```

### 2. Download Mods

Download SMAPI mods from sources like:

- [Nexus Mods](https://www.nexusmods.com/stardewvalley)
- [ModDrop](https://www.moddrop.com/stardew-valley)
- [CurseForge](https://www.curseforge.com/stardewvalley)
- [Stardew Valley Official Forum](https://forums.stardewvalley.net/forums/mods.25/)

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
Attach to the [server console](/admins/operations/commands) with `docker compose exec server
attach-cli` — its top pane shows the server logs, where SMAPI lists all loaded mods at startup.
:::

## Mod Types

| Type | Install On | Example |
|------|-----------|---------|
| **Server-only** | Server | JunimoServer core, automation mods |
| **Content mods** | Server AND clients | New items, NPCs, maps |
| **Client-only** | Client only | UI improvements, client utilities |

::: warning Content Mods
Content mods that add items, NPCs, or maps must be installed on **both the server and every
client**, with matching versions and configuration. A mismatch causes sync issues such as missing
items or NPCs, so share your mod list and versions with players.
:::

## Mod Compatibility

Most mods work on JunimoServer without any changes. Assume a mod is fine unless it's listed
below or you run into problems with it.

We don't keep a list of mods that are known to work. Such a list would suggest that anything
missing from it is unsupported, which isn't true, and it would be impossible to keep up to date
across thousands of mods and every game update. This page lists only the mods and types of mods
that are known to cause problems.

When picking mods, prefer ones that are actively maintained and check the comments for
multiplayer feedback.

### Mod types that usually don't work

These are broad types rather than specific mods. A headless server can't support them:

- **Client and UI mods on the server** — the server doesn't render graphics or take input, so
  UI, HUD, and interface mods do nothing there. Install them on each player's client instead.
- **Mods that need keyboard, mouse, or the game window** — the server runs headless with input
  and rendering turned off, so anything driven by keybinds or per-frame drawing won't work.
- **Mods that assume single-player** — the server always runs in multiplayer mode, so a mod that
  expects one local player can behave unexpectedly.
- **Mods that change the save-folder layout** — the server manages its own save folders and
  Docker volumes, so a mod that restructures how saves are stored can conflict with that.
- **Mods that add their own always-on or no-pause behavior** — the server already runs
  always-on and handles day transitions itself, so a mod doing the same can fight it.

::: warning Mods that add host-side events
The server plays the host automatically, with no one at the keyboard to click through prompts.
Mods that add new host-side events, festivals, or cutscenes can stall it if the automation
doesn't know how to advance them — most often as a hang during a day transition. This doesn't
mean they're incompatible, but test them before running them on a live server.
:::

### Known incompatible mods

| Mod | Problem | Status | Last checked |
|-----|---------|--------|--------------|
| _None so far_ | — | — | — |

**Status** is either **Incompatible** (no known workaround) or **Workaround** (works with the
setup noted in the row). **Last checked** is the date we last confirmed the entry — if it's old,
the mod may have changed since, so it's worth re-testing and opening a PR if it works now.

### Reporting a problem

If a mod misbehaves, please [report it](/community/reporting-bugs) so we can fix it or add it
here. Attaching a [diagnostics bundle](/community/reporting-bugs#collect-diagnostics) gives us the
logs and setup automatically — no copy-pasting. In your own words, also tell us:

- **The mod's name and version**
- **Where it goes wrong** — on the server, on a client, or when connecting

Each report becomes a fix or a new entry, which keeps this list short and accurate.

## Troubleshooting

### Mod Not Loading

1. **Check the server console**: Look for error messages in `docker compose exec server attach-cli`
2. **Verify mod location**: Ensure correct directory structure
3. **Check dependencies**: Some mods require other mods
4. **Update mods**: Ensure compatibility with your game version

### "Missing assembly" Errors

The mod is missing a dependency. Check the mod's page for required mods.

### Performance Issues

If performance degrades after adding mods:

- Add mods one at a time to identify problems
- Check mod resource usage requirements
- Consider increasing server resources
- Remove unused mods
