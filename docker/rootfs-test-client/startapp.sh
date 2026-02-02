#!/bin/bash

set -euo pipefail

# Game client startup script for test containers
# Simpler than server - no Steam SDK setup, no server mods

MODS_DEST_DIR="/data/Mods"
GAME_DEST_DIR="/data/game"
GAME_EXECUTABLE="${GAME_DEST_DIR}/StardewValley"
SMAPI_EXECUTABLE="${GAME_DEST_DIR}/StardewModdingAPI"

print_error() {
    echo -e "\e[31m$1\e[0m"
}

init_xauthority() {
    # X authority setup for GUI access
    touch ~/.Xauthority
    xauth generate :0 . trusted
    xauth add :0 . `mcookie`
    export XAUTHORITY=~/.Xauthority
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

init_permissions() {
    chmod +x "${GAME_EXECUTABLE}"
    chmod -R 755 "${GAME_DEST_DIR}"
}

# Initialize
echo "=== Stardew Test Client Starting ==="
echo "API Port: ${JUNIMO_TEST_PORT:-5123}"

init_xauthority
wait_for_game_files
init_smapi
init_mods
init_permissions

# Run the game through SMAPI
LOG_FILE="/tmp/client-output.log"
touch "${LOG_FILE}"

echo "Starting SMAPI..."
exec "${SMAPI_EXECUTABLE}" 2>&1 | tee "${LOG_FILE}"
