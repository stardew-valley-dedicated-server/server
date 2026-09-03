#!/bin/bash

set -euo pipefail

# Game server startup script for the dedicated host
# Hosts the always-on server via SMAPI

MODS_DEST_DIR="/data/Mods"
GAME_DEST_DIR="/data/game"
GAME_EXECUTABLE="${GAME_DEST_DIR}/StardewValley"
SMAPI_EXECUTABLE="${GAME_DEST_DIR}/StardewModdingAPI"
STEAM_SDK_DIR="${HOME}/.steam/sdk64"
# Completion marker written by steam-auth only after the full depot download succeeds. Gate on it
# rather than the StardewValley executable: the downloader pre-allocates every file at full size
# up front, so the executable exists (zero-filled) mid-download — launching on it would run a
# half-downloaded game. StardewValleyAppId is 413150 (see tools/steam-service/Program.cs).
GAME_DOWNLOAD_MARKER="${GAME_DEST_DIR}/.download-manifest-413150"
API_PORT="${API_PORT:-8080}"
# Lifecycle phase served on the API port until the mod takes over: "downloading" | "starting".
PHASE_FILE="/tmp/startup-phase"

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

# Mirrors the mod's API on the two points a client can observe: /status needs the API key when
# one is set, and everything else gets 503 so /health keeps meaning "the mod's API is up".
phase_response() {
    local request_line line path name value scheme token="" deadline body status
    # Lines over 8 KiB are dropped (connection closed), not parsed as several records.
    IFS= read -r -n 8193 request_line || return 0
    [ "${#request_line}" -le 8192 ] || return 0
    path="${request_line#* }"
    path="${path%% *}"
    path="${path%%\?*}"
    # The client is connected from here on and holds the only listener, so the rest of the
    # request gets a 5s deadline instead of patience.
    deadline=$((SECONDS + 5))
    while [ $((deadline - SECONDS)) -gt 0 ] && IFS= read -r -t $((deadline - SECONDS)) -n 8193 line; do
        [ "${#line}" -le 8192 ] || return 0
        line="${line%$'\r'}"
        [ -n "${line}" ] || break
        name="${line%%:*}"
        if [ "${name,,}" = "authorization" ]; then
            value="${line#*:}"
            value="${value#"${value%%[! ]*}"}"
            scheme="${value:0:7}"
            if [ "${scheme,,}" = "bearer " ]; then
                token="${value:7}"
            fi
        fi
    done

    body="{\"isOnline\":false,\"phase\":\"$(cat "${PHASE_FILE}" 2>/dev/null || echo starting)\"}"
    if [ -n "${API_KEY:-}" ] && [ "${token}" != "${API_KEY}" ]; then
        status="401 Unauthorized"
        body='{"error":"Unauthorized. Provide a valid Authorization header: Bearer <api-key>"}'
    elif [ "${path}" = "/status" ]; then
        status="200 OK"
    else
        status="503 Service Unavailable"
    fi
    printf 'HTTP/1.1 %s\r\nContent-Type: application/json\r\nContent-Length: %d\r\nConnection: close\r\n\r\n%s' \
        "${status}" "${#body}" "${body}"
}

# Keeps /status answering while the game downloads and boots. bash + nc because the image has
# nothing else. The mod stops it the moment it binds the port itself (ApiPortHandoff).
start_phase_responder() {
    if [ "${API_ENABLED:-true}" != "true" ]; then
        return
    fi

    if [ -e "${GAME_DOWNLOAD_MARKER}" ]; then
        echo "starting" > "${PHASE_FILE}"
    else
        echo "downloading" > "${PHASE_FILE}"
    fi

    local in_fifo="/tmp/phase-responder.in" out_fifo="/tmp/phase-responder.out"
    rm -f "${in_fifo}" "${out_fifo}"
    mkfifo "${in_fifo}" "${out_fifo}"

    (
        # The mod TERMs this loop and then asserts the port is free, so the trap has to take nc
        # down too. Both ends run in the background: bash defers traps during a foreground
        # command, but not during wait.
        trap 'pkill -TERM -P $BASHPID || true; exit 0' TERM
        while true; do
            # -N makes nc exit when the client closes (Connection: close); -w 5 covers a client that
            # never does. Each FIFO open blocks until the other end opens, so the two commands open
            # them in opposite order to avoid a deadlock.
            nc -l -N -w 5 "${API_PORT}" > "${in_fifo}" < "${out_fifo}" &
            phase_response < "${in_fifo}" > "${out_fifo}" &
            wait
        done
    ) &
    # The mod kills this pid before binding; the healthcheck waits for the file to go.
    echo "$!" > "${API_HANDOFF_PID_FILE}"
    echo "Phase responder serving /status on port ${API_PORT} ($(cat "${PHASE_FILE}"))"
}

init_stardew() {
    local STEAM_AUTH_GAME_DIR="/data/game"

    # Installation check
    if [ -e "${GAME_DOWNLOAD_MARKER}" ]; then
        echo "Game already initialized, skipping."
        return
    fi

    echo "Using steam-auth service for game files..."

    # Game files but no marker (volume populated by hand, or before the marker existed). That looks
    # identical to a half-finished download, so still wait — but say why, or it reads as a hang.
    if [ -e "${GAME_EXECUTABLE}" ]; then
        echo "Game files are present but the download-completion marker is missing."
        echo "Waiting for steam-auth to re-verify them (it only writes the marker once the depot is complete)."
    fi

    echo "Waiting for steam-auth to finish downloading the game files (see: docker compose logs -f steam-auth)..."

    while [ ! -e "${GAME_DOWNLOAD_MARKER}" ]; do
        sleep 5
        echo "Still waiting for game files at ${STEAM_AUTH_GAME_DIR}..."
    done

    echo "Game files detected!"
    echo "starting" > "${PHASE_FILE}"

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

clear_smapi_marker_prompts() {
    # SMAPI's crash/update marker checks block startup on a raw Console.ReadKey() that our
    # piped, non-interactive stdin can't answer, hanging the server on the first start after
    # any crash. Prevent the prompts instead; crash details survive in ErrorLogs/SMAPI-crash.txt.
    rm -f "${GAME_DEST_DIR}/smapi-internal/StardewModdingAPI.crash.marker" \
        "${GAME_DEST_DIR}/smapi-internal/StardewModdingAPI.update.marker"
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
    # Ownership is set by the root cont-init hook (etc/cont-init.d/50-server-init.sh) before
    # this script runs as the non-root app user; here we only ensure the game binary is
    # executable (the app user owns it by now, so this succeeds without root).
    chmod +x "${GAME_EXECUTABLE}"
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

echo "Initializing SMAPI..."

# Prepare (system time is synced as root by etc/cont-init.d/50-server-init.sh before this runs)
start_phase_responder
init_xauthority
init_display_settings
init_stardew
init_steam_sdk
init_smapi
clear_smapi_marker_prompts
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
# Caveat: the PTY covers stdout only, and the FIFO only ever delivers \n-terminated lines — SMAPI
# prompts that read a raw keystroke (Console.ReadKey: crash/update markers, PressAnyKeyToExit)
# can't be answered through this channel; prevent them upstream (see clear_smapi_marker_prompts)
echo "Starting SMAPI..."
script -q -f --return -c "tail -f \"${INPUT_FIFO}\" | \"${SMAPI_EXECUTABLE}\"" "${LOG_FILE}" &
SMAPI_PID=$!

wait $SMAPI_PID
echo "SMAPI executable stopped"
