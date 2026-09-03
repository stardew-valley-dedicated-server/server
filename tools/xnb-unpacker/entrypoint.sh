#!/bin/sh
set -e

# Starts as root only to take ownership of the mounted host dirs, then drops to USER_ID:GROUP_ID
# (default 1000) so the unpacked files land owned by the operator's host user instead of root.
# Pass USER_ID/GROUP_ID (e.g. -e USER_ID=$(id -u)) to match your host account; 0/0 stays root.

USER_ID="${USER_ID:-1000}"
GROUP_ID="${GROUP_ID:-1000}"

# /game holds the input and receives the unpacked output; /output is optional. Both are host
# bind mounts.
chown -R "${USER_ID}:${GROUP_ID}" /game 2>/dev/null || true
[ -d /output ] && chown -R "${USER_ID}:${GROUP_ID}" /output 2>/dev/null || true

# A non-root Xvfb can't create the X socket dir itself.
mkdir -p /tmp/.X11-unix && chmod 1777 /tmp/.X11-unix

# gosu resets HOME to the target uid's passwd home ("/" when the uid has no entry), so HOME is
# set after the drop: the game instance StardewXnbHack spins up resolves its config dir via
# Environment.GetFolderPath(ApplicationData), which yields "" (a relative path into /game) unless
# $HOME/.config already exists.
exec gosu "${USER_ID}:${GROUP_ID}" env HOME=/tmp /bin/sh -e -c '
    mkdir -p "$HOME/.config"
    cp -f /tmp/StardewXnbHack .
    Xvfb :99 -screen 0 1024x768x24 &
    export DISPLAY=:99
    ./StardewXnbHack
    rm -rf ./StardewXnbHack
'
