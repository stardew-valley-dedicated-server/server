#!/bin/bash

source tools/utils.sh

usage() {
  echo "Parse version information from Stardew Valley dll."
  echo "Usage: $0 <version part>"
  echo ""
  echo "Version parts:"
  echo "  label      - Echo the version label"
  echo "  build      - Echo the version build number"
  echo "  protocol   - Echo the protocol version override"
  echo ""
  echo "Options:"
  echo "  -h, --help - Show this help message"
  exit 1
}

get_dll_version() {
    FILE="$1"

    # Convert the search string to little endian UTF16 hex
    SEARCH_TERM="ProductVersion"
    SEARCH_HEX=$(echo -n "${SEARCH_TERM}" | xxd -p | sed 's/../&00/g')

    # Search for the hex string in the file and extract the value
    xxd -p -c 256 "${FILE}" | tr -d '\n' | grep -oP "(?<=${SEARCH_HEX}).{0,64}" | \
    xxd -r -p | tr -d '\0' | tail -n1
}

# Initialize variables
VERSION_TYPE=""
SHOW_HELP=false
FILE="$CI_GAME_PATH\Stardew Valley.dll"

# Parse all arguments
while [[ $# -gt 0 ]]; do
    case "$1" in
        -h|--help)
            SHOW_HELP=true
            shift
            ;;
        -p|--path)
            FILE=$2
            shift 2
            ;;
        --)
            shift
            break
            ;;
        -*)
            print_error "Error: Unknown option: $1"
            echo ""
            SHOW_HELP=true
            shift
            ;;
        *)
            if [[ -z "$VERSION_TYPE" ]]; then
                VERSION_TYPE="${1}"
            else
                print_error "Error: Unexpected argument: $1"
                echo ""
                SHOW_HELP=true
                shift
            fi
            shift
            ;;
    esac
done

# Show help after all arguments have been parsed
if $SHOW_HELP; then
    usage
fi

# Default if not specified
if [[ -z "$VERSION_TYPE" ]]; then
    VERSION_TYPE="label"
fi

readonly version=$(get_dll_version "$FILE")

# Split result by commas
IFS=',' read -r -a version_parts <<<"$version"

# Use the provided argument to select which version part to echo
case $VERSION_TYPE in
    # Original: versionLabel
    label)
        echo $(trim "${version_parts[0]}")
        ;;
    # Original: versionBuildNumber
    build)
        echo $(trim "${version_parts[2]}")
        ;;
    # Original: protocolVersionOverride
    protocol)
        echo $(trim "${version_parts[3]}")
        ;;
    *)
        print_error "Error: Invalid argument, received '${VERSION_TYPE}' but expected 'label', 'build' or 'protocol'."
        echo ""
        usage
        ;;
esac

# Restore terminal settings, prevents [A and similar garbage in bash history after script ends
stty sane
