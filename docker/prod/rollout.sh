#!/usr/bin/env bash
# Guarded, scripted prod rollout: the manual runbook as one script, run from the OPERATOR's machine
# (it drives the box over ssh). See "Scripted rollout" in docker/prod/README.md.
#
#   rollout.sh [--dry-run]                    default: checks preconditions, prints every step, changes nothing
#   rollout.sh --apply [flags]                do the rollout
#   rollout.sh --resume-only                  re-enable the autodeploy cron and stop
#   rollout.sh --rollback <backup-ts> [--apply [--restore-db]]
#
# Flags: --sha <git-sha>      wait until the app image on GHCR was built from this commit
#        --skip-wait          do not wait for the image (use the :latest that is there)
#        --compute            also pull and recreate wall-geometry and splat-cpu
#        --images-tar         also tar wall-images/ into the backup (can be large)
#        --keep-paused        on failure, leave autodeploy paused (default: the trap re-enables it)
#        --restore-db         with --rollback --apply: also pg_restore the dump (typed confirmation)
# Env:   ROLLOUT_HOST (ssh alias, default ionos), ROLLOUT_DIR (deploy dir on the box),
#        ROLLOUT_SSH (full ssh command prefix, replaces ssh; used by the tests), ROLLOUT_LOG_DIR,
#        ROLLOUT_MIN_FREE_GB (default 5), ROLLOUT_WAIT_MIN (image wait, default 30).
# Secrets: .env is never read into this process or printed; only its key NAMES are listed.
# SC2016: the remote commands are single-quoted on purpose, the BOX expands them.
# shellcheck disable=SC2016
set -euo pipefail

HOST=${ROLLOUT_HOST:-ionos}
DIR=${ROLLOUT_DIR:-/home/patrickweindl/blocwerk}
SSH_CMD=${ROLLOUT_SSH:-"ssh -o BatchMode=yes -o ConnectTimeout=15 $HOST"}
MIN_FREE_GB=${ROLLOUT_MIN_FREE_GB:-5}
WAIT_MIN=${ROLLOUT_WAIT_MIN:-30}
HERE=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
REPO=$(cd "$HERE/../.." && pwd)
COMPOSE_NEW=$REPO/docker/docker-compose.prod.yml
AUTODEPLOY_NEW=$HERE/autodeploy.sh
ENV_EXAMPLE=$REPO/docker/prod.env.example
MIGRATIONS=$REPO/src/Blocwerk.Core/Migrations
# .env keys in the example that the box does not need (retired webhook deploy).
OPTIONAL_KEYS=" WEBHOOK_SECRET COMPOSE_DIR "
MARK='#PAUSED-ROLLOUT# '

MODE=dry APPLY=0 SHA="" SKIP_WAIT=0 COMPUTE=0 IMAGES_TAR=0 KEEP_PAUSED=0 RESTORE_DB=0 ROLLBACK_TS=""
TS=$(date -u +%Y%m%dT%H%M%SZ)
LOG_DIR=${ROLLOUT_LOG_DIR:-${XDG_STATE_HOME:-$HOME/.local/state}/blocwerk-rollout}
LOG=$LOG_DIR/rollout-$TS.log
mkdir -p "$LOG_DIR"
read -r -a SSH <<<"$SSH_CMD"
PAUSED_BY_US=0 LOCKED=0 BACKUP_DIR=""

log() { printf '%s %s\n' "$(date -u +%H:%M:%SZ)" "$*" | tee -a "$LOG"; }
die() { log "ERROR: $*"; exit 1; }
# Run a command string on the box, in the deploy dir. stdin is passed through.
rr() { "${SSH[@]}" "cd '$DIR' && $1"; }

usage() { sed -n '2,/^set -euo/p' "$0" | sed '$d;s/^# \{0,1\}//'; exit "${1:-0}"; }

