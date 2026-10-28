#!/bin/sh
# Compute gate, an ArgoCD Sync hook that runs after the web app and before the workers (sync waves 1 and 2): the
# compute pass of docker/prod/autodeploy.sh. The workers keep their job queue in memory, so replacing one mid-job
# loses the job. Per worker in WORKERS ("component|url|target|key-variable", space-separated):
#   - not running, or its rollout fingerprint unchanged: nothing to wait for;
#   - otherwise go after two consecutive idle polls of GET /v1/info (POLL_SECONDS apart), so the app has fetched a
#     result that just finished; a probe that fails (refused, timed out, an error) counts as busy unless the kube API
#     says no pod of the worker is ready: a new gate pod's first requests can be refused (gate-lib.sh);
#   - cap: after MAX_DEFER busy polls go ahead regardless (job counts only, no start times: see autodeploy.sh).
# Always exits 0.
# shellcheck source=SCRIPTDIR/gate-lib.sh
. /gate/gate-lib.sh

# probe <url> <key>: idle | busy | down | unknown
probe() {
  body=$(curl -s -f --max-time 5 -H "Authorization: Bearer $2" "$1/v1/info")
  rc=$?
  if [ "$rc" -eq 6 ] || [ "$rc" -eq 7 ]; then echo down; return; fi
  [ "$rc" -eq 0 ] || { echo unknown; return; }
  flat=$(printf '%s' "$body" | tr -d ' \n')
  q=$(printf '%s' "$flat" | sed -n 's/.*"queued":\([0-9][0-9]*\).*/\1/p')
  r=$(printf '%s' "$flat" | sed -n 's/.*"running":\([0-9][0-9]*\).*/\1/p')
  if [ "$q" = "0" ] && [ "$r" = "0" ]; then echo idle; else echo busy; fi
}

gate_worker() {
  name=$1 url=$2 target=$3 key=$4
  pods=$(pods_json "$name") || { echo "$name: cannot read its pods; gating as if it changed"; pods="?"; }
  running=$(running_rollouts "$pods")
  if [ "$pods" != "?" ] && [ -z "$running" ]; then echo "$name: not running; nothing to wait for"; return; fi
  if [ "$running" = "$target" ]; then echo "$name: unchanged; no gate"; return; fi

  n=0 idle=0
  while :; do
    state=$(probe "$url" "$key")
    case "$state" in
      idle)
        idle=$((idle + 1))
        if [ "$idle" -ge 2 ]; then echo "$name: idle twice; deploying"; return; fi
        echo "$name: idle, deploying after the next poll if it stays idle" ;;
      *)
        idle=0
        if [ "$state" != "busy" ] && [ "$(pod_readiness "$name")" = "none" ]; then
          echo "$name: probe $state and no pod of it is ready; deploying"; return
        fi
        if [ "$n" -ge "$MAX_DEFER" ]; then echo "$name: still $state after $n busy polls; deploying anyway (cap)"; return; fi
        n=$((n + 1))
        echo "$name: $state, deferring ($n/$MAX_DEFER)" ;;
    esac
    sleep "$POLL_SECONDS"
  done
}

for spec in $WORKERS; do
  old_ifs=$IFS
  IFS='|'
  # shellcheck disable=SC2086  # split on | on purpose
  set -- $spec
  IFS=$old_ifs
  eval "key=\${$4:-}"
  gate_worker "$1" "$2" "$3" "$key"
done
exit 0
