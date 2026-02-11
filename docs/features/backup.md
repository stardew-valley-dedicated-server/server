# Backup & Recovery

JunimoServer provides multiple backup mechanisms to keep your farm safe.

## Backup Overview

| Backup Type | Frequency | Location | Managed By |
|-------------|-----------|----------|------------|
| SMAPI Auto-Backup | Each save | `/data/Stardew/save-backups` | SMAPI |
| Docker Volumes | Persistent | Host filesystem | Docker |
| Manual Backups | On-demand | Your choice | You |

## SMAPI Automatic Backups

SMAPI creates compressed backups of your save files every time the game saves.

### Viewing Backups

```sh
docker compose exec server ls -al /data/Stardew/save-backups
```

You'll see timestamped zip files like:

```
FarmName_123456789_2024-01-15_14-30-00.zip
FarmName_123456789_2024-01-14_20-15-30.zip
...
```

### Restoring from SMAPI Backup

**1. Move current save aside:**

```sh
docker compose exec server bash -c "cd /config/xdg/config/StardewValley && mv Saves Saves_old && mkdir Saves"
```

**2. List available backups:**

```sh
docker compose exec server ls -al /data/Stardew/save-backups
```

**3. Restore the desired backup:**

```sh
docker compose exec server unzip /data/Stardew/save-backups/BACKUP_FILENAME.zip -d /config/xdg/config/StardewValley/Saves/
```

**4. Verify and restart:**

```sh
docker compose exec server ls -al /config/xdg/config/StardewValley/Saves
docker compose restart
```

## Docker Volume Persistence

Your data lives in Docker volumes that persist across container restarts and updates:

| Volume | Contents |
|--------|----------|
| `server_saves` | Save files |
| `server_settings` | Server configuration |
| `server_game-data` | Game files |
| `server_steam-session` | Steam tokens |

### Inspecting Volumes

```sh
# List volumes
docker volume ls

# Inspect a volume
docker volume inspect server_saves
```

## Manual Backups

For maximum safety, create periodic manual backups.

### Backup Saves Volume

**Linux/macOS:**

```sh
docker run --rm -v server_saves:/saves -v $(pwd):/backup ubuntu tar czf /backup/saves-backup-$(date +%Y%m%d).tar.gz /saves
```

**Windows PowerShell:**

```powershell
docker run --rm -v server_saves:/saves -v ${PWD}:/backup ubuntu tar czf /backup/saves-backup-$(Get-Date -Format "yyyyMMdd").tar.gz /saves
```

### Backup All Volumes

Create a comprehensive backup:

```sh
# Stop server first
docker compose down

# Backup each volume
docker run --rm -v server_saves:/data -v $(pwd):/backup ubuntu tar czf /backup/saves-$(date +%Y%m%d).tar.gz /data
docker run --rm -v server_settings:/data -v $(pwd):/backup ubuntu tar czf /backup/settings-$(date +%Y%m%d).tar.gz /data

# Restart server
docker compose up -d
```

### Restore from Manual Backup

```sh
# Stop server
docker compose down

# Remove old volume
docker volume rm server_saves

# Recreate volume
docker volume create server_saves

# Restore from backup
docker run --rm -v server_saves:/data -v $(pwd):/backup ubuntu bash -c "cd /data && tar xzf /backup/saves-YYYYMMDD.tar.gz --strip-components=1"

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

**2. Copy save folder into volume:**

```sh
docker run --rm -v server_saves:/saves -v $(pwd):/backup ubuntu cp -r /backup/YourFarm_123456789 /saves/
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

1. Don't panic — volumes persist
2. Rollback server version if needed
3. Restore saves from pre-upgrade backup

## Next Steps

- [VNC Interface](/admins/operations/vnc) — Access save management
- [Upgrading](/admins/operations/upgrading) — Safe upgrade procedures
- [Troubleshooting](/admins/troubleshooting) — Recovery help
