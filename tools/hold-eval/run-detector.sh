#!/bin/bash
# Runs the app's detector, outliner and shape clean-up on photo files, in a Linux container (OpenCV does not load
# on macOS), and writes one JSON per photo.
#
#   run-detector.sh <model-dir> <photo-dir> <out-dir> [--seed=circle] [--segmenter=false] [--markers=false]
#   run-detector.sh --redetect <model-dir> <out-dir> <wall id> <database>
#       plans the app's re-detect for the wall's live panels in a database COPY on this machine's Postgres
#       (HOLD_EVAL_PG, default port 55432, user blocwerk) and writes what it proposes; it applies nothing, but it
#       brings the copy to this build's schema first, so only ever name a throwaway copy
#   run-detector.sh --test [<dotnet test args>]
#       runs test/Blocwerk.HoldDetection.Tests in the same container
#
# <model-dir> holds climbingcrux.onnx and, for the segmenter, hold-segmenter-encoder.onnx and
# hold-segmenter-decoder.onnx (export_segmenter.py). With --test it is taken from HOLD_EVAL_MODELS, when set.
# Photos and results stay outside the repository: they are personal data. Build output goes to a docker volume, so
# the macOS build folders are left alone. HOLD_EVAL_CPUS (default 4) caps the container's CPUs.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
repo="$(cd "$here/../.." && pwd)"
image=blocwerk-hold-eval
docker image inspect "$image" >/dev/null 2>&1 || docker build -q -t "$image" "$here" >/dev/null
common=(--rm --cpus "${HOLD_EVAL_CPUS:-4}" -v "$repo:/repo:ro" -v blocwerk-hold-eval-build:/build -v blocwerk-hold-eval-nuget:/root/.nuget
        -e DOTNET_CLI_TELEMETRY_OPTOUT=1 -e DOTNET_NOLOGO=1)
# A Debug build with optimisation: the Release build reads the version from the repository history, which the
# read-only mount inside the container does not offer.
build_tool='dotnet build tools/hold-eval/HoldEval --artifacts-path /build -c Debug -p:Optimize=true -v q --nologo 2>&1 | grep -E " error |Build succeeded" | sort -u || true'
tool='dotnet /build/bin/HoldEval/debug/Blocwerk.HoldEval.dll /models/climbingcrux.onnx /out'

case "${1:-}" in
  --test)
    shift
    mounts=()
    [ -n "${HOLD_EVAL_MODELS:-}" ] && mounts=(-v "$HOLD_EVAL_MODELS:/models:ro" -e HOLD_EVAL_MODELS=/models)
    exec docker run "${common[@]}" "${mounts[@]}" "$image" bash -c '
      dotnet build test/Blocwerk.HoldDetection.Tests --artifacts-path /build -c Debug -v q --nologo 2>&1 | grep -E " error |Build succeeded" | sort -u || true
      out=/build/bin/Blocwerk.HoldDetection.Tests/debug
      if [ -n "${HOLD_EVAL_MODELS:-}" ]; then mkdir -p "$out/models" && cp -u /models/*.onnx "$out/models/"; fi
      exec dotnet test test/Blocwerk.HoldDetection.Tests --artifacts-path /build -c Debug --no-build "$@"' -- "$@"
    ;;
  --redetect)
    models="${2:?model folder}"; out="${3:?output folder}"; wall="${4:?wall id}"; database="${5:?database name}"
    mkdir -p "$out"
    pg="${HOLD_EVAL_PG:-Host=host.docker.internal;Port=55432;Username=blocwerk}"
    exec docker run "${common[@]}" -v "$models:/models:ro" -v "$out:/out" "$image" bash -c "$build_tool; exec $tool \"--redetect=\$1\" \"--db=\$2\"" -- "$wall" "$pg;Database=$database"
    ;;
esac

models="${1:?model folder}"; photos="${2:?photo folder}"; out="${3:?output folder}"; shift 3
mkdir -p "$out"
exec docker run "${common[@]}" -v "$models:/models:ro" -v "$photos:/photos:ro" -v "$out:/out" "$image" bash -c "$build_tool; shopt -s nullglob; exec $tool /photos/*.jpg \"\$@\"" -- "$@"
