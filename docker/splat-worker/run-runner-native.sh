#!/usr/bin/env bash
# Run a Blocwerk 3D runner natively on a Mac (Apple Silicon): Brush trains on the GPU through Metal.
# The runner connects OUT to the Blocwerk server (no inbound port) and trains the photo-real views it
# is given; see README "3D runner". The key comes from the environment only, never an argument.
#
#   BWR_KEY=bwr_... caffeinate -i ./run-runner-native.sh --server https://blocwerk.app
#
# Needs: Python >= 3.11 (or uv); no COLMAP (the server runs it). Downloads the pinned Brush release
# (sha256-checked) into ./.bin and makes a venv in ./.venv on first run (shared with run-native.sh).
set -euo pipefail
HERE=$(cd "$(dirname "$0")" && pwd)
SHARED=$(cd "$HERE/../compute-jobs-py" && pwd)

BRUSH_VERSION=v0.3.0
BRUSH_ASSET=brush-app-aarch64-apple-darwin
BRUSH_SHA256=65b2631398c839be3c1d4d7160fe2326389dec87830aac0710985e6690a1048c

[ "$(uname -s)" = Darwin ] || { echo "run-runner-native.sh is for macOS; on Linux run the runner from the Docker image" >&2; exit 1; }
[ "$(uname -m)" = arm64 ] || { echo "Brush's macOS build is Apple Silicon only" >&2; exit 1; }

if [ -z "${BRUSH_BIN:-}" ]; then
  BRUSH_BIN="$HERE/.bin/$BRUSH_ASSET/brush_app"
  if [ ! -x "$BRUSH_BIN" ]; then
    mkdir -p "$HERE/.bin"
    tmp=$(mktemp -d)
    curl -fsSL -o "$tmp/brush.tar.xz" \
      "https://github.com/ArthurBrussee/brush/releases/download/$BRUSH_VERSION/$BRUSH_ASSET.tar.xz"
    echo "$BRUSH_SHA256  $tmp/brush.tar.xz" | shasum -a 256 -c - >/dev/null
    tar -xJf "$tmp/brush.tar.xz" -C "$HERE/.bin"
    rm -rf "$tmp"
    xattr -dr com.apple.quarantine "$HERE/.bin" 2>/dev/null || true
  fi
fi
export BRUSH_BIN
[ -n "${BWR_KEY:-}" ] || { echo "set BWR_KEY to the runner key (bwr_...) shown when the runner was created" >&2; exit 1; }

if [ ! -x "$HERE/.venv/bin/python" ]; then
  if command -v uv >/dev/null; then
    uv venv -q --python 3.12 "$HERE/.venv"
    VIRTUAL_ENV="$HERE/.venv" uv pip install -q -r "$HERE/requirements.txt"
  else
    python3 -m venv "$HERE/.venv"
    "$HERE/.venv/bin/pip" install -q -r "$HERE/requirements.txt"
  fi
fi

export PYTHONPATH="$HERE:$SHARED${PYTHONPATH:+:$PYTHONPATH}"
export RUNNER_WORK_DIR=${RUNNER_WORK_DIR:-$HOME/Library/Caches/blocwerk-runner}
export BRUSH_CACHE_DIR=${BRUSH_CACHE_DIR:-$HOME/Library/Caches/blocwerk-runner/brush-cache}
export GIT_SHA=${GIT_SHA:-$(git -C "$HERE" rev-parse --short HEAD 2>/dev/null || echo unknown)}
cd "$HERE"
exec "$HERE/.venv/bin/python" -m splatworker.gpurunner "$@"
