# Global build arguments
ARG SMAPI_VERSION=4.5.2

# ============================================================================
# Stage 1: Build steam-service downloader
# ============================================================================
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS steam-service-builder
WORKDIR /src

# Repo-level build props + code-style rules apply inside the image build too
COPY ./Directory.Build.props ./CodeStyle.props ./.editorconfig ./
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
RUN curl -fL --retry 5 --retry-delay 2 "https://github.com/Pathoschild/SMAPI/releases/download/${SMAPI_VERSION}/SMAPI-${SMAPI_VERSION}-installer.zip" -o /tmp/smapi.zip && \
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
# (.editorconfig: code-style rules enforced at build via EnforceCodeStyleInBuild)
COPY ./Directory.Build.props ./CodeStyle.props ./.editorconfig /src/
COPY ./mod/JunimoServer/JunimoServer.csproj /src/mod/JunimoServer/
COPY ./mod/JunimoServer.Shared/JunimoServer.Shared.csproj /src/mod/JunimoServer.Shared/

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

# Build diagnostics tool
COPY ./tools/diagnostics /src/diagnostics
RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet publish /src/diagnostics \
    --configuration Release \
    --self-contained true \
    --runtime linux-x64 \
    -p:PublishSingleFile=true \
    -p:PublishTrimmed=true \
    -o /output/diagnostics

# Build server mod
COPY ./mod /src/mod
RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet build /src/mod/JunimoServer/JunimoServer.csproj \
    --configuration ${BUILD_CONFIGURATION} \
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

# Build the E2E test-fixture farm mod (needs game DLLs, hence built here). It is NOT a
# loaded mod in normal operation — it's copied into /data/Mods only on servers that opt in
# via SDVD_TEST_FIXTURE_FARM_MOD (see docker/rootfs/startapp.sh). manifest.json is copied
# alongside the dll because EnableModDeploy=false leaves it in the project dir, not /output.
COPY ./tests/fixtures/TestFarmMod /src/tests/fixtures/TestFarmMod
RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet build /src/tests/fixtures/TestFarmMod/TestFarmMod.csproj \
    --configuration ${BUILD_CONFIGURATION} \
    /p:GamePath=/game \
    -o /output/TestFarmMod && \
    cp /src/tests/fixtures/TestFarmMod/manifest.json /output/TestFarmMod/manifest.json

# ============================================================================
# Stage 4: Final runtime image (no game files, downloads on first run)
# ============================================================================
FROM --platform=$BUILDPLATFORM jlesage/baseimage-gui:debian-13-v4.13 AS server

ARG SMAPI_VERSION
ARG TMUX_VERSION=3.7c

ENV APP_NAME="Stardew Dedicated Server" \
    USER_ID=1000 \
    GROUP_ID=1000 \
    HOME=/config \
    LANG=en_US.UTF-8 \
    LANGUAGE=en_US:en \
    LC_ALL=en_US.UTF-8 \
    ACCEPT_EULA=Y \
    DEBIAN_FRONTEND=noninteractive \
    TERMINAL=xterm \
    DARK_MODE=1 \
    # Display
    DISPLAY=:0 \
    DISPLAY_HEIGHT=720 \
    DISPLAY_WIDTH=1280 \
    SERVER_FPS=0 \
    # SMAPI
    SMAPI_USE_CURRENT_SHELL=true \
    SMAPI_VERSION=${SMAPI_VERSION} \
    SMAPI_MODS_PATH=/data/Mods \
    # Pid file of startapp.sh's phase responder; read by the mod (ApiPortHandoff) and the HEALTHCHECK
    API_HANDOFF_PID_FILE=/tmp/phase-responder.pid

# add-pkg (base image helper), not raw apt-get: it pre-creates /var/log's symlink target
# (/config — a runtime volume, absent at build time, so tcpdump's postinst fails without it)
# and installs --no-install-recommends, keeping out systemd, whose postinst dies on that
# same symlink (mesa-utils recommends it). add-pkg cleans the apt cache itself.
RUN add-pkg \
    # Base packages
    ca-certificates \
    curl \
    # .NET globalization (SMAPI FailFasts at startup without it)
    libicu76 \
    # SkiaSharp's native image decoder (Content Patcher EditImage) needs fontconfig to load
    libfontconfig1 \
    locales \
    procps \
    unzip \
    xz-utils \
    # NTP fallback for the clock sync in etc/cont-init.d/50-server-init.sh (hwclock has no RTC
    # in most container runtimes)
    ntpsec-ntpdate \
    # Additional packages
    htop \
    nano \
    tcpdump \
    # attach-cli input pane runs on zsh/ZLE (ghost text + TAB completion; server-command-loop)
    zsh \
    # TigerVNC
    tigervnc-standalone-server \
    mesa-utils \
    && echo "en_US.UTF-8 UTF-8" > /etc/locale.gen \
    && locale-gen \
    # Configure TigerVNC
    && cp /usr/bin/Xvnc /opt/base/bin/Xvnc

# Install ffmpeg 8.1.2 (BtbN build of the stable 8.1 branch) rather than the distro package, whose
# x11grab timing bugs break the test-recording pipeline. Pin month-end autobuilds only: BtbN prunes
# daily builds after 14 days (month-end builds live two years), and the "latest" tag drifts.
RUN curl -fL --retry 5 --retry-delay 2 -o /tmp/ffmpeg.tar.xz https://github.com/BtbN/FFmpeg-Builds/releases/download/autobuild-2026-06-30-13-34/ffmpeg-n8.1.2-21-gce3c09c101-linux64-gpl-8.1.tar.xz && \
    tar -xf /tmp/ffmpeg.tar.xz -C /tmp && \
    install -m 0755 /tmp/ffmpeg-n8.1.2-21-gce3c09c101-linux64-gpl-8.1/bin/ffmpeg /usr/local/bin/ffmpeg && \
    install -m 0755 /tmp/ffmpeg-n8.1.2-21-gce3c09c101-linux64-gpl-8.1/bin/ffprobe /usr/local/bin/ffprobe && \
    rm -rf /tmp/ffmpeg*

