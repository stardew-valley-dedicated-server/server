#!/usr/bin/env bash
# JunimoServer update: pulls the configured image, installs the docker-compose.yml that matches
# it, and restarts. One command for latest, preview, and pinned IMAGE_VERSION.
#
#   curl -fsSL https://docs.junimoserver.com/update.sh | bash
#
# Run it from the directory that holds your docker-compose.yml and .env.
set -euo pipefail

REPO="stardew-valley-dedicated-server/server"

die() { echo "Error: $*" >&2; exit 1; }

command -v docker >/dev/null 2>&1 || die "Docker is not installed or not on PATH."
docker compose version >/dev/null 2>&1 || die "The Docker Compose plugin is required (docker compose v2)."
command -v curl >/dev/null 2>&1 || die "curl is required."
[ -f docker-compose.yml ] || die "No docker-compose.yml here. Run this from your server directory, or install first: https://docs.junimoserver.com/install.sh"

echo "Pulling images..."
docker compose pull

# Resolve the image tag Compose will actually use (honors .env and any override file).
server_image="$(docker compose config --images 2>/dev/null | grep -E '^sdvd/server:' | head -n1 || true)"
[ -n "$server_image" ] || server_image="sdvd/server:latest"

# The image bakes in the commit it was built from (docker/Dockerfile: ENV SDVD_GIT_SHA); fetch the
# docker-compose.yml from that exact commit so Compose always matches the image. Mirrors the
# resolution in docker/rootfs/startapp.sh (raw@sha, else the latest release).
sha="$(docker image inspect "$server_image" --format '{{range .Config.Env}}{{println .}}{{end}}' 2>/dev/null | sed -n 's/^SDVD_GIT_SHA=//p' | head -n1 || true)"
if [ -n "${sha:-}" ] && [ "$sha" != "unknown" ]; then
    compose_url="https://raw.githubusercontent.com/${REPO}/${sha}/docker-compose.yml"
else
    compose_url="https://github.com/${REPO}/releases/latest/download/docker-compose.yml"
fi

echo "Fetching docker-compose.yml..."
tmp="$(mktemp)"
curl -fsSL -o "$tmp" "$compose_url" || die "Could not download docker-compose.yml from $compose_url"
mv "$tmp" docker-compose.yml

echo "Restarting..."
docker compose up -d --remove-orphans
echo "Update complete."
