#!/bin/bash

echo "Note: Steam credentials are required to download Stardew Valley, leave blank if already downloaded."
echo ""

# Prompt for Steam credentials
read -p "Enter your Steam username: " STEAM_USER
echo ""
read -sp "Enter your Steam password: " STEAM_PASS
echo ""
echo ""

# Export the Steam credentials for this session
export STEAM_USER
export STEAM_PASS

# Load existing .env variables
if [ -f ".env" ]; then
    set -a
    source .env
    set +a
fi

# Start Docker Compose using the current environment
docker compose up -d --force-recreate
