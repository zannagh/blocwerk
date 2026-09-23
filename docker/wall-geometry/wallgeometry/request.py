"""Solve request document: validation and conversion to solver observations.

The request carries everything the solver knows about the wall. Nothing about a specific wall is
hardcoded anywhere else: segment names, declared angles, which segments are vertical references and
which marker pairs are level all come from here. See README.md for the exact contract.
"""
from dataclasses import dataclass, field

import numpy as np

from .camera import exif_focal_px

ROLES = ["TL", "TR", "BR", "BL", "H", "V"]
ROLES_PER_SEGMENT = 6
SYNTH_SIGMA_PX = 15.0  # pixel sigma for corners flagged synthetic (e.g. rebuilt past the frame edge)
DICT_SIZES = {"DICT_4X4_50": 50, "DICT_4X4_100": 100, "DICT_4X4_250": 250, "DICT_4X4_1000": 1000}
LEGACY_SCHEME, PLAN_SCHEME = "segment*6+role", "plan"
SUPPORTED_SCHEMES = {LEGACY_SCHEME, PLAN_SCHEME}
MAX_SEGMENTS = 100
MAX_LEVEL_PAIRS = 500
MAX_NAME = 128


class RequestError(ValueError):
    """The request document is invalid. The message is safe to return to the client."""


@dataclass
class Segment:
    index: int
    name: str
    declared_angle_deg: float | None
    vertical_reference: bool


