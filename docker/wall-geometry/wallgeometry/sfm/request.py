"""The `solve-sfm` request document (README "Solve from features"): validation into an SfmRequest.

Everything is optional except what a feature needs: per-photo device gravity (+ the stored image size that picks its
axis mapping) and hold detections, segment angle hints, one measured distance, anchors (+ the reference geometry they
are known in), options.
Pixel coordinates are in the photo's stored resolution, OpenCV convention (as the geometry document).
"""
import math
from dataclasses import dataclass, field

from computejobs.geometry import GeometryError, check_geometry

from ..request import DICT_SIZES, RequestError

MAX_PHOTOS = 5000
MAX_HOLDS = 2000
MAX_SEGMENTS = 100
MAX_ANCHORS = 500
DEFAULT_OPTIONS = {"cameraHeightMm": 1450.0, "minFacetAreaM2": 0.4, "seed": 7}
OPTION_RANGES = {"cameraHeightMm": (300.0, 3000.0), "minFacetAreaM2": (0.05, 20.0), "seed": (0, 2 ** 31)}


@dataclass
class SegmentHint:
    index: int
    name: str
    declared_angle_deg: float | None
    vertical_reference: bool


@dataclass
class SfmRequest:
    gravity: dict = field(default_factory=dict)      # stem -> (aX, aY, aZ)
    sizes: dict = field(default_factory=dict)        # stem -> (width, height) of the stored image
    holds: dict = field(default_factory=dict)        # stem -> [(x, y)]
    segments: list = field(default_factory=list)     # [SegmentHint]
    measured: dict | None = None                     # {"photo", "a", "b", "mm"}
    anchors: dict = field(default_factory=dict)      # model stem -> reference camera image
    reference: dict | None = None
    dictionary: str = "DICT_4X4_50"
    marker_size_mm: float = 125.0
    options: dict = field(default_factory=lambda: dict(DEFAULT_OPTIONS))


def _num(v, what, lo=-1e9, hi=1e9):
    if isinstance(v, bool) or not isinstance(v, (int, float)) or not math.isfinite(v) or not lo <= v <= hi:
        raise RequestError(f"{what} must be a finite number in [{lo:g}, {hi:g}]")
    return float(v)


def _point(v, what):
    if not isinstance(v, (list, tuple)) or len(v) != 2:
        raise RequestError(f"{what} must be [x, y]")
    return (_num(v[0], f"{what}[0]", -1e5, 1e5), _num(v[1], f"{what}[1]", -1e5, 1e5))


def _name(v, what):
    if not isinstance(v, str) or not 0 < len(v) <= 128:
        raise RequestError(f"{what} must be a non-empty string (at most 128 characters)")
    return v


def _photos(req, photos):
    if not isinstance(photos, list) or len(photos) > MAX_PHOTOS:
        raise RequestError(f"'photos' must be a list of at most {MAX_PHOTOS}")
    seen = set()
    for i, p in enumerate(photos):
        if not isinstance(p, dict):
            raise RequestError(f"photos[{i}] must be an object")
        name = _name(p.get("name"), f"photos[{i}].name")
        if name in seen:
            raise RequestError(f"photo {name} is listed twice")
        seen.add(name)
        g = p.get("deviceGravity")
        if g is not None:
            if not isinstance(g, list) or len(g) != 3:
                raise RequestError(f"photos[{i}].deviceGravity must be [aX, aY, aZ]")
            req.gravity[name] = tuple(_num(x, f"photos[{i}].deviceGravity", -20, 20) for x in g)
        size = p.get("imageSize")
        if size is not None:
            if not isinstance(size, list) or len(size) != 2:
                raise RequestError(f"photos[{i}].imageSize must be [width, height]")
            req.sizes[name] = tuple(_num(x, f"photos[{i}].imageSize", 1, 1e5) for x in size)
        holds = p.get("holds")
        if holds is not None:
            if not isinstance(holds, list) or len(holds) > MAX_HOLDS:
                raise RequestError(f"photos[{i}].holds must be a list of at most {MAX_HOLDS} [x, y]")
            req.holds[name] = [_point(h, f"photos[{i}].holds[{k}]") for k, h in enumerate(holds)]


def _segments(req, segs):
    if not isinstance(segs, list) or len(segs) > MAX_SEGMENTS:
        raise RequestError(f"'segments' must be a list of at most {MAX_SEGMENTS}")
    for i, s in enumerate(segs):
        if not isinstance(s, dict):
            raise RequestError(f"segments[{i}] must be an object")
        idx = s.get("index", i)
        if isinstance(idx, bool) or not isinstance(idx, int) or not 0 <= idx < 1000:
            raise RequestError(f"segments[{i}].index must be an integer in [0, 1000)")
        ang = s.get("declaredAngleDeg")
        req.segments.append(SegmentHint(idx, str(s.get("name") or f"segment {idx}")[:128],
                                        None if ang is None else _num(ang, f"segments[{i}].declaredAngleDeg", -90, 90),
                                        bool(s.get("verticalReference", False))))


def _measured(m):
    if not isinstance(m, dict):
        raise RequestError("'measuredDistance' must be an object")
    return {"photo": _name(m.get("photo"), "measuredDistance.photo"), "a": _point(m.get("a"), "measuredDistance.a"),
            "b": _point(m.get("b"), "measuredDistance.b"), "mm": _num(m.get("mm"), "measuredDistance.mm", 10, 1e5)}


def _anchors(req, anchors, reference):
    if not isinstance(anchors, dict) or not 0 < len(anchors) <= MAX_ANCHORS:
        raise RequestError(f"'anchors' must map up to {MAX_ANCHORS} anchor stems to reference camera images")
    try:
        check_geometry(reference)
    except GeometryError as e:
        raise RequestError(f"reference: {e}") from e
    images = {c["image"] for c in reference["cameras"]}
    for stem, image in anchors.items():
        _name(stem, "anchor stem")
        if _name(image, f"anchors[{stem!r}]") not in images:
            raise RequestError(f"anchor {stem}: the reference has no camera {image!r}")
    req.anchors, req.reference = dict(anchors), reference


def _options(req, opts):
    if not isinstance(opts, dict):
        raise RequestError("'options' must be an object")
    for k, v in opts.items():
        if k not in OPTION_RANGES:
            raise RequestError(f"unknown option {k!r} (known: {', '.join(sorted(OPTION_RANGES))})")
        req.options[k] = _num(v, f"options.{k}", *OPTION_RANGES[k])
    req.options["seed"] = int(req.options["seed"])


def parse_sfm_request(doc):
    if not isinstance(doc, dict):
        raise RequestError("the request must be a JSON object")
    req = SfmRequest()
    _photos(req, doc.get("photos", []))
    _segments(req, doc.get("segments", []))
    if doc.get("measuredDistance") is not None:
        req.measured = _measured(doc["measuredDistance"])
    if doc.get("anchors"):
        _anchors(req, doc["anchors"], doc.get("reference"))
    elif doc.get("reference") is not None:
        raise RequestError("'reference' is only used with 'anchors'")
    if doc.get("dictionary") is not None:
        if doc["dictionary"] not in DICT_SIZES:
            raise RequestError(f"dictionary must be one of {sorted(DICT_SIZES)}")
        req.dictionary = doc["dictionary"]
    if doc.get("markerSizeMm") is not None:
        req.marker_size_mm = _num(doc["markerSizeMm"], "markerSizeMm", 1, 5000)
    _options(req, doc.get("options") or {})
    return req
