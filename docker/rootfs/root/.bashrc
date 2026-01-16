# ~/.bashrc: executed by bash(1) for interactive shells

# If not running interactively, don't do anything
case $- in
    *i*) ;;
      *) return;;
esac

# Display welcome message
cat << 'EOF'
═══════════════════════════════════════════════════════════════════
   STARDEW VALLEY DEDICATED SERVER - CONTAINER SHELL
═══════════════════════════════════════════════════════════════════
You are in the Docker container's bash shell.

To interact with the server:
  /opt/base/bin/attach-cli    - Attach to server CLI (recommended)

Common shell tasks:
  ls /config/xdg/config/StardewValley/Saves  - View save files
  ls /data/game/Mods                       - View installed mods
  cat /tmp/server-output.log                  - View raw server logs

To exit this shell:
  exit    - Return to your host terminal
═══════════════════════════════════════════════════════════════════

EOF

# Enable some basic bash features
shopt -s checkwinsize

# Set a basic prompt
PS1='\u@\h:\w\$ '
