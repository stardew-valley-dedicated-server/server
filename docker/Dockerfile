# ============================================================================
# Stage 1: Download game files (for mod compilation only, not in final image)
# ============================================================================
FROM steamcmd/steamcmd:latest AS game-downloader

ARG SMAPI_VERSION=4.1.10

RUN apt-get update && apt-get install -y \
    curl \
    unzip \
    libicu-dev

COPY --chmod=755 ./tools/download-game.sh /tools/download-game.sh

# Download Stardew Valley
RUN --mount=type=secret,id=steam_user \
    --mount=type=secret,id=steam_pass \
    /tools/download-game.sh

# Remove unnecessary wave bank files to reduce size
RUN rm -fv "/game/Content/XACT/Wave Bank.xwb" "/game/Content/XACT/Wave Bank(1.4).xwb"

# Install SMAPI (needed for mod compilation DLL references)
RUN curl -L "https://github.com/Pathoschild/SMAPI/releases/download/${SMAPI_VERSION}/SMAPI-${SMAPI_VERSION}-installer.zip" -o /tmp/smapi.zip && \
    unzip -q /tmp/smapi.zip -d /tmp/smapi && \
    printf "2\n\n" | "/tmp/smapi/SMAPI ${SMAPI_VERSION} installer/internal/linux/SMAPI.Installer" \
    --install \
    --game-path /game && \
    rm -rf /tmp/smapi /tmp/smapi.zip

# ============================================================================
# Stage 2: Build mod using game DLLs from stage 1
# ============================================================================
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS mod-builder

ARG Configuration=Debug

COPY --from=game-downloader /game /game
COPY ./mod /src/mod
COPY ./Directory.Build.props /src/

RUN dotnet build /src/mod/JunimoServer/JunimoServer.csproj \
    --configuration Debug \
    /p:GamePath=/game \
    -o /output/JunimoServer

RUN ls -al /output

# ============================================================================
# Stage 3: Final runtime image (no game files, downloads on first run)
# ============================================================================
FROM --platform=$BUILDPLATFORM jlesage/baseimage-gui:debian-11-v4.6.7 AS server

ENV APP_NAME="Stardew Dedicated Server" \
    USER_ID=0 \
    GROUP_ID=0 \
    HOME=/root \
    LANG=en_US.UTF-8 \
    LANGUAGE=en_US:en \
    LC_ALL=en_US.UTF-8 \
    ACCEPT_EULA=Y \
    DEBIAN_FRONTEND=noninteractive \
    TERMINAL=xterm \
    DARK_MODE=1 \
    # Display
    DISPLAY=:0 \
    DISPLAY_HEIGHT=800 \
    DISPLAY_WIDTH=1280 \
    DISABLE_RENDERING=true\
    # SMAPI
    SMAPI_USE_CURRENT_SHELL=true \
    SMAPI_VERSION=4.1.10 \
    SMAPI_MODS_PATH=/data/Mods

# Add 32-bit architecture and update the sources list for SteamCMD
RUN printf "\ndeb http://deb.debian.org/debian/ bullseye contrib non-free" >> /etc/apt/sources.list && \
    dpkg --add-architecture i386 && \
    apt-get update

# Install base packages
RUN apt-get update && apt-get install --no-install-recommends -y \
    ca-certificates \
    curl \
    locales \
    # Includes pgrep for service handling
    procps \
    unzip \
    # Includes xev for resize-handler.sh
    x11-utils && \
    echo "en_US.UTF-8 UTF-8" > /etc/locale.gen && \
    locale-gen

# Install SteamCMD (needed for runtime game download)
RUN echo steam steam/question select "I AGREE" | debconf-set-selections && \
    echo steam steam/license note '' | debconf-set-selections && \
    apt-get install -y steamcmd && \
    mv /usr/games/steamcmd /opt/base/bin/ && \
    steamcmd +force_install_dir /data/steam +@sSteamCmdForcePlatformType linux +login anonymous +app_update 1007 +quit && \
    mkdir -p /root/.steam/sdk64 && \
    mv /data/steam/linux64/steamclient.so /root/.steam/sdk64/steamclient.so && \
    rm -rf /data/steam

# Install GUI packages
RUN apt-get install -y \
    polybar \
    rofi \
    slop \
    # Used by polybar taskbar script
    wmctrl \
    xwallpaper

