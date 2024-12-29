#!/bin/bash

load_env() {
    set -o allexport
    source $(realpath .env) set
    set +o allexport
}

trim() {
    echo "$1" | sed 's/^[[:space:]]*//;s/[[:space:]]*$//'
}

command_exists() {
    if which "$1" >/dev/null 2>&1; then
        return 0
    fi
    return 1
}

file_exists() {
    if [[ -f "$1" ]]; then
        return 0
    fi
    return 1
}

var_exists() {
    if [[ -n "$(printf '%s\n' "${!1}")" ]]; then
        return 0
    fi
    return 1
}

print_success() {
    printf "\e[32m%s\e[0m\n" "$1"
}

print_error() {
    printf "\e[31m%s\e[0m\n" "$1"
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

# get_latest_git_release() {
#   curl --silent "https://api.github.com/repos/$1/releases/latest" |
#     grep '"tag_name":' |
#     sed -E 's/.*"([^"]+)".*/\1/'
# }

# Automatically load env variables when sourcing this file
load_env