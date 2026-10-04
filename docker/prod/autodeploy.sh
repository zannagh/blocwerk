#!/usr/bin/env bash
# Blocwerk auto-deploy. Replaces the webhook receiver used on the previous host: a public
# server should not expose an inbound endpoint that drives the docker socket. This polls
# GHCR instead, so the only traffic is outbound.
#
# THIS is the script production actually runs. It lives on the production host at
# /home/patrickweindl/blocwerk/autodeploy.sh and is driven by the deploying user's crontab,
# once a minute plus once at boot. The copy here is the source of truth for review; edits made
# here are not live until they are copied to the host (README.md next to this file).
# docker/deploy-hook/ is the OLD webhook path from the previous host and is not used in production.
#
# Two parts, run in this order every minute:
#  1. the app (blocwerk): pulled and recreated behind the app's busy gate and a maintenance notice.
#  2. the compute services (wall-geometry, splat-cpu), from an EXIT trap after part 1: pulled and
#     recreated once they have no job queued or running. A slow pass never delays part 1.
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

# ---------- the compute services (run AFTER the app, from the EXIT trap below) ----------
#
# wall-geometry and splat-cpu keep their job queue IN MEMORY: recreating one mid-job loses the job
# (the app then reports the capture step as failed). The app's /health/ready-to-deploy does not
# cover them (it gates on edits and uploads only), so each service is asked directly: the
# authenticated GET /v1/info returns jobs {queued, running}. The probe runs INSIDE the container
# via `docker compose exec`, using the container's own COMPUTE_API_KEY and PORT, so the key is never
# read from .env, never on a command line and never leaves the container.
#
# Rule: recreate only after two consecutive idle polls (about a minute apart), so the app has
# fetched the result of a job that just finished before its files vanish with the container. A
# failed probe ("unknown") counts as busy, unless docker also reports the container unhealthy: then
# nothing useful is running and it deploys. A service that reports busy is never overridden by
# its health status.
#
# Cap: after COMPUTE_MAX_DEFER busy polls since the new image appeared it deploys regardless, so a
# service that is never idle cannot pin an old image forever. /v1/info reports job COUNTS only, no
# start times, so this is not a per-job age: when it fires it can cut off a job that started a
# moment earlier. It only fires after 240 busy minutes (the splat worker's own per-job timeout,
# SPLAT_TIMEOUT_S), i.e. when jobs have run back to back for four hours; the app then reports that
# one step as failed and it can be retried.
#
# A service that is not running (profile off, or stopped by hand) is left alone, never started.
# Everything here runs with `set -e` off (it is called on the left of `||`), so every step checks
# its own result, and every docker call that can hang is bounded by `timeout`.
COMPUTE_SERVICES="wall-geometry splat-cpu"
COMPUTE_MAX_DEFER=240
EXEC_TIMEOUT=20s
PULL_TIMEOUT=15m
UP_TIMEOUT=5m

# Log a line only when it differs from the last one logged under the same key, so a condition that
# persists (registry down, lock busy) costs one line instead of one per minute. log_clear forgets it.
log_change() {
  local file=$DIR/.autodeploy-log-$1
  if [ "$(cat "$file" 2>/dev/null)" != "$2" ]; then
    echo "$2"
    printf '%s' "$2" > "$file"
  fi
}
log_clear() { rm -f "$DIR/.autodeploy-log-$1"; }

compute_idle() {
  timeout "$EXEC_TIMEOUT" docker compose exec -T "$1" python -c '
import json, os, urllib.request as u
req = u.Request("http://127.0.0.1:%s/v1/info" % os.environ.get("PORT", "8000"),
                headers={"Authorization": "Bearer " + os.environ.get("COMPUTE_API_KEY", "")})
jobs = json.load(u.urlopen(req, timeout=5))["jobs"]
print("idle" if jobs["queued"] + jobs["running"] == 0 else "busy")' 2>/dev/null || echo unknown
}

compute_pull() {
  timeout "$PULL_TIMEOUT" docker compose pull -q "$1" >/dev/null 2>&1
}

