#!/bin/bash

set -euo pipefail

MODS_DEST_DIR="/data/Mods"
GAME_DEST_DIR="/data/game"
GAME_EXECUTABLE="${GAME_DEST_DIR}/StardewValley"
SMAPI_EXECUTABLE="${GAME_DEST_DIR}/StardewModdingAPI"
STEAM_DEST_DIR="/root/.steam/sdk64/"

# ============================================================================
# Helpers
# ============================================================================

print_error() {
    echo -e "\e[31m$1\e[0m"
}

print_warning() {
    echo -e "\e[33m$1\e[0m"
}

# ============================================================================
# Environment validation
# ============================================================================

validate_environment() {
    local has_warnings=false

    if [ "${API_ENABLED:-true}" = "true" ] && [ -z "${API_KEY:-}" ]; then
        echo ""
        print_warning "╔═══════════════════════════════════════════════════════════════════════╗"
        print_warning "║  WARNING: API_KEY is not set!                                         ║"
        print_warning "║                                                                       ║"
        print_warning "║  The REST API is enabled but has no authentication.                   ║"
        print_warning "║  Anyone with network access to port 8080 can control your server.     ║"
        print_warning "║                                                                       ║"
        print_warning "║  Set API_KEY in .env or disable the API with API_ENABLED=false.       ║"
        print_warning "╚═══════════════════════════════════════════════════════════════════════╝"
        echo ""
        has_warnings=true
    fi

    if [ "$has_warnings" = true ] && [ "${ALLOW_INSECURE_SETUP:-}" != "true" ]; then
        print_error "╔═══════════════════════════════════════════════════════════════════════╗"
        print_error "║  Refusing to start with insecure configuration.                       ║"
        print_error "║                                                                       ║"
        print_error "║  Fix the warnings above, or set ALLOW_INSECURE_SETUP=true             ║"
        print_error "║  in your .env file to start anyway.                                   ║"
        print_error "╚═══════════════════════════════════════════════════════════════════════╝"
        echo ""
        exit 1
    fi
}

# ============================================================================
# Time sync
# ============================================================================

init_time_sync() {
    echo "Synchronizing system time..."

    if hwclock --hctosys 2>/dev/null; then
        echo "Time synced from hardware clock"
        return 0
    fi

    if ntpdate -u pool.ntp.org 2>/dev/null; then
        echo "Time synced from NTP server"
        return 0
    fi

    echo "Warning: Could not sync time automatically"
    echo "Current time: $(date -u '+%Y-%m-%d %H:%M:%S UTC')"
    echo "If Galaxy P2P disconnects occur after ~30 seconds, check system time sync"
}

# ============================================================================
# Game files
# ============================================================================

init_stardew() {
    if [ -e "${GAME_EXECUTABLE}" ]; then
        echo "Game already initialized, skipping."
        return
    fi

    local STEAM_AUTH_GAME_DIR="/data/game"
    local STEAM_AUTH_GAME_EXEC="${STEAM_AUTH_GAME_DIR}/StardewValley"

    echo "Using steam-auth service for game files..."

    if [ ! -e "${STEAM_AUTH_GAME_EXEC}" ]; then
        echo ""
        print_warning "╔═══════════════════════════════════════════════════════════════════════╗"
        print_warning "║  Game files not found! Please run setup first:                        ║"
        print_warning "║                                                                       ║"
        print_warning "║  make setup                                                           ║"
        print_warning "╚═══════════════════════════════════════════════════════════════════════╝"
        echo ""
        echo "Waiting for game files to appear..."

        while [ ! -e "${STEAM_AUTH_GAME_EXEC}" ]; do
            sleep 5
            echo "Still waiting for game files at ${STEAM_AUTH_GAME_DIR}..."
        done

        echo "Game files detected!"
    fi

    if [ ! -e "${GAME_DEST_DIR}" ]; then
        echo "Linking game files from ${STEAM_AUTH_GAME_DIR} to ${GAME_DEST_DIR}..."
        ln -s "${STEAM_AUTH_GAME_DIR}" "${GAME_DEST_DIR}"
    fi

    echo "Game files ready (via steam-auth service)"
}

# ============================================================================
# Steam SDK
# ============================================================================

