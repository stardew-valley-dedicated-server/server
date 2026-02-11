# Configuration Overview

JunimoServer uses two configuration mechanisms:

| File | Purpose | When Applied |
|------|---------|--------------|
| `server-settings.json` | Game and server settings (farm name, cabins, players) | On server startup |
| `.env` file | Docker infrastructure, credentials, networking | On container start |

## Quick Reference

**Common tasks:**

- Change farm name, player limit, or cabin behavior → [Server Settings](/admins/configuration/server-settings)
- Set Steam credentials, VNC password, or ports → [Environment Variables](/admins/configuration/environment)
- Enable Discord bot and chat relay → [Discord Integration](/admins/configuration/discord)
- Require password to join → [Password Protection](/features/password-protection/)

## Configuration Files

### server-settings.json

Game settings like farm name, farm type, cabin strategy, and player limits. Created automatically on first startup inside the Docker `settings` volume.

To edit:

```sh
# View current settings
docker compose exec server cat /data/settings/server-settings.json

# Copy out for editing
docker compose cp server:/data/settings/server-settings.json ./server-settings.json
# ... edit the file ...
docker compose cp ./server-settings.json server:/data/settings/server-settings.json
```

See [Server Settings](/admins/configuration/server-settings) for full reference.

### .env File

Docker and infrastructure settings like Steam credentials, VNC password, and port mappings. Create this file in the same directory as `docker-compose.yml`.

Example minimal `.env`:

```sh
STEAM_USERNAME=your_steam_username
STEAM_PASSWORD=your_steam_password
VNC_PASSWORD=your_secure_password
```

See [Environment Variables](/admins/configuration/environment) for full reference.

## Next Steps

- [Server Settings](/admins/configuration/server-settings) — Game and server behavior
- [Environment Variables](/admins/configuration/environment) — Docker infrastructure
- [Discord Integration](/admins/configuration/discord) — Bot setup and chat relay
