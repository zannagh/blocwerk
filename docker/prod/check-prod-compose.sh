#!/usr/bin/env bash
# Checks docker/docker-compose.prod.yml (what production runs) against docker/docker-compose.yml, so a
# service or setting added to the base file is not silently missing on the box. Run from anywhere:
#   docker/prod/check-prod-compose.sh
# Fails when:
#  1. `docker compose config` rejects either file (prod is rendered with prod.env.example),
#  2. a base service is missing from prod (or prod has one the base lacks),
#  3. a shared service runs a different image,
#  4. a shared service differs in environment keys, volume mount targets, published container
#     ports or healthcheck presence,
#  5. prod publishes any port beyond 127.0.0.1 (Caddy on the `edge` network is the only way in),
# unless the difference is listed as intentional below.
# COMPOSE overrides the compose command (e.g. a standalone `docker-compose` binary).
set -euo pipefail

cd "$(dirname "$0")/.."
read -r -a compose <<< "${COMPOSE:-docker compose}"

# Base services production deliberately does not run.
#  deploy-hook: replaced by prod/autodeploy.sh (no inbound webhook on a public host).
#  splat-worker, splat-worker-cuda, gpu-runner: need a GPU; the box has none (splat-cpu + a remote runner).
ABSENT_SERVICES="deploy-hook splat-worker splat-worker-cuda gpu-runner"

# Intentional differences on shared services, one per line: <service>:<kind>:<base|prod>:<value>.
#  env:base:KEY      the base sets KEY, prod does not (falls back to the built-in default; the two
#                    STORAGEPATHs default to /app/beta-videos and /app/wall-images, where prod mounts).
#  env:prod:KEY      prod-only settings (secrets the base has no use for, rollout toggles).
#  volume:<side>:T   a mount target only one side has.
#  port:<side>:P     a container port only one side publishes.
#  healthcheck:<side>  only one side defines a healthcheck in compose.
EXCEPTIONS="
blocwerk:env:base:BETAVIDEO__STORAGEPATH
blocwerk:env:base:WALLIMAGE__STORAGEPATH
blocwerk:env:base:SPLATSERVICE__MAXSTEPS
blocwerk:env:base:CAPTURE__HEICJPEGQUALITY
blocwerk:env:base:CAPTURE__VIDEOFRAMESPERSECOND
blocwerk:env:base:CAPTURE__FRAMESHARPNESSWINDOW
blocwerk:env:base:CAPTURE__FRAMEJPEGQ
blocwerk:env:base:CAPTURE__SHARPNESSEDGE
blocwerk:env:base:GEOMETRYSERVICE__TEXTURES__MMPERPX
blocwerk:env:base:GEOMETRYSERVICE__TEXTURES__MAXSIDEPX
blocwerk:env:base:GEOMETRYSERVICE__TEXTURES__JPEGQUALITY
blocwerk:env:prod:SMTP__HOST
blocwerk:env:prod:SMTP__PORT
blocwerk:env:prod:SMTP__USERNAME
blocwerk:env:prod:SMTP__PASSWORD
blocwerk:env:prod:SMTP__FROM
blocwerk:env:prod:SMTP__FROMNAME
blocwerk:env:prod:SMTP__SECURITY
blocwerk:env:prod:BLOCWERK__ENCRYPTIONKEY
blocwerk:env:prod:RUNNERS__MODE
blocwerk:env:prod:BLOCWERK__SERVER__TRUSTEDPROXIES__0
blocwerk:env:prod:BLOCWERK__AUTH__APIKEYLOGIN__ENABLED
blocwerk:env:prod:BLOCWERK__AUTH__APIKEYLOGIN__ALLOWEDUSERIDS__0
blocwerk:volume:base:/data/beta-videos
blocwerk:volume:base:/data/wall-images
blocwerk:volume:prod:/app/beta-videos
blocwerk:volume:prod:/app/wall-images
blocwerk:volume:prod:/app/models/climbingcrux.onnx
blocwerk:port:base:5050
postgres:healthcheck:prod
"

tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT

# The base file requires two deploy-hook variables; dummies are enough to render it.
WEBHOOK_SECRET=ci COMPOSE_DIR=/tmp "${compose[@]}" -f docker-compose.yml --env-file .env.example \
  --profile '*' config --format json > "$tmp/base.json"
"${compose[@]}" -f docker-compose.prod.yml --env-file prod.env.example \
  --profile '*' config --format json > "$tmp/prod.json"

services() { jq -r '.services | keys[]' "$1" | sort; }
image() { jq -r --arg s "$2" '.services[$s].image // ""' "$1"; }
# One line per item of a kind for a service, sorted: env keys, mount targets, container ports.
items() {
  case "$3" in
    env) jq -r --arg s "$2" '.services[$s].environment // {} | keys[]' "$1" ;;
    volume) jq -r --arg s "$2" '.services[$s].volumes // [] | .[].target' "$1" ;;
    port) jq -r --arg s "$2" '.services[$s].ports // [] | .[].target | tostring' "$1" ;;
    healthcheck) jq -r --arg s "$2" 'if .services[$s].healthcheck then "yes" else empty end' "$1" ;;
  esac | sort -u
}
excepted() { grep -qxF "$1" <<< "$EXCEPTIONS"; }

errors=0
fail() { echo "::error::$*"; errors=$((errors + 1)); }

for s in $(comm -23 <(services "$tmp/base.json") <(services "$tmp/prod.json")); do
  grep -qxF "$s" <<< "$(tr ' ' '\n' <<< "$ABSENT_SERVICES")" \
    || fail "service '$s' is in docker-compose.yml but not in docker-compose.prod.yml"
done
for s in $(comm -13 <(services "$tmp/base.json") <(services "$tmp/prod.json")); do
  fail "service '$s' is in docker-compose.prod.yml but not in docker-compose.yml"
done

for s in $(comm -12 <(services "$tmp/base.json") <(services "$tmp/prod.json")); do
  [ "$(image "$tmp/base.json" "$s")" = "$(image "$tmp/prod.json" "$s")" ] \
    || fail "service '$s' runs '$(image "$tmp/prod.json" "$s")' in prod but '$(image "$tmp/base.json" "$s")' in the base file"
  for kind in env volume port healthcheck; do
    items "$tmp/base.json" "$s" "$kind" > "$tmp/b"
    items "$tmp/prod.json" "$s" "$kind" > "$tmp/p"
    while read -r v; do
      [ "$kind" = healthcheck ] && key="$s:$kind:base" || key="$s:$kind:base:$v"
      excepted "$key" || fail "$s: $kind '$v' only in docker-compose.yml (add it to prod, or list '$key' as intentional)"
    done < <(comm -23 "$tmp/b" "$tmp/p")
    while read -r v; do
      [ "$kind" = healthcheck ] && key="$s:$kind:prod" || key="$s:$kind:prod:$v"
      excepted "$key" || fail "$s: $kind '$v' only in docker-compose.prod.yml (add it to the base, or list '$key' as intentional)"
    done < <(comm -13 "$tmp/b" "$tmp/p")
  done
done

# Nothing in prod may listen beyond loopback.
while read -r line; do
  fail "prod publishes $line beyond 127.0.0.1"
done < <(jq -r '.services | to_entries[] | .key as $s | (.value.ports // [])[]
                | select((.host_ip // "") != "127.0.0.1") | "\($s) port \(.target)"' "$tmp/prod.json")

if [ "$errors" -gt 0 ]; then
  echo "$errors drift problem(s). Fix docker-compose.prod.yml (and the box), or list the difference as intentional in $0."
  exit 1
fi
echo "docker-compose.prod.yml renders and matches docker-compose.yml's services and settings."
