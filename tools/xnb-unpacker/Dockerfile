# Use a minimal Debian image
# Note: Debian is chosen over Alpine because Mesa/X11 libraries and glibc dependencies
# are required by StardewXnbHack. Alpine would need extra compatibility layers and
# end up larger and more complex.
FROM debian:trixie-slim

# Install required packages and clean up apt cache in a single layer
RUN apt-get update && \
    apt-get install -y --no-install-recommends \
    ca-certificates \
    curl \
    unzip \
    libicu76 \
    xvfb \
    mesa-utils \
    x11-utils \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /tmp

# Download and extract the latest StardewXnbHack Linux release
RUN set -e; \
    TAG=$(curl -s https://api.github.com/repos/Pathoschild/StardewXnbHack/releases/latest \
    | grep -o '"tag_name": *"[^"]*"' | cut -d'"' -f4); \
    curl -L "https://github.com/Pathoschild/StardewXnbHack/releases/download/${TAG}/StardewXnbHack-${TAG}-for-Linux.zip" \
    -o stardewxnbhack.zip; \
    unzip stardewxnbhack.zip; \
    rm stardewxnbhack.zip; \
    mv "StardewXnbHack ${TAG} for Linux"/StardewXnbHack ./StardewXnbHack; \
    chmod +x StardewXnbHack; \
    rm -rf "StardewXnbHack ${TAG} for Linux"

# X socket dir with the standard sticky world-writable mode, so a non-root Xvfb can create its socket.
RUN mkdir -p /tmp/.X11-unix && chmod 1777 /tmp/.X11-unix

# Create run script. The container runs as the host user (`docker run --user`, see README) so the
# unpacked files belong to them; a per-run HOME under /tmp works for any uid.
# 1. Creates a HOME with an existing .config: the game instance StardewXnbHack spins up resolves
#    its config dir via Environment.GetFolderPath(ApplicationData), which yields "" (a relative
#    path into /game) unless $HOME/.config already exists
# 2. Starts Xvfb so the game can run headlessly
# 3. Copies StardewXnbHack from /tmp to the current working directory /game
# 4. Launches StardewXnbHack
# 5. Deletes StardewXnbHack
RUN echo '#!/bin/sh\n'\
    'set -e\n'\
    'export HOME=$(mktemp -d)\n'\
    'mkdir -p "$HOME/.config"\n'\
    'cp -f /tmp/StardewXnbHack .\n'\
    'Xvfb :99 -screen 0 1024x768x24 &\n'\
    'export DISPLAY=:99\n'\
    './StardewXnbHack\n'\
    'rm -rf ./StardewXnbHack\n'\
    > /entrypoint.sh && chmod +x /entrypoint.sh

# Mount the game folder
WORKDIR /game

# Use JSON exec form for ENTRYPOINT
ENTRYPOINT ["/entrypoint.sh"]
