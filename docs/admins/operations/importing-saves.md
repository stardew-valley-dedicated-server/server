# Importing Saves

Copy an existing save into the saves volume, then import it from the server console. You run both steps
on the machine hosting the server, not on the computer the save came from.

The farm comes over intact. The owner keeps their levels, items, money, relationships, and progress, and
selects their farmer from the farmhand list as usual when they connect. The only change is that the
server runs the farm with its own automated host instead of a person.

## 1. Copy the save folder

A save is a **folder** named `{FarmName}_{number}` (e.g. `YourFarm_123456789`). Copy the whole folder.
On a normal install it lives here:

| OS | Saves directory |
|----|-----------------|
| Windows | `%appdata%\StardewValley\Saves` |
| macOS | `~/.config/StardewValley/Saves` |
| Linux | `~/.config/StardewValley/Saves` |

It must land under `Saves/` inside the volume, at `Saves/{FarmName}_{number}`. Put the save folder on
the server host, then run this from the directory that contains it:

```sh
docker run --rm -v <project>_saves:/sv -v $(pwd):/backup alpine cp -r /backup/YourFarm_123456789 /sv/Saves/
```

::: info
Replace `<project>` with your prefix. Run `docker volume ls` to find it (e.g. `server_saves`).
:::

## 2. Import and load it

In the server console (see [Connecting to the CLI](/admins/operations/commands#connecting-to-cli)), run:

```text
saves import YourFarm_123456789 --swap-host-to 76561198XXXXXXXXX --reload
```

- **`--swap-host-to <id>`** keeps the save's owner a player. The `<id>` is their platform id (see
  [Finding a player's id](#finding-a-player-s-id) below). Without it, the server takes over their
  farmer. This rewrites the save in place, so back up first.
- **`--reload`** loads the save right away instead of waiting for a restart. Use `--force-reload` if
  players are connected and you want to kick them first.

Skipped `--reload`? Run `saves reload` (or restart) to load it.

## Finding a player's id

<!--@include: ../../_partials/platform-id-lookup.md-->

## Good to know

- **Back up the local save you are importing.** A swap rewrites that folder in place. If the rewrite
  fails the save is left untouched, but once it succeeds there is no undo without your backup.
- **Your previous server save is kept.** Importing only changes which save the server loads on boot, so
  the old one stays in the volume.
- **Importing is one-way.** There is no supported way to move server progress back into a local
  single-player or co-op game, so import once you are ready to continue the farm on the server.
- **Wrong id?** Re-run the import with the correct `--swap-host-to` id before the save loads, and it
  overrides the binding. Once the save has loaded, copy the original folder in again (step 1) and
  re-import to start fresh.
- **Id already in use.** If the id you give already belongs to another farmhand in that save, the import
  refuses and names it. Double-check you have the right player's id.

## See also

- [`saves` command reference](/admins/operations/commands#saves)
- [Backup & Recovery](/features/backup)
