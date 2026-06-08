#!/bin/bash

set -euo pipefail

# Game server startup script for the dedicated host
# Hosts the always-on server via SMAPI

MODS_DEST_DIR="/data/Mods"
GAME_DEST_DIR="/data/game"
GAME_EXECUTABLE="${GAME_DEST_DIR}/StardewValley"
SMAPI_EXECUTABLE="${GAME_DEST_DIR}/StardewModdingAPI"
STEAM_SDK_DIR="/root/.steam/sdk64"

# Validate required environment variables
validate_environment() {
    local has_warnings=false

    # Security warnings
    if [ -z "${VNC_PASSWORD:-}" ]; then
        echo ""
        echo -e "\e[33m╔═══════════════════════════════════════════════════════════════════════╗\e[0m"
        echo -e "\e[33m║  WARNING: VNC_PASSWORD is not set!                                     ║\e[0m"
        echo -e "\e[33m║                                                                       ║\e[0m"
        echo -e "\e[33m║  The VNC web interface will be accessible without a password.          ║\e[0m"
        echo -e "\e[33m║  Set VNC_PASSWORD in your .env file to secure it.                     ║\e[0m"
        echo -e "\e[33m╚═══════════════════════════════════════════════════════════════════════╝\e[0m"
        echo ""
        has_warnings=true
    fi

    if [ "${API_ENABLED:-true}" = "true" ] && [ -z "${API_KEY:-}" ]; then
        echo ""
        echo -e "\e[33m╔═══════════════════════════════════════════════════════════════════════╗\e[0m"
        echo -e "\e[33m║  WARNING: API_KEY is not set!                                         ║\e[0m"
        echo -e "\e[33m║                                                                       ║\e[0m"
        echo -e "\e[33m║  The REST API is enabled but has no authentication.                   ║\e[0m"
        echo -e "\e[33m║  Anyone with network access to port 8080 can control your server.     ║\e[0m"
        echo -e "\e[33m║                                                                       ║\e[0m"
        echo -e "\e[33m║  Set API_KEY in .env or disable the API with API_ENABLED=false.       ║\e[0m"
        echo -e "\e[33m╚═══════════════════════════════════════════════════════════════════════╝\e[0m"
        echo ""
        has_warnings=true
    fi

    if [ "$has_warnings" = true ] && [ "${ALLOW_INSECURE_SETUP:-}" != "true" ]; then
        echo -e "\e[31m╔═══════════════════════════════════════════════════════════════════════╗\e[0m"
        echo -e "\e[31m║  Refusing to start with insecure configuration.                       ║\e[0m"
        echo -e "\e[31m║                                                                       ║\e[0m"
        echo -e "\e[31m║  Fix the warnings above, or set ALLOW_INSECURE_SETUP=true             ║\e[0m"
        echo -e "\e[31m║  in your .env file to start anyway.                                   ║\e[0m"
        echo -e "\e[31m╚═══════════════════════════════════════════════════════════════════════╝\e[0m"
        echo ""
        exit 1
    fi
}

# Run validation before anything else
validate_environment

print_error() {
    echo -e "\e[31m$1\e[0m"
}

init_time_sync() {
    echo "Synchronizing system time..."

    # Try hardware clock sync first (works with host in most cases)
    if hwclock --hctosys 2>/dev/null; then
        echo "Time synced from hardware clock"
        return 0
    fi

    # Fallback to NTP sync (requires internet)
    if ntpdate -u pool.ntp.org 2>/dev/null; then
        echo "Time synced from NTP server"
        return 0
    fi

    # If both fail, warn but continue (time might already be correct)
    echo "Warning: Could not sync time automatically"
    echo "Current time: $(date -u '+%Y-%m-%d %H:%M:%S UTC')"
    echo "If Galaxy P2P disconnects occur after ~30 seconds, check system time sync"
}

init_xauthority() {
    # Can not be done in Dockerfile, because xauth needs to access the running display ":0"
    # The 'generate' command queries the X server's Security extension which
    # may not be available (depends on Xvnc config). It's not required:
    # the 'add' command below creates the auth entry directly.
    touch ~/.Xauthority
    xauth generate :0 . trusted 2>/dev/null || true
    xauth add :0 . $(mcookie)

    # Expected by e.g. tint2
    export XAUTHORITY=~/.Xauthority
}

