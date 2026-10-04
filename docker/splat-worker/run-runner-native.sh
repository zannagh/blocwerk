#!/usr/bin/env bash
# Run a Blocwerk 3D runner natively on a Mac (Apple Silicon): Brush trains on the GPU through Metal.
# The runner connects OUT to the Blocwerk server (no inbound port) and trains the photo-real views it
# is given; see README "3D runner". The key comes from the environment only, never an argument.
#
#   BWR_KEY=bwr_... caffeinate -i ./run-runner-native.sh --server https://blocwerk.app
#
# Needs: Python >= 3.11 (or uv); no COLMAP (the server runs it). Downloads the pinned Brush release
# (sha256-checked) into ./.bin and makes a venv in ./.venv on first run (shared with run-native.sh).
# Also renders wall textures for the server when the Mac has the memory (half of it is the blend budget; see
# splatworker/gpurunner/textures.py): the wall-geometry package is put on PYTHONPATH, its requirements are the venv's.
set -euo pipefail
HERE=$(cd "$(dirname "$0")" && pwd)
SHARED=$(cd "$HERE/../compute-jobs-py" && pwd)
GEOMETRY=$(cd "$HERE/../wall-geometry" && pwd)  # the wallgeometry package: the runner renders wall textures with it

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

# The venv holds requirements.txt, which includes wall-geometry's numpy / scipy / OpenCV pins (same versions): a
# runner with enough memory (RUNNER_TEXTURES=0 turns it off) also renders wall textures, with the wallgeometry package
# put on PYTHONPATH below. A venv made before that is brought up to date once: its stamp is the checksum of
# requirements.txt it was installed from.
if [ ! -x "$HERE/.venv/bin/python" ]; then
  if command -v uv >/dev/null; then
    uv venv -q --python 3.12 "$HERE/.venv"
  else
    python3 -m venv "$HERE/.venv"
  fi
fi
REQ_SUM=$(shasum -a 256 "$HERE/requirements.txt" | cut -d' ' -f1)
if [ "$(cat "$HERE/.venv/.requirements-sha256" 2>/dev/null)" != "$REQ_SUM" ]; then
  echo "installing the Python requirements into $HERE/.venv ..." >&2
  if command -v uv >/dev/null; then
    VIRTUAL_ENV="$HERE/.venv" uv pip install -q -r "$HERE/requirements.txt"
  else
    "$HERE/.venv/bin/pip" install -q -r "$HERE/requirements.txt"
  fi
  echo "$REQ_SUM" > "$HERE/.venv/.requirements-sha256"
fi

export PYTHONPATH="$HERE:$SHARED:$GEOMETRY${PYTHONPATH:+:$PYTHONPATH}"
export RUNNER_WORK_DIR=${RUNNER_WORK_DIR:-$HOME/Library/Caches/blocwerk-runner}
export BRUSH_CACHE_DIR=${BRUSH_CACHE_DIR:-$HOME/Library/Caches/blocwerk-runner/brush-cache}
export GIT_SHA=${GIT_SHA:-$(git -C "$HERE" rev-parse --short HEAD 2>/dev/null || echo unknown)}
cd "$HERE"
exec "$HERE/.venv/bin/python" -m splatworker.gpurunner "$@"
