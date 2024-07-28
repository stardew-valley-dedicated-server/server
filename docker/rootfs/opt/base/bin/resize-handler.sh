#!/bin/bash

# Description:
#   Watch for file changes.
#
# Usage:
#   resize-handler.sh

update_toolbar() {
    /etc/services.d/polybar/run &
}

update_wallpaper() {
    if [ -e "/data/images/wallpaper.png" ]; then
        xwallpaper --zoom /data/images/wallpaper.png
    fi
}

get_screen_size() {
    xrandr | grep '*' | awk '{print $1}'
}

# Get the initial screen size
oldSize=$(get_screen_size)

# sleep 3
update_toolbar
update_wallpaper

while true; do
    # Listen to display resize events
    xev -root -event randr | while read -r line; do
        newSize=$(get_screen_size)
        
        # Check if the screen size has changed
        if [ "$oldSize" != "$newSize" ]; then
            echo "Resize detected: '$oldSize' -> '$newSize'"

            update_toolbar
            update_wallpaper

            oldSize=$newSize
        fi
    done
done