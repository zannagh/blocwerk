#!/bin/sh
# Pulls the newest blocwerk image and recreates just that one service. Driven by deploy.sh.
set -eu

SERVICE="${DEPLOY_SERVICE:-blocwerk}"
COMPOSE_FILE="${COMPOSE_DIR:?COMPOSE_DIR is not set}/docker-compose.yml"

stamp() { date -u '+%F %T'; }

# A single GitHub release fires both a 'release' and a 'package' delivery, so two runs can land
# nearly at once. Serialize them: the second one bails instead of racing the first.
exec 9>/tmp/deploy-hook.lock
if ! flock -n 9; then
  echo "[deploy-hook] $(stamp) a deploy is already running; skipping this trigger."
  exit 0
fi

# The GHCR package is private and the host's docker login lives in the macOS keychain (unusable
# inside this Linux container), so authenticate with our own read:packages token when one is set.
# Skipped automatically if the package is made public (GHCR_TOKEN left empty).
if [ -n "${GHCR_TOKEN:-}" ]; then
  echo "[deploy-hook] $(stamp) logging in to ghcr.io as ${GHCR_USER:-zannagh}..."
  echo "$GHCR_TOKEN" | docker login ghcr.io -u "${GHCR_USER:-zannagh}" --password-stdin
fi

echo "[deploy-hook] $(stamp) pulling ${SERVICE} image..."
docker compose -f "$COMPOSE_FILE" pull "$SERVICE"

echo "[deploy-hook] $(stamp) recreating ${SERVICE}..."
docker compose -f "$COMPOSE_FILE" up -d "$SERVICE"

# Drop the now-dangling old image so the disk doesn't fill up over many deploys.
docker image prune -f >/dev/null 2>&1 || true

echo "[deploy-hook] $(stamp) done."
