#!/usr/bin/env bash
# JunimoServer installer: scaffolds a server directory with docker-compose.yml and .env.
#
#   curl -fsSL https://docs.junimoserver.com/install.sh | bash
#
# Set DIR=<path> to choose the directory (default ./junimoserver). It does not start the server:
# add your Steam credentials to .env first (see the printed next steps).
set -euo pipefail

REPO="stardew-valley-dedicated-server/server"
BASE="https://github.com/${REPO}/releases/latest/download"
DIR="${DIR:-junimoserver}"

die() { echo "Error: $*" >&2; exit 1; }

command -v docker >/dev/null 2>&1 || die "Docker is not installed or not on PATH."
docker compose version >/dev/null 2>&1 || die "The Docker Compose plugin is required (docker compose v2)."
command -v curl >/dev/null 2>&1 || die "curl is required."

mkdir -p "$DIR"
cd "$DIR"
echo "Installing into $(pwd)"

curl -fsSL -o docker-compose.yml "${BASE}/docker-compose.yml" || die "Could not download docker-compose.yml"

if [ -f .env ]; then
    echo "Keeping existing .env (not overwritten)."
else
    curl -fsSL -o .env "${BASE}/.env.example" || die "Could not download .env.example"
    echo "Wrote .env from .env.example."
fi

cat <<EOF

JunimoServer files are ready in $(pwd).

Next steps:
  1. cd ${DIR}
  2. Edit .env and set at least STEAM_USERNAME, STEAM_PASSWORD, and VNC_PASSWORD.
  3. Authenticate with Steam:  docker compose run --rm -it steam-auth setup
  4. Start the server:         docker compose up -d

Update later with:  curl -fsSL https://docs.junimoserver.com/update.sh | bash
EOF
