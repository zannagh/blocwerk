#!/usr/bin/env bash
# Dry-run harness for autodeploy.sh: runs the real script against a fake `docker` on PATH and a
# throwaway AUTODEPLOY_DIR, and checks what it decided. Nothing touches docker, the network or the
# host. Run: bash docker/prod/test-autodeploy.sh
set -euo pipefail

HERE=$(cd "$(dirname "$0")" && pwd)
SCRIPT=$HERE/autodeploy.sh
WORK=$(mktemp -d)
trap 'rm -rf "$WORK"' EXIT
mkdir -p "$WORK/bin"

# The fake docker. Its answers come from files in $AUTODEPLOY_DIR/fake (ready, eta, flush_code,
# latest, running); every call that matters is appended to $AUTODEPLOY_DIR/calls.
cat > "$WORK/bin/docker" <<'FAKE'
#!/usr/bin/env bash
f=$AUTODEPLOY_DIR/fake
answer() { cat "$f/$1" 2>/dev/null || true; }
args="$*"
case "$args" in
  # The compute services (wall-geometry, splat-cpu): `ps -q` answers from fake/ps-<svc> (empty = not running, e.g.
  # splat-cpu behind its off-by-default profile); every pull of one is recorded.
  "compose pull -q wall-geometry"|"compose pull -q splat-cpu") echo "pull ${args##* }" >> "$AUTODEPLOY_DIR/calls" ;;
  "compose ps -q wall-geometry"|"compose ps -q splat-cpu") answer "ps-${args##* }" ;;
  "inspect --format {{.Config.Image}} cid-"*) echo "image-of-${args##* }" ;;
  "inspect --format {{.Image}} cid-app") answer running ;;
  "inspect --format {{.Image}} cid-"*) echo "sha:same" ;;
  "image inspect --format {{.Id}} image-of-"*) echo "sha:same" ;;
  "image inspect"*) answer latest ;;
  "compose pull"*) exit 0 ;;
  "compose ps -q blocwerk") echo cid-app ;;
  "compose ps"*) exit 0 ;;
  "compose up -d --no-deps"*) echo "up ${args##* }" >> "$AUTODEPLOY_DIR/calls" ;;
  "compose up -d blocwerk") echo up >> "$AUTODEPLOY_DIR/calls" ;;
  "login"*) exit 0 ;;
  run*ready-to-deploy) answer ready ;;
  run*busy-eta) answer eta ;;
  run*flush-edits)
    echo flush >> "$AUTODEPLOY_DIR/calls"
    code=$(answer flush_code)
    if [ "$code" = 200 ]; then printf '{"sessions":1,"savedOnServer":1}\n200'; else printf '\n%s' "$code"; fi ;;
  run*announce) echo announce >> "$AUTODEPLOY_DIR/calls"; exit 0 ;;
  *) exit 1 ;;
esac
FAKE
chmod +x "$WORK/bin/docker"

# The fake flock, for hosts without one (macOS): fd 8 is the app lock, taken when
# $AUTODEPLOY_DIR/fake/app_lock_held exists; fd 9 (the compute pass) is free only when
# $AUTODEPLOY_DIR/fake/compute_lock_free exists, so the compute part stays skipped in the app scenarios, exactly
# as it is without flock. A host WITH a real flock also runs the real concurrency scenario below.
REAL_FLOCK=$(command -v flock || true)
cat > "$WORK/bin/flock" <<'FAKE'
#!/usr/bin/env bash
[ "$2" = 8 ] && [ ! -e "$AUTODEPLOY_DIR/fake/app_lock_held" ] && exit 0
[ "$2" = 9 ] && [ -e "$AUTODEPLOY_DIR/fake/compute_lock_free" ] && exit 0
exit 1
FAKE
chmod +x "$WORK/bin/flock"
# The fake timeout (macOS has none): drops the duration and runs the command.
cat > "$WORK/bin/timeout" <<'FAKE'
#!/usr/bin/env bash
shift
exec "$@"
FAKE
chmod +x "$WORK/bin/timeout"

failures=0
scenario() {
  local name=$1 ready=$2 eta=$3 flush=$4 deferrals=$5 expect=$6 says=${7:-}
  local dir=$WORK/$name
  mkdir -p "$dir/fake"
  [ -z "${HOLD_APP_LOCK:-}" ] || touch "$dir/fake/app_lock_held"
  printf 'BLOCWERK_DEPLOY_API_KEY=bwk_test\n' > "$dir/.env"
  printf '%s' "$ready" > "$dir/fake/ready"
  printf '%s' "$eta" > "$dir/fake/eta"
  printf '%s' "$flush" > "$dir/fake/flush_code"
  printf 'sha:new' > "$dir/fake/latest"
  printf 'sha:old' > "$dir/fake/running"
  printf 'sha:older' > "$dir/.autodeploy-target"
  [ "$deferrals" = none ] || printf '%s' "$deferrals" > "$dir/.autodeploy-deferrals"
  : > "$dir/calls"

  local log
  log=$(AUTODEPLOY_DIR=$dir PATH="$WORK/bin:$PATH" ANNOUNCE_GRACE=0 bash "$SCRIPT" 2>&1 || true)
  local calls state
  calls=$(tr '\n' ' ' < "$dir/calls")
  state=$(cat "$dir/.autodeploy-deferrals" 2>/dev/null || echo none)
  local got="calls=[${calls% }] deferrals=$state"
  if [ "$got" = "$expect" ] && { [ -z "$says" ] || grep -qF "$says" <<< "$log"; }; then
    echo "ok   $name"
  else
    echo "FAIL $name: expected '$expect'${says:+ saying \"$says\"}, got '$got'"
    while IFS= read -r line; do echo "     | $line"; done <<< "$log"
    failures=$((failures + 1))
  fi
}

