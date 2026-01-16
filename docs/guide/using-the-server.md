# Using the Server

Once your JunimoServer is up and running, you can manage it through the web-based VNC interface and access your save files.

## Web VNC Interface

### Connecting

To connect to the web VNC interface, open your browser and navigate to:

```
http://YOUR_SERVER_IP:VNC_PORT
```

Replace `YOUR_SERVER_IP` with your server's IP address and `VNC_PORT` with the port you configured (default is 5800).

::: tip Local Access
If you're running the server on your local machine, use `http://localhost:5800`
:::

You'll be prompted to enter the VNC password you configured in your `.env` file.

### VNC Settings Panel

The settings panel on the left side of the VNC interface provides several options:

- **Connection Quality**: Adjust the image quality to balance between visual clarity and bandwidth usage
- **Compression Level**: Control the compression to optimize for your network speed
- **Scaling Mode**: Adjust how the display scales to your browser window

::: warning Avoid Remote Resizing
Do **not** use the `Remote Resizing` scaling mode. This mode can cause stability issues and may require a server restart to fix. Use other scaling options instead.
:::

### Clipboard Functionality

Copy and paste operations work through the VNC interface, but with a special method:

1. Open the settings panel on the left
2. Use the clipboard text area to transfer text
3. Copy text into this area to send it to the server
4. Text copied on the server will appear in this area

::: info
Direct copy/paste with Ctrl+C/Ctrl+V won't work. You must use the settings panel clipboard area.
:::

## Managing Save Files

### Save File Location

Your farm save files are stored in the `saves` volume, which is mounted at `/config/xdg/config/StardewValley` inside the container. This directory contains all your game progress and farm data.

When using Docker, this volume is persisted separately from the container, so your save files remain safe even if you restart or update the server.

### SMAPI Automatic Backups

SMAPI automatically creates backups of your save files. These backups are stored at `/data/Stardew/save-backups` inside the container.

To check existing backups:

```sh
docker compose exec server ls -al /data/Stardew/save-backups
```

### Restoring from SMAPI Backups

If you need to restore a save from SMAPI's automatic backups:

**1. Move the current save out of the way**

```sh
docker compose exec server bash -c "cd /config/xdg/config/StardewValley && mv Saves Saves_old && mkdir Saves"
```

**2. Check available backups**

```sh
docker compose exec server ls -al /data/Stardew/save-backups
```

**3. Unpack the desired backup** (replace `BACKUP_FILENAME` with the actual filename)

```sh
docker compose exec server unzip /data/Stardew/save-backups/BACKUP_FILENAME.zip -d /config/xdg/config/StardewValley/Saves/
```

**4. Verify the restore**

```sh
docker compose exec server bash -c "cd /config/xdg/config/StardewValley && ls -al Saves && ls -al Saves_old"
```

You should see your save folder in both locations.

### Manual Backups

For additional safety, it's recommended to also create manual backups of the `saves` volume at regular intervals.

To manually backup your saves:

**Using Docker:**

```sh
# Create a backup of the saves volume
docker run --rm -v server_saves:/saves -v $(pwd):/backup ubuntu tar czf /backup/saves-backup-$(date +%Y%m%d).tar.gz /saves
```

**On Windows PowerShell:**

```powershell
# Create a backup of the saves volume
docker run --rm -v server_saves:/saves -v ${PWD}:/backup ubuntu tar czf /backup/saves-backup-$(Get-Date -Format "yyyyMMdd").tar.gz /saves
```

### Accessing Save Files

If you need to access or modify save files directly:

1. **Stop the server** with `docker compose down`
2. **Locate the volume** - Docker stores the saves volume on your system
3. **Access files** through Docker volume inspection:

```sh
# Find where Docker stores the volume
docker volume inspect server_saves

# Copy files out of the volume
docker run --rm -v server_saves:/saves -v $(pwd):/backup ubuntu cp -r /saves /backup/
```

## Console Commands

JunimoServer provides a dedicated CLI for sending commands directly to the server.

### Using the CLI

To attach to the interactive server console:

```sh
# Using make
make cli

# Or using docker compose directly
docker compose exec server attach-cli
```

The CLI provides a split-pane tmux interface:
- **Top pane**: Server logs (read-only, scrollable with mouse wheel)
- **Bottom pane**: Interactive command input

### CLI Commands

| Command | Description |
|---------|-------------|
| `cli exit` / `cli quit` / `cli detach` | Detach from the CLI session |
| `cli clear` | Clear the input pane |
| Any other input | Sent directly to the SMAPI console |

You can also run standard SMAPI console commands by typing them in the CLI input pane.

## Next Steps

- [Managing Mods](/guide/managing-mods) - Learn how to add SMAPI mods to your server
- [Upgrading](/guide/upgrading) - Keep your server up to date
