#!/usr/bin/env bash
# Tests for docker/prod/rollout.sh against a fake box: ROLLOUT_SSH points at a stub that runs the remote
# command locally, with stub docker/crontab/pgrep/flock first on PATH. Nothing real is touched.
#   docker/prod/tests/rollout-test.sh
# Every case ends by asserting that the crontab is unpaused again and the lock is gone.
# SC2016: the stubs and the perl one-liner are single-quoted on purpose.
# shellcheck disable=SC2016
set -uo pipefail
# A signal that was ignored when bash started cannot be trapped, and a background job from a
# non-interactive shell starts with INT ignored: the perl exec below resets INT/TERM/HUP to default.

HERE=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
ROLLOUT=$HERE/../rollout.sh
ENV_EXAMPLE=$HERE/../../prod.env.example
T=$(mktemp -d)
trap 'rm -rf "$T"' EXIT
FAILS=0

setup_box() {
  rm -rf "${T:?}/box" "${T:?}/bin" "${T:?}/logs"
  mkdir -p "$T/box" "$T/bin" "$T/logs"
  touch "$T/box/docker-compose.yml" "$T/box/autodeploy.sh" "$T/box/appsettings.json"
  grep -oE '^[A-Z][A-Za-z0-9_]*=' "$ENV_EXAMPLE" | sed 's/$/x/' >"$T/box/.env"
  printf '%s\n' '# autodeploy' '*/5 * * * * /srv/blocwerk/autodeploy.sh >> /var/log/ad.log 2>&1' \
    '0 3 * * * /srv/blocwerk/autodeploy.sh --prune' >"$T/cron"
  printf '#!/bin/bash\nexec bash -c "$1"\n' >"$T/bin/ssh"
  printf '#!/bin/bash\nexit 0\n' >"$T/bin/flock"
  printf '#!/bin/bash\nexit 1\n' >"$T/bin/pgrep"
  cat >"$T/bin/crontab" <<EOF
#!/bin/bash
if [ "\$1" = -l ]; then cat "$T/cron"; else cat >"$T/cron"; fi
EOF
  cat >"$T/bin/docker" <<EOF
#!/bin/bash
case "\$*" in
  *"pull -q blocwerk"*)
    case \$(cat "$T/pull_mode") in
      unauth) echo "Error response from daemon: unauthorized" >&2; exit 1 ;;
      fail) echo "network down" >&2; exit 1 ;;
    esac ;;
  *"image inspect"*) echo oldrev ;;
  "compose -f - "*|"compose config"*) cat >/dev/null 2>&1 || true; echo cfg ;;
  "compose ps -q"*) echo cid ;;
  *"curlimages/curl"*) echo 200 ;;
  *"pg_dump"*) echo dump ;;
esac
exit 0
EOF
  chmod +x "$T"/bin/*
}

# run_case <name> <pull_mode> <expected rc> <signal|-> [ENV=val ...]
run_case() {
  local name=$1 mode=$2 want=$3 sig=$4 pid rc i
  shift 4
  setup_box
  echo "$mode" >"$T/pull_mode"
  env PATH="$T/bin:$PATH" ROLLOUT_SSH="$T/bin/ssh" ROLLOUT_DIR="$T/box" ROLLOUT_LOG_DIR="$T/logs" \
    ROLLOUT_POLL_SEC=30 "$@" perl -e '$SIG{$_} = "DEFAULT" for qw(INT TERM HUP); exec @ARGV' "$ROLLOUT" --apply --sha abc123 >"$T/out" 2>&1 &
  pid=$!
  if [ "$sig" != - ]; then
    for ((i = 0; i < 100; i++)); do grep -q 'waiting$' "$T/out" 2>/dev/null && break; sleep 0.2; done
    kill "-$sig" "$pid"
  fi
  wait "$pid"
  rc=$?
  check "$name: reached the pause" "grep -q 'autodeploy cron paused' '$T/out'"
  check "$name: exit code $want" "[ $rc = $want ]"
  check "$name: crontab unpaused" "! grep -q 'PAUSED-ROLLOUT' '$T/cron' && [ \$(grep -c '^[^#].*autodeploy.sh' '$T/cron') = 2 ]"
  check "$name: lock released" "[ ! -d '$T/box/.rollout.lock' ]"
  check "$name: cleanup ran exactly once" "[ \$(grep -c 'trap: re-enabling' '$T/out') = 1 ]"
  check "$name: no stray sleep" "! pgrep -f 'sleep 30' >/dev/null 2>&1"
}

check() {
  if eval "$2"; then echo "ok   $1"; else echo "FAIL $1"; sed 's/^/     | /' "$T/out" | tail -15; FAILS=$((FAILS + 1)); fi
}

run_case "SIGINT during image wait" fail 130 INT
run_case "SIGTERM during image wait" fail 143 TERM
run_case "SIGHUP during image wait" fail 129 HUP
run_case "unauthorized pull fails fast" unauth 1 - ROLLOUT_POLL_SEC=1 ROLLOUT_AUTH_FAILS=3
check "unauthorized: points to the README section" "grep -q 'Renew the GHCR token' '$T/out'"
run_case "overall timeout" fail 1 - ROLLOUT_POLL_SEC=1 ROLLOUT_WAIT_SEC=3
check "timeout: says so" "grep -q 'did not appear on GHCR' '$T/out'"

if [ "$FAILS" = 0 ]; then echo "all rollout tests passed"; else echo "$FAILS check(s) failed"; exit 1; fi
