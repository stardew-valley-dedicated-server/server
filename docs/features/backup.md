# Backup & Recovery

## Backup Overview

| Backup Type | Frequency | Location | Managed By |
|-------------|-----------|----------|------------|
| SMAPI Auto-Backup | Once per day | `/data/game/save-backups` | SMAPI |
| Previous save (game) | Every save | `_old` files inside the save folder | Game |
| Docker Volumes | Persistent | Host filesystem | Docker |
| Manual Backups | On-demand | Your choice | You |

## SMAPI Automatic Backups

SMAPI's built-in Save Backup mod compresses all your saves into a single dated zip once per day.

### Viewing Backups

```sh
docker compose exec server ls -al /data/game/save-backups
```

You'll see dated zip files named `{date} - SMAPI {version} with Stardew Valley {version}.zip` (the version numbers reflect your installed build):

```
2026-03-26 - SMAPI 4.5.1 with Stardew Valley 1.6.15.zip
2026-03-21 - SMAPI 4.5.1 with Stardew Valley 1.6.15.zip
...
```

Each archive contains every save folder (e.g. `Junimo_430843225/`) at its root.

### Restoring from SMAPI Backup

**1. Move current save aside:**

```sh
docker compose exec server bash -c "cd /config/xdg/config/StardewValley && mv Saves Saves_old && mkdir Saves"
```

**2. List available backups:**

```sh
docker compose exec server ls -al /data/game/save-backups
```

**3. Restore the desired backup:**

```sh
docker compose exec server unzip "/data/game/save-backups/BACKUP_FILENAME.zip" -d /config/xdg/config/StardewValley/Saves/
```

**4. Verify and restart:**

```sh
docker compose exec server ls -al /config/xdg/config/StardewValley/Saves
docker compose restart
```

## Previous-Save Copy (`_old` files)

