#!/bin/sh
# PreSync deploy gate for the web pod: the in-cluster form of docker/prod/autodeploy.sh, same decisions in the same
# order. Always exits 0 (a gate that fails would block every sync); everything it decides is in its log.
#   1. Compare what runs with what this sync deploys: the pods' rollout fingerprint (image, env, volumes, resources)
#      against TARGET. Unchanged = the sync does not replace the app, so there is nothing to wait for.
#   2. Busy gate: wait while /health/ready-to-deploy says busy, MAX_DEFER polls; past that keep waiting while
#      /health/busy-eta reports an end, up to ETA_MAX_DEFER polls in all; then go ahead regardless. An app that does
#      not answer counts as busy while its pod is (or may be) ready: from a new gate pod that is the gate's own
#      network (see gate-lib.sh), not a stopped app. Only "no web pod is ready" lets an unreachable app pass at once.
#   3. Announce the maintenance (best effort), wait ANNOUNCE_GRACE, then force the flush of open editors (best
#      effort), so an edit made while the notice was on its way is saved too. Then the sync replaces the pod.
# shellcheck source=SCRIPTDIR/gate-lib.sh
. /gate/gate-lib.sh

URL="http://$RELEASE:$PORT"

pods=$(pods_json web) || { echo "cannot read the running web pods from the kube API; gating as if the app changed"; pods=""; api_failed=1; }
running=$(running_rollouts "$pods")
echo "target: $TARGET_IMAGE (rollout $TARGET); running rollout(s): ${running:-none}"
if [ -z "${api_failed:-}" ]; then
  if [ -z "$running" ]; then
    echo "no web pod runs; nothing to protect"
    exit 0
  fi
  if [ "$running" = "$TARGET" ]; then
    echo "the web pod does not change in this sync; no gate"
    exit 0
  fi
fi

n=0
while :; do
  state=$(curl -s --max-time 5 "$URL/health/ready-to-deploy" || echo unreachable)
  if [ "$state" = "unreachable" ]; then
    readiness=$(pod_readiness web)
    if [ "$readiness" = "none" ]; then
      echo "app not reachable and no web pod is ready; nothing to protect"
      break
    fi
    if [ "$n" -ge "$MAX_DEFER" ]; then
      echo "app still not reachable after $n polls (web pod readiness: $readiness); deploying anyway (cap)"
      break
    fi
    echo "app not reachable but the web pod readiness is '$readiness'; treating it as busy ($((n + 1))/$MAX_DEFER)"
    n=$((n + 1))
    sleep "$POLL_SECONDS"
    continue
  fi
  [ "$state" = "busy" ] || break
  if [ "$n" -ge "$ETA_MAX_DEFER" ]; then
    echo "still busy after $n polls; deploying anyway (cap)"
    break
  fi
  if [ "$n" -ge "$MAX_DEFER" ]; then
    eta=$(curl -s --max-time 5 "$URL/health/busy-eta" || echo 0)
    case "$eta" in '' | *[!0-9]*) eta=0 ;; esac
    if [ "$eta" -le 0 ]; then
      echo "still busy after $n polls and no end reported; deploying anyway (cap)"
      break
    fi
    echo "app busy for about $((eta / 60)) more min, deferring ($((n + 1))/$ETA_MAX_DEFER)"
  else
    echo "app busy, deferring ($((n + 1))/$MAX_DEFER)"
  fi
  n=$((n + 1))
  sleep "$POLL_SECONDS"
done
echo "gate passed: state=$state after $n deferral(s)"

if [ "$state" = "unreachable" ]; then
  echo "app not reachable; nothing to announce or flush"
  exit 0
fi
if [ -z "${BLOCWERK_DEPLOY_API_KEY:-}" ]; then
  echo "no BLOCWERK_DEPLOY_API_KEY (secret not set); deploying without announcing or flushing"
  exit 0
fi

if curl -s -f --max-time 5 -X POST \
  -H "Authorization: Bearer $BLOCWERK_DEPLOY_API_KEY" -H "Content-Type: application/json" \
  -d "{\"etaSeconds\":$ANNOUNCE_ETA}" "$URL/api/v1/maintenance/announce" >/dev/null; then
  echo "announced; giving clients ${ANNOUNCE_GRACE}s to notice"
  sleep "$ANNOUNCE_GRACE"
else
  echo "announce failed (endpoint missing, key not installation-scoped, or app unreachable); deploying anyway"
fi

out=$(curl -s --max-time "$FLUSH_TIMEOUT" -X POST -H "Authorization: Bearer $BLOCWERK_DEPLOY_API_KEY" \
  -w '\n%{http_code}' "$URL/api/v1/maintenance/flush-edits" || true)
code=$(printf '%s' "$out" | tail -n 1)
case "$code" in
  200) echo "flushed open editors: $(printf '%s' "$out" | sed '$d')" ;;
  404) echo "flush endpoint missing (older app); deploying anyway" ;;
  *) echo "flush failed (HTTP ${code:-none}); deploying anyway" ;;
esac
exit 0
