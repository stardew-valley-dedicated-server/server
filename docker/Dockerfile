# Global build arguments
ARG SMAPI_VERSION=4.5.1

# ============================================================================
# Stage 1: Build steam-service downloader
# ============================================================================
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS steam-service-builder
WORKDIR /src

COPY ./tools/steam-service/*.csproj ./
RUN dotnet restore

COPY ./tools/steam-service/ .
RUN dotnet publish -c Release -o /app --no-restore

# ============================================================================
# Stage 2: Download game files using steam-service (filtered, no Content folder)
# ============================================================================
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS game-downloader

ARG SMAPI_VERSION

# Install dependencies
RUN apt-get update && apt-get install -y \
    curl \
    unzip \
    && rm -rf /var/lib/apt/lists/*

# Copy steam-service from builder
COPY --from=steam-service-builder /app /steam-service
WORKDIR /steam-service

# Download game (skip SDK during build, not needed for compiling the mod and downloaded at runtime via setup)
RUN --mount=type=secret,id=steam_username \
    --mount=type=secret,id=steam_password \
    --mount=type=secret,id=steam_refresh_token \
    if [ -f /run/secrets/steam_username ]; then export STEAM_USERNAME="$(cat /run/secrets/steam_username)"; fi && \
    if [ -f /run/secrets/steam_password ]; then export STEAM_PASSWORD="$(cat /run/secrets/steam_password)"; fi && \
    if [ -f /run/secrets/steam_refresh_token ]; then export STEAM_REFRESH_TOKEN="$(cat /run/secrets/steam_refresh_token)"; fi && \
    export GAME_DIR=/game && \
    dotnet SteamService.dll download --skip-sdk

# Download and install SMAPI
RUN curl -L "https://github.com/Pathoschild/SMAPI/releases/download/${SMAPI_VERSION}/SMAPI-${SMAPI_VERSION}-installer.zip" -o /tmp/smapi.zip && \
    unzip -q /tmp/smapi.zip -d /tmp/smapi && \
    printf "2\n\n" | "/tmp/smapi/SMAPI ${SMAPI_VERSION} installer/internal/linux/SMAPI.Installer" \
    --install \
    --game-path /game && \
    rm -rf /tmp/smapi /tmp/smapi.zip

# ============================================================================
# Stage 3: Build mod using game DLLs from stage 2
# ============================================================================
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS mod-builder

ARG BUILD_CONFIGURATION=Debug

COPY --from=game-downloader /game /game

# Copy project files first for better layer caching
COPY ./Directory.Build.props /src/
COPY ./mod/JunimoServer/JunimoServer.csproj /src/mod/JunimoServer/

# Restore dependencies with cache mount
RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet restore /src/mod/JunimoServer/JunimoServer.csproj


# Build dll-patcher tool
COPY ./tools/dll-patcher /src/dll-patcher
RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet publish /src/dll-patcher \
    --configuration Release \
    --self-contained true \
    --runtime linux-x64 \
    -p:PublishSingleFile=true \
    -p:PublishTrimmed=true \
    -o /output/dll-patcher

# Build netdebug tool
COPY ./tools/netdebug /src/netdebug
RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet publish /src/netdebug \
    --configuration Release \
    --self-contained true \
    --runtime linux-x64 \
    -p:PublishSingleFile=true \
    -p:PublishTrimmed=true \
    -o /output/netdebug

# Build server mod
COPY ./mod /src/mod
RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet build /src/mod/JunimoServer/JunimoServer.csproj \
    --configuration ${BUILD_CONFIGURATION} \
    --no-restore \
    /p:GamePath=/game \
    -o /output/JunimoServer

# Build openapi-generator tool and generate OpenAPI spec from mod
COPY ./tools/openapi-generator /src/openapi-generator
RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet build /src/openapi-generator/openapi-generator.csproj \
    --configuration Release \
    -o /output/openapi-generator && \
    dotnet /output/openapi-generator/openapi-generator.dll \
    /output/JunimoServer/JunimoServer.dll \
    /output/openapi.json

# ============================================================================
# Stage 4: Final runtime image (no game files, downloads on first run)
# ============================================================================
FROM --platform=$BUILDPLATFORM jlesage/baseimage-gui:debian-11-v4.11.1 AS server

ARG SMAPI_VERSION

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
    DISABLE_RENDERING=true \
    # SMAPI
    SMAPI_USE_CURRENT_SHELL=true \
    SMAPI_VERSION=${SMAPI_VERSION} \
    SMAPI_MODS_PATH=/data/Mods

# Install all apt packages in one layer to reduce image size and layers
RUN apt-get update && apt-get install -y \
    # Base packages
    ca-certificates \
    curl \
    locales \
    procps \
    unzip \
    x11-utils \
    # GUI packages
    polybar \
    rofi \
    slop \
    wmctrl \
    xwallpaper \
    # Additional packages
    exa \
    htop \
    nano \
    scrot \
    tcpdump \
    # TigerVNC
    tigervnc-standalone-server \
    && echo "en_US.UTF-8 UTF-8" > /etc/locale.gen && \
    locale-gen \
    # Configure TigerVNC
    && cp /usr/bin/Xvnc /opt/base/bin/Xvnc \
    # Clean up apt cache
    && apt-get autoremove -y \
    && apt-get clean \
    && rm -rf /var/lib/apt/lists/*

# Install tmux (binary download, separate layer for cacheability)
RUN curl -L -O https://github.com/tmux/tmux-builds/releases/download/v3.6a/tmux-3.6a-linux-x86_64.tar.gz && \
    tar -xzf tmux-3.6a-linux-x86_64.tar.gz && \
    install -m 0755 ./tmux /usr/local/bin/tmux && \
    rm -r ./tmux tmux-3.6a-linux-x86_64.tar.gz

# Install vsdbg for remote debugging
# TODO: Make installation optional for local/debug builds, commented for now
# RUN curl -sSL https://aka.ms/getvsdbgsh | bash /dev/stdin -v latest -l /root/vsdbg

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

# System cleanup and configuration
RUN \
    # Fix stuck dbus statoverride
    rm -f /var/lib/dpkg/statoverride && \
    # Prevent unnecessary logs
    rm -f /etc/cont-init.d/89-info.sh && \
    # Prevent overriding OpenGL drivers
    rm -rf /etc/cont-env.d/LIBGL_DRIVERS_PATH

# Copy pre-built tools from build stages
COPY --from=mod-builder /output/dll-patcher /opt/dll-patcher
COPY --from=mod-builder /output/netdebug /opt/netdebug

# Set executable permissions for application scripts and Supervisor services.
#
# Custom service definitions are copied to temp, chmod'd (non-empty files only), then merged into
# /etc/services.d after copying all system files. This avoids modifying base image service files.
# Note: .dep files can be executable scripts (return boolean) or empty markers. The ! -empty check
# makes only non-empty files executable, handling both cases correctly.
COPY docker/rootfs/etc/services.d /tmp/services/
RUN find /tmp/services -type f ! -empty -exec chmod +x {} \;

# Copy system files (merges with base image)
COPY docker/rootfs/ /

# Apply our chmod'd services and set other script permissions
RUN cp -a /tmp/services/. /etc/services.d/ && \
    rm -rf /tmp/services && \
    chmod +x /startapp.sh /opt/base/bin/*

# Copy mod from build stage (no game files included in image)
COPY --from=mod-builder /output/JunimoServer /data/Mods/JunimoServer

# Copy OpenAPI spec (for extraction via: docker cp or docker run --rm sdvd/server cat /data/openapi.json)
COPY --from=mod-builder /output/openapi.json /data/openapi.json

# Game files and save data are in volumes (game downloaded at runtime)
VOLUME [ "/data/game", "/config/xdg/config/StardewValley" ]

# Expose HTTP API port for external tools and automated testing
EXPOSE 8080

HEALTHCHECK --interval=1s --timeout=1s --start-period=10s --retries=15 \
    CMD test -f /tmp/server-output.log || exit 1

WORKDIR /data
