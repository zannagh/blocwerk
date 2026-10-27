#!/usr/bin/env bash
# Offline tests of the chart: helm lint, helm template with every CI values file, kubeconform on the output, and the
# rules a render can break silently. Needs helm, yq (mikefarah v4), kubeconform and shellcheck; no cluster.
#   deploy/chart/tests/chart-test.sh
set -euo pipefail

CHART="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUT="$(mktemp -d)"
trap 'rm -rf "$OUT"' EXIT
FAILED=0

fail() { echo "FAIL: $*" >&2; FAILED=$((FAILED + 1)); }
render() { helm template blocwerk "$CHART" --namespace blocwerk "$@"; }
# q <file> <yq expression>: evaluate on each document of a render, one result per line.
q() { yq e -r -N "$2" "$1"; }

shellcheck -s sh -x "$CHART"/files/*.sh

for values in default "$CHART"/ci/*-values.yaml; do
  name="$(basename "$values" .yaml)"
  args=()
  [[ "$values" == default ]] || args=(-f "$values")
  helm lint --strict "$CHART" ${args[@]+"${args[@]}"} >/dev/null || fail "$name: helm lint"
  render ${args[@]+"${args[@]}"} > "$OUT/$name.yaml" || { fail "$name: helm template"; continue; }
  kubeconform -strict -summary "$OUT/$name.yaml" || fail "$name: kubeconform"

  f="$OUT/$name.yaml"
  [[ -z "$(q "$f" 'select(.kind == "Secret") | .metadata.name')" ]] || fail "$name: renders a Secret (secrets are referenced by name only)"
  missing="$(q "$f" 'select(.kind == "Deployment" or .kind == "StatefulSet" or .kind == "Job" or .kind == "CronJob") |
    (.spec.jobTemplate.spec.template.spec // .spec.template.spec) as $p | $p.containers[] |
    select(.resources.requests.memory == null or .resources.limits.memory == null) | .name')"
  [[ -z "$missing" ]] || fail "$name: containers without memory request+limit: $missing"
  web_replicas="$(q "$f" 'select(.kind == "Deployment" and .metadata.name == "blocwerk") | .spec.replicas')"
  [[ "$web_replicas" == 0 || "$web_replicas" == 1 ]] || fail "$name: web replicas $web_replicas"

  # The gate's targets must be the fingerprints the Deployments carry, or it compares against nothing.
  web_fp="$(q "$f" 'select(.kind == "Deployment" and .metadata.name == "blocwerk") | .spec.template.metadata.annotations["blocwerk.app/rollout"]')"
  gate_fp="$(q "$f" 'select(.kind == "Job" and .metadata.name == "blocwerk-deploy-gate") | .spec.template.spec.containers[0].env[] | select(.name == "TARGET") | .value')"
  [[ -n "$web_fp" && "$web_fp" == "$gate_fp" ]] || fail "$name: web fingerprint '$web_fp' != gate target '$gate_fp'"
  workers="$(q "$f" 'select(.kind == "Job" and .metadata.name == "blocwerk-compute-gate") | .spec.template.spec.containers[0].env[] | select(.name == "WORKERS") | .value')"
  while read -r worker fp; do
    [[ -n "$worker" ]] || continue
    grep -q "^$worker|http://blocwerk-$worker:[0-9]*|$fp|" <<<"${workers// /$'\n'}" || fail "$name: compute gate has no target $fp for $worker"
  done < <(q "$f" 'select(.kind == "Deployment" and .metadata.labels["app.kubernetes.io/component"] != "web" and .metadata.labels["app.kubernetes.io/component"] != "aspire") |
    .metadata.labels["app.kubernetes.io/component"] + " " + .spec.template.metadata.annotations["blocwerk.app/rollout"]')
done

# splat-cpu is optional and nothing may assume it: off (default and prod-like) = no object and no SPLATSERVICE__ in the app.
for name in default prod-like-values; do
  [[ -z "$(q "$OUT/$name.yaml" 'select(.metadata.name == "blocwerk-splat-cpu") | .kind')" ]] || fail "$name: splat-cpu rendered while off"
  grep -q 'SPLATSERVICE__' "$OUT/$name.yaml" && fail "$name: SPLATSERVICE__ set while splat-cpu is off"
done
grep -q 'name: SPLATSERVICE__URL' "$OUT/all-on-values.yaml" || fail "all-on: SPLATSERVICE__URL missing"
limit="$(q "$OUT/prod-like-values.yaml" 'select(.metadata.name == "blocwerk-geometry" and .kind == "Deployment") | .spec.template.spec.containers[0].resources.limits.memory')"
[[ "$limit" == 1536Mi ]] || fail "prod-like: geometry memory limit $limit, expected 1536Mi"

# Hard rules enforced by the schema.
render --set web.replicas=2 >/dev/null 2>&1 && fail "schema accepted web.replicas=2"
render --set geometry.replicas=3 >/dev/null 2>&1 && fail "schema accepted geometry.replicas=3"
render --set web.env.POSTGRES__HOST=x >/dev/null 2>&1 && fail "schema accepted a chart-computed variable in web.env"
render --set deployGate.enabled=false | grep -q 'argocd.argoproj.io/hook' && fail "hooks rendered with the gate off"

[[ "$FAILED" -eq 0 ]] || { echo "$FAILED failure(s)" >&2; exit 1; }
echo "chart tests passed"