# Install tmux (binary download, separate layer for cacheability)
# -f + --retry: the GitHub release fetch occasionally fails the TLS handshake (curl exit 35) on CI
# runners; retry the transient network error instead of failing the whole build
RUN curl -fL --retry 5 --retry-delay 2 -O https://github.com/tmux/tmux-builds/releases/download/v${TMUX_VERSION}/tmux-${TMUX_VERSION}-linux-x86_64.tar.gz && \
    tar -xzf tmux-${TMUX_VERSION}-linux-x86_64.tar.gz && \
    install -m 0755 ./tmux /usr/local/bin/tmux && \
    rm -r ./tmux tmux-${TMUX_VERSION}-linux-x86_64.tar.gz

# Install vsdbg for remote debugging
# TODO: Make installation optional for local/debug builds, commented for now
# RUN curl -sSL https://aka.ms/getvsdbgsh | bash /dev/stdin -v latest -l /root/vsdbg

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
    rm -rf /etc/cont-env.d/LIBGL_DRIVERS_PATH && \
    # Pin the runtime HOME so Steam's ~/.steam lookup matches where startapp.sh links
    # steamclient.so (${HOME}/.steam/sdk64); a mismatch fails SteamAPI_Init and with it
    # GameServer.Init(). /config is owned by USER_ID at startup, so it stays writable for any
    # configured non-root user. set-cont-env is used because cont-env overrides container env,
    # so HOME can't be supplied from compose.
    set-cont-env HOME /config

# Copy pre-built tools from build stages
COPY --from=mod-builder /output/dll-patcher /opt/dll-patcher
COPY --from=mod-builder /output/netdebug /opt/netdebug
COPY --from=mod-builder /output/diagnostics /opt/diagnostics

# Set executable permissions for application scripts and Supervisor services.
#
# Custom service definitions are copied to temp, chmod'd (non-empty files only), then merged into
# /etc/services.d after copying all system files. This avoids modifying base image service files.
# Note: .dep files can be executable scripts (return boolean) or empty markers. The ! -empty check
# makes only non-empty files executable, handling both cases correctly.
#
# xrdb/min_running_time=0 declares the xrdb one-shot as expected-to-exit-immediately (the same
# value the base image ships for its own always-exiting services — verified in the built
# image's /etc/services.d). cinit treats a service exiting
# before min_running_time (default 500ms) as a failed start and tears the whole container down
# (exit 1) — and xrdb (load X resources, exit) only outlives 500ms when it happens to spend that
# long waiting on a still-warming Xvnc, a timing race a one-shot must not have to win.
# min_running_time is a VALUE file cinit reads — it must stay non-executable, or cinit tries to
# run it (fails 126).
COPY docker/rootfs/etc/services.d /tmp/services/
RUN find /tmp/services -type f ! -empty ! -name min_running_time -exec chmod +x {} \; && \
    find /tmp/services -type f -name min_running_time -exec chmod 0644 {} \;

# Copy system files (merges with base image)
COPY docker/rootfs/ /

# Apply our chmod'd services and set other script permissions
RUN cp -a /tmp/services/. /etc/services.d/ && \
    rm -rf /tmp/services && \
    chmod +x /startapp.sh /opt/base/bin/* /etc/cont-init.d/50-server-init.sh

# Copy mod from build stage (no game files included in image)
COPY --from=mod-builder /output/JunimoServer /data/Mods/JunimoServer

# Stage the E2E test-fixture farm mod OUTSIDE the loaded Mods path. startapp.sh copies it
# into /data/Mods/TestFarmMod only when SDVD_TEST_FIXTURE_FARM_MOD=true, so it never loads
# on normal servers (keeping Data/AdditionalFarms vanilla for everyone else).
COPY --from=mod-builder /output/TestFarmMod /opt/test-fixtures/TestFarmMod

# Copy OpenAPI spec (for extraction via: docker cp or docker run --rm sdvd/server cat /data/openapi.json)
COPY --from=mod-builder /output/openapi.json /data/openapi.json

# Git commit the image was built from (the diagnostics tool reports it). Declared last, not with the
# ARGs up top: its value changes every build and a changed ARG/ENV invalidates every layer after it —
# up top that re-ran the apt install and the ffmpeg/tmux downloads. Nothing in the build consumes it.
ARG SDVD_GIT_SHA=unknown
ENV SDVD_GIT_SHA=${SDVD_GIT_SHA}

# Game files and save data are in volumes (game downloaded at runtime)
VOLUME [ "/data/game", "/config/xdg/config/StardewValley" ]

# Expose HTTP API port for external tools and automated testing
EXPOSE 8080

# Unhealthy without connecting while the phase responder owns the port: it serves one connection at
# a time, and a 2s probe would keep stealing it from real status clients.
HEALTHCHECK --interval=2s --timeout=2s --start-period=120s --retries=15 \
    CMD { [ ! -e "${API_HANDOFF_PID_FILE}" ] && curl -fsS --max-time 2 "http://127.0.0.1:${API_PORT:-8080}/health" > /dev/null; } \
        || [ "${API_ENABLED:-true}" = "false" ] || exit 1

WORKDIR /data
