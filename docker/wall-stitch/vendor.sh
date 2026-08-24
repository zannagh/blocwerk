#!/usr/bin/env bash
# Refresh the vendored pipeline copy under pipeline/ from the upstream working tree.
#
# The pipeline lives outside this repo while it is being developed. This script takes a
# snapshot of it so the container image is self-contained; it copies every .py file, so
# modules added upstream are picked up without editing this script.
#
#   ./vendor.sh [UPSTREAM_ROOT]        # default: ~/Desktop/wall-photos/work
#
# It then re-applies the one local change we carry: hm_common.py's hardcoded developer
# paths become environment lookups. See README.md -> "Vendored pipeline".
set -euo pipefail

ROOT="${1:-$HOME/Desktop/wall-photos/work}"
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

for pair in "stitch:stitch" "holds-match:holds_match"; do
    src="$ROOT/${pair%%:*}"
    dst="$HERE/pipeline/${pair##*:}"
    [ -d "$src" ] || { echo "missing upstream directory: $src" >&2; exit 1; }
    rm -rf "$dst" && mkdir -p "$dst"
    find "$src" -maxdepth 1 -name '*.py' -exec cp {} "$dst/" \;
    echo "vendored $(find "$dst" -name '*.py' | wc -l | tr -d ' ') file(s) from $src"
done

python3 "$HERE/vendor_patch.py"
