#!/bin/sh

# Runs as root (cont-init.d) before the app service drops to USER_ID:GROUP_ID. Fixes ownership of
# the volumes/paths the app writes, which startapp.sh can no longer do as the non-root app user.

set -e

USER_ID="${USER_ID:-1000}"
GROUP_ID="${GROUP_ID:-1000}"

# The game and SMAPI resolve their config dir via Environment.GetFolderPath(ApplicationData),
# which yields "" (a relative path into the service's cwd) unless XDG_CONFIG_HOME already
# exists. The saves volume normally mounts inside it, but not every deployment mounts one.
mkdir -p "${XDG_CONFIG_HOME:-/config/xdg/config}"

# Take ownership of everything the app writes: the game volume (also written by the steam-auth
# sidecar), the image-baked Mods, the settings/diagnostics bind mounts, and the saves volume
# nested under /config. Fresh Docker volumes and COPYed image dirs start root-owned, so this
# must run every boot. steam-auth chowns the same game volume to the same uid, so the two
# writers stay consistent. Individual failures are expected on some bind mounts (Docker Desktop
# host folders reject chown), so the sweep is tolerant; the check below is what gates startup.
echo "[server-init] Taking ownership of /data and /config for ${USER_ID}:${GROUP_ID}..."
chown -R "${USER_ID}:${GROUP_ID}" /data /config || true

# The app user must own the paths it writes unconditionally; anything else is a broken start.
for dir in /config /data/game /data/Mods; do
    if [ "$(stat -c %u "${dir}")" != "${USER_ID}" ]; then
        echo "[server-init] ERROR: ${dir} is not owned by USER_ID ${USER_ID} after chown" >&2
        exit 1
    fi
done

# Opt-in Galaxy SDK debug logging (docs: troubleshooting, "Capturing Galaxy SDK logs"). The SDK
# reads GalaxyPeer.ini from the game's working directory, which is this root-owned service dir,
# so the operator's copy in the game volume is mirrored here; removing it there disables logging.
if [ -f /data/game/GalaxyPeer.ini ]; then
    cp /data/game/GalaxyPeer.ini /etc/services.d/app/GalaxyPeer.ini
else
    rm -f /etc/services.d/app/GalaxyPeer.ini
fi
