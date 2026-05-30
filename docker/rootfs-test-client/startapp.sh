#!/bin/bash

set -euo pipefail

# Game client startup script for test containers
# Supports both LAN and Steam/Galaxy connections

MODS_DEST_DIR="/data/Mods"
GAME_DEST_DIR="/data/game"
GAME_EXECUTABLE="${GAME_DEST_DIR}/StardewValley"
SMAPI_EXECUTABLE="${GAME_DEST_DIR}/StardewModdingAPI"
STEAM_SDK_DIR="/root/.steam/sdk64"

print_error() {
    echo -e "\e[31m$1\e[0m"
}

init_xauthority() {
    # X authority setup for GUI access
    # The 'generate' command queries the X server's Security extension which
    # may not be available (depends on Xvnc config). It's not required:
    # the 'add' command below creates the auth entry directly.
    touch ~/.Xauthority
    xauth generate :0 . trusted 2>/dev/null || true
    xauth add :0 . $(mcookie)
    export XAUTHORITY=~/.Xauthority
}

init_display_settings() {
    # Disable X screensaver and DPMS power management
    # Prevents display blanking during long test runs
    xset s off 2>/dev/null || true
    xset -dpms 2>/dev/null || true
    xset s noblank 2>/dev/null || true
}

wait_for_game_files() {
    # Game files are mounted via shared volume from steam-auth
    if [ -e "${GAME_EXECUTABLE}" ]; then
        echo "Game files found at ${GAME_DEST_DIR}"
        return
    fi

    echo "Waiting for game files at ${GAME_DEST_DIR}..."
    while [ ! -e "${GAME_EXECUTABLE}" ]; do
        sleep 2
        echo "Still waiting for game files..."
    done
    echo "Game files detected!"
}

init_smapi() {
    if [ -e "${SMAPI_EXECUTABLE}" ]; then
        echo "SMAPI already installed"
    else
        echo "Installing SMAPI ${SMAPI_VERSION}..."

        curl -L "https://github.com/Pathoschild/SMAPI/releases/download/${SMAPI_VERSION}/SMAPI-${SMAPI_VERSION}-installer.zip" -o /tmp/smapi.zip
        unzip -q /tmp/smapi.zip -d /tmp/smapi/

        printf "2\n\n" | "/tmp/smapi/SMAPI ${SMAPI_VERSION} installer/internal/linux/SMAPI.Installer" \
            --install \
            --game-path "${GAME_DEST_DIR}"

        rm -rf /tmp/smapi /tmp/smapi.zip
        echo "SMAPI installed successfully!"
    fi

    # Apply SMAPI config overrides
    if [ -f /data/smapi-config.json ]; then
        echo "Applying SMAPI config overrides..."
        cp -f /data/smapi-config.json "${GAME_DEST_DIR}/smapi-internal/config.user.json"
    fi
}

init_mods() {
    # Copy default SMAPI mods (ErrorHandler, ConsoleCommands)
    mkdir -p "${MODS_DEST_DIR}/smapi"
    if [ -d "${GAME_DEST_DIR}/Mods" ]; then
        cp -r "${GAME_DEST_DIR}/Mods/"* "${MODS_DEST_DIR}/smapi/" 2>/dev/null || true
    fi

    # Ensure test-client mod is in place (already copied during docker build)
    if [ ! -d "${MODS_DEST_DIR}/JunimoTestClient" ]; then
        print_error "JunimoTestClient mod not found!"
        exit 1
    fi

    echo "Mods ready: $(ls -1 ${MODS_DEST_DIR} | tr '\n' ' ')"
}

init_steam_sdk() {
    # Set up Steam SDK for Galaxy connections
    # The SDK is downloaded by steam-service to .steam-sdk subfolder in the game volume
    local SDK_SOURCE="${GAME_DEST_DIR}/.steam-sdk/linux64/steamclient.so"

    if [ ! -e "${SDK_SOURCE}" ]; then
        echo "Steam SDK not found at ${SDK_SOURCE}, skipping SDK setup"
        echo "Steam/Galaxy connections will not work without the SDK"
        return
    fi

    mkdir -p "${STEAM_SDK_DIR}"
    if [ ! -e "${STEAM_SDK_DIR}/steamclient.so" ]; then
        echo "Linking Steam SDK to ${STEAM_SDK_DIR}..."
        ln -s "${SDK_SOURCE}" "${STEAM_SDK_DIR}/steamclient.so"
    else
        echo "Steam SDK already linked"
    fi

    # Create steam_appid.txt in the game directory (needed by Galaxy SDK)
    echo "413150" > "${GAME_DEST_DIR}/steam_appid.txt"

    echo "Steam SDK initialized for client connections"
}

init_permissions() {
    chmod +x "${GAME_EXECUTABLE}"
    chmod -R 755 "${GAME_DEST_DIR}"
}

echo "Initializing SMAPI..."

# Prepare
init_xauthority
init_display_settings
wait_for_game_files
init_steam_sdk
init_smapi
init_mods
init_permissions

# Run the game through SMAPI.
LOG_FILE="/tmp/client-output.log"

# Ensure log file exists
touch "${LOG_FILE}"

# Start SMAPI, piping output to log file + stdout
echo "Starting SMAPI..."
"${SMAPI_EXECUTABLE}" 2>&1 | tee "${LOG_FILE}" &
SMAPI_PID=$!

wait $SMAPI_PID
echo "SMAPI executable stopped"
