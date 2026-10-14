"""Hold geometry in photo pixels, shared by the scorer and the tile renderer.

A hold is a dict with "x", "y" (centre, fractions of the photo), "radius" (fraction of the photo's longer side) and
"outline" (a list of [x, y] fractions, or None for a plain circle). A plain circle is taken the way the app draws it:
on a square stretched over the photo, so an ellipse with the radius measured along each side.
"""
import math

import cv2
import numpy as np
from shapely.geometry import Point, Polygon
from shapely.validation import make_valid

CIRCLE_SEGMENTS = 32


def outline_px(hold: dict, width: int, height: int) -> list[tuple[float, float]]:
    shape = hold.get("outline")
    if shape and len(shape) >= 3:
        return [(p[0] * width, p[1] * height) for p in shape]
    r = hold["radius"]
    return [((hold["x"] + r * math.cos(2 * math.pi * i / CIRCLE_SEGMENTS)) * width,
             (hold["y"] + r * math.sin(2 * math.pi * i / CIRCLE_SEGMENTS)) * height) for i in range(CIRCLE_SEGMENTS)]


def polygon(hold: dict, width: int, height: int):
    poly = Polygon(outline_px(hold, width, height))
    if not poly.is_valid:
        poly = make_valid(poly)
    return poly if poly.area > 0 else Point(hold["x"] * width, hold["y"] * height).buffer(1.0)


def diameter(poly) -> float:
    """The diameter of the circle with the polygon's area."""
    return 2 * math.sqrt(poly.area / math.pi)


def iou(a, b) -> float:
    shared = a.intersection(b).area
    union = a.area + b.area - shared
    return shared / union if union > 0 else 0.0


def inside(a, b) -> float:
    """The share of a that lies inside b."""
    return a.intersection(b).area / a.area if a.area > 0 else 0.0


def _rings(poly) -> list[np.ndarray]:
    parts = getattr(poly, "geoms", [poly])
    return [np.array(p.exterior.coords, dtype=np.float64) for p in parts if p.geom_type == "Polygon" and p.area > 0]


def boundary_f(a, b, tolerance: float) -> float:
    """Boundary F-score of two pixel polygons: the share of each one's boundary within the tolerance of the other's."""
    minx = min(a.bounds[0], b.bounds[0]) - tolerance - 2
    miny = min(a.bounds[1], b.bounds[1]) - tolerance - 2
    maxx = max(a.bounds[2], b.bounds[2]) + tolerance + 2
    maxy = max(a.bounds[3], b.bounds[3]) + tolerance + 2
    size = (int(maxy - miny) + 1, int(maxx - minx) + 1)
    if size[0] * size[1] > 64_000_000:
        return float("nan")

    def edge(poly) -> np.ndarray:
        mask = np.zeros(size, np.uint8)
        rings = [np.round(r - (minx, miny)).astype(np.int32) for r in _rings(poly)]
        cv2.polylines(mask, rings, True, 255, 1)
        return mask

    ea, eb = edge(a), edge(b)
    da = cv2.distanceTransform(255 - ea, cv2.DIST_L2, 3)
    db = cv2.distanceTransform(255 - eb, cv2.DIST_L2, 3)
    na, nb = int((ea > 0).sum()), int((eb > 0).sum())
    if na == 0 or nb == 0:
        return 0.0
    precision = float(((ea > 0) & (db <= tolerance)).sum()) / na
    recall = float(((eb > 0) & (da <= tolerance)).sum()) / nb
    return 2 * precision * recall / (precision + recall) if precision + recall > 0 else 0.0


def boundary_tolerance(poly) -> float:
    """2 to 3 px for an ordinary hold, growing with the hold so a volume is not held to a foot chip's tolerance."""
    return min(8.0, max(2.0, 0.03 * diameter(poly)))


def size_class(poly, long_side: int) -> str:
    """tiny: foot chips and screw-ons; small; normal; large: big holds and volumes."""
    d = diameter(poly) / long_side
    if d < 0.008:
        return "tiny"
    if d < 0.016:
        return "small"
    if d < 0.04:
        return "normal"
    return "large"


