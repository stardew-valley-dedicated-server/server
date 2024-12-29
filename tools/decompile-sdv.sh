#!/bin/bash

cleanup() {
    print_error "Script interrupted."
    if [[ -n "$BACKGROUND_PID" ]]; then
        print_error "Killing ilspycmd process..."
        kill -9 "$BACKGROUND_PID" 2>/dev/null
    fi
    exit 1
}

check_dependency_ilspycmd() {
    if ! command_exists ilspycmd; then
        echo "ilspycmd is not installed."
        read -p "Do you want to install ilspycmd? (yes/no) " choice

        case "$choice" in
            y|Y|j|J|yes|Yes|YES )
                dotnet tool install --global ilspycmd
                ;;
            * )
                echo "ilspycmd is required to run this script. Exiting."
                exit 1
                ;;
        esac
    fi
}

get_src_directory() {
    if ! var_exists CI_GAME_PATH; then
        error_ci_game_path_not_set
    fi

    local -r src="${CI_GAME_PATH}/Stardew Valley.dll"

    # Directory separators:
    # Windows -> \
    # Linux -> /
    if ! file_exists "$src"; then
        error_file_not_found "$src"
    fi


    echo "$src"
}

get_dest_directory() {
    local -r versionLabel=$(tools/parse-sdv-version.sh label)
    local -r versionBuildNumber=$(tools/parse-sdv-version.sh build)
    echo "./decompiled/sdv-${versionLabel}-${versionBuildNumber}"
}

decompile() {
    check_dependency_ilspycmd

    local -r src=$1
    local -r dest=$2

    echo "Decompiling Stardew Valley..."
    
    printf "Input: \e[90m%s\e[0m\n" "$(realpath "$src")"
    printf "Output: \e[90m%s\e[0m\n\n" "$(realpath "$dest")"

    if file_exists "$dest/Stardew Valley.csproj"; then
        print_success "Skipping, already decompiled."
        exit 0
    fi

    # By default it is not possible to abort ilspycmd, but in background we can kill its PID
    ilspycmd --nested-directories --project --outputdir "$dest" "$src" &
    BACKGROUND_PID=$!
    wait "$BACKGROUND_PID"
    BACKGROUND_PID=

    if [ $? -eq 0 ]; then
        print_success "Done!"
    else
        error_ilspycmd_failed
    fi
}

error_ci_game_path_not_set() {
    print_error "Error: \"CI_GAME_PATH\" is not set."
    exit 2
}

error_file_not_found() {
    print_error "Error: Source file \"$1\" does not exist. Did you install Stardew Valley and set \"CI_GAME_PATH\"?"
    exit 3
}

error_ilspycmd_failed() {
    print_error "Error: ilspycmd process failed with exit code \"$?\"."
    exit 4
}

main() {
    . tools/utils.sh
    
    trap cleanup SIGINT
    
    src_dir=$(get_src_directory)
    if [ $? -ne 0 ]; then
        echo $src_dir
        exit 1
    fi
    
    dest_dir=$(get_dest_directory)
    if [ $? -ne 0 ]; then
        echo $dest_dir
        exit 1
    fi

    decompile "$src_dir" "$dest_dir"
}

main