init_display_settings() {
    # Disable X screensaver and DPMS power management
    # Prevents display blanking during long-running sessions
    xset s off 2>/dev/null || true
    xset -dpms 2>/dev/null || true
    xset s noblank 2>/dev/null || true
}

init_stardew() {
    # Installation check
    if [ -e "${GAME_EXECUTABLE}" ]; then
        echo "Game already initialized, skipping."
        return
    fi

    local STEAM_AUTH_GAME_DIR="/data/game"
    local STEAM_AUTH_GAME_EXEC="${STEAM_AUTH_GAME_DIR}/StardewValley"

    echo "Using steam-auth service for game files..."

    # Check if game files exist in the shared volume
    if [ ! -e "${STEAM_AUTH_GAME_EXEC}" ]; then
        echo ""
        echo -e "\e[33m╔═══════════════════════════════════════════════════════════════════════╗\e[0m"
        echo -e "\e[33m║  Game files not found! Please run setup first:                        ║\e[0m"
        echo -e "\e[33m║                                                                       ║\e[0m"
        echo -e "\e[33m║  make setup                                                           ║\e[0m"
        echo -e "\e[33m╚═══════════════════════════════════════════════════════════════════════╝\e[0m"
        echo ""
        echo "Waiting for game files to appear..."

        # Poll until game files appear
        while [ ! -e "${STEAM_AUTH_GAME_EXEC}" ]; do
            sleep 5
            echo "Still waiting for game files at ${STEAM_AUTH_GAME_DIR}..."
        done

        echo "Game files detected!"
    fi

    # Symlink the game directory to expected location
    if [ ! -e "${GAME_DEST_DIR}" ]; then
        echo "Linking game files from ${STEAM_AUTH_GAME_DIR} to ${GAME_DEST_DIR}..."
        ln -s "${STEAM_AUTH_GAME_DIR}" "${GAME_DEST_DIR}"
    fi

    echo "Game files ready (via steam-auth service)"
}

init_patch_dll() {
    # Patch the game DLL to disable sound initialization (runs before SMAPI loads)
    # The patcher itself checks if patching is needed by examining the IL code
    echo "Running DLL patcher..."
    /opt/dll-patcher/SDVPatcher "${GAME_DEST_DIR}/Stardew Valley.dll"

    if [ $? -ne 0 ]; then
        echo "Warning: DLL patching failed, continuing anyway..."
    fi
}

init_smapi() {
    # Installation check
    if [ -e "${SMAPI_EXECUTABLE}" ]; then
        echo "SMAPI already initialized, skipping."
    else
        echo "Installing SMAPI ${SMAPI_VERSION}..."

        # Download
        curl -L https://github.com/Pathoschild/SMAPI/releases/download/${SMAPI_VERSION}/SMAPI-${SMAPI_VERSION}-installer.zip -o /data/smapi.zip
        unzip -q /data/smapi.zip -d /data/smapi/

        # Install
        printf "2\n\n" | "/data/smapi/SMAPI ${SMAPI_VERSION} installer/internal/linux/SMAPI.Installer" \
            --install \
            --game-path "${GAME_DEST_DIR}"

        # Cleanup
        rm -rf "/data/smapi" /data/smapi.zip

        echo "SMAPI installed successfully!"
    fi

    # Always override the config file so we can update the one that is stored inside a volume
    echo "Applying SMAPI runtime overrides..."
    cp -rf /data/smapi-config.json ${GAME_DEST_DIR}/smapi-internal/config.user.json
}

