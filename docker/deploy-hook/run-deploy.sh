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

# Busy gate: hold the deploy while a user is actively creating a boulder or editing a wall (their
# unsaved in-flight work would be lost when the container is recreated). The app's
# /health/ready-to-deploy endpoint answers 200 + body "idle" when idle and 503 + body "busy" when
# busy. busybox wget (curl is not in docker:27-cli) has no --fail flag, so we capture the body with
# -O - and only proceed when wget succeeded AND that body says "idle". Any non-zero wget (a 503, or
# unreachable) OR a 2xx whose body isn't "idle" (a fail-open we must not trust) keeps us waiting.
# Interval ~15s, capped at ~120 tries (~30 min) so a stuck-busy state can never block a deploy forever.
#
# The host must be the CURRENT domain, the same one ANNOUNCE_URL uses. The old blocwerk.zannagh.me
# 301-redirects; this particular poll survives that (it sends no Authorization header), but having
# the two URLs disagree about which host is production is how the announcement quietly ends up
# pointed at a redirector that strips its bearer token.
HEALTH_URL="${HEALTH_URL:-https://blocwerk.app/health/ready-to-deploy}"
GATE_INTERVAL="${GATE_INTERVAL:-15}"
GATE_MAX_TRIES="${GATE_MAX_TRIES:-120}"

gate_try=0
while :; do
  # `if body=$(...)` keeps set -e from aborting on a non-zero wget: the assignment's status is
  # wget's, and using it as an if-condition is exempt from set -e.
  if body=$(wget -q -O - -T 5 "$HEALTH_URL" 2>/dev/null); then
    case "$body" in
      *idle*)
        echo "[deploy-hook] $(stamp) busy gate clear (idle), proceeding."
        break
        ;;
    esac
  fi

  gate_try=$((gate_try + 1))
  if [ "$gate_try" -ge "$GATE_MAX_TRIES" ]; then
    waited_min=$(( gate_try * GATE_INTERVAL / 60 ))
    echo "[deploy-hook] $(stamp) WARNING: busy-gate timed out after ~30m (${waited_min}m); DEPLOYING ANYWAY — a user may have been mid-edit."
    break
  fi

  echo "[deploy-hook] $(stamp) app busy (or gate unreachable), waiting... (try ${gate_try}/${GATE_MAX_TRIES})"
  sleep "$GATE_INTERVAL"
done

# Maintenance announcement: tell the STILL-LIVE container that it is about to be recreated, so the
# browsers and kiosk tablets connected to it show "the server is updating" instead of a bare
# reconnect spinner. Posted after the gate cleared and before the pull, with a short grace period so
# the notice actually reaches the circuits before the process dies.
#
# BEST EFFORT ONLY. An unset key, an unreachable app or a rejected POST is a warning and nothing
# more: a cosmetic notice must never stand between a merged fix and production.
#
# The token comes from the environment and is never stored in this repo. Mint one on /administration
# ("Installation API Keys") and put it in the deploy host's .env as BLOCWERK_DEPLOY_API_KEY.
#
# The URL must be the CURRENT domain: the old blocwerk.zannagh.me host 301-redirects and the
# Authorization header is dropped on the redirect, which would make the key authenticate as nobody.
ANNOUNCE_URL="${ANNOUNCE_URL:-https://blocwerk.app/api/v1/maintenance/announce}"

# How long the notice stays up if the deploy never completes. This MUST be longer than a real
# deploy takes, not merely long enough to be noticed: the notice is what the browser shows while
# the container is gone, and if it expires first the pill drops back to "Session ended" — which
# invites a reload straight into the service worker's offline page — in the middle of the very
# deploy it was raised for. A cold IONOS pull of a fresh image plus the recreate runs into minutes,
# so the default is ten of them.
#
# Over-shooting is close to free: clients reload on the instance id CHANGING, never on the notice
# expiring, and the new container starts with no announcement at all — so a fast deploy clears the
# notice the moment the page reloads, whatever this says. The server clamps to 30 minutes.
ANNOUNCE_ETA="${ANNOUNCE_ETA:-600}"
ANNOUNCE_GRACE="${ANNOUNCE_GRACE:-5}"

# Posts the announcement body given as $1. Bounded, quiet, and it never puts the key on a command
# line that gets echoed or into a URL.
#
# curl first, busybox wget second, and the order is deliberate. Verified by running the real image:
# `docker:27-cli` is Alpine, ships NO curl, and its `wget` is a symlink to busybox 1.37.0. Given an
# explicit Content-Type that busybox suppresses its own `application/x-www-form-urlencoded` default
# and emits exactly one Content-Type, so the wget branch is correct on the image we actually deploy
# with today. It is not correct by contract: busybox only special-cases a Content-Type the caller
# supplied, older builds add theirs regardless, and a duplicated Content-Type is answered 415 by
# ASP.NET — which a best-effort POST would swallow as a warning nobody reads. So where curl exists
# it wins, because curl's explicit -H genuinely REPLACES its default rather than racing it.
announce_maintenance() {
  if command -v curl >/dev/null 2>&1; then
    curl -fsS -o /dev/null --max-time 10 \
      -H "Authorization: Bearer ${BLOCWERK_DEPLOY_API_KEY}" \
      -H "Content-Type: application/json" \
      --data "$1" \
      "$ANNOUNCE_URL" 2>/dev/null
  else
    wget -q -O /dev/null -T 10 \
      --header="Authorization: Bearer ${BLOCWERK_DEPLOY_API_KEY}" \
      --header="Content-Type: application/json" \
      --post-data="$1" \
      "$ANNOUNCE_URL" 2>/dev/null
  fi
}

if [ -z "${BLOCWERK_DEPLOY_API_KEY:-}" ]; then
  echo "[deploy-hook] $(stamp) WARNING: BLOCWERK_DEPLOY_API_KEY is not set; skipping the maintenance announcement."
else
  echo "[deploy-hook] $(stamp) announcing maintenance to ${ANNOUNCE_URL} (eta ${ANNOUNCE_ETA}s)..."
  # As with the gate above, `if cmd; then` keeps set -e from aborting on a non-zero exit.
  if announce_maintenance "{\"message\":\"Blocwerk is updating - one moment.\",\"etaSeconds\":${ANNOUNCE_ETA}}"; then
    echo "[deploy-hook] $(stamp) announcement accepted; waiting ${ANNOUNCE_GRACE}s so clients see it."
    sleep "$ANNOUNCE_GRACE"
  else
    echo "[deploy-hook] $(stamp) WARNING: the maintenance announcement failed (bad key, or app unreachable); deploying anyway."
  fi
fi

echo "[deploy-hook] $(stamp) pulling ${SERVICE} image..."
docker compose -f "$COMPOSE_FILE" pull "$SERVICE"

echo "[deploy-hook] $(stamp) recreating ${SERVICE}..."
docker compose -f "$COMPOSE_FILE" up -d "$SERVICE"

# Drop the now-dangling old image so the disk doesn't fill up over many deploys.
docker image prune -f >/dev/null 2>&1 || true

echo "[deploy-hook] $(stamp) done."