# Install additional packages
RUN apt-get install -y \
    exa \
    htop \
    nano

# Install latest tmux
RUN curl -L -O https://github.com/tmux/tmux-builds/releases/download/v3.6a/tmux-3.6a-linux-x86_64.tar.gz && \
    tar -xzf tmux-3.6a-linux-x86_64.tar.gz && \
    install -m 0755 ./tmux /usr/local/bin/tmux && \
    rm -r ./tmux && \
    rm ./tmux-3.6a-linux-x86_64.tar.gz

# Install vsdbg for remote debugging
# TODO: Make installation optional for local/debug builds, commented for now
# RUN curl -sSL https://aka.ms/getvsdbgsh | bash /dev/stdin -v latest -l /root/vsdbg

# Install TigerVNC (replaces pre-compiled x11, so that it includes the GLX extension)
RUN apt-get install -y \
    tigervnc-standalone-server && \
    cp /usr/bin/Xvnc /opt/base/bin/Xvnc

# Install polybar theme
RUN add-pkg --virtual build git && \
    git clone --depth=1 https://github.com/adi1090x/polybar-themes.git && \
    cd ./polybar-themes && \
    chmod +x ./setup.sh && \
    echo "1" | ./setup.sh && \
    cd .. && \
    rm -rf ./polybar-themes && \
    del-pkg build

# Install WebVNC icon
COPY docker/rootfs/data/images/junimo.png /data/images/junimo.png
RUN install_app_icon.sh /data/images/junimo.png && \
    rm /data/images/junimo.png

# Install Node, used for steam-appticket-generator
RUN curl -fsSL https://deb.nodesource.com/setup_24.x | bash - && \
    apt-get install -y nodejs

# Install .NET SDK, used for the DLL patcher
RUN curl -fsSL https://packages.microsoft.com/config/debian/11/packages-microsoft-prod.deb -o packages-microsoft-prod.deb && \
    dpkg -i packages-microsoft-prod.deb && \
    rm packages-microsoft-prod.deb && \
    apt-get update && \
    apt-get install -y dotnet-sdk-9.0

RUN \
    # Clear apt cache
    apt-get autoremove -y && \
    apt-get clean && \
    rm -rf /var/lib/apt/lists/* && \
    # Fix stuck dbus statoverride
    rm /var/lib/dpkg/statoverride && \
    # Prevent uneccessary logs
    rm /etc/cont-init.d/89-info.sh && \
    # Prevent overriding OpenGL drivers
    rm -rf /etc/cont-env.d/LIBGL_DRIVERS_PATH

# Copy and install steam-appticket-generator
# TODO: Actually build a bundle (or compile with deno?), then throw away node_modules
COPY tools/steam-appticket-generator/ /data/Tools/steam-appticket-generator/
RUN cd /data/Tools/steam-appticket-generator/ && npm ci

# Copy and install DLL patcher
COPY tools/dll-patcher/ /tmp/dll-patcher/
RUN cd /tmp/dll-patcher && \
    dotnet publish -c Release -o /opt/dll-patcher && \
    rm -rf /tmp/dll-patcher

# Set executable permissions for application scripts and Supervisor services.
#
# Custom service definitions are copied to temp, chmod'd (non-empty files only), then merged into
# /etc/services.d after copying all system files. This avoids modifying base image service files.
# Note: .dep files can be executable scripts (return boolean) or empty markers. The ! -empty check
# makes only non-empty files executable, handling both cases correctly.
COPY docker/rootfs/etc/services.d /tmp/our-services/
RUN find /tmp/our-services -type f ! -empty -exec chmod +x {} \;

# Copy system files (merges with base image)
COPY docker/rootfs/ /

# Apply our chmod'd services and set other script permissions
RUN cp -a /tmp/our-services/. /etc/services.d/ && \
    rm -rf /tmp/our-services && \
    chmod +x /startapp.sh /opt/base/bin/*

# Copy mod from build stage (no game files included in image)
COPY --from=mod-builder /output/JunimoServer /data/Mods/JunimoServer

# Game files and save data are in volumes (game downloaded at runtime)
VOLUME [ "/data/Stardew", "/config/xdg/config/StardewValley" ]

WORKDIR /data