init_mods() {
    rm -rf ${MODS_DEST_DIR}/smapi/
    mkdir -p ${MODS_DEST_DIR}/smapi/
    cp -r ${GAME_DEST_DIR}/Mods/* ${MODS_DEST_DIR}/smapi/

    # E2E test fixture: opt-in extra mod that adds a second Data/AdditionalFarms entry,
    # used by the by-Id modded-farm disambiguation test. Staged at /opt/test-fixtures by
    # the image build; copied in here (a sibling of smapi/, so the rm -rf above leaves it
    # untouched) only when the test broker sets the flag.
    if [ "${SDVD_TEST_FIXTURE_FARM_MOD:-false}" = "true" ] && [ -d /opt/test-fixtures/TestFarmMod ]; then
        echo "Installing E2E test fixture mod: TestFarmMod"
        rm -rf "${MODS_DEST_DIR}/TestFarmMod"
        cp -r /opt/test-fixtures/TestFarmMod "${MODS_DEST_DIR}/TestFarmMod"
    fi
}

init_permissions() {
    chmod +x "${GAME_EXECUTABLE}"
    chmod -R 755 "${GAME_DEST_DIR}"
    chown -R 1000:1000 "${GAME_DEST_DIR}"
}

init_steam_sdk() {
    # Set up Steam SDK for GameServer mode (SDR networking)
    # The SDK is downloaded by steam-service to .steam-sdk subfolder in the game volume
    local SDK_SOURCE="${GAME_DEST_DIR}/.steam-sdk/linux64/steamclient.so"

    if [ ! -e "${SDK_SOURCE}" ]; then
        echo "Steam SDK not found at ${SDK_SOURCE}, skipping SDK setup"
        echo "Steam GameServer (SDR) mode may not work without the SDK"
        return
    fi

    # Create the target directory and symlink
    mkdir -p "${STEAM_SDK_DIR}"
    if [ ! -e "${STEAM_SDK_DIR}/steamclient.so" ]; then
        echo "Linking Steam SDK to ${STEAM_SDK_DIR}..."
        ln -s "${SDK_SOURCE}" "${STEAM_SDK_DIR}/steamclient.so"
    else
        echo "Steam SDK already linked"
    fi

    # Create steam_appid.txt with Stardew Valley's AppID
    # The SDK defaults to 480 (Spacewar) which causes SDR connection failures
    echo "413150" > "${GAME_DEST_DIR}/steam_appid.txt"
}

init_gui() {
    # Polybar is managed by the supervisor service (disabled returns "false").
    # Do NOT start it here. Doing so races with the supervisor's own polybar/run
    # (which calls `pkill polybar` first), causing the `polybar-msg` call below
    # to fail with "No active ipc channels" (exit 2) and crash the script.

    if [ "${SERVER_FPS:-0}" != "0" ]; then
        if [ -e "/data/images/wallpaper-junimo-server.png" ]; then
            xwallpaper --zoom /data/images/wallpaper-junimo-server.png
        fi

        # colors-dark.sh calls `polybar-msg cmd restart` which requires polybar
        # to be running. The supervisor may not have started it yet, so tolerate failure.
        bash /root/.config/polybar/shades/scripts/colors-dark.sh --light-green || true
    fi
}

echo "Initializing SMAPI..."

# Prepare
init_time_sync
init_gui
init_xauthority
init_display_settings
init_stardew
init_steam_sdk
init_smapi
# init_patch_dll # This seems to strip debug symbols from SDV, so currently disabled to avoid issues in Space Core mod
init_mods
init_permissions

# Run the game through SMAPI (with FIFO to pipe commands via CLI).
LOG_FILE="/tmp/server-output.log"
INPUT_FIFO="/tmp/smapi-input"

# Ensure log file exists
touch "${LOG_FILE}"

# Ensure FIFO pipe exists
rm -f "${INPUT_FIFO}"
mkfifo "${INPUT_FIFO}"

# Start SMAPI, piping stdin from FIFO and output to log file + stdout
# Using `script` to create a PTY so SMAPI prints colored output (make it think it's a terminal)
# Using `tail -f` on the FIFO to keep it open and avoid blocking
# Note: `script` writes to both stdout (for docker logs) and the typescript file simultaneously
echo "Starting SMAPI..."
script -q -f --return -c "tail -f \"${INPUT_FIFO}\" | \"${SMAPI_EXECUTABLE}\"" "${LOG_FILE}" &
SMAPI_PID=$!

wait $SMAPI_PID
echo "SMAPI executable stopped"
