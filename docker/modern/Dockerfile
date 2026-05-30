# ============================================================================
# Modern server image — WIP / EXPERIMENTAL, NOT IN PRODUCTION USE
#
# Next-iteration attempt at the server image (Alpine/musl). Unfinished and not
# fully tested. The production image is docker/Dockerfile (Debian/glibc). This
# image is not wired into the Makefile or CI; it builds only via
# docker/modern/docker-compose.yml, manually.
# Reference: docs/admins/operations/modern-docker.md
# ============================================================================
# Modern Docker Image for Stardew Valley Dedicated Server
#
# Stack: Alpine + Xvfb (X11) + WebRTC (go2rtc) + PipeWire + s6-overlay
#
# OpenGL: SwiftShader (Subzero JIT, ~5MB) + Mesa Zink (no LLVM, ~15MB)
#   Instead of llvmpipe + LLVM (~465MB). SwiftShader provides software
#   Vulkan 1.3, Zink translates OpenGL calls to Vulkan.
# ============================================================================

ARG SMAPI_VERSION=4.5.1
ARG S6_OVERLAY_VERSION=3.2.0.2
ARG GO2RTC_VERSION=1.9.7
ARG BUILD_CONFIGURATION=Debug
ARG MESA_VERSION=25.0.3
ARG SWIFTSHADER_VERSION=2024-12-11

# ============================================================================
# Stage 0a: Build SwiftShader (Subzero JIT — no LLVM dependency)
# Uses Debian for build (llvm-subzero has glibc-isms), output .so works on
# Alpine via gcompat since it only links libc/libpthread/libdl/libstdc++.
# ============================================================================
FROM debian:bookworm-slim AS swiftshader-builder

