#!/usr/bin/env bash
# Refresh the vendored pipeline copy under pipeline/ from the upstream working tree.
#
# The pipeline lives outside this repo while it is being developed. This script takes a
# snapshot of it so the container image is self-contained. It copies the entrypoint plus
# every .py file of the `wallpipe` package, so modules added upstream are picked up
# without editing this script.
#
#   ./vendor.sh [UPSTREAM_ROOT]        # default: ~/Desktop/wall-photos/work
#
# vendor_patch.py then verifies the snapshot: the vendored copy must contain no
# developer-home paths, because the container has no such home. See README.md ->
# "Vendored pipeline".
set -euo pipefail

ROOT="${1:-$HOME/Desktop/wall-photos/work}"
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SRC="$ROOT/stitch"
DST="$HERE/pipeline"

[ -f "$SRC/wall_pipeline.py" ] || { echo "missing upstream entrypoint: $SRC/wall_pipeline.py" >&2; exit 1; }
[ -d "$SRC/wallpipe" ] || { echo "missing upstream package: $SRC/wallpipe" >&2; exit 1; }

rm -rf "$DST" && mkdir -p "$DST/wallpipe"
cp "$SRC/wall_pipeline.py" "$DST/wall_pipeline.py"
find "$SRC/wallpipe" -maxdepth 1 -name '*.py' -exec cp {} "$DST/wallpipe/" \;
echo "vendored wall_pipeline.py + $(find "$DST/wallpipe" -name '*.py' | wc -l | tr -d ' ') module(s) from $SRC"

python3 "$HERE/vendor_patch.py"
