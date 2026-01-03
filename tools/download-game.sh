#!/bin/bash
set -euo pipefail

# Read secrets and strip trailing newlines + quotes
USER=$(< /run/secrets/steam_user tr -d '\n')
PASS=$(< /run/secrets/steam_pass tr -d '\n')

# Run SteamCMD, mask username in logs
steamcmd +@sSteamCmdForcePlatformType linux \
         +force_install_dir /game \
         +login "$USER" "$PASS" \
         +app_update 413150 \
         +quit 2>&1 | sed -E "s/(Logging in user) '[^']*'/\1 '*****'/"
