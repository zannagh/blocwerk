#!/bin/bash
set -e

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PROJECT_ROOT="$(dirname "$SCRIPT_DIR")"
IMAGE="ghcr.io/zannagh/blocwerk:latest"

echo "Building blocwerk Docker image..."
docker build -t "$IMAGE" -f "$SCRIPT_DIR/Dockerfile" "$PROJECT_ROOT"

echo "Saving image to tarball..."
docker save "$IMAGE" | gzip > "$SCRIPT_DIR/blocwerk.tar.gz"

echo "Done: docker/blocwerk.tar.gz ($(du -h "$SCRIPT_DIR/blocwerk.tar.gz" | cut -f1))"
