#!/bin/bash

set -euo pipefail

MODS_DEST_DIR="/data/Mods"
GAME_DEST_DIR="/data/game"
GAME_EXECUTABLE="${GAME_DEST_DIR}/StardewValley"
SMAPI_EXECUTABLE="${GAME_DEST_DIR}/StardewModdingAPI"
STEAM_DEST_DIR="/root/.steam/sdk64/"

print_text_for_duration() {
    local text=$1
    local duration=$2
    local interval=$3

    local end_time=$((SECONDS + duration))

    while [ $SECONDS -lt $end_time ]; do
        local remaining_time=$((end_time - SECONDS))
        echo "(${remaining_time}s) $text"
        sleep $interval
    done
}

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
    touch ~/.Xauthority
    xauth generate :0 . trusted
    xauth add :0 . `mcookie`

    # Expected by e.g. tint2
    export XAUTHORITY=~/.Xauthority
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
    mkdir -p "${STEAM_DEST_DIR}"
    if [ ! -e "${STEAM_DEST_DIR}/steamclient.so" ]; then
        echo "Linking Steam SDK to ${STEAM_DEST_DIR}..."
        ln -s "${SDK_SOURCE}" "${STEAM_DEST_DIR}/steamclient.so"
    else
        echo "Steam SDK already linked"
    fi

    # Create steam_appid.txt with Stardew Valley's AppID
    # The SDK defaults to 480 (Spacewar) which causes SDR connection failures
    echo "413150" > "${GAME_DEST_DIR}/steam_appid.txt"
}

init_gui() {
    # Always start polybar for the rendering toggle button
    /etc/services.d/polybar/run &

    if [ "$DISABLE_RENDERING" != "true" ]; then
        if [ -e "/data/images/wallpaper-junimo-server.png" ]; then
            xwallpaper --zoom /data/images/wallpaper-junimo-server.png
        fi

        bash /root/.config/polybar/shades/scripts/colors-dark.sh --light-green
    fi
}

# Prepare
init_time_sync
init_gui
init_xauthority
init_stardew
init_steam_sdk
init_smapi
init_patch_dll
init_mods
init_permissions

# Run the game through SMAPI with FIFO for command input
SESSION_NAME="stardew-server"
LOG_FILE="/tmp/server-output.log"
INPUT_FIFO="/tmp/smapi-input"

# Touch the log file to ensure it exists
touch "${LOG_FILE}"

# Create FIFO for command input
rm -f "${INPUT_FIFO}"
mkfifo "${INPUT_FIFO}"

# Start SMAPI with stdin from FIFO, output to log file and stdout
# Using 'script' to create a PTY so SMAPI outputs colors (thinks it's a terminal)
# Using tail -f on the FIFO to keep it open and avoid blocking
# Note: 'script' writes to both stdout (for docker logs) and the typescript file simultaneously
script -q -f --return -c "tail -f \"${INPUT_FIFO}\" | \"${SMAPI_EXECUTABLE}\"" "${LOG_FILE}" &
SMAPI_PID=$!

# Wait for SMAPI process to exit (when it exits, the server has stopped)
wait $SMAPI_PID
echo "Server session ended"
