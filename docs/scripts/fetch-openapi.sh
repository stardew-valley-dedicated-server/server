#!/bin/bash
# Extracts OpenAPI spec from the Docker image
# Usage: ./scripts/fetch-openapi.sh [image-tag]
#
# The spec is generated at Docker build time and embedded in the image.

set -e

IMAGE="${1:-sdvd/server:latest}"

# Resolve the directory this script is in
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# Output path relative to the script
OUTPUT="$SCRIPT_DIR/../assets/openapi.json"

echo "Extracting OpenAPI spec from $IMAGE..."

# Create a temporary container, copy the file, then remove container
CONTAINER_ID=$(docker create "$IMAGE")
docker cp "$CONTAINER_ID:/data/openapi.json" "$OUTPUT"
docker rm "$CONTAINER_ID" > /dev/null

echo "OpenAPI spec saved to $OUTPUT"
