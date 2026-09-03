#!/bin/sh

# Runs as root (cont-init.d) before the app service drops to USER_ID:GROUP_ID. Takes ownership of
# the paths the non-root client writes: the game volume (shared with the server + steam-auth, so
# all containers agree on the same uid), the bundled Mods, and the app HOME under /config. Fresh
# Docker volumes and COPYed image dirs start root-owned, so this runs every boot.

USER_ID="${USER_ID:-1000}"
GROUP_ID="${GROUP_ID:-1000}"

# The game and SMAPI resolve their config dir via Environment.GetFolderPath(ApplicationData),
# which yields "" (a relative path into the service's cwd) unless XDG_CONFIG_HOME already
# exists — as root that silently landed in /etc/services.d/app; as the app user it's EACCES.
mkdir -p "${XDG_CONFIG_HOME:-/config/xdg/config}"

echo "[client-init] Taking ownership of /data and /config for ${USER_ID}:${GROUP_ID}..."
chown -R "${USER_ID}:${GROUP_ID}" /data /config 2>/dev/null || true
