# Importing Saves

Copy an existing save into the saves volume, then import it from the server console.

## 1. Copy the save folder

A save is a **folder** named `{FarmName}_{number}` (e.g. `YourFarm_123456789`). Copy the whole folder.
On a normal install it lives here:

| OS | Saves directory |
|----|-----------------|
| Windows | `%appdata%\StardewValley\Saves` |
| macOS | `~/.config/StardewValley/Saves` |
| Linux | `~/.config/StardewValley/Saves` |

It must land under `Saves/` inside the volume, at `Saves/{FarmName}_{number}`:

```sh
docker run --rm -v <project>_saves:/sv -v $(pwd):/backup alpine cp -r /backup/YourFarm_123456789 /sv/Saves/
```

::: info
Replace `<project>` with your prefix. Run `docker volume ls` to find it (e.g. `server_saves`).
:::

## 2. Import and load it

```text
saves import YourFarm_123456789 --swap-host-to 76561198XXXXXXXXX --reload
```

- **`--swap-host-to <id>`** keeps the save's owner a player (Steam64 or GOG Galaxy id). Without it, the
  server takes over their farmer. This rewrites the save in place, so back up first.
- **`--reload`** loads the save right away instead of waiting for a restart. Use `--force-reload` if
  players are connected and you want to kick them first.

Skipped `--reload`? Run `saves reload` (or restart) to load it.

## See also

- [`saves` command reference](/admins/operations/commands#saves)
- [Backup & Recovery](/features/backup)
