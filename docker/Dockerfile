FROM jlesage/baseimage-gui:debian-11-v4

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

# Install SteamCMD
RUN echo steam steam/question select "I AGREE" | debconf-set-selections && \
    echo steam steam/license note '' | debconf-set-selections && \
    apt-get install -y \
    steamcmd && \
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

# Install TigerVNC (replaces pre-compiled x11 to include GLX extension)
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

# Copy mods
COPY .output/mods /data/Mods

# Copy remaining files
COPY docker/rootfs/ /

VOLUME [ "/data/Stardew", "/config/xdg/config/StardewValley" ]

WORKDIR /data