update_compute() {
  local svc=$1 cid image latest running health state deferrals
  local defer_file=$DIR/.autodeploy-$svc-deferrals target_file=$DIR/.autodeploy-$svc-target idle_file=$DIR/.autodeploy-$svc-idle

  cid=$(timeout "$EXEC_TIMEOUT" docker compose ps -q "$svc" 2>/dev/null) || return 0
  [ -n "$cid" ] || return 0
  image=$(docker inspect --format '{{.Config.Image}}' "$cid" 2>/dev/null) || return 0

  if ! compute_pull "$svc"; then
    login || true
    if ! compute_pull "$svc"; then
      log_change "$svc-pull" "$svc: pull failed even after re-authenticating; leaving it alone (logged once until it changes)"
      return 0
    fi
  fi
  if [ -f "$DIR/.autodeploy-log-$svc-pull" ]; then
    echo "$svc: pull works again"
    log_clear "$svc-pull"
  fi

  latest=$(docker image inspect --format '{{.Id}}' "$image" 2>/dev/null || echo none)
  running=$(docker inspect --format '{{.Image}}' "$cid" 2>/dev/null || echo none)
  if [ "$latest" = "none" ] || [ "$running" = "$latest" ]; then
    rm -f "$defer_file" "$target_file" "$idle_file"
    return 0
  fi

  if [ "$(cat "$target_file" 2>/dev/null || echo none)" != "$latest" ]; then
    echo "$svc: new image $latest (was $running)"
    printf "%s" "$latest" > "$target_file"
    rm -f "$defer_file" "$idle_file"
  fi

  health=$(docker inspect --format '{{if .State.Health}}{{.State.Health.Status}}{{end}}' "$cid" 2>/dev/null || true)
  state=$(compute_idle "$svc")
  [ -n "$state" ] || state=unknown
  deferrals=$(cat "$defer_file" 2>/dev/null || true)
  case "$deferrals" in ''|*[!0-9]*) deferrals=0 ;; esac

  local deploy=no
  if [ "$state" = "idle" ]; then
    if [ -f "$idle_file" ]; then
      deploy=yes
    else
      touch "$idle_file"
      echo "$svc: idle, deploying on the next poll if it stays idle"
      return 0
    fi
  elif [ "$state" = "unknown" ] && [ "$health" = "unhealthy" ]; then
    deploy=yes
  elif [ "$deferrals" -ge "$COMPUTE_MAX_DEFER" ]; then
    echo "$svc: still $state after $deferrals busy polls; deploying anyway (cap)"
    deploy=yes
  fi

  if [ "$deploy" = "no" ]; then
    rm -f "$idle_file"
    echo $((deferrals + 1)) > "$defer_file"
    echo "$svc: $state, deferring ($((deferrals + 1))/$COMPUTE_MAX_DEFER)"
    return 0
  fi

  rm -f "$defer_file" "$target_file" "$idle_file"
  echo "$svc: deploying ($state, health ${health:-n/a})..."
  if ! timeout "$UP_TIMEOUT" docker compose up -d --no-deps "$svc"; then
    echo "$svc: recreate failed or timed out; will retry next minute"
    return 0
  fi
  echo "$svc: done."
}

# One compute pass at a time: a pull may take minutes, longer than the cron interval. Running after
# the app part means a slow compute pass never delays an app deploy, and the next minute's run does
# its app part and then simply skips compute while this pass still holds the lock.
run_compute() {
  if ! command -v flock >/dev/null 2>&1; then
    log_change compute-lock "flock not found; skipping the compute services (logged once)"
    return 0
  fi
  if ! { exec 9>"$DIR/.autodeploy-compute.lock"; } 2>/dev/null || ! flock -n 9; then
    log_change compute-lock "compute pass still running (or lock unavailable); skipping this minute (logged once)"
    return 0
  fi
  log_clear compute-lock
  local svc
  for svc in $COMPUTE_SERVICES; do
    update_compute "$svc" || echo "$svc: update check failed; will retry next minute"
  done
  exec 9>&-
}

# ---------- pg_stat_statements reset (run from the EXIT trap, before the compute pass) ----------
#
# The per-statement timings are only useful while they are recent, and the diagnostics keep two days
# (docker/prod/README.md, "Diagnostics"): reset them once the last reset is 48 h old. The time of the
# last reset is the epoch in $PGSTATS_STATE; a missing file counts as "never reset". A failure
# (postgres down, extension not created yet) is logged once and retried next minute; it never
# affects a deploy.
PGSTATS_STATE=$DIR/.autodeploy-pgstats-reset
PGSTATS_MAX_AGE=172800

reset_db_stats() {
  local now last
  now=$(date +%s)
  last=$(cat "$PGSTATS_STATE" 2>/dev/null || true)
  case "$last" in ''|*[!0-9]*) last=0 ;; esac
  [ $((now - last)) -ge "$PGSTATS_MAX_AGE" ] || return 0
  if timeout "$EXEC_TIMEOUT" docker compose exec -T postgres \
       psql -U postgres -d blocwerk -qAt -c "select pg_stat_statements_reset()" >/dev/null 2>&1; then
    printf '%s' "$now" > "$PGSTATS_STATE"
    echo "pg_stat_statements reset (every $((PGSTATS_MAX_AGE / 3600)) h)"
    log_clear pgstats
  else
    log_change pgstats "pg_stat_statements reset failed (postgres down or the extension not created yet); retrying every minute (logged once)"
  fi
}

# The app part below ends with `exit` on every path (nothing to do, deferred, deployed, failed).
# The EXIT trap runs the stats reset and then the compute part after it, whatever the outcome; the
# script's exit status stays the app part's.
trap 'reset_db_stats || true; run_compute || true' EXIT

# ---------- the app (unchanged) ----------

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
