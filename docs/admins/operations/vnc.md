# Web Interface (VNC)

::: warning Advanced Tool
VNC is for debugging and advanced troubleshooting only. For normal operation, use the [CLI](/admins/operations/commands) and connect to your server with your game client like any multiplayer game.

By default, rendering is disabled (`DISABLE_RENDERING=true`) for performance — VNC will show a black screen. This is intentional.
:::

## When to Use VNC

- Debugging visual issues that can't be diagnosed via logs
- Troubleshooting mod loading problems
- Rare edge cases where CLI isn't enough

For everything else, use the CLI (`docker compose exec server attach-cli`) or in-game commands.

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

## Save Files

Save files are stored in the `saves` Docker volume at `/config/xdg/config/StardewValley/Saves`.

For backup, restore, and import procedures, see [Backup & Recovery](/features/backup).

## Why is VNC showing a black screen?

This is **expected behavior**. By default, `DISABLE_RENDERING=true` which means the server doesn't draw graphics to its own display.

::: info This doesn't affect players
Players always see the game normally on their own screens. The `DISABLE_RENDERING` setting only affects the server's display (what you see via VNC). Your server is running correctly — connect with your game client to verify.
:::

### Enabling VNC display (for debugging only)

If you specifically need to see the server's display for debugging:

**Option 1: Environment variable (persistent)**

1. Set `DISABLE_RENDERING=false` in `.env`
2. Restart: `docker compose restart`

**Option 2: Console command (temporary)**

```sh
docker compose exec server attach-cli
# Then type one of:
#   rendering on       - Enable rendering
#   rendering off      - Disable rendering
#   rendering toggle   - Toggle current state
#   rendering status   - Show current state
```

The console command only lasts until the server restarts.

## Troubleshooting

If VNC won't load:

1. Check `VNC_PASSWORD` is set in `.env`
2. Verify firewall allows TCP on VNC port (default 5800)
3. Use `http://` not `https://`
4. Try a different browser

