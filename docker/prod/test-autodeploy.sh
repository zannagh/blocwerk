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
  "image inspect"*) answer latest ;;
  "compose pull"*) exit 0 ;;
  "compose ps -q blocwerk") echo cid-app ;;
  "compose ps"*) exit 0 ;;
  "inspect --format {{.Image}} cid-app") answer running ;;
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

failures=0
scenario() {
  local name=$1 ready=$2 eta=$3 flush=$4 deferrals=$5 expect=$6 says=${7:-}
  local dir=$WORK/$name
  mkdir -p "$dir/fake"
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

if [ "$failures" -gt 0 ]; then
  echo "$failures scenario(s) failed"
  exit 1
fi
echo "all scenarios passed"
