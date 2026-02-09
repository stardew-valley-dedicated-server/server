# Troubleshooting

Common issues and solutions for JunimoServer administrators.

## Server Won't Start

### Basic Checks

```sh
# Check if Docker is running
docker ps

# Check container status
docker compose ps

# View logs
docker compose logs -f
```

### Common Causes

**Docker not running:**

- Start Docker Desktop (Windows/macOS)
- Start Docker service: `sudo systemctl start docker` (Linux)

**Invalid `.env` configuration:**

- Check all required variables are set
- Verify no syntax errors (no spaces around `=`)
- Ensure passwords don't contain special characters that need escaping

**Steam credentials invalid:**

- Verify username and password are correct
- Re-run setup: `docker compose run --rm -it steam-auth setup`

**Port conflicts:**

- Check if ports are in use: `netstat -tulpn | grep 5800`
- Change ports in `.env` if needed

## Steam Authentication Issues

### Steam auth container not starting

```sh
docker compose logs steam-auth
```

Common fixes:

- Re-run setup: `docker compose run --rm -it steam-auth setup`
- Delete session and retry: `docker volume rm server_steam-session`

### Token expired

Steam tokens last about 200 days. When expired:

```sh
docker compose run --rm -it steam-auth setup
```

### Steam Guard failing

- Ensure you have access to the email/phone for Steam Guard
- Try the QR code method if other methods fail
- Wait a few minutes and retry if rate-limited

### Game server can't reach steam-auth

```sh
# Test from inside server container
docker compose exec server wget -qO- http://steam-auth:3001/health

# Check steam-auth health directly
curl http://localhost:3001/health
```

## Player Connection Issues

### Players can't connect at all

1. Verify server is running: `docker compose ps`
2. Get invite code via CLI: `docker compose exec server attach-cli` then `info`
3. Share the correct invite code with players

### Same-network players can't connect

This is usually a "hairpinning" issue — traffic can't loop back to the same network.

```sh
docker compose exec server netdebug nat
```

If "Hairpinning: Not supported":

- Have players connect from mobile hotspot
- Use a VPN service
- Enable direct IP connections (if acceptable for your use case)

### Intermittent disconnections

- Check server resources: `docker stats`
- Review logs for errors: `docker compose logs -f`
- Consider increasing server RAM if low on memory

### Steam clients specifically failing

Check for these log messages:

| Message | Meaning | Fix |
|---------|---------|-----|
| `GameServer.Init() failed` | Steamworks SDK issue | Restart, check game files |
| `Failed to connect to Steam servers` | Network issue | Check firewall, outbound UDP |
| `SDR relay status: Unknown` | SDR initializing | Wait a few seconds, retry |

### GOG clients specifically failing

- Verify invite code starts with "G"
- Run `netdebug nat` to check NAT type
- GOG has ~50% success rate vs Steam's ~99%

## VNC Issues

### VNC won't load

1. Check `VNC_PASSWORD` is set in `.env`
2. Verify port is accessible: `curl http://localhost:5800`
3. Use `http://` not `https://`
4. Try a different browser
5. Check firewall allows TCP on VNC port

### VNC shows black screen

- Check `DISABLE_RENDERING` in `.env`
- If `true`, VNC won't show game graphics (this is normal)
- Set to `false` if you need to see the display

### VNC is laggy

- Increase compression in VNC settings panel
- Reduce connection quality setting
- Check network bandwidth between you and server

## Save File Issues

### Save won't load

```sh
# Check save exists
docker compose exec server ls -la /config/xdg/config/StardewValley/Saves

# Check SMAPI logs for errors
docker compose logs server | grep -i error
```

### Importing a save doesn't work

1. Stop server: `docker compose down`
2. Copy save correctly into the volume
3. Ensure folder structure is correct
4. Check file permissions

### Save corruption

Restore from SMAPI backup:

```sh
# List backups
docker compose exec server ls -al /data/Stardew/save-backups

# Restore (replace BACKUP_FILENAME)
docker compose exec server unzip /data/Stardew/save-backups/BACKUP_FILENAME.zip -d /config/xdg/config/StardewValley/Saves/
```

## Mod Issues

### Mods not loading

1. Check SMAPI console for errors (via VNC or logs)
2. Verify mods are in correct location
3. Check mod compatibility with current game version
4. Try removing mods one by one to find problem

### Content mods causing issues

- Ensure all players have matching content mods
- Check mod load order in SMAPI output
- Update mods to latest versions

### "Missing assembly" errors

The mod is missing a dependency. Check the mod's page for required dependencies.

## Password Protection Issues

### Players stuck in lobby

- Verify `SERVER_PASSWORD` is correct in `.env`
- Check player is using `!login <password>` command
- Verify lobby layout exists: `!lobby list`

### Can't create lobby layouts

- Must be admin: `!admin <yourname>`
- Use `!lobby create <name>` to enter edit mode
- Save with `!lobby save` when done

## Performance Issues

### Server running slowly

```sh
# Check resource usage
docker stats

# Check for errors
docker compose logs server | grep -i error
```

Solutions:

- Enable `DISABLE_RENDERING=true`
- Increase container memory limits
- Reduce `MaxPlayers` setting
- Remove resource-heavy mods

### High memory usage

- Restart periodically: `docker compose restart`
- Check for mod memory leaks
- Consider increasing host memory

## Docker Issues

### Volume name conflicts

Docker prefixes volumes with project directory name:

```sh
# List all volumes
docker volume ls

# Remove specific volume (careful!)
docker volume rm server_game-data
```

### Container names

JunimoServer uses fixed container names:

| Container | Name |
|-----------|------|
| Game Server | `sdvd-server` |
| Steam Auth | `sdvd-steam-auth` |

```sh
# Use names directly
docker logs sdvd-server
docker exec -it sdvd-server bash
```

### Cleaning up

```sh
# Remove stopped containers and unused images
docker system prune

# Remove all volumes (DANGEROUS - deletes saves!)
# docker volume prune
```

## Getting More Help

If none of these solutions work:

1. **Collect logs:** `docker compose logs > logs.txt`
2. **Check GitHub Issues:** [stardew-valley-dedicated-server/server/issues](https://github.com/stardew-valley-dedicated-server/server/issues)
3. **Join Discord:** [discord.gg/w23GVXdSF7](https://discord.gg/w23GVXdSF7)
4. **Create an issue:** Include logs, configuration (without passwords), and steps to reproduce

## Next Steps

- [Getting Help](/community/getting-help) — Community support channels
- [Reporting Bugs](/community/reporting-bugs) — How to report issues