init_steam_sdk() {
    local SDK_SOURCE="${GAME_DEST_DIR}/.steam-sdk/linux64/steamclient.so"

    if [ ! -e "${SDK_SOURCE}" ]; then
        echo "Steam SDK not found at ${SDK_SOURCE}, skipping SDK setup"
        echo "Steam GameServer (SDR) mode may not work without the SDK"
        return
    fi

    mkdir -p "${STEAM_DEST_DIR}"

    # On musl-based systems (Alpine), the real steamclient.so segfaults
    # because it has deep glibc internals. Use a stub that makes
    # GameServer.Init() fail gracefully (returns false, no crash).
    if [ -f /lib/ld-musl-x86_64.so.1 ]; then
        echo "Detected musl libc — using steamclient.so stub (glibc-only binary)"
        echo "Steam Datagram Relay (SDR) is unavailable on Alpine/musl"
        echo "Players can connect via Galaxy P2P or direct IP"
        cp /opt/lib/steamclient_stub.so "${STEAM_DEST_DIR}/steamclient.so"
    elif [ ! -e "${STEAM_DEST_DIR}/steamclient.so" ]; then
        echo "Linking Steam SDK to ${STEAM_DEST_DIR}..."
        ln -s "${SDK_SOURCE}" "${STEAM_DEST_DIR}/steamclient.so"
    else
        echo "Steam SDK already linked"
    fi

    # Ensure dlopen("steamclient.so") finds it
    export LD_LIBRARY_PATH="${STEAM_DEST_DIR}${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"

    echo "413150" > "${GAME_DEST_DIR}/steam_appid.txt"
}

# ============================================================================
# SMAPI
# ============================================================================

init_smapi() {
    if [ -e "${SMAPI_EXECUTABLE}" ]; then
        echo "SMAPI already initialized, skipping."
    else
        echo "Installing SMAPI ${SMAPI_VERSION}..."

        curl -L "https://github.com/Pathoschild/SMAPI/releases/download/${SMAPI_VERSION}/SMAPI-${SMAPI_VERSION}-installer.zip" -o /data/smapi.zip
        unzip -q /data/smapi.zip -d /data/smapi/

        printf "2\n\n" | "/data/smapi/SMAPI ${SMAPI_VERSION} installer/internal/linux/SMAPI.Installer" \
            --install \
            --game-path "${GAME_DEST_DIR}"

        rm -rf "/data/smapi" /data/smapi.zip

        echo "SMAPI installed successfully!"
    fi

    # Always override config so updates take effect
    echo "Applying SMAPI runtime overrides..."
    cp -rf /data/smapi-config.json "${GAME_DEST_DIR}/smapi-internal/config.user.json"
}

# ============================================================================
# Mods
# ============================================================================

init_mods() {
    rm -rf "${MODS_DEST_DIR}/smapi/"
    mkdir -p "${MODS_DEST_DIR}/smapi/"
    cp -r "${GAME_DEST_DIR}/Mods/"* "${MODS_DEST_DIR}/smapi/"
}

# ============================================================================
# Permissions
# ============================================================================

init_permissions() {
    chmod +x "${GAME_EXECUTABLE}"
    chmod -R 755 "${GAME_DEST_DIR}"
    chown -R 1000:1000 "${GAME_DEST_DIR}"
}

# ============================================================================
# Main
# ============================================================================

validate_environment
init_time_sync
init_stardew
init_steam_sdk
init_smapi
init_mods
init_permissions

# Run the game through SMAPI with FIFO for command input
LOG_FILE="/tmp/server-output.log"
INPUT_FIFO="/tmp/smapi-input"

touch "${LOG_FILE}"

rm -f "${INPUT_FIFO}"
mkfifo "${INPUT_FIFO}"

# LD_PRELOAD the pthread shim for any glibc-linked .so files that reference
# __pthread_key_create (glibc internal symbol not provided by musl/gcompat)
export LD_PRELOAD="/opt/lib/pthread_shim.so${LD_PRELOAD:+:$LD_PRELOAD}"

# Start SMAPI with stdin from FIFO, output to log file and stdout
# Using 'script' to create a PTY so SMAPI outputs colors (thinks it's a terminal)
script -q -f --return -c "tail -f \"${INPUT_FIFO}\" | \"${SMAPI_EXECUTABLE}\"" "${LOG_FILE}" &
SMAPI_PID=$!

wait $SMAPI_PID
echo "Server session ended"
