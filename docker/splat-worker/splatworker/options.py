"""Request options for kind=splat (JSON part `options`)."""
import math
from dataclasses import asdict, dataclass

from computejobs.geometry import GeometryError, check_geometry

MATCHERS = ("auto", "exhaustive", "sequential", "pairs")
AUTO_EXHAUSTIVE_MAX = 150  # beyond this many photos `auto` switches to the sequential matcher


class OptionsError(ValueError):
    pass


@dataclass
class SplatOptions:
    maxSteps: int = 15000
    maxImageEdge: int = 1800
    matcher: str = "auto"
    cropMarginMm: float = 400.0
    spz: bool = True

    def to_dict(self):
        return asdict(self)


def _int(v, name, lo, hi):
    if (isinstance(v, bool) or not isinstance(v, (int, float)) or not math.isfinite(v) or v != int(v)
            or not lo <= v <= hi):
        raise OptionsError(f"options.{name} must be an integer in [{lo}, {hi}]")
    return int(v)


def parse_options(doc):
    if doc is None:
        return SplatOptions()
    if not isinstance(doc, dict):
        raise OptionsError("options must be a JSON object")
    known = set(SplatOptions.__dataclass_fields__)
    unknown = set(doc) - known
    if unknown:
        raise OptionsError(f"unknown option(s) {sorted(unknown)}; known: {sorted(known)}")
    o = SplatOptions()
    if "maxSteps" in doc:
        o.maxSteps = _int(doc["maxSteps"], "maxSteps", 100, 100000)
    if "maxImageEdge" in doc:
        o.maxImageEdge = _int(doc["maxImageEdge"], "maxImageEdge", 480, 4096)
    if "matcher" in doc:
        if doc["matcher"] not in MATCHERS:
            raise OptionsError(f"options.matcher must be one of {MATCHERS}")
        o.matcher = doc["matcher"]
    if "cropMarginMm" in doc:
        o.cropMarginMm = float(_int(doc["cropMarginMm"], "cropMarginMm", 0, 5000))
    if "spz" in doc:
        if not isinstance(doc["spz"], bool):
            raise OptionsError("options.spz must be true or false")
        o.spz = doc["spz"]
    return o


def resolve_matcher(matcher, n_photos, n_frames=0):
    """auto: `pairs` (frames.build_pairs) when video frames came along, else exhaustive up to
    AUTO_EXHAUSTIVE_MAX photos and sequential beyond."""
    if matcher != "auto":
        return matcher
    if n_frames:
        return "pairs"
    return "exhaustive" if n_photos <= AUTO_EXHAUSTIVE_MAX else "sequential"


def validate_geometry(doc):
    """Shape + range check of a solved wall-geometry document (cameras, optional K/dist/size, facets)."""
    try:
        return check_geometry(doc)
    except GeometryError as e:
        raise OptionsError(str(e)) from e
