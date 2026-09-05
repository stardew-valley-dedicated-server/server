#!/bin/bash

# Runs as root (s6 oneshot 'init-runtime') before any app service. Does the root-only work the
# app services can't, since they drop to USER_ID:GROUP_ID: prepare runtime dirs and take ownership
# of the volumes/paths the app user writes.

set -u

USER_ID="${USER_ID:-1000}"
GROUP_ID="${GROUP_ID:-1000}"
APP_HOME="${HOME:-/home/app}"

# The app uid/gid need real passwd/group entries: Openbox (via glib) segfaults on a uid with no
# passwd record, and other tools misbehave. Only add them when the ids aren't already known.
if [ "${USER_ID}" != "0" ]; then
    getent group "${GROUP_ID}" >/dev/null || addgroup -g "${GROUP_ID}" app
    getent passwd "${USER_ID}" >/dev/null \
        || adduser -D -H -u "${USER_ID}" -G "$(getent group "${GROUP_ID}" | cut -d: -f1)" -h "${APP_HOME}" app
fi

# Per-user runtime dir for pipewire (XDG_RUNTIME_DIR=/tmp/runtime), owned by the app user.
mkdir -p /tmp/runtime
chown "${USER_ID}:${GROUP_ID}" /tmp/runtime
chmod 0700 /tmp/runtime

# Take ownership of everything the app writes: the game volume (also written by the steam-auth
# sidecar), the saves volume, the settings bind mount, the bundled Mods, and the app HOME.
# Fresh Docker volumes and COPYed image dirs start root-owned, so this runs every boot.
# The game and SMAPI resolve their config dir via Environment.GetFolderPath(ApplicationData),
# which yields "" (a relative path into the service's cwd) unless $HOME/.config already exists.
mkdir -p "${APP_HOME}/.config"

# Individual failures are expected on some bind mounts (Docker Desktop host folders reject chown),
# so the sweep is tolerant; the check below is what gates startup.
echo "[init-runtime] Taking ownership of /data and ${APP_HOME} for ${USER_ID}:${GROUP_ID}..."
chown -R "${USER_ID}:${GROUP_ID}" /data "${APP_HOME}" 2>/dev/null || true

# The app user must own the paths it writes unconditionally; anything else is a broken start.
for dir in "${APP_HOME}" /data/game /data/Mods; do
    if [ "$(stat -c %u "${dir}")" != "${USER_ID}" ]; then
        echo "[init-runtime] ERROR: ${dir} is not owned by USER_ID ${USER_ID} after chown" >&2
        exit 1
    fi
done

exit 0
