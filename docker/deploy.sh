#!/bin/bash
set -e

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
TARBALL="$SCRIPT_DIR/blocwerk.tar.gz"

# Either load from tarball or pull from GHCR
if [ -f "$TARBALL" ]; then
    echo "Loading blocwerk image from tarball..."
    docker load < "$TARBALL"
elif command -v docker &> /dev/null; then
    echo "No tarball found, pulling from GHCR..."
    docker compose -f "$SCRIPT_DIR/docker-compose.yml" pull blocwerk
fi

echo "Restarting services..."
docker compose -f "$SCRIPT_DIR/docker-compose.yml" down
docker compose -f "$SCRIPT_DIR/docker-compose.yml" up -d

echo "Done. Services:"
docker compose -f "$SCRIPT_DIR/docker-compose.yml" ps
