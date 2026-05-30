#!/bin/bash
# Sends rendering toggle command to SMAPI via the input FIFO
INPUT_FIFO="/tmp/smapi-input"

if [ ! -p "$INPUT_FIFO" ]; then
    echo "SMAPI not running"
    exit 1
fi

if [ "$1" = "on" ]; then
    echo "rendering on" > "$INPUT_FIFO"
elif [ "$1" = "off" ]; then
    echo "rendering off" > "$INPUT_FIFO"
else
    # Default: toggle based on current state via status check
    echo "rendering toggle" > "$INPUT_FIFO"
fi