while [ $# -gt 0 ]; do
  case $1 in
    --dry-run) MODE=dry ;;
    --apply) APPLY=1 ;;
    --resume-only) MODE=resume ;;
    --rollback) MODE=rollback; ROLLBACK_TS=${2:?--rollback needs a backup timestamp}; shift ;;
    --sha) SHA=${2:?--sha needs a git sha}; shift ;;
    --skip-wait) SKIP_WAIT=1 ;;
    --compute) COMPUTE=1 ;;
    --images-tar) IMAGES_TAR=1 ;;
    --keep-paused) KEEP_PAUSED=1 ;;
    --restore-db) RESTORE_DB=1 ;;
    -h|--help) usage 0 ;;
    *) echo "unknown argument: $1" >&2; usage 2 ;;
  esac
  shift
done
# --apply turns the default dry run into a rollout, and makes a --rollback real.
if [ "$MODE" = dry ] && [ "$APPLY" = 1 ]; then MODE=apply; fi
if [ "$MODE" = apply ] && [ -z "$SHA" ] && [ "$SKIP_WAIT" = 0 ]; then
  die "--apply needs --sha <git-sha> (wait for that CI build) or --skip-wait"
fi
[ "$RESTORE_DB" = 0 ] || [ "$MODE" = rollback ] || die "--restore-db only makes sense with --rollback"
if [ "$MODE" = dry ] || { [ "$MODE" = rollback ] && [ "$APPLY" = 0 ]; }; then
  log "DRY RUN: nothing on the box is changed"
fi

# ---------- crontab pause / resume (idempotent, marker based) ----------

# Rewrites the crontab through awk. The result is built first and installed only if non-empty, so a
# failing filter can never replace the crontab with nothing.
cron_edit() {
  rr "new=\$(crontab -l | awk -v m='$MARK' '$1') && [ -n \"\$new\" ] && printf '%s\n' \"\$new\" | crontab -"
}

cron_pause() {
  rr "crontab -l 2>/dev/null | grep -c 'autodeploy.sh' >/dev/null" || die "no autodeploy line in the crontab"
  PAUSED_BY_US=1
  cron_edit '/autodeploy\.sh/ && !/^#/ {print m $0; next} {print}'
  local n
  n=$(rr "crontab -l | grep -c '^$MARK'" || true)
  [ "${n:-0}" -ge 1 ] || die "pausing autodeploy did not take effect"
  log "autodeploy cron paused ($n line(s) commented with marker)"
  # a run that started before the pause may still be deploying: let it finish
  rr 'for i in $(seq 1 60); do pgrep -f "[a]utodeploy.sh" >/dev/null || exit 0; sleep 2; done; exit 1' \
    || die "an autodeploy run is still active after 120s"
}

cron_resume() {
  cron_edit 'index($0, m) == 1 {print substr($0, length(m) + 1); next} {print}'
  PAUSED_BY_US=0
  log "autodeploy cron resumed ($(rr "crontab -l | grep -c 'autodeploy.sh'" || true) line(s), $(rr "crontab -l | grep -c '^$MARK'" || true) still marked)"
}

# ---------- lock, log shipping, trap ----------

lock() {
  rr "mkdir .rollout.lock 2>/dev/null && echo \"$TS \$(id -un)\" > .rollout.lock/owner" \
    || die "lock held on the box (.rollout.lock: $(rr 'cat .rollout.lock/owner' 2>/dev/null || echo '?')); remove it only if no rollout runs"
  LOCKED=1
}

ship_log() {
  [ -n "$BACKUP_DIR" ] || return 0
  rr "cat > '$BACKUP_DIR/rollout.log' && chmod 600 '$BACKUP_DIR/rollout.log'" <"$LOG" 2>/dev/null || true
}

on_exit() {
  local rc=$?
  trap - EXIT
  if [ "$rc" -ne 0 ]; then
    log "FAILED (exit $rc)"
    [ -z "$BACKUP_DIR" ] || log "backup is in $BACKUP_DIR; undo with: rollout.sh --rollback ${BACKUP_DIR#backups/} --apply"
    if [ "$PAUSED_BY_US" = 1 ] && [ "$KEEP_PAUSED" = 0 ] && [ "$MODE" = apply ]; then
      log "trap: re-enabling autodeploy"
      cron_resume || log "trap: COULD NOT re-enable autodeploy, run --resume-only"
    elif [ "$PAUSED_BY_US" = 1 ]; then
      log "autodeploy stays PAUSED; run --resume-only when ready"
    fi
  fi
  [ "$LOCKED" = 0 ] || rr 'rm -rf .rollout.lock' || true
  ship_log
  log "log: $LOG${BACKUP_DIR:+ and $HOST:$DIR/$BACKUP_DIR/rollout.log}"
  exit "$rc"
}
trap on_exit EXIT

