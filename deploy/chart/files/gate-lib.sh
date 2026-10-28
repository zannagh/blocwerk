#!/bin/sh
# Shared by deploy-gate.sh and compute-gate.sh (sourced). POSIX sh: the hook image is curlimages/curl (busybox).
#
# A gate pod is new on every sync, and the k3s network policy controller covers a new pod a moment after it starts:
# its first connections can be refused (seen on k3s 1.36: the first request fails, the next one a second later
# works). So nothing below may treat one failed request as an answer. A gate that cannot tell must wait, never pass.

SA=/var/run/secrets/kubernetes.io/serviceaccount
API_TRIES=${API_TRIES:-6}
API_RETRY_SECONDS=${API_RETRY_SECONDS:-5}

# pods_json <component>: this release's pods of one component, from the kube API, whitespace removed so the
# annotation can be found with grep. Retries; prints nothing and returns 1 when the API cannot be read.
pods_json() {
  tries=0
  while :; do
    # Not `curl | tr`: a pipeline's status is the last command's, so a refused request would read as "no pods".
    if body=$(curl -sf --max-time 10 --cacert "$SA/ca.crt" -H "Authorization: Bearer $(cat "$SA/token")" \
      "https://kubernetes.default.svc/api/v1/namespaces/$(cat "$SA/namespace")/pods?labelSelector=app.kubernetes.io/instance%3D$RELEASE,app.kubernetes.io/component%3D$1"); then
      body=$(printf '%s' "$body" | tr -d ' \n\t')
      case "$body" in
        *'"kind":"PodList"'*) printf '%s' "$body"; return 0 ;;
      esac
    fi
    tries=$((tries + 1))
    [ "$tries" -lt "$API_TRIES" ] || return 1
    sleep "$API_RETRY_SECONDS"
  done
}

# running_rollouts <pods-json>: the distinct rollout fingerprints of those pods, space-separated, "" when none runs.
running_rollouts() {
  printf '%s' "$1" | grep -o '"blocwerk.app/rollout":"[0-9a-f]*"' | cut -d'"' -f4 | sort -u | tr '\n' ' ' | sed 's/ $//'
}

# pod_readiness <component>: "ready" when one of its pods reports a ready container, "none" when the API answered
# and none does, "unknown" when the API cannot be read. One try: the callers poll it, and the hook deadlines count
# one API timeout per poll. Always called in $( ), so the API_TRIES below stays in that subshell.
pod_readiness() {
  API_TRIES=1
  p=$(pods_json "$1") || { echo unknown; return; }
  if printf '%s' "$p" | grep -q '"ready":true'; then echo ready; else echo none; fi
}