THIN_SHARE = 0.2
SHORT_SIDE_SHARE = 0.45
IMPLAUSIBLE = 0.03


def implausibility(poly) -> tuple[float, float]:
    """How unlike a solid moulded body an outline is: the share of its area in parts thinner than a hold can be
    (spikes, tendrils), and the share that narrow slits cut out of it. "Thin" is relative to the hold: a fifth of the
    diameter of the circle with its area (stricter than the 13 % the app's MaskPlausibility cleans at, so what the
    cleaning leaves is still measured), less for a hold that is thin as a whole. Implausible from 3 % of the area."""
    if poly.area <= 0 or poly.geom_type != "Polygon":
        return 0.0, 0.0
    box = poly.minimum_rotated_rectangle
    xs, ys = box.exterior.coords.xy
    sides = sorted(math.hypot(xs[i + 1] - xs[i], ys[i + 1] - ys[i]) for i in range(2))
    r = min(THIN_SHARE * diameter(poly), SHORT_SIDE_SHARE * sides[0]) / 2
    if r < 0.75:
        return 0.0, 0.0
    opened = poly.buffer(-r).buffer(r)
    closed = poly.buffer(r).buffer(-r)
    return max(0.0, 1 - opened.area / poly.area), max(0.0, closed.area / poly.area - 1)


def is_implausible(poly) -> bool:
    thin, slit = implausibility(poly)
    return thin > IMPLAUSIBLE or slit > IMPLAUSIBLE


LOW_CONTRAST = 18.0


def contrast(photo_bgr: np.ndarray, poly) -> float:
    """How far the hold's mean colour is from the wall around it (CIE Lab distance): below LOW_CONTRAST it is a
    whitish, wooden or chalked hold on light wood, which a colour threshold cannot cut out."""
    minx, miny, maxx, maxy = poly.bounds
    pad = 0.5 * max(maxx - minx, maxy - miny) + 4
    x0, y0 = max(0, int(minx - pad)), max(0, int(miny - pad))
    x1, y1 = min(photo_bgr.shape[1], int(maxx + pad) + 1), min(photo_bgr.shape[0], int(maxy + pad) + 1)
    crop = cv2.cvtColor(photo_bgr[y0:y1, x0:x1], cv2.COLOR_BGR2Lab).astype(np.float32)
    inner = np.zeros(crop.shape[:2], np.uint8)
    rings = [np.round(r - (x0, y0)).astype(np.int32) for r in _rings(poly)]
    cv2.fillPoly(inner, rings, 255)
    d = diameter(poly)
    k = lambda share: cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (2 * max(1, int(share * d)) + 1,) * 2)
    ring = cv2.dilate(inner, k(0.35)) & ~cv2.dilate(inner, k(0.12))
    core = cv2.erode(inner, k(0.08))
    if core.sum() == 0 or ring.sum() == 0:
        return 100.0
    a, b = crop[core > 0].mean(axis=0), np.median(crop[ring > 0], axis=0)
    return float(np.linalg.norm((a - b) * (100 / 255, 1, 1)))


MIN_BOX_AREA = 0.25
MIN_BOX_EXTENT = 0.6


def is_fragment(det: dict, poly, width: int, height: int) -> bool:
    """True when a traced outline covers only a fragment of its own detector box (a bolt hole, half the hold): the
    app's own sanity check (OpenCvHoldOutlineSession.FitsBox)."""
    if not det.get("outline") or not det.get("boxWidth") or not det.get("boxHeight"):
        return False
    bw, bh = det["boxWidth"] * width, det["boxHeight"] * height
    minx, miny, maxx, maxy = poly.bounds
    return poly.area < MIN_BOX_AREA * bw * bh or maxx - minx < MIN_BOX_EXTENT * bw or maxy - miny < MIN_BOX_EXTENT * bh
