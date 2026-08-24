#!/usr/bin/env python3
"""Re-applies the one local change the vendored pipeline carries.

Upstream `hm_common.py` hardcodes a developer's home directory for the work root, the
input images and the ONNX model. The sidecar runs one job per directory, so those come
from the environment instead - with the upstream values kept as defaults, so the file
still behaves identically outside a container. Idempotent.
"""
import os
import pathlib
import sys

HERE = pathlib.Path(__file__).resolve().parent
TARGET = HERE / "pipeline" / "holds_match" / "hm_common.py"

ORIGINAL = '''WALL_ROOT = "/Users/patrickweindl/Desktop/wall-photos/work"
OLD_IMG = os.path.join(WALL_ROOT, "holds", "wall-photo.jpg")
NEW_IMG = os.path.join(WALL_ROOT, "06-final", "wall-orthophoto.png")
HOLDS_JSON = os.path.join(WALL_ROOT, "holds", "holds.json")
WALL_JSON = os.path.join(WALL_ROOT, "holds", "wall.json")
ONNX = "/Users/patrickweindl/Projects/blocwerk/src/Blocwerk.HoldDetection/models/climbingcrux.onnx"'''

PATCHED = '''# VENDORED-COPY PATCH (docker/wall-stitch): the upstream copy hardcodes a developer's
# Desktop.  The sidecar runs one job per directory, so the root and the model path come
# from the environment; the upstream defaults are kept so the file still behaves
# identically when the variables are unset.  Remove this once the upstream CLI grows
# --old/--new/--holds/--wall flags.
WALL_ROOT = os.environ.get(
    "WALLSTITCH_WORK_ROOT", "/Users/patrickweindl/Desktop/wall-photos/work")
OLD_IMG = os.environ.get("WALLSTITCH_OLD_IMG") or os.path.join(WALL_ROOT, "holds", "wall-photo.jpg")
NEW_IMG = os.environ.get("WALLSTITCH_NEW_IMG") or os.path.join(WALL_ROOT, "06-final", "wall-orthophoto.png")
HOLDS_JSON = os.environ.get("WALLSTITCH_HOLDS_JSON") or os.path.join(WALL_ROOT, "holds", "holds.json")
WALL_JSON = os.environ.get("WALLSTITCH_WALL_JSON") or os.path.join(WALL_ROOT, "holds", "wall.json")
ONNX = os.environ.get(
    "WALLSTITCH_ONNX_MODEL",
    "/Users/patrickweindl/Projects/blocwerk/src/Blocwerk.HoldDetection/models/climbingcrux.onnx")'''


def main() -> int:
    if not TARGET.exists():
        print(f"nothing to patch: {TARGET} is missing", file=sys.stderr)
        return 1
    text = TARGET.read_text()
    if PATCHED in text:
        print("hm_common.py already patched")
        return 0
    if ORIGINAL not in text:
        print("hm_common.py no longer matches the expected block - the upstream paths may "
              "have been generalised. Check whether this patch is still needed.", file=sys.stderr)
        return 2
    TARGET.write_text(text.replace(ORIGINAL, PATCHED))
    print("patched hm_common.py")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
