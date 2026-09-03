#!/bin/sh

# Runs as root (cont-init.d) before the app service drops to USER_ID:GROUP_ID.
# Handles the two things startapp.sh can no longer do once it runs as the non-root app user:
# fixing ownership of the volumes/paths the app writes, and syncing the system clock.

USER_ID="${USER_ID:-1000}"
GROUP_ID="${GROUP_ID:-1000}"

# Take ownership of everything the app writes: the game volume (also written by the steam-auth
# sidecar), the image-baked Mods, the settings/diagnostics bind mounts, and the saves volume
# nested under /config. Fresh Docker volumes and COPYed image dirs start root-owned, so this
# must run every boot. steam-auth chowns the same game volume to the same uid, so the two
# writers stay consistent.
# The game and SMAPI resolve their config dir via Environment.GetFolderPath(ApplicationData),
# which yields "" (a relative path into the service's cwd) unless XDG_CONFIG_HOME already
# exists. The saves volume normally mounts inside it, but not every deployment mounts one.
mkdir -p "${XDG_CONFIG_HOME:-/config/xdg/config}"

echo "[server-init] Taking ownership of /data and /config for ${USER_ID}:${GROUP_ID}..."
chown -R "${USER_ID}:${GROUP_ID}" /data /config 2>/dev/null || true

# Sync the system clock. Needs root + the SYS_TIME cap (docker-compose.yml); as the dropped app
# user it silently fails, and a skewed clock breaks GOG Galaxy P2P (~30s disconnects).
echo "[server-init] Synchronizing system time..."
if hwclock --hctosys 2>/dev/null; then
    echo "[server-init] Time synced from hardware clock"
elif ntpdate -u pool.ntp.org 2>/dev/null; then
    echo "[server-init] Time synced from NTP server"
else
    echo "[server-init] Warning: could not sync time automatically"
    echo "[server-init] Current time: $(date -u '+%Y-%m-%d %H:%M:%S UTC')"
    echo "[server-init] If Galaxy P2P disconnects occur after ~30 seconds, check system time sync"
fi
