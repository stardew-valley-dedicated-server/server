# Web Interface (VNC)

JunimoServer provides a web-based VNC interface for graphical server management.

## Connecting

Open your browser and navigate to:

```
http://YOUR_SERVER_IP:VNC_PORT
```

Replace:
- `YOUR_SERVER_IP` with your server's IP address (or `localhost` for local access)
- `VNC_PORT` with the port configured in `.env` (default: `5800`)

Example: `http://localhost:5800`

You'll be prompted to enter the VNC password configured in your `.env` file.

## VNC Settings Panel

The settings panel on the left side provides several options:

| Option | Description |
|--------|-------------|
| Connection Quality | Balance between visual clarity and bandwidth |
| Compression Level | Optimize for your network speed |
| Scaling Mode | How the display fits your browser window |

::: warning Avoid Remote Resizing
Do **not** use the `Remote Resizing` scaling mode. This can cause stability issues and may require a server restart to fix. Use other scaling options instead.
:::

## Clipboard

Copy and paste works through the VNC interface, but requires a special method:

1. Open the settings panel on the left
2. Use the clipboard text area to transfer text
3. Paste text into this area to send it to the server
4. Text copied on the server appears in this area

::: info
Direct copy/paste with Ctrl+C/Ctrl+V won't work. You must use the settings panel clipboard area.
:::

## Managing Save Files

### Save Location

Save files are stored in the `saves` Docker volume, mounted at `/config/xdg/config/StardewValley/Saves` inside the container.

This volume persists separately from the container, so saves remain safe across restarts and updates.

### SMAPI Automatic Backups

SMAPI creates automatic backups at `/data/Stardew/save-backups`.

Check existing backups:

```sh
docker compose exec server ls -al /data/Stardew/save-backups
```

### Restoring from Backup

If you need to restore a save:

**1. Move current save aside:**

```sh
docker compose exec server bash -c "cd /config/xdg/config/StardewValley && mv Saves Saves_old && mkdir Saves"
```

**2. List available backups:**

```sh
docker compose exec server ls -al /data/Stardew/save-backups
```

**3. Restore a backup (replace `BACKUP_FILENAME`):**

```sh
docker compose exec server unzip /data/Stardew/save-backups/BACKUP_FILENAME.zip -d /config/xdg/config/StardewValley/Saves/
```

**4. Verify the restore:**

```sh
docker compose exec server ls -al /config/xdg/config/StardewValley/Saves
```

### Manual Backups

For additional safety, create manual backups of the saves volume:

**Linux/macOS:**

```sh
docker run --rm -v server_saves:/saves -v $(pwd):/backup ubuntu tar czf /backup/saves-backup-$(date +%Y%m%d).tar.gz /saves
```

**Windows PowerShell:**

```powershell
docker run --rm -v server_saves:/saves -v ${PWD}:/backup ubuntu tar czf /backup/saves-backup-$(Get-Date -Format "yyyyMMdd").tar.gz /saves
```

### Accessing Save Files Directly

To copy saves out for manual editing:

```sh
# Find volume location
docker volume inspect server_saves

# Copy files from volume
docker run --rm -v server_saves:/saves -v $(pwd):/backup ubuntu cp -r /saves /backup/
```

## Importing Existing Saves

To use an existing Stardew Valley save:

1. Stop the server: `docker compose down`
2. Copy your save folder into the volume:

```sh
docker run --rm -v server_saves:/saves -v $(pwd):/backup ubuntu cp -r /backup/YourSaveFolder /saves/
```

3. Start the server: `docker compose up -d`

::: warning Backup First
Always backup your original save before importing. The server may modify save files.
:::

## Rendering Mode

By default, `DISABLE_RENDERING=true` optimizes performance by skipping visual rendering.

If you need to see the full game display (for debugging):

1. Set `DISABLE_RENDERING=false` in `.env`
2. Restart: `docker compose restart`

Or toggle via console:

```sh
docker compose exec server attach-cli
# Then type: rendering on
```

## Next Steps

- [Console & Chat Commands](/admins/operations/commands) — CLI and in-game management
- [Networking](/admins/operations/networking) — Connection troubleshooting