# ---------- preflight ----------

latest_repo_migration() {
  find "$MIGRATIONS" -name '[0-9]*_*.cs' ! -name '*.Designer.cs' | sed 's#.*/##;s/\.cs$//' | sort | tail -1
}

check_env_keys() {
  local want have k missing=0
  want=$(grep -oE '^[A-Z][A-Za-z0-9_]*=' "$ENV_EXAMPLE" | tr -d =)
  have=$(rr "grep -oE '^[A-Za-z_][A-Za-z0-9_]*=' .env" | tr -d =)
  for k in $want; do
    if ! grep -qx "$k" <<<"$have"; then
      case $OPTIONAL_KEYS in *" $k "*) log "  .env: $k absent (optional, retired)"; continue ;; esac
      # a key the compose file reads with a default (${K:-x}) or not at all is not required
      if ! grep -qE "\\$\{$k(:\\?|\\})" "$COMPOSE_NEW"; then
        log "  .env: $k absent (compose default applies or unused; fine)"
        continue
      fi
      log "  .env: MISSING key $k"
      missing=1
    fi
  done
  [ "$missing" = 0 ] || die ".env lacks required keys (names above; add them by hand)"
  log "  .env has every required key from prod.env.example ($(wc -w <<<"$want" | tr -d " ") checked, names only)"
  if rr "grep -q '^COMPOSE_PROFILES=.*compute' .env"; then
    log "  COMPOSE_PROFILES includes compute"
  else
    log "  WARNING: COMPOSE_PROFILES lacks compute; compute services are not started"
  fi
}

check_disk() {
  local avail_kb need_kb=$((MIN_FREE_GB * 1024 * 1024)) tar_kb=0
  avail_kb=$(rr "df -Pk . | awk 'NR==2{print \$4}'")
  if [ "$IMAGES_TAR" = 1 ]; then tar_kb=$(rr "du -sk wall-images | cut -f1"); fi
  [ "$avail_kb" -ge $((need_kb + tar_kb)) ] || die "free disk $((avail_kb / 1024)) MB below $((need_kb / 1024)) MB (+ $((tar_kb / 1024)) MB images tar)"
  log "  disk: $((avail_kb / 1024 / 1024)) GB free (need $MIN_FREE_GB GB + $((tar_kb / 1024)) MB tar)"
}

# Old vs new compose, rendered WITHOUT interpolation so no secret value can appear.
compose_diff() {
  local old new
  if ! old=$(rr 'docker compose config --no-interpolate' 2>/dev/null) \
    || ! new=$(rr 'docker compose -f - config --no-interpolate' <"$COMPOSE_NEW" 2>/dev/null); then
    old=$(rr 'cat docker-compose.yml')
    new=$(cat "$COMPOSE_NEW")
    log "  (rendering unavailable, diffing the raw files)"
  fi
  if diff -u <(echo "$old") <(echo "$new") >"$LOG_DIR/compose.diff"; then
    log "  compose: no differences"
  else
    log "  compose diff (old -> new, $(grep -c '^[+-][^+-]' "$LOG_DIR/compose.diff") changed lines, first 60):"
    sed -n '3,62p' "$LOG_DIR/compose.diff" | sed 's/^/    /' | tee -a "$LOG"
  fi
}

