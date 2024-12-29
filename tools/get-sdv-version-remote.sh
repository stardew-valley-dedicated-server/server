#!/bin/bash

# Requires: https://github.com/SteamRE/DepotDownloader/releases/tag/DepotDownloader_2.6.0
# TODO: Add auto-download with check if already downloaded

source tools/utils.sh

# Usage information
usage() {
    echo "Fetch latest version information for Stardew Valley from Steam.\n"
    echo "Usage: $0 [options]"
    echo ""
    echo "Options:"
    echo "  -h, --help        Show this help message"
    echo "  -v, --verbose     Show verbose output"
    exit 1
}


# Download latest file from steam depot to parse version info from
run_depot_downloader() {
    tools/depot-downloader/DepotDownloader.exe \
        -app $APP_ID \
        -depot $DEPOT_ID \
        -username "$STEAM_USER" \
        -password "$STEAM_PASS" \
        -dir "$TEMP_DIR" \
        -filelist "$TEMP_FILE"
}

create_temp() {
    TEMP_DIR=$(mktemp -d)
    TEMP_FILE=$(mktemp --tmpdir="$TEMP_DIR")

    mkdir -p "$TEMP_DIR"
    echo "$FILE" > "$TEMP_FILE"
}

clear_temp() {
    rm -rf $TEMP_DIR
}

main() {
    # DepotDownloader config
    APP_ID=413150
    DEPOT_ID=413151
    FILE="Stardew Valley.dll"
    TEMP_DIR=""
    TEMP_FILE=""

    # Script state
    show_help=false
    suppress_output=true
    output_path=

    # Parse command-line arguments
    OPTIONS=$(getopt -o ho::v --long help,output::,verbose -- "$@")
    if [ $? -ne 0 ]; then
        usage
    fi

    eval set -- "$OPTIONS"
    while true; do
        case "$1" in
            -h|--help)
                show_help=true
                shift
                ;;
            -o|--output)
                output_path="${2:-./version.txt}"
                shift 2
                ;;
            -v|--verbose)
                suppress_output=false
                shift
                ;;
            --)
                shift
                break
                ;;
            *)
                echo "Error: Unknown option: $1"
                usage
                ;;
        esac
    done

    if [ "$show_help" = true ]; then
        usage
    fi

    create_temp

    if [ -n "$output_path" ] && [ "$suppress_output" = false ]; then
        echo "Using output_path: '$output_path'"
    fi

    suppress_output_conditionally "$suppress_output" run_depot_downloader

    version=$(tools/get-sdv-version-local.sh -p "$TEMP_DIR/$FILE" label)
    clear_temp

    if [ -n "$output_path" ]; then
        # Write version to file
        printf "$version" > "$output_path"

        if [ "$suppress_output" = false ]; then
            print_success "Done! Created version to '$output_path'"
        fi
    else
        # echo version to file
        printf "$version"
    fi
}

main "$@"
