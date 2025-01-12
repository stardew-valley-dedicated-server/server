#!/bin/bash

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
        # TODO: Check if update necessary, add version to installed.txt
        # TODO: See new fetch-sdv-version and parse-sdv-version scripts
        echo "Game already initialized, skipping."
        return;
    fi

    # Download & Install
    echo "Installing Stardew Valley..."

    steamcmd +@sSteamCmdForcePlatformType linux \
        +force_install_dir ${GAME_DEST_DIR} \
        +login "${STEAM_USER}" "${STEAM_PASS}" "${STEAM_GUARD_CODE}" \
        +app_update 413150 \
        +quit

    # Capture the exit status of the steamcmd command
    EXIT_STATUS=$?

    # Check if the command was successful
    if [ $EXIT_STATUS -ne 0 ]; then
        print_error "Error: steamcmd command failed with exit status $EXIT_STATUS"
        exit 1
    fi

    # Removing these files causes a "FileNotFoundException", but it doesn't
    # seem to cause problems but reduces game storage size by about 70-80%.
    echo "Removing uneccessary files..."
    rm -rfv "${GAME_DEST_DIR}/Content/XACT/Wave Bank.xwb" "${GAME_DEST_DIR}/Content/XACT/Wave Bank(1.4).xwb"

    echo "Stardew Valley downloaded successfully!"
}

init_smapi() {
    # Installation check
    if [ -e "${SMAPI_EXECUTABLE}" ]; then
        # TODO: Check if update necessary, add version to installed.txt
        echo "SMAPI already initialized, skipping."
    else
        # Download
        curl -L https://github.com/Pathoschild/SMAPI/releases/download/${SMAPI_VERSION}/SMAPI-${SMAPI_VERSION}-installer.zip -o /data/smapi.zip
        unzip -q /data/smapi.zip -d /data/smapi/

        # Install
        echo -e "2\n\n" | "/data/smapi/SMAPI ${SMAPI_VERSION} installer/internal/linux/SMAPI.Installer" \
            --install \
            --game-path "${GAME_DEST_DIR}"

        # Cleanup
        rm -rf "/data/smapi"
    fi
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

# Run the game
${GAME_EXECUTABLE}