Each save keeps the previous save alongside the new one, as `<save>_old` and `SaveGameInfo_old` in the same folder. The game uses these only for crash recovery, but you can roll back to them by hand. The server never deletes them, and they are **not** in [SMAPI's daily zips](#smapi-automatic-backups).

This is one *save* behind — usually one in-game day, but not always: cabin migration and the [`farmhand`](/admins/operations/commands#farmhand) command each save without advancing the day, leaving `_old` at an earlier point of the same day. Check the dates the rollback prints before swapping.

List the copies:

```sh
docker compose exec server sh -c 'ls -la /config/xdg/config/StardewValley/Saves/*/*_old'
```

### Finding your active save name

The server loads the save named in a pointer file, not by scanning folders, so renaming a folder without updating it breaks startup. Print the active save name:

```sh
docker compose exec server sed -n 's/.*"SaveNameToLoad": *"\([^"]*\)".*/\1/p' /config/xdg/config/StardewValley/.smapi/mod-data/junimohost.server/junimohost.gameloader.json
```

Change the active save with the [`saves`](/admins/operations/commands#saves) command, not by renaming folders.

### Rolling back one day

Stop the server, swap the current save with its `_old` copy, then start again. The script backs up the folder first, prints both dates, and keeps every rename inside the save folder so an interrupted run leaves all files intact:

```sh
docker compose stop server

docker compose run --rm --no-deps --entrypoint sh server -c '
  set -eu
  SAVE=Junimo_474497955193478706
  cd /config/xdg/config/StardewValley/Saves/$SAVE
  for f in $SAVE SaveGameInfo ${SAVE}_old SaveGameInfo_old; do
    [ -s "$f" ] || { echo "missing or empty: $f"; exit 1; }
  done
  [ ! -e ${SAVE}_swap ] && [ ! -e SaveGameInfo_swap ] || { echo "leftover _swap files, clean up first"; exit 1; }
  BACKUP=/config/xdg/config/StardewValley/Backups/${SAVE}_$(date +%Y%m%d-%H%M%S)
  mkdir -p "$BACKUP" && cp -a . "$BACKUP" && echo "backup: $BACKUP"
  echo "current: $(grep -oE "<(dayOfMonth|currentSeason|year)>[^<]*" $SAVE | tr "\n" " ")"
  echo "old:     $(grep -oE "<(dayOfMonth|currentSeason|year)>[^<]*" ${SAVE}_old | tr "\n" " ")"
  mv $SAVE ${SAVE}_swap && mv SaveGameInfo SaveGameInfo_swap
  mv ${SAVE}_old $SAVE && mv SaveGameInfo_old SaveGameInfo
  mv ${SAVE}_swap ${SAVE}_old && mv SaveGameInfo_swap SaveGameInfo_old
  echo "swapped. main save now: $(grep -oE "<(dayOfMonth|currentSeason|year)>[^<]*" $SAVE | tr "\n" " ")"
'

docker compose start server
```

- Replace the `SAVE=` line with your active save name (above).
- Run from the compose directory in PowerShell, Git Bash, or a Unix shell — not `cmd.exe`.
- Running it again swaps back.
- The backup lands in `Backups/`, beside `Saves/`, so the game never lists it as a second save; the boot-time ownership sweep fixes its owner on restart.

### Cleaning up rollback backups

Rollback copies accumulate under `Backups/`. List them, then remove all or one:

```sh
# List rollback backups
docker compose exec server ls -la /config/xdg/config/StardewValley/Backups

# Remove all of them
docker compose exec server rm -rf /config/xdg/config/StardewValley/Backups

# Or remove a single one
docker compose exec server rm -rf /config/xdg/config/StardewValley/Backups/<BACKUP_NAME>
```

Restore a folder from a backup with the server stopped:

```sh
docker compose stop server

docker compose run --rm --no-deps --entrypoint sh server -c '
  cp -a /config/xdg/config/StardewValley/Backups/<BACKUP_NAME>/. /config/xdg/config/StardewValley/Saves/<SAVE>/
'

docker compose start server
```

## Docker Volume Persistence

Your data lives in Docker volumes and bind mounts that persist across container restarts and updates:

| Data | Storage | Contents |
|------|---------|----------|
| Saves | `saves` volume | Save files |
| Game data | `game-data` volume | Game files |
| Steam session | `steam-session` volume | Steam tokens |
| Discord dashboard | `discord-bot-data` volume | Dashboard ownership id ([details](/admins/configuration/discord#status-dashboard)) |
| Settings | `.local-container/settings/` bind mount | Server configuration (`server-settings.json`) |

::: info Volume Names
Docker Compose prefixes each volume with your project directory name, so the `saves` volume is actually `<project>_saves` (e.g. `server_saves` if you cloned into a `server/` directory). Run `docker volume ls` to see the real names, and substitute your prefix wherever `<project>_saves` appears below.
:::

### Inspecting Volumes

```sh
# List volumes (find your real prefix here)
docker volume ls

# Inspect the saves volume
docker volume inspect <project>_saves
```

## Manual Backups

For maximum safety, create periodic manual backups.

All commands below use `<project>_saves` for the saves volume. Replace it with your real prefix (see the volume names note above).

### Backup Saves Volume

**Linux/macOS:**

```sh
docker run --rm -v <project>_saves:/saves -v $(pwd):/backup alpine tar czf /backup/saves-backup-$(date +%Y%m%d).tar.gz /saves
```

**Windows PowerShell:**

```powershell
docker run --rm -v <project>_saves:/saves -v ${PWD}:/backup alpine tar czf /backup/saves-backup-$(Get-Date -Format "yyyyMMdd").tar.gz /saves
```

### Backup All Data

Create a comprehensive backup:

```sh
# Stop server first
docker compose down

# Backup saves volume
docker run --rm -v <project>_saves:/data -v $(pwd):/backup alpine tar czf /backup/saves-$(date +%Y%m%d).tar.gz /data

# Backup settings (bind mount, just copy the directory)
cp -r .local-container/settings settings-backup-$(date +%Y%m%d)

# Restart server
docker compose up -d
```

### Restore from Manual Backup

```sh
# Stop server
docker compose down

# Remove old volume
docker volume rm <project>_saves

# Recreate volume
docker volume create <project>_saves

# Restore from backup
docker run --rm -v <project>_saves:/data -v $(pwd):/backup alpine sh -c "cd /data && tar xzf /backup/saves-YYYYMMDD.tar.gz --strip-components=1"

# Restart
docker compose up -d
```

## Backup Best Practices

### Frequency

| Situation | Recommended Frequency |
|-----------|----------------------|
| Active development | Before any changes |
| Regular play | Daily |
| Stable server | Weekly |
| Before upgrades | Always |

### Storage

- Keep backups off the server (cloud storage, different machine)
- Maintain multiple generations (daily, weekly, monthly)
- Test restore procedures periodically
- Document your backup locations

### Automation

Consider automating backups with cron (Linux) or Task Scheduler (Windows):

```sh
# Example cron entry (daily at 3 AM)
0 3 * * * /path/to/backup-script.sh
```

::: tip Bringing in a save from elsewhere?
This page covers backing up and restoring saves you already host. To import an existing save into the
server, see [Importing Saves](/admins/operations/importing-saves).
:::

## Recovery Scenarios

### Lost a Day

1. The game keeps the previous save as `_old` files in the save folder
2. Roll back one day with [Rolling back one day](#rolling-back-one-day)
3. Check the printed dates before confirming — one save back is usually one day, not always

### Corrupted Save

1. Check SMAPI backups for recent clean save
2. Restore from backup as described above
3. If all backups corrupted, check manual backups

### Accidental Deletion

1. Saves volume persists unless explicitly deleted
2. Restore from SMAPI or manual backup
3. Consider more frequent backups

### Server Crash

1. Docker auto-restarts containers
2. Last save should be intact
3. Check logs for cause: `docker compose logs`

### Failed Upgrade

1. Don't panic. Volumes persist.
2. Rollback server version if needed
3. Restore saves from pre-upgrade backup