RUN apt-get update && apt-get install -y --no-install-recommends \
    build-essential cmake ninja-build git python3 \
    zlib1g-dev pkg-config ca-certificates \
    && rm -rf /var/lib/apt/lists/*

RUN git clone https://github.com/google/swiftshader.git --depth 1 /src/swiftshader && \
    cd /src/swiftshader && \
    git submodule update --init --depth 1 third_party/SPIRV-Headers third_party/SPIRV-Tools

WORKDIR /src/swiftshader/build

RUN cmake .. \
    -GNinja \
    -DCMAKE_BUILD_TYPE=MinSizeRel \
    -DREACTOR_BACKEND=Subzero \
    -DSWIFTSHADER_BUILD_TESTS=FALSE \
    -DSWIFTSHADER_BUILD_BENCHMARKS=FALSE \
    -DBUILD_VULKAN_WRAPPER=FALSE \
    -DCMAKE_INSTALL_PREFIX=/output && \
    ninja -j$(nproc) vk_swiftshader && \
    # Install the Vulkan ICD and its JSON manifest
    mkdir -p /output/lib /output/share/vulkan/icd.d && \
    cp Linux/libvk_swiftshader.so /output/lib/ && \
    strip /output/lib/libvk_swiftshader.so && \
    # Create ICD JSON pointing to library location in runtime image
    echo '{"file_format_version":"1.0.0","ICD":{"library_path":"/usr/lib/libvk_swiftshader.so","api_version":"1.3.0"}}' \
    > /output/share/vulkan/icd.d/swiftshader_icd.json

# ============================================================================
# Stage 0b: Build Mesa with Zink only (no LLVM)
# ============================================================================
FROM alpine:edge AS mesa-builder

RUN apk add --no-cache \
    build-base meson ninja python3 py3-mako py3-yaml py3-packaging \
    # Mesa build deps (Zink needs Vulkan headers + libdrm)
    libdrm-dev vulkan-headers vulkan-loader-dev \
    # EGL/GLX platform deps
    libx11-dev libxext-dev libxrandr-dev libxshmfence-dev libxxf86vm-dev \
    libxcb-dev xcb-util-keysyms-dev libxfixes-dev libxdamage-dev \
    # Wayland (for EGL platform support)
    wayland-dev wayland-protocols \
    # Other Mesa deps
    zlib-dev expat-dev elfutils-dev \
    flex bison git \
    # Download tools
    curl xz

ARG MESA_VERSION

RUN mkdir -p /src && \
    curl -L "https://archive.mesa3d.org/mesa-${MESA_VERSION}.tar.xz" -o /tmp/mesa.tar.xz && \
    tar -xf /tmp/mesa.tar.xz -C /src && \
    rm /tmp/mesa.tar.xz

WORKDIR /src/mesa-${MESA_VERSION}

RUN meson setup builddir \
    --prefix=/output \
    --buildtype=release \
    -D b_lto=true \
    -D gallium-drivers=zink \
    -D vulkan-drivers= \
    -D llvm=disabled \
    -D glx=dri \
    -D egl=enabled \
    -D platforms=x11,wayland \
    -D gles1=disabled \
    -D gles2=disabled \
    -D gallium-vdpau=disabled \
    -D gallium-va=disabled \
    -D gallium-xa=disabled \
    -D valgrind=disabled \
    -D libunwind=disabled && \
    ninja -C builddir -j$(nproc) && \
    ninja -C builddir install && \
    # Strip all shared libraries
    find /output -name '*.so*' -exec strip --strip-unneeded {} \;

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
# Stage 2: Download game files using steam-service
# ============================================================================
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS game-downloader

ARG SMAPI_VERSION

RUN apt-get update && apt-get install -y \
    curl \
    unzip \
    && rm -rf /var/lib/apt/lists/*

COPY --from=steam-service-builder /app /steam-service
WORKDIR /steam-service

# Download game (skip SDK during build — downloaded at runtime via setup)
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
# Stage 3: Build mod
# ============================================================================
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS mod-builder

ARG BUILD_CONFIGURATION

COPY --from=game-downloader /game /game

# Copy project files first for better layer caching
COPY ./Directory.Build.props /src/
COPY ./mod/JunimoServer/JunimoServer.csproj /src/mod/JunimoServer/

# Restore dependencies
RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet restore /src/mod/JunimoServer/JunimoServer.csproj

# Build dll-patcher tool
COPY ./tools/dll-patcher /src/dll-patcher
RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet publish /src/dll-patcher \
    --configuration Release \
    --self-contained true \
    --runtime linux-musl-x64 \
    -p:PublishSingleFile=true \
    -p:PublishTrimmed=true \
    -o /output/dll-patcher

# Build server mod
COPY ./mod /src/mod
RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet build /src/mod/JunimoServer/JunimoServer.csproj \
    --configuration ${BUILD_CONFIGURATION} \
    --no-restore \
    /p:GamePath=/game \
    -o /output/JunimoServer

# Build openapi-generator and generate OpenAPI spec
COPY ./tools/openapi-generator /src/openapi-generator
RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet build /src/openapi-generator/openapi-generator.csproj \
    --configuration Release \
    -o /output/openapi-generator && \
    dotnet /output/openapi-generator/openapi-generator.dll \
    /output/JunimoServer/JunimoServer.dll \
    /output/openapi.json

# ============================================================================
# Stage 4: Runtime image
# ============================================================================
FROM alpine:edge AS server

ARG SMAPI_VERSION
ARG S6_OVERLAY_VERSION
ARG GO2RTC_VERSION

# ---------- Environment ----------

ENV HOME=/root \
    LANG=en_US.UTF-8 \
    # X11 display
    DISPLAY=:99 \
    # OpenGL via Zink (OpenGL→Vulkan) + SwiftShader (software Vulkan)
    MESA_LOADER_DRIVER_OVERRIDE=zink \
    GALLIUM_DRIVER=zink \
    LIBGL_ALWAYS_SOFTWARE=1 \
    # Tell Vulkan loader where to find SwiftShader ICD
    VK_ICD_FILENAMES=/usr/share/vulkan/icd.d/swiftshader_icd.json \
    # Display
    DISPLAY_WIDTH=1280 \
    DISPLAY_HEIGHT=720 \
    SERVER_FPS=0 \
    # SMAPI
    SMAPI_USE_CURRENT_SHELL=true \
    SMAPI_VERSION=${SMAPI_VERSION} \
    SMAPI_MODS_PATH=/data/Mods

# ---------- s6-overlay ----------

ADD https://github.com/just-containers/s6-overlay/releases/download/v${S6_OVERLAY_VERSION}/s6-overlay-noarch.tar.xz /tmp/
ADD https://github.com/just-containers/s6-overlay/releases/download/v${S6_OVERLAY_VERSION}/s6-overlay-x86_64.tar.xz /tmp/
RUN tar -C / -Jxpf /tmp/s6-overlay-noarch.tar.xz && \
    tar -C / -Jxpf /tmp/s6-overlay-x86_64.tar.xz && \
    rm /tmp/s6-overlay-*.tar.xz

# ---------- System packages ----------

RUN apk add --no-cache \
    # X11 virtual framebuffer + window manager
    xvfb-run xorg-server xf86-video-dummy xdpyinfo openbox \
    # Screen capture + streaming
    ffmpeg \
    # Vulkan loader (needed for SwiftShader ICD dispatch)
    vulkan-loader \
    # Mesa runtime deps (libdrm, libxshmfence etc. needed by our custom Zink build)
    libdrm libxshmfence libxcb libx11 libxext wayland-libs-client \
    # Audio (null sink)
    pipewire pipewire-pulse openal-soft-libs \
    # Game native dependencies
    icu-libs libstdc++ libgcc gcompat \
    # Utilities
    curl ca-certificates bash coreutils util-linux tmux unzip \
    procps

# ---------- Custom Mesa (Zink only, no LLVM) + SwiftShader ----------

COPY --from=swiftshader-builder /output/lib/libvk_swiftshader.so /usr/lib/
COPY --from=swiftshader-builder /output/share/vulkan/icd.d/swiftshader_icd.json /usr/share/vulkan/icd.d/

# Copy Mesa Zink libraries (libGL, libEGL, Zink DRI driver, etc.)
COPY --from=mesa-builder /output/lib/ /usr/lib/
COPY --from=mesa-builder /output/share/ /usr/share/

# ---------- .NET 6.0 runtime ----------
# Not available in Alpine packages; install from Microsoft tarball
ENV DOTNET_ROOT=/usr/share/dotnet
RUN curl -sSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh && \
    chmod +x /tmp/dotnet-install.sh && \
    /tmp/dotnet-install.sh --channel 6.0 --runtime dotnet --install-dir ${DOTNET_ROOT} && \
    ln -s ${DOTNET_ROOT}/dotnet /usr/local/bin/dotnet && \
    rm /tmp/dotnet-install.sh

# ---------- go2rtc ----------

ADD https://github.com/AlexxIT/go2rtc/releases/download/v${GO2RTC_VERSION}/go2rtc_linux_amd64 /usr/local/bin/go2rtc
RUN chmod +x /usr/local/bin/go2rtc

# ---------- Locale ----------

RUN apk add --no-cache musl-locales && \
    echo "en_US.UTF-8 UTF-8" > /etc/locale.gen || true

# ---------- Copy rootfs overlay ----------

COPY docker/modern/rootfs/ /

# ---------- Build native shims for Steam SDK glibc compat ----------
# 1. pthread_shim.so: Provides __pthread_key_create and other glibc-internal
#    symbols that musl/gcompat don't export.
# 2. steamclient_stub.so: Stub replacement for steamclient.so which is
#    glibc-only and segfaults on musl. Returns failure codes so
#    GameServer.Init() fails gracefully instead of crashing.

RUN apk add --no-cache gcc musl-dev && \
    gcc -shared -fPIC -o /opt/lib/pthread_shim.so /opt/lib/pthread_shim.c -lpthread && \
    gcc -shared -fPIC -o /opt/lib/steamclient_stub.so /opt/lib/steamclient_stub.c && \
    rm /opt/lib/pthread_shim.c /opt/lib/steamclient_stub.c && \
    apk del gcc musl-dev

# ---------- Set permissions ----------

RUN chmod +x /opt/bin/* && \
    find /etc/s6-overlay/s6-rc.d -name run -exec chmod +x {} \; && \
    find /etc/s6-overlay/s6-rc.d -name up -exec chmod +x {} \;

# ---------- Copy mod from build stage ----------

COPY --from=mod-builder /output/dll-patcher /opt/dll-patcher
COPY --from=mod-builder /output/JunimoServer /data/Mods/JunimoServer
COPY --from=mod-builder /output/openapi.json /data/openapi.json

# ---------- Volumes ----------

VOLUME [ "/data/game", "/data/Saves" ]

# ---------- Ports ----------

# WebRTC web UI
EXPOSE 1984
# Game API
EXPOSE 8080
# Steam Game Relay
EXPOSE 24642/udp
# Steam Query
EXPOSE 27015/udp

# ---------- Health check ----------

HEALTHCHECK --interval=1s --timeout=1s --start-period=10s --retries=15 \
    CMD test -f /tmp/server-output.log || exit 1

WORKDIR /data

ENTRYPOINT ["/init"]