@dataclass
class SolveRequest:
    marker_size_mm: float
    dictionary: str
    id_scheme: str
    segments: dict
    level_pairs: list
    photos: list
    options: dict
    size_overrides_mm: dict = field(default_factory=dict)
    marker_segments: dict = field(default_factory=dict)

    def marker_size(self, mid):
        return self.size_overrides_mm.get(mid, self.marker_size_mm)

    def segment_of(self, mid):
        """Nominal segment: the plan's assignment when given, else the legacy `id // 6`."""
        return self.marker_segments.get(mid, mid // ROLES_PER_SEGMENT)

    def role_of(self, mid):
        """Legacy role name; a plan's ids carry no role."""
        return None if self.id_scheme == PLAN_SCHEME else ROLES[mid % ROLES_PER_SEGMENT]

    def segment_name(self, idx):
        s = self.segments.get(idx)
        return s.name if s else f"segment {idx} (undeclared)"


def marker_object_points(size_mm):
    """Corners in the marker frame, OpenCV IPPE_SQUARE order TL,TR,BR,BL; +z out of the wall."""
    h = size_mm / 2.0
    return np.array([[-h, h, 0], [h, h, 0], [h, -h, 0], [-h, -h, 0]], dtype=np.float64)


def _num(v, what, lo=None, hi=None):
    if isinstance(v, bool) or not isinstance(v, (int, float)) or not np.isfinite(v):
        raise RequestError(f"{what} must be a finite number")
    if (lo is not None and v < lo) or (hi is not None and v > hi):
        raise RequestError(f"{what} must be within [{lo}, {hi}]")
    return float(v)


def _req(d, key, where):
    if not isinstance(d, dict) or key not in d:
        raise RequestError(f"{where}: missing '{key}'")
    return d[key]


def _parse_segments(raw):
    if not isinstance(raw, list) or not raw or len(raw) > MAX_SEGMENTS:
        raise RequestError(f"'segments' must be a non-empty list of at most {MAX_SEGMENTS}")
    out = {}
    for i, s in enumerate(raw):
        idx = _req(s, "index", f"segments[{i}]")
        if not isinstance(idx, int) or isinstance(idx, bool) or not 0 <= idx < MAX_SEGMENTS * 10:
            raise RequestError(f"segments[{i}].index must be an integer in [0, {MAX_SEGMENTS * 10 - 1}]")
        if idx in out:
            raise RequestError(f"segment {idx} declared twice")
        ang = s.get("declaredAngleDeg")
        out[idx] = Segment(idx, str(s.get("name") or f"segment {idx}")[:MAX_NAME],
                           None if ang is None else _num(ang, f"segments[{i}].declaredAngleDeg", -90, 90),
                           bool(s.get("verticalReference", False)))
    return out


def _parse_marker(m, where, max_id):
    mid = _req(m, "id", where)
    if not isinstance(mid, int) or isinstance(mid, bool) or not 0 <= mid < max_id:
        raise RequestError(f"{where}.id must be an integer in [0, {max_id - 1}]")
    c = np.asarray(_req(m, "corners", where), dtype=object)
    if c.shape != (4, 2):
        raise RequestError(f"{where}.corners must be 4 [x, y] pairs (TL, TR, BR, BL)")
    corners = np.array([[_num(v, f"{where}.corners") for v in p] for p in c], dtype=np.float64)
    syn = m.get("synthetic", [False] * 4)
    if isinstance(syn, bool):
        syn = [syn] * 4
    if not isinstance(syn, list) or len(syn) != 4:
        raise RequestError(f"{where}.synthetic must be a bool or 4 bools")
    sigma = np.where(np.array(syn, bool), SYNTH_SIGMA_PX, 1.0)
    if m.get("sigmaPx") is not None:
        sigma = np.maximum(sigma, _num(m["sigmaPx"], f"{where}.sigmaPx", 0.1, 100))
    return {"id": mid, "corners": corners, "sigma": sigma, "synthetic": bool(any(syn)),
            "synthetic_corners": [ROLES[k] for k in range(4) if syn[k]],
            "refined": bool(m.get("refined", True))}


def _parse_photo(p, i, max_id, limits):
    where = f"photos[{i}]"
    name = _req(p, "name", where)
    if not isinstance(name, str) or not 0 < len(name) <= MAX_NAME:
        raise RequestError(f"{where}.name must be a non-empty string of at most {MAX_NAME} characters")
    w = int(_num(_req(p, "width", where), f"{where}.width", 16, 20000))
    h = int(_num(_req(p, "height", where), f"{where}.height", 16, 20000))
    f35, fpx = p.get("focal35mm"), p.get("focalPx")
    if fpx is None and f35 is None:
        raise RequestError(f"{where}: need 'focal35mm' (EXIF FocalLengthIn35mmFormat) or 'focalPx'")
    f0 = _num(fpx, f"{where}.focalPx", 10, 1e6) if fpx is not None else \
        exif_focal_px(_num(f35, f"{where}.focal35mm", 1, 2000), w, h)
    group = str(p.get("cameraGroup") or f"f35={f35}")[:MAX_NAME]
    markers = p.get("markers", [])
    if not isinstance(markers, list):
        raise RequestError(f"{where}.markers must be a list")
    if len(markers) > limits.get("max_markers_per_photo", 200):
        raise RequestError(f"{where}: too many markers")
    parsed, seen = [], set()
    for k, m in enumerate(markers):
        mk = _parse_marker(m, f"{where}.markers[{k}]", max_id)
        if mk["id"] in seen:
            raise RequestError(f"{where}: marker id {mk['id']} appears twice in one photo "
                               "(a false positive; reject duplicates before sending)")
        seen.add(mk["id"])
        parsed.append(mk)
    return {"name": name, "width": w, "height": h, "focal_px": f0, "group": group, "markers": parsed}


def parse_request(doc, limits=None):
    limits = limits or {}
    if not isinstance(doc, dict):
        raise RequestError("request must be a JSON object")
    dictionary = str(doc.get("dictionary", "DICT_4X4_50"))
    if dictionary not in DICT_SIZES:
        raise RequestError(f"unsupported dictionary {dictionary}; supported: {sorted(DICT_SIZES)}")
    scheme = str(doc.get("idScheme", "segment*6+role"))
    if scheme not in SUPPORTED_SCHEMES:
        raise RequestError(f"unsupported idScheme {scheme}")
    # A plan may use every id of the dictionary; the legacy scheme only whole segments of six.
    max_id = DICT_SIZES[dictionary] if scheme == PLAN_SCHEME else \
        DICT_SIZES[dictionary] // ROLES_PER_SEGMENT * ROLES_PER_SEGMENT
    size = _num(_req(doc, "markerSizeMm", "request"), "markerSizeMm", 5, 2000)
    overrides = {}
    raw_overrides = doc.get("markerSizeOverridesMm") or {}
    if not isinstance(raw_overrides, dict) or len(raw_overrides) > max_id:
        raise RequestError(f"'markerSizeOverridesMm' must be an object with at most {max_id} entries")
    for k, v in raw_overrides.items():
        if not (isinstance(k, str) and k.isdigit() and int(k) < max_id):
            raise RequestError(f"markerSizeOverridesMm keys must be marker ids in [0, {max_id - 1}]")
        overrides[int(k)] = _num(v, f"markerSizeOverridesMm[{k}]", 5, 2000)
    segments = _parse_segments(doc.get("segments"))
    marker_segments = _parse_marker_segments(doc.get("markerSegments"), scheme, max_id)
    photos_raw = doc.get("photos")
    if not isinstance(photos_raw, list) or not photos_raw:
        raise RequestError("'photos' must be a non-empty list")
    if len(photos_raw) > limits.get("max_photos", 200):
        raise RequestError(f"too many photos (max {limits.get('max_photos', 200)})")
    photos = [_parse_photo(p, i, max_id, limits) for i, p in enumerate(photos_raw)]
    if scheme == PLAN_SCHEME:
        unplanned = sorted({m["id"] for p in photos for m in p["markers"]} - set(marker_segments))
        if unplanned:
            raise RequestError(f"marker id(s) {unplanned} are not in 'markerSegments' (send planned markers only)")
    names = [p["name"] for p in photos]
    if len(set(names)) != len(names):
        raise RequestError("photo names must be unique")
    pairs = []
    raw_pairs = doc.get("levelPairs") or []
    if not isinstance(raw_pairs, list) or len(raw_pairs) > MAX_LEVEL_PAIRS:
        raise RequestError(f"'levelPairs' must be a list of at most {MAX_LEVEL_PAIRS} pairs")
    for i, pr in enumerate(raw_pairs):
        if not (isinstance(pr, list) and len(pr) == 2
                and all(isinstance(v, int) and not isinstance(v, bool) and 0 <= v < max_id for v in pr)
                and pr[0] != pr[1]):
            raise RequestError(f"levelPairs[{i}] must be two distinct marker ids")
        pairs.append((pr[0], pr[1]))
    options = _parse_options(doc.get("options"))
    return SolveRequest(size, dictionary, scheme, segments, pairs, photos, options, overrides, marker_segments)


def _parse_marker_segments(raw, scheme, max_id):
    """Optional {"<marker id>": segmentIndex} (required for the "plan" scheme): where each marker sits."""
    if raw is None:
        if scheme == PLAN_SCHEME:
            raise RequestError("idScheme 'plan' needs 'markerSegments' (marker id -> segment index)")
        return {}
    if not isinstance(raw, dict) or not raw or len(raw) > max_id:
        raise RequestError(f"'markerSegments' must be a non-empty object with at most {max_id} entries")
    out = {}
    for k, v in raw.items():
        if not (isinstance(k, str) and k.isdigit() and int(k) < max_id):
            raise RequestError(f"markerSegments keys must be marker ids in [0, {max_id - 1}]")
        if isinstance(v, bool) or not isinstance(v, int) or not 0 <= v < MAX_SEGMENTS * 10:
            raise RequestError(f"markerSegments[{k}] must be a segment index in [0, {MAX_SEGMENTS * 10 - 1}]")
        out[int(k)] = v
    return out


FACET_OPTIONS = {"foldDeg": (0.1, 45.0), "mergeDeg": (0.1, 45.0), "mergeMm": (1.0, 1000.0)}


def _parse_options(raw):
    """Only the documented options, each checked (unknown keys are refused, not silently ignored)."""
    options = raw or {}
    if not isinstance(options, dict):
        raise RequestError("'options' must be an object")
    unknown = set(options) - {"validate", "autoDownweight", "facets"}
    if unknown:
        raise RequestError(f"unknown option(s) {sorted(unknown)}; known: autoDownweight, facets, validate")
    for k in ("validate", "autoDownweight"):
        if k in options and not isinstance(options[k], bool):
            raise RequestError(f"options.{k} must be true or false")
    facets = options.get("facets")
    if facets is None:
        return options
    if not isinstance(facets, dict):
        raise RequestError("options.facets must be an object")
    unknown = set(facets) - set(FACET_OPTIONS) - {"minMarkersPerFacet"}
    if unknown:
        raise RequestError(f"unknown options.facets key(s) {sorted(unknown)}")
    clean = {k: _num(v, f"options.facets.{k}", *FACET_OPTIONS[k]) for k, v in facets.items() if k in FACET_OPTIONS}
    if "minMarkersPerFacet" in facets:
        v = facets["minMarkersPerFacet"]
        if isinstance(v, bool) or not isinstance(v, int) or not 1 <= v <= 50:
            raise RequestError("options.facets.minMarkersPerFacet must be an integer in [1, 50]")
        clean["minMarkersPerFacet"] = v
    return {**options, "facets": clean}


def observations(req):
    """Flat observation list (one per marker per photo), the solver's working representation."""
    obs = []
    for p in req.photos:
        for m in p["markers"]:
            obs.append({"image": p["name"], "id": m["id"], "corners": m["corners"].copy(),
                        "sigma": m["sigma"].copy(), "synthetic": m["synthetic"],
                        "synthetic_corners": m["synthetic_corners"],
                        "seg": req.segment_of(m["id"]),
                        "obj": marker_object_points(req.marker_size(m["id"])),
                        "img_w": p["width"], "img_h": p["height"]})
    return obs
