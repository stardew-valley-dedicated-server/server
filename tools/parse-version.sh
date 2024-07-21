#!/bin/bash

source tools/utils.sh

usage() {
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

# Initialize variables
version_part=""
show_help=false
file_path="$CI_GAME_PATH\Stardew Valley.dll"

# Parse all arguments
while [[ $# -gt 0 ]]; do
    case "$1" in
        -h|--help)
            show_help=true
            shift
            ;;
        -p|--path)
            file_path=$2
            shift 2
            ;;
        --)
            shift
            break
            ;;
        -*)
            print_error "Error: Unknown option: $1"
            echo ""
            show_help=true
            shift
            ;;
        *)
            if [[ -z "$version_part" ]]; then
                version_part="$1"
            else
                print_error "Error: Unexpected argument: $1"
                echo ""
                show_help=true
                shift
            fi
            shift
            ;;
    esac
done

# Show help after all arguments have been parsed
if $show_help; then
    usage
fi

# Handle and store the first argument
if [[ -z "$version_part" ]]; then
    print_error "Error: Missing required 'version_part' argument"
    echo ""
    usage
fi

readonly version=$(get_dll_version "$file_path")

echo "version"
echo $version

# Split result by commas
IFS=',' read -r -a version_parts <<<"$version"

# Use the provided argument to select which version part to echo
case $version_part in
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
        print_error "Error: Invalid argument, received '${version_part}' but expected 'label', 'build' or 'protocol'."
        echo ""
        usage
        ;;
esac

# Restore terminal settings, prevents [A and similar garbage in bash history after script ends
stty sane