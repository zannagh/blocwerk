"""Request options for kind=splat (JSON part `options`)."""
import math
from dataclasses import asdict, dataclass
from typing import Optional

from computejobs.geometry import GeometryError, check_geometry

from .profiles import DEFAULT_QUALITY, QUALITIES, resolve

MATCHERS = ("auto", "exhaustive", "sequential", "pairs")
AUTO_EXHAUSTIVE_MAX = 150  # beyond this many photos `auto` switches to the sequential matcher


class OptionsError(ValueError):
    pass


@dataclass
class SplatOptions:
    quality: str = DEFAULT_QUALITY  # profiles.PROFILES: draft | high | max
    maxSteps: Optional[int] = None  # None = the profile's
    maxImageEdge: Optional[int] = None  # None = the profile's (photos; video frames stay at its frame_edge)
    matcher: str = "auto"
    cropMarginMm: float = 400.0
    spz: bool = True
    colourMatch: bool = True  # ingest matches the video frames' colours to the photos' (colour.py)

    def to_dict(self):
        return asdict(self)

    def profile(self):
        """The quality profile with this request's maxSteps / maxImageEdge overrides."""
        return resolve(self.quality, self.maxSteps, self.maxImageEdge)


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
    if "quality" in doc:
        if doc["quality"] not in QUALITIES:
            raise OptionsError(f"options.quality must be one of {QUALITIES}")
        o.quality = doc["quality"]
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
    if "colourMatch" in doc:
        if not isinstance(doc["colourMatch"], bool):
            raise OptionsError("options.colourMatch must be true or false")
        o.colourMatch = doc["colourMatch"]
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
