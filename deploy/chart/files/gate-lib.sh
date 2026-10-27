#!/bin/sh
# Shared by deploy-gate.sh and compute-gate.sh (sourced). POSIX sh: the hook image is curlimages/curl (busybox).

SA=/var/run/secrets/kubernetes.io/serviceaccount

# pods_json <component>: this release's pods of one component, from the kube API, whitespace removed so the
# annotation can be found with grep. Prints nothing and returns 1 when the API cannot be read.
pods_json() {
  curl -sf --max-time 10 --cacert "$SA/ca.crt" -H "Authorization: Bearer $(cat "$SA/token")" \
    "https://kubernetes.default.svc/api/v1/namespaces/$(cat "$SA/namespace")/pods?labelSelector=app.kubernetes.io/instance%3D$RELEASE,app.kubernetes.io/component%3D$1" |
    tr -d ' \n\t'
}

# running_rollouts <pods-json>: the distinct rollout fingerprints of those pods, space-separated, "" when none runs.
running_rollouts() {
  printf '%s' "$1" | grep -o '"blocwerk.app/rollout":"[0-9a-f]*"' | cut -d'"' -f4 | sort -u | tr '\n' ' ' | sed 's/ $//'
}