preflight() {
  log "== preflight"
  rr 'echo "  ssh ok: $(hostname)"' | tee -a "$LOG"
  rr 'docker compose version >/dev/null && command -v flock >/dev/null' || die "docker compose or flock missing on the box"
  check_disk
  check_env_keys
  # validated from stdin so a dry run stages no file on the box
  rr 'docker compose -f - config -q' <"$COMPOSE_NEW" || die "new compose file fails 'docker compose config -q' with the box's .env"
  log "  new compose renders with the box's .env (config -q silent)"
  compose_diff
  if rr 'cmp -s autodeploy.sh -' <"$AUTODEPLOY_NEW"; then log "  autodeploy.sh: unchanged"; else log "  autodeploy.sh: differs, will be installed"; fi
  log "  app image revision now: $(rr 'docker inspect --format "{{index .Config.Labels \"org.opencontainers.image.revision\"}}" "$(docker compose ps -q blocwerk)"' || echo unknown)"
  log "  repo latest migration: $(latest_repo_migration); db latest: $(db_latest_migration)"
  log "  app health now: $(app_code /health) (/health/ready-to-deploy: $(app_code /health/ready-to-deploy))"
  if [ -n "$SHA" ] && [ "$(git -C "$REPO" rev-parse HEAD)" != "$(git -C "$REPO" rev-parse "$SHA" 2>/dev/null || echo x)" ]; then
    log "  WARNING: this checkout (HEAD) is not $SHA; compose/migration comparisons use the checkout"
  fi
}

app_code() {
  rr "docker run --rm --network edge curlimages/curl:latest -s -o /dev/null -w '%{http_code}' --max-time 5 http://blocwerk:5050$1 2>/dev/null || true"
}

db_latest_migration() {
  rr "docker compose exec -T postgres psql -U postgres -d blocwerk -Atc 'select \"MigrationId\" from \"__EFMigrationsHistory\" order by 1 desc limit 1'" 2>/dev/null || echo unknown
}

# ---------- apply steps ----------

backup() {
  BACKUP_DIR=backups/$TS
  log "== backup to $BACKUP_DIR"
  rr "umask 077 && mkdir -p '$BACKUP_DIR'"
  rr "docker compose exec -T postgres pg_dump -U postgres -Fc blocwerk > '$BACKUP_DIR/blocwerk.dump'"
  rr "test -s '$BACKUP_DIR/blocwerk.dump' && docker compose exec -T postgres pg_restore -l < '$BACKUP_DIR/blocwerk.dump' >/dev/null" \
    || die "database dump is empty or unreadable"
  rr "cp docker-compose.yml '$BACKUP_DIR/docker-compose.yml' && cp .env '$BACKUP_DIR/env' && cp autodeploy.sh '$BACKUP_DIR/autodeploy.sh' && cp appsettings.json '$BACKUP_DIR/appsettings.json' && crontab -l | sed 's/^$MARK//' > '$BACKUP_DIR/crontab' && chmod 600 '$BACKUP_DIR'/*"
  rr "docker compose images -q 2>/dev/null >'$BACKUP_DIR/images.txt'; docker compose ps --format '{{.Service}} {{.Image}}' >>'$BACKUP_DIR/images.txt'; chmod 600 '$BACKUP_DIR/images.txt'" || true
  if [ "$IMAGES_TAR" = 1 ]; then
    rr "tar -czf '$BACKUP_DIR/wall-images.tar.gz' wall-images && chmod 600 '$BACKUP_DIR/wall-images.tar.gz'"
  fi
  log "  backup: $(rr "ls -1 '$BACKUP_DIR' | tr '\n' ' '")"
}

wait_for_image() {
  [ "$SKIP_WAIT" = 0 ] || { log "== image wait skipped (--skip-wait)"; return 0; }
  log "== waiting for the app image built from $SHA (up to $WAIT_MIN min)"
  local i rev
  for ((i = 0; i < WAIT_MIN * 2; i++)); do
    rr 'docker compose pull -q blocwerk >/dev/null 2>&1' || log "  pull failed (GHCR token expired? see README); retrying"
    rev=$(rr 'docker image inspect --format "{{index .Config.Labels \"org.opencontainers.image.revision\"}}" ghcr.io/zannagh/blocwerk:latest' 2>/dev/null || true)
    if [ -n "$rev" ] && { [ "$rev" = "$SHA" ] || [[ $rev == "$SHA"* ]] || [[ $SHA == "$rev"* ]]; }; then
      log "  image revision $rev matches"
      return 0
    fi
    log "  latest is built from '${rev:-unknown}', waiting"
    sleep 30
  done
  die "image for $SHA did not appear on GHCR within $WAIT_MIN min"
}

install_files() {
  log "== install compose and autodeploy.sh (staged as .new, validated, atomic mv)"
  rr 'cat > docker-compose.yml.new' <"$COMPOSE_NEW"
  rr 'cat > autodeploy.sh.new && chmod +x autodeploy.sh.new' <"$AUTODEPLOY_NEW"
  rr 'docker compose -f docker-compose.yml.new config -q' || die "staged compose invalid"
  rr 'mv docker-compose.yml.new docker-compose.yml && mv autodeploy.sh.new autodeploy.sh'
}

services() { if [ "$COMPUTE" = 1 ]; then echo "blocwerk wall-geometry splat-cpu"; else echo blocwerk; fi; }

deploy() {
  log "== pull and up -d: $(services)"
  rr "docker compose pull -q $(services)"
  rr "docker compose up -d $(services)"
}

verify() {
  log "== health and migrations"
  local i code want got
  for ((i = 0; i < 60; i++)); do
    code=$(app_code /health)
    [ "$code" = 200 ] && break
    sleep 3
  done
  [ "$code" = 200 ] || die "app did not answer 200 on /health within 180s (last: ${code:-none})"
  log "  /health: 200"
  want=$(latest_repo_migration)
  got=$(db_latest_migration)
  [ "$want" = "$got" ] || die "latest migration mismatch: repo $want, database $got"
  log "  latest migration applied: $got"
}

do_apply() {
  preflight
  log "== lock, pause"
  lock
  cron_pause
  backup
  wait_for_image
  install_files
  deploy
  verify
  cron_resume
  log "rollout done. Post-steps (UI checks, flags) are yours; backup: $BACKUP_DIR"
}

# ---------- rollback ----------

do_rollback() {
  local b=backups/$ROLLBACK_TS
  log "== rollback plan from $b"
  rr "test -d '$b' && test -f '$b/docker-compose.yml' && test -f '$b/env'" || die "no complete backup $b on the box"
  log "  restore: docker-compose.yml, .env, autodeploy.sh, appsettings.json, then up -d"
  log "  NOTE: images are :latest, so this restores the files, not the old image; autodeploy stays PAUSED"
  [ "$RESTORE_DB" = 0 ] || log "  restore the database from $b/blocwerk.dump (DESTRUCTIVE, needs typed confirmation)"
  if [ "$APPLY" = 0 ]; then log "dry run: add --apply to execute"; return 0; fi
  if [ "$RESTORE_DB" = 1 ]; then
    rr "test -s '$b/blocwerk.dump'" || die "no dump in $b"
    printf 'Type "restore-db %s" to DROP and restore the database: ' "$ROLLBACK_TS" >&2
    read -r answer
    [ "$answer" = "restore-db $ROLLBACK_TS" ] || die "confirmation did not match"
  fi
  lock
  cron_pause
  BACKUP_DIR=$b
  rr "cp '$b/docker-compose.yml' docker-compose.yml && cp '$b/env' .env && chmod 600 .env && cp '$b/appsettings.json' appsettings.json && cp '$b/autodeploy.sh' autodeploy.sh.new && chmod +x autodeploy.sh.new && mv autodeploy.sh.new autodeploy.sh"
  if [ "$RESTORE_DB" = 1 ]; then
    rr "docker compose stop blocwerk"
    rr "docker compose exec -T postgres pg_restore -U postgres -d blocwerk --clean --if-exists --no-owner < '$b/blocwerk.dump'"
  fi
  rr "docker compose up -d $(services)"
  log "rollback applied; autodeploy is still paused (it would redeploy :latest). Run --resume-only when ready."
}

case $MODE in
  dry) preflight; log "dry run complete: would pause cron, back up, $([ "$SKIP_WAIT" = 1 ] && echo 'skip the image wait' || echo "wait for $SHA"), install, up -d $(services), verify, resume" ;;
  apply) do_apply ;;
  resume) lock; cron_resume ;;
  rollback) do_rollback ;;
esac
