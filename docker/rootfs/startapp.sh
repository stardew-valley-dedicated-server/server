#!/bin/bash

set -euo pipefail

MODS_DEST_DIR="/data/Mods"
GAME_DEST_DIR="/data/Stardew"
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

    # Read Steam credentials from Docker secrets (preferred) or environment variables (fallback)
    local STEAM_USER_VALUE="${STEAM_USER:-}"
    local STEAM_PASS_VALUE="${STEAM_PASS:-}"

    if [ -f "/run/secrets/steam_user" ]; then
        STEAM_USER_VALUE=$(< /run/secrets/steam_user tr -d '\n')
        echo "Using Steam username from Docker secret"
    fi

    if [ -f "/run/secrets/steam_pass" ]; then
        STEAM_PASS_VALUE=$(< /run/secrets/steam_pass tr -d '\n')
        echo "Using Steam password from Docker secret"
    fi

    # Validate Steam credentials
    if [ -z "${STEAM_USER_VALUE}" ] || [ -z "${STEAM_PASS_VALUE}" ]; then
        print_error "Error: Steam credentials are required for first-time setup"
        print_error "Please provide credentials via Docker secrets (secrets/steam_user.txt and secrets/steam_pass.txt)"
        print_error "or environment variables (STEAM_USER and STEAM_PASS)"
        exit 1
    fi

    # Download & Install
    echo "Installing Stardew Valley..."
    echo "This is a one-time download and may take several minutes..."

    steamcmd +@sSteamCmdForcePlatformType linux \
        +force_install_dir ${GAME_DEST_DIR} \
        +login "${STEAM_USER_VALUE}" "${STEAM_PASS_VALUE}" "${STEAM_GUARD_CODE}" \
        +app_update 413150 \
        +quit

    # Capture the exit status of the steamcmd command
    EXIT_STATUS=$?

    # Check if the command was successful
    if [ $EXIT_STATUS -ne 0 ]; then
        print_error "Error: steamcmd command failed with exit status $EXIT_STATUS"
        print_error "Please check your Steam credentials and try again"
        exit 1
    fi

    # Removing these files causes a "FileNotFoundException", but it doesn't
    # seem to cause problems but reduces game storage size by about 70-80%.
    echo "Removing unnecessary files..."
    rm -fv "${GAME_DEST_DIR}/Content/XACT/Wave Bank.xwb" "${GAME_DEST_DIR}/Content/XACT/Wave Bank(1.4).xwb"

    echo "Stardew Valley downloaded successfully!"
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

init_gui() {
    if [ "$DISABLE_RENDERING" = "true" ]; then
        return;
    fi

    /etc/services.d/polybar/run &

    if [ -e "/data/images/wallpaper.png" ]; then
        xwallpaper --zoom /data/images/wallpaper.png
    fi

    bash /root/.config/polybar/shades/scripts/colors-dark.sh --light-green
}

# Prepare
init_gui
init_xauthority
init_stardew
init_smapi
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

# Start a background tail to stdout (so 'docker compose logs' works)
tail -f "${LOG_FILE}" &
TAIL_PID=$!

# Start SMAPI with stdin from FIFO, output to log file
# Using 'script' to create a PTY so SMAPI outputs colors (thinks it's a terminal)
# Using tail -f on the FIFO to keep it open and avoid blocking
script -q -f --return -c "tail -f \"${INPUT_FIFO}\" | \"${SMAPI_EXECUTABLE}\"" "${LOG_FILE}" &
SMAPI_PID=$!

# Wait for SMAPI process to exit (when it exits, the server has stopped)
wait $SMAPI_PID

# Cleanup
kill $TAIL_PID 2>/dev/null || true
echo "Server session ended"
