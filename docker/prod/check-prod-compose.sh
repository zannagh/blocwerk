#!/usr/bin/env bash
# Checks docker/docker-compose.prod.yml (what production runs) against docker/docker-compose.yml, so a
# service or setting added to the base file is not silently missing on the box. Run from anywhere:
#   docker/prod/check-prod-compose.sh
# Fails when:
#  1. `docker compose config` rejects either file (prod is rendered with prod.env.example),
#  2. a base service is missing from prod (or prod has one the base lacks), unless listed below,
#  3. a shared service runs a different image,
#  4. a base service's environment key is missing from prod, unless listed below.
# COMPOSE overrides the compose command (e.g. a standalone `docker-compose` binary).
set -euo pipefail

cd "$(dirname "$0")/.."
read -r -a compose <<< "${COMPOSE:-docker compose}"

# Base services production deliberately does not run.
#  deploy-hook: replaced by deploy/autodeploy.sh (no inbound webhook on a public host).
#  splat-worker, splat-worker-cuda, gpu-runner: need a GPU; the box has none (splat-cpu + a remote runner).
ABSENT_SERVICES="deploy-hook splat-worker splat-worker-cuda gpu-runner"

# Base environment keys production deliberately does not set (service:KEY). Each one falls back to the
# app's/service's built-in default, so leaving it out is safe; add it to prod to change that default.
# The two STORAGEPATHs default to /app/beta-videos and /app/wall-images, which is where prod mounts them.
ABSENT_ENV="
blocwerk:BETAVIDEO__STORAGEPATH
blocwerk:WALLIMAGE__STORAGEPATH
blocwerk:SPLATSERVICE__MAXSTEPS
blocwerk:CAPTURE__HEICJPEGQUALITY
blocwerk:CAPTURE__VIDEOFRAMESPERSECOND
blocwerk:CAPTURE__FRAMESHARPNESSWINDOW
blocwerk:CAPTURE__FRAMEJPEGQ
blocwerk:CAPTURE__SHARPNESSEDGE
blocwerk:GEOMETRYSERVICE__TEXTURES__MMPERPX
blocwerk:GEOMETRYSERVICE__TEXTURES__MAXSIDEPX
blocwerk:GEOMETRYSERVICE__TEXTURES__JPEGQUALITY
"

tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT

# The base file requires two deploy-hook variables; dummies are enough to render it.
WEBHOOK_SECRET=ci COMPOSE_DIR=/tmp "${compose[@]}" -f docker-compose.yml --env-file .env.example \
  --profile '*' config --format json > "$tmp/base.json"
"${compose[@]}" -f docker-compose.prod.yml --env-file prod.env.example \
  --profile '*' config --format json > "$tmp/prod.json"

services() { jq -r '.services | keys[]' "$1" | sort; }
envkeys() { jq -r --arg s "$2" '.services[$s].environment // {} | keys[]' "$1" | sort; }
image() { jq -r --arg s "$2" '.services[$s].image // ""' "$1"; }
listed() { grep -qxF "$1" <<< "$(tr ' ' '\n' <<< "$2")"; }

errors=0
fail() { echo "::error::$*"; errors=$((errors + 1)); }

for s in $(comm -23 <(services "$tmp/base.json") <(services "$tmp/prod.json")); do
  listed "$s" "$ABSENT_SERVICES" || fail "service '$s' is in docker-compose.yml but not in docker-compose.prod.yml"
done
for s in $(comm -13 <(services "$tmp/base.json") <(services "$tmp/prod.json")); do
  fail "service '$s' is in docker-compose.prod.yml but not in docker-compose.yml"
done

for s in $(comm -12 <(services "$tmp/base.json") <(services "$tmp/prod.json")); do
  [ "$(image "$tmp/base.json" "$s")" = "$(image "$tmp/prod.json" "$s")" ] \
    || fail "service '$s' runs '$(image "$tmp/prod.json" "$s")' in prod but '$(image "$tmp/base.json" "$s")' in the base file"
  for k in $(comm -23 <(envkeys "$tmp/base.json" "$s") <(envkeys "$tmp/prod.json" "$s")); do
    listed "$s:$k" "$ABSENT_ENV" || fail "$s: environment key '$k' is in docker-compose.yml but not in docker-compose.prod.yml"
  done
done

if [ "$errors" -gt 0 ]; then
  echo "$errors drift problem(s). Add the setting to docker-compose.prod.yml (and the box), or list it as intentional in $0."
  exit 1
fi
echo "docker-compose.prod.yml renders and matches docker-compose.yml's services and settings."
