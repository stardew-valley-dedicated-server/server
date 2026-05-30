#!/bin/bash
# Sends a render-rate command to SMAPI via the input FIFO.
#
# The in-mod command takes an integer now: `rendering <fps>` (0 disables, N>0
# enables at N fps). This script maps the polybar button's click to that:
#   on              -> rendering 10   (a fixed debug rate)
#   off             -> rendering 0
#   (anything else) -> read current state from GET /rendering and flip:
#                      fps>0 -> rendering 0, else rendering 10
#
# The button exists to recover from a black VNC screen, so if the API can't be
# reached the safe default is to enable rendering.
INPUT_FIFO="/tmp/smapi-input"
DEBUG_FPS=10
API_PORT="${API_PORT:-8080}"

if [ ! -p "$INPUT_FIFO" ]; then
    echo "SMAPI not running"
    exit 1
fi

case "$1" in
    on)
        echo "rendering ${DEBUG_FPS}" > "$INPUT_FIFO"
        ;;
    off)
        echo "rendering 0" > "$INPUT_FIFO"
        ;;
    *)
        # State-read toggle: ask the API for the current fps, then flip.
        # Response is JSON like {"fps":15}; extract the integer without jq.
        response="$(curl -fsS --max-time 2 "http://127.0.0.1:${API_PORT}/rendering" 2>/dev/null)"
        current_fps="$(printf '%s' "$response" | grep -o '"fps":[0-9]*' | grep -o '[0-9]*')"

        if [ -n "$current_fps" ] && [ "$current_fps" -gt 0 ] 2>/dev/null; then
            echo "rendering 0" > "$INPUT_FIFO"
        else
            # fps is 0, unknown, or the API was unreachable: enable (recover from black screen).
            echo "rendering ${DEBUG_FPS}" > "$INPUT_FIFO"
        fi
        ;;
esac
