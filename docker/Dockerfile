###############
# BUILD STAGE #
###############
FROM jlesage/baseimage-gui:debian-11 as build-stage
WORKDIR /app

# Install Go 1.21
RUN apt-get update && \
    apt-get install -y wget && \
    wget https://golang.org/dl/go1.21.5.linux-amd64.tar.gz && \
    tar -C /usr/local -xzf go1.21.5.linux-amd64.tar.gz && \
    rm go1.21.5.linux-amd64.tar.gz

# Add Go to PATH
ENV PATH="/usr/local/go/bin:${PATH}"

# Verify Go installation
RUN go version

# Copy go mod and sum files 
COPY daemon/go.mod daemon/go.sum ./

# Download all dependencies. 
# Dependencies will be cached if the go.mod and go.sum files are not changed 
RUN go mod download 

COPY daemon .

# Build the Go app
RUN go build -o game-daemon ./cmd/daemon/daemon.go

###############
# FINAL STAGE #
###############
FROM jlesage/baseimage-gui:debian-11

ENV APP_NAME="Stardew Dedicated Server"

# set user to root
ENV USER_ID=0
ENV GROUP_ID=0
ENV TZ=America/New_York
ENV HOME=/root

# Preselect answers for steamcmd installation
RUN echo steam steam/license note '' | debconf-set-selections
RUN echo steam steam/question select "I AGREE" | debconf-set-selections

RUN apt-get update \
	&& apt-get install --no-install-recommends -y software-properties-common \
	&& apt-add-repository non-free \
	&& dpkg --add-architecture i386 \
	&& apt-get update \
	&& ACCEPT_EULA=Y apt-get install --no-install-recommends -y wget unzip tar curl pulseaudio steamcmd

# TODO: 
#RUN useradd steam
#USER steam
#WORKDIR /home/steam

ARG STARDEW_VERSION=1.6.6
ARG SMAPI_VERSION=4.0.8
ARG STEAM_USER=
ARG STEAM_PASS=

# Add SteamCMD to PATH
ENV PATH="/usr/games:${PATH}"
ENV SMAPI_USE_CURRENT_SHELL=true

# Install Stardew Valley
RUN steamcmd +force_install_dir /data/Stardew +login "${STEAM_USER}" "${STEAM_PASS}" +app_update 413150 +quit

# Install SMAPI
RUN wget https://github.com/Pathoschild/SMAPI/releases/download/${SMAPI_VERSION}/SMAPI-${SMAPI_VERSION}-installer.zip -qO /data/nexus.zip && \
    unzip /data/nexus.zip -d /data/nexus/ && \
    /bin/bash -c "echo -e \"2\n\n\" | /data/nexus/SMAPI\ ${SMAPI_VERSION}\ installer/internal/linux/SMAPI.Installer --install --game-path \"/data/Stardew\"" || : && \
    rm -rf "/data/Stardew/Mods/SaveBackup" && \
	rm -rf "/data/nexus"

#RUN mkdir -p /root/.steam/sdk64 \
#	&& cp /config/xdg/data/Steam/steamcmd/linux64/steamclient.so /root/.steam/sdk64/steamclient.so 
	
# TODO: Check if we can remove this, we don't want to use recompiled DLLs
# Replace DLLs with recompiled without audio functionality
RUN rm -rf "/data/Stardewsteam_api64.dll" && \
    wget https://storage.googleapis.com/junimo-public/steam/steam_api64.dll -O /data/Stardew/steam_api64.dll && \
    rm -rf "/data/Stardew/libsteam_api.so" && \
    wget https://storage.googleapis.com/junimo-public/steam/libsteam_api.so -O /data/Stardew/libsteam_api.so && \
    mkdir -p /root/.steam/sdk64 && \
    wget https://storage.googleapis.com/junimo-public/steam/steamclient.so -O /root/.steam/sdk64/steamclient.so

COPY ["docker/config.user.json", "/data/Stardew/smapi-internal/"] 

RUN chmod +x /data/Stardew/StardewValley && \
    chmod -R 777 /data/Stardew/ && \
    chown -R 1000:1000 /data/Stardew

# Copy mods
COPY ["docker/mods", "/data/Stardew/Mods/"]
RUN chmod -R 777 /data/Stardew/Mods

# Copy deamon
COPY --from=build-stage /app/game-daemon /opt/game-daemon
RUN chmod +x /opt/game-daemon

# Copy baseimage-gui entrypoint
COPY docker/docker-entrypoint.sh /startapp.sh