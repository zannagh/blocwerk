"""CLI-only corner refinement from the capture's PNGs. The algorithm lives in the solver package
(docker/wall-geometry/wallgeometry/refine.py); this only loads the photo by name."""
import os
import sys

import cv2

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "..",
                                "docker", "wall-geometry"))
from wallgeometry import refine as _core  # noqa: E402

# Full-res capture PNGs: GLYPH_PNG_DIR, else tools/glyph/png (where detect.py and sweep.py look).
PNG_DIR = os.environ.get(
    "GLYPH_PNG_DIR",
    os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "png"))

_cache = {}


def _gray(image):
    if image not in _cache:
        path = os.path.join(PNG_DIR, image + ".png")
        g = cv2.imread(path, cv2.IMREAD_GRAYSCALE)
        if g is None:
            raise FileNotFoundError(path)
        _cache.clear()
        _cache[image] = _core.prepare(g)
    return _cache[image]


def refine_corners(image, corners, mean_side):
    """Return (refined 4x2 corners, per-corner shift px, per-corner ok flags)."""
    return _core.refine_corners(_gray(image), corners, mean_side)
