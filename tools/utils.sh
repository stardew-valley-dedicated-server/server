#!/bin/bash

load_env() {
    set -o allexport
    source .env set
    set +o allexport
}

trim() {
    echo "$1" | sed 's/^[[:space:]]*//;s/[[:space:]]*$//'
}

# Function to print a message in green
print_success() {
    echo -e "\e[32m$1\e[0m"
}

# Function to print a message in red
print_error() {
    echo -e "\e[31m$1\e[0m"
}

suppress_output_conditionally() {
    local suppress=$1
    shift
    if [ "$suppress" = true ]; then
        "$@" &> /dev/null
    else
        "$@"
    fi
}

# Function to check if a command exists
command_exists() {
    if command -v "$1" &> /dev/null; then
        return 0
    else
        return 1
    fi
}

file_exists() {
    if [[ -f "$src" ]]; then
        return 0
    fi
    return 1
}

# Function to check if a given env var is set, print error and exit script if not
var_exists() {
    local -r env_value=$(printf '%s\n' "${!1}")
    if [[ -z "$env_value" ]]; then
        return 1
    fi
    return 0
}

get_dll_version() {
    # CYGWIN, WSL + LINUX
    strings "$1" -el | grep -A1 'ProductVersion' | tail -n1

    # CYGWIN + POWERSHELL ONLY, VERY MUCH NO NO
    # win_path=$(cygpath -a -w "$1")
    # powershell "(Get-Item -path '$win_path').VersionInfo.ProductVersion"
}

# get_latest_git_release Pathoschild/SMAPI
get_latest_git_release() {
  curl --silent "https://api.github.com/repos/$1/releases/latest" |
    grep '"tag_name":' |
    sed -E 's/.*"([^"]+)".*/\1/'
}

# For convenience we automatically load env variables when sourcing this file
load_env