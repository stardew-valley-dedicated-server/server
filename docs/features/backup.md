# Backup & Recovery

## Backup Overview

| Backup Type | Frequency | Location | Managed By |
|-------------|-----------|----------|------------|
| SMAPI Auto-Backup | Once per day | `/data/game/save-backups` | SMAPI |
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

## Docker Volume Persistence

Your data lives in Docker volumes and bind mounts that persist across container restarts and updates:

| Data | Storage | Contents |
|------|---------|----------|
| Saves | `saves` volume | Save files |
| Game data | `game-data` volume | Game files |
| Steam session | `steam-session` volume | Steam tokens |
| Settings | `.local-container/settings/` bind mount | Server configuration (`server-settings.json`) |

::: info Volume Names
Docker Compose prefixes volume names with your project directory. If your directory is `junimoserver`, the saves volume is `junimoserver_saves`. Run `docker volume ls` to see actual names.
:::

### Inspecting Volumes

```sh
# List volumes
docker volume ls

# Inspect a volume (replace prefix with your project directory name)
docker volume inspect junimoserver_saves
```

## Manual Backups

For maximum safety, create periodic manual backups.

### Backup Saves Volume

Replace `junimoserver` below with your project directory name (see volume names note above).

**Linux/macOS:**

```sh
docker run --rm -v junimoserver_saves:/saves -v $(pwd):/backup ubuntu tar czf /backup/saves-backup-$(date +%Y%m%d).tar.gz /saves
```

**Windows PowerShell:**

```powershell
docker run --rm -v junimoserver_saves:/saves -v ${PWD}:/backup ubuntu tar czf /backup/saves-backup-$(Get-Date -Format "yyyyMMdd").tar.gz /saves
```

### Backup All Data

Create a comprehensive backup:

```sh
# Stop server first
docker compose down

# Backup saves volume (replace junimoserver with your directory name)
docker run --rm -v junimoserver_saves:/data -v $(pwd):/backup ubuntu tar czf /backup/saves-$(date +%Y%m%d).tar.gz /data

# Backup settings (bind mount, just copy the directory)
cp -r .local-container/settings settings-backup-$(date +%Y%m%d)

# Restart server
docker compose up -d
```

### Restore from Manual Backup

Replace `junimoserver` below with your project directory name.

```sh
# Stop server
docker compose down

# Remove old volume
docker volume rm junimoserver_saves

# Recreate volume
docker volume create junimoserver_saves

# Restore from backup
docker run --rm -v junimoserver_saves:/data -v $(pwd):/backup ubuntu bash -c "cd /data && tar xzf /backup/saves-YYYYMMDD.tar.gz --strip-components=1"

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

## Importing Existing Saves

To bring an existing save to your server:

**1. Stop the server:**

```sh
docker compose down
```

**2. Copy save folder into volume** (replace `junimoserver` with your directory name)**:**

```sh
docker run --rm -v junimoserver_saves:/saves -v $(pwd):/backup ubuntu cp -r /backup/YourFarm_123456789 /saves/
```

**3. Start server:**

```sh
docker compose up -d
```

**4. Verify in game:**

Connect via VNC and check that your save appears.

## Recovery Scenarios

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

