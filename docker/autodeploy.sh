#!/usr/bin/env bash
# Blocwerk auto-deploy. Replaces the webhook receiver used on the previous host: a public
# server should not expose an inbound endpoint that drives the docker socket. This polls
# GHCR instead, so the only traffic is outbound.
#
# THIS is the script production actually runs. It lives on the IONOS host at
# /home/patrickweindl/blocwerk/autodeploy.sh and is driven by the deploying user's crontab,
# once a minute plus once at boot. The copy here is the source of truth for review; edits made
# here are not live until they are copied to the host. deploy-hook/ next to this file is the
# OLD webhook path from the previous host and is not used in production.
#
# Pulling when nothing changed is cheap (a manifest check, no layers), so the cost of
# running this every minute is negligible.
set -euo pipefail

DIR=/home/patrickweindl/blocwerk
IMAGE=ghcr.io/zannagh/blocwerk:latest
SERVICE=blocwerk
# How many consecutive busy checks to tolerate before deploying regardless. The app reports
# busy while someone is mid-edit (creating a boulder, editing a wall); at one poll a minute
# this defers a redeploy for at most half an hour.
MAX_DEFER=30
STATE=$DIR/.autodeploy-deferrals
# The image a deferral is waiting on, so a pending target is logged once rather than
# once a minute while the busy gate holds it back.
TARGET=$DIR/.autodeploy-target
# How long the "updating" notice survives if this deploy never completes, and how long to pause
# after announcing so it actually reaches the circuits. Set the ETA LONGER than a deploy takes:
# it is the lifetime of the notice someone is staring at while the container is gone. Overshooting
# is free - clients reload when the instance id CHANGES, not when the notice expires.
ANNOUNCE_ETA=600
ANNOUNCE_GRACE=5

cd "$DIR"

login() {
  local user token
  # Read directly rather than sourcing: .env contains values with shell-special characters.
  user=$(grep -m1 '^GHCR_USER=' .env | cut -d= -f2-)
  token=$(grep -m1 '^GHCR_TOKEN=' .env | cut -d= -f2-)
  printf '%s' "$token" | docker login ghcr.io -u "$user" --password-stdin >/dev/null 2>&1
}

before=$(docker image inspect --format '{{.Id}}' "$IMAGE" 2>/dev/null || echo none)

# Pull, logging in and retrying once if the registry rejects us (token rotated, creds expired).
if ! docker compose pull -q "$SERVICE" >/dev/null 2>&1; then
  login
  if ! docker compose pull -q "$SERVICE" >/dev/null 2>&1; then
    echo "pull failed even after re-authenticating; leaving the running version alone"
    exit 1
  fi
fi

# Compare what the container is RUNNING against what we now hold locally, rather than asking
# "did this pull change anything". The old check did the latter, which made a deferral
# permanent: once the new image was on disk every later pull changed nothing and the script
# exited here, before ever reaching the busy gate below - so a deferred deploy never retried
# and MAX_DEFER could never fire. $before survives only for the pull-failure message above.
latest=$(docker image inspect --format '{{.Id}}' "$IMAGE" 2>/dev/null || echo none)
running=$(docker inspect --format '{{.Image}}' "$(docker compose ps -q "$SERVICE" 2>/dev/null)" 2>/dev/null || echo none)

if [ "$latest" = "none" ] || [ "$running" = "$latest" ]; then
  rm -f "$STATE" "$TARGET"
  exit 0
fi

last_target=$(cat "$TARGET" 2>/dev/null || echo none)
if [ "$last_target" != "$latest" ]; then
  echo "new image $latest (was $running)"
  printf "%s" "$latest" > "$TARGET"
  rm -f "$STATE"
fi

# Busy gate: avoid recreating the container out from under someone mid-edit. Reached over the
# shared edge network because the app publishes no host port.
ready=$(docker run --rm --network edge curlimages/curl:latest -s --max-time 5 \
          http://blocwerk:5050/health/ready-to-deploy 2>/dev/null || echo unreachable)
deferrals=$(cat "$STATE" 2>/dev/null || echo 0)

if [ "$ready" = "busy" ] && [ "$deferrals" -lt "$MAX_DEFER" ]; then
  echo $((deferrals + 1)) > "$STATE"
  echo "app busy, deferring ($((deferrals + 1))/$MAX_DEFER)"
  exit 0
fi

rm -f "$STATE" "$TARGET"

# Tell the still-live container it is about to be recreated, so connected browsers and kiosk
# tablets show "Blocwerk is updating" instead of a bare reconnect spinner, and reload themselves
# once the new process answers /alive with a different instance id.
#
# Posted over the internal edge network for the same reason the busy gate is: the app publishes no
# host port, and going out via the public name risks a redirect that would strip the bearer token.
# Read from .env by grep, not by sourcing it, exactly like the GHCR credentials above.
#
# Best effort by design. An announcement that cannot be delivered must never hold up a deploy, so
# every failure here is a log line and nothing more.
announce() {
  local key
  key=$(grep -m1 '^BLOCWERK_DEPLOY_API_KEY=' .env | cut -d= -f2-)
  if [ -z "$key" ]; then
    echo "no BLOCWERK_DEPLOY_API_KEY in .env; deploying without announcing"
    return 0
  fi

  if docker run --rm --network edge curlimages/curl:latest -s -f --max-time 5 \
       -X POST \
       -H "Authorization: Bearer $key" \
       -H "Content-Type: application/json" \
       -d "{\"etaSeconds\":$ANNOUNCE_ETA}" \
       http://blocwerk:5050/api/v1/maintenance/announce >/dev/null 2>&1; then
    echo "announced; giving clients ${ANNOUNCE_GRACE}s to notice"
    sleep "$ANNOUNCE_GRACE"
  else
    echo "announce failed (endpoint missing, key not installation-scoped, or app unreachable); deploying anyway"
  fi
}

announce

echo "deploying..."
docker compose up -d "$SERVICE"
echo "done."
