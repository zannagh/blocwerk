"""Shape + range check of a client-supplied wall-geometry document (tools/glyph/wall-geometry.schema.md).

Both workers take a solved geometry from the client (textures: to render; splat: to align and crop).
Everything the jobs read is checked here: types, finite numbers, sane magnitudes and counts, so a
malformed or hostile document is a 422 at submission instead of a crash or a memory blow-up later.
"""
import math

MAX_CAMERAS = 1000
MAX_SEGMENTS = 100
MAX_FACETS = 64          # over all segments
MAX_MARKERS = 5000
MAX_ABS_MM = 1e6         # 1 km: no wall is bigger
MAX_IMAGE_SIDE = 20000
DICTIONARIES = {"DICT_4X4_50", "DICT_4X4_100", "DICT_4X4_250", "DICT_4X4_1000"}


class GeometryError(ValueError):
    """The geometry document is unusable; the message is safe to return to the client."""


def _nums(v, n, what, bound=MAX_ABS_MM):
    if not isinstance(v, list):
        raise GeometryError(f"{what} must be a list of {n} numbers")
    flat = [x for row in v for x in (row if isinstance(row, list) else [row])]
    if len(flat) != n or not all(isinstance(x, (int, float)) and not isinstance(x, bool)
                                 and math.isfinite(x) and abs(x) <= bound for x in flat):
        raise GeometryError(f"{what} must be {n} finite numbers (|x| <= {bound:g})")
    return flat


def _num(v, what, lo, hi):
    if isinstance(v, bool) or not isinstance(v, (int, float)) or not math.isfinite(v) or not lo <= v <= hi:
        raise GeometryError(f"{what} must be a finite number in [{lo:g}, {hi:g}]")
    return v


def _camera(c, i, full):
    where = f"geometry.cameras[{i}]"
    if not isinstance(c, dict) or not isinstance(c.get("image"), str) or not 0 < len(c["image"]) <= 128:
        raise GeometryError(f"{where}.image must be a non-empty string")
    _nums(c.get("R"), 9, f"{where}.R", 10)
    _nums(c.get("t"), 3, f"{where}.t")
    if full or c.get("K") is not None:
        _nums(c.get("K"), 9, f"{where}.K")
    if full or c.get("dist") is not None:
        d = c.get("dist")
        if not isinstance(d, list) or not 0 < len(d) <= 14:
            raise GeometryError(f"{where}.dist must be a list of 1..14 numbers")
        _nums(d, len(d), f"{where}.dist", 1e3)
    for k in ("width", "height"):
        if full or c.get(k) is not None:
            v = c.get(k)
            if isinstance(v, bool) or not isinstance(v, int) or not 16 <= v <= MAX_IMAGE_SIDE:
                raise GeometryError(f"{where}.{k} must be an integer in [16, {MAX_IMAGE_SIDE}]")


def _facet(f, where):
    if not isinstance(f, dict):
        raise GeometryError(f"{where} must be an object")
    for k in ("origin", "u", "v", "normal"):
        _nums(f.get(k), 3, f"{where}.{k}")
    e = f.get("extentMm")
    if not isinstance(e, dict):
        raise GeometryError(f"{where}.extentMm must be an object")
    vals = [_num(e.get(k), f"{where}.extentMm.{k}", -MAX_ABS_MM, MAX_ABS_MM) for k in ("aMin", "aMax", "bMin", "bMax")]
    if vals[0] > vals[1] or vals[2] > vals[3]:
        raise GeometryError(f"{where}.extentMm: min must not exceed max")


def _marker(m, i):
    where = f"geometry.markers[{i}]"
    if not isinstance(m, dict) or isinstance(m.get("id"), bool) or not isinstance(m.get("id"), int):
        raise GeometryError(f"{where}.id must be an integer")
    _nums(m.get("cornersPlaneMm"), 8, f"{where}.cornersPlaneMm")
    if m.get("sizeMm") is not None:
        _num(m["sizeMm"], f"{where}.sizeMm", 1, 5000)


def check_geometry(doc, *, textures=False):
    """Raise GeometryError unless `doc` is usable. textures=True also requires what rendering needs
    (intrinsics, image sizes, facet frames, markers, dictionary)."""
    if not isinstance(doc, dict):
        raise GeometryError("'geometry' must be a solved wall-geometry document (a JSON object)")
    cams = doc.get("cameras")
    if not isinstance(cams, list) or not cams or len(cams) > MAX_CAMERAS:
        raise GeometryError(f"'geometry' must have 1..{MAX_CAMERAS} cameras")
    for i, c in enumerate(cams):
        _camera(c, i, textures)
    segs = doc.get("segments")
    if not isinstance(segs, list) or len(segs) > MAX_SEGMENTS or (textures and not segs):
        raise GeometryError(f"'geometry' must contain 'segments' (a list of at most {MAX_SEGMENTS})")
    n_facets = 0
    for i, s in enumerate(segs):
        facets = s.get("facets", []) if isinstance(s, dict) else None
        if not isinstance(facets, list):
            raise GeometryError(f"geometry.segments[{i}].facets must be a list")
        n_facets += len(facets)
        if n_facets > MAX_FACETS:
            raise GeometryError(f"'geometry' has more than {MAX_FACETS} facets")
        for k, f in enumerate(facets):
            _facet(f, f"geometry.segments[{i}].facets[{k}]")
    if textures:
        if doc.get("dictionary") not in DICTIONARIES:
            raise GeometryError(f"geometry.dictionary must be one of {sorted(DICTIONARIES)}")
        _num(doc.get("markerSizeMm"), "geometry.markerSizeMm", 1, 5000)
        markers = doc.get("markers", [])
        if not isinstance(markers, list) or len(markers) > MAX_MARKERS:
            raise GeometryError(f"geometry.markers must be a list of at most {MAX_MARKERS}")
        for i, m in enumerate(markers):
            _marker(m, i)
    return doc
