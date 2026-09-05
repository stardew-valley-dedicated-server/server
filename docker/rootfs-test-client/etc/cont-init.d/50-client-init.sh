#!/bin/sh

# Runs as root (cont-init.d) before the app service drops to USER_ID:GROUP_ID. Takes ownership of
# the paths the non-root client writes: the game volume (shared with the server + steam-auth, so
# all containers agree on the same uid), the bundled Mods, and the app HOME under /config. Fresh
# Docker volumes and COPYed image dirs start root-owned, so this runs every boot.

set -e

USER_ID="${USER_ID:-1000}"
GROUP_ID="${GROUP_ID:-1000}"

# The game and SMAPI resolve their config dir via Environment.GetFolderPath(ApplicationData),
# which yields "" (a relative path into the service's cwd) unless XDG_CONFIG_HOME already
# exists — as root that silently landed in /etc/services.d/app; as the app user it's EACCES.
mkdir -p "${XDG_CONFIG_HOME:-/config/xdg/config}"

# Individual failures are expected on some bind mounts (Docker Desktop host folders reject
# chown), so the sweep is tolerant; the check below is what gates startup.
echo "[client-init] Taking ownership of /data and /config for ${USER_ID}:${GROUP_ID}..."
chown -R "${USER_ID}:${GROUP_ID}" /data /config || true

# The app user must own the paths it writes unconditionally; anything else is a broken start.
for dir in /config /data/game /data/Mods; do
    if [ "$(stat -c %u "${dir}")" != "${USER_ID}" ]; then
        echo "[client-init] ERROR: ${dir} is not owned by USER_ID ${USER_ID} after chown" >&2
        exit 1
    fi
done
