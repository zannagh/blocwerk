#!/usr/bin/env bash
# Compares what the chart renders with what production runs today (docker/docker-compose.prod.yml): the environment
# variable NAMES and the mount targets of the app and of both workers. Values are not compared (they come from the
# box's .env). Every difference must be listed in INTENDED below with its reason; anything else fails, so a variable
# added to the compose file but not to the chart (or the other way round) is caught.
#   deploy/chart/tests/compare-compose.sh
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
COMPOSE="$ROOT/docker/docker-compose.prod.yml"
OUT="$(mktemp -d)"
trap 'rm -rf "$OUT"' EXIT
helm template blocwerk "$ROOT/deploy/chart" --namespace blocwerk -f "$ROOT/deploy/chart/ci/all-on-values.yaml" > "$OUT/r.yaml"

# Keys the chart takes from secrets.app (envFrom), which a render cannot list.
APP_SECRET_KEYS="SMTP__HOST SMTP__USERNAME SMTP__PASSWORD SMTP__FROM SMTP__FROMNAME BLOCWERK__ENCRYPTIONKEY VAPID__SUBJECT VAPID__PUBLICKEY VAPID__PRIVATEKEY"

# "<service> <env|mount> <compose-only|chart-only> <name>" -> reason
INTENDED='
blocwerk mount compose-only /app/models/climbingcrux.onnx|the models/ subdirectory of the data volume is mounted at /app/models (a missing file must stay missing, not become a directory)
blocwerk mount compose-only /app/models/hold-segmenter-encoder.onnx|as above
blocwerk mount compose-only /app/models/hold-segmenter-decoder.onnx|as above
blocwerk mount chart-only /app/models|as above
blocwerk mount chart-only /tmp|uploads are buffered in an emptyDir with a size limit (compose: the container layer)
wall-geometry env compose-only COMPUTE_CALLBACK_SECRET|empty in production (the app polls); no callback secret is wired
wall-geometry mount chart-only /tmp|job files in an emptyDir with a size limit (compose: the container layer)
splat-cpu env compose-only COMPUTE_CALLBACK_SECRET|empty in production (the app polls); no callback secret is wired
splat-cpu mount chart-only /tmp|job files in an emptyDir with a size limit (compose: the container layer)
'

compose_env() { yq -r ".services.\"$1\".environment | keys | .[]" "$COMPOSE"; }
compose_mounts() { yq -r ".services.\"$1\".volumes[] | split(\":\") | .[1]" "$COMPOSE"; }
chart_env() { yq e -r -N "select(.kind == \"Deployment\" and .metadata.name == \"$1\") | .spec.template.spec.containers[0].env[].name" "$OUT/r.yaml"; }
chart_mounts() { yq e -r -N "select(.kind == \"Deployment\" and .metadata.name == \"$1\") | .spec.template.spec.containers[0].volumeMounts[]?.mountPath" "$OUT/r.yaml"; }

FAILED=0
# diff_sets <service> <kind> <compose-list-file> <chart-list-file>
diff_sets() {
  local service="$1" kind="$2" side name reason
  while read -r side name; do
    [[ -n "$name" ]] || continue
    reason="$(grep -F "$service $kind $side $name|" <<<"$INTENDED" | cut -d'|' -f2- || true)"
    if [[ -n "$reason" ]]; then
      echo "  $service $kind $side $name: $reason"
    else
      echo "UNEXPECTED: $service $kind $side $name" >&2
      FAILED=$((FAILED + 1))
    fi
  done < <(comm -3 <(sort -u "$3") <(sort -u "$4") | sed -e 's/^\t/chart-only /' -e '/^chart-only /!s/^/compose-only /')
}

check() {
  local service="$1" deployment="$2" extra="${3:-}"
  compose_env "$service" > "$OUT/ce"
  { chart_env "$deployment"; tr ' ' '\n' <<<"$extra"; } | sed '/^$/d' > "$OUT/he"
  compose_mounts "$service" > "$OUT/cm" 2>/dev/null || : > "$OUT/cm"
  chart_mounts "$deployment" > "$OUT/hm"
  echo "$service vs $deployment:"
  diff_sets "$service" env "$OUT/ce" "$OUT/he"
  diff_sets "$service" mount "$OUT/cm" "$OUT/hm"
}

check blocwerk blocwerk "$APP_SECRET_KEYS"
check wall-geometry blocwerk-geometry
check splat-cpu blocwerk-splat-cpu
[[ "$FAILED" -eq 0 ]] || { echo "$FAILED unexpected difference(s) between the chart and $COMPOSE" >&2; exit 1; }
echo "chart matches the compose file (intended differences listed above)"