# name                     ready  eta    flush  deferrals  expected
scenario idle-deploys       idle   0      200    none      "calls=[announce flush up] deferrals=none" 'flushed open editors: {"sessions":1'
scenario old-app-404        idle   0      404    none      "calls=[announce flush up] deferrals=none" "flush endpoint missing (older app)"
scenario flush-unreachable  idle   0      000    none      "calls=[announce flush up] deferrals=none" "flush failed (HTTP 000)"
scenario busy-defers        busy   0      200    3         "calls=[] deferrals=4"
scenario busy-cap-forces    busy   0      200    30        "calls=[announce flush up] deferrals=none"
scenario present-edit-waits busy   7200   200    30        "calls=[] deferrals=31"
scenario present-edit-long  busy   7200   200    1000      "calls=[] deferrals=1001"
scenario safety-cap-forces  busy   7200   200    1440      "calls=[announce flush up] deferrals=none"
# A newer image while deferred keeps the count (8df10aa): target differs, count goes on.
scenario new-image-keeps    busy   0      200    12        "calls=[] deferrals=13"

# Another run holds the app lock (a slow deploy still announcing or flushing): this run does nothing at all,
# not even a deferral count, and leaves the deploy to the run that holds the lock.
HOLD_APP_LOCK=1 scenario overlapping-run-quiet busy 0 200 3 "calls=[] deferrals=3"
HOLD_APP_LOCK=1 scenario overlapping-idle-quiet idle 0 200 none "calls=[] deferrals=none"

# The compute pass (after an app run with nothing to deploy): a running service is pulled and checked, a stopped one
# is left alone. Since O7 splat-cpu is off by default (own profile, docs/runbooks/o7-splat-on-runners.md), so the
# pass must not touch it, let alone start it; while it runs again (rollback) it is updated as before.
compute_scenario() {
  local name=$1 splat_cid=$2 expect=$3
  local dir=$WORK/$name
  mkdir -p "$dir/fake"
  touch "$dir/fake/compute_lock_free"
  printf 'BLOCWERK_DEPLOY_API_KEY=bwk_test\n' > "$dir/.env"
  printf 'sha:new' > "$dir/fake/latest"
  printf 'sha:new' > "$dir/fake/running"
  printf 'cid-wall-geometry' > "$dir/fake/ps-wall-geometry"
  printf '%s' "$splat_cid" > "$dir/fake/ps-splat-cpu"
  : > "$dir/calls"
  local log got
  log=$(AUTODEPLOY_DIR=$dir PATH="$WORK/bin:$PATH" bash "$SCRIPT" 2>&1 || true)
  got="calls=[$(tr '\n' ' ' < "$dir/calls" | sed 's/ $//')]"
  if [ "$got" = "$expect" ]; then
    echo "ok   $name"
  else
    echo "FAIL $name: expected '$expect', got '$got'"
    while IFS= read -r line; do echo "     | $line"; done <<< "$log"
    failures=$((failures + 1))
  fi
}

compute_scenario splat-cpu-off-left-alone ""            "calls=[pull wall-geometry]"
compute_scenario splat-cpu-on-updated     cid-splat-cpu "calls=[pull wall-geometry pull splat-cpu]"

# The same with the real flock, where there is one: a background holder keeps the lock file locked, and the
# run under test sees the real flock (its PATH has the fake docker only).
if [ -n "$REAL_FLOCK" ]; then
  dir=$WORK/real-lock
  mkdir -p "$dir/fake" "$WORK/realbin"
  ln -sf "$WORK/bin/docker" "$WORK/realbin/docker"
  printf 'idle' > "$dir/fake/ready"
  printf 'sha:new' > "$dir/fake/latest"
  printf 'sha:old' > "$dir/fake/running"
  printf 'BLOCWERK_DEPLOY_API_KEY=bwk_test\n' > "$dir/.env"
  : > "$dir/calls"
  "$REAL_FLOCK" "$dir/.autodeploy-app.lock" sleep 8 &
  holder=$!
  sleep 1
  AUTODEPLOY_DIR=$dir PATH="$WORK/realbin:$PATH" bash "$SCRIPT" >/dev/null 2>&1 || true
  kill "$holder" 2>/dev/null || true
  wait "$holder" 2>/dev/null || true
  if [ -s "$dir/calls" ]; then
    echo "FAIL real-lock: an overlapping run acted: $(tr '\n' ' ' < "$dir/calls")"
    failures=$((failures + 1))
  else
    echo "ok   real-lock"
  fi
fi

if [ "$failures" -gt 0 ]; then
  echo "$failures scenario(s) failed"
  exit 1
fi
echo "all scenarios passed"
