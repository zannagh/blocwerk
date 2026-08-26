"""Turning a finished pipeline run into the job result.

`manifest.json` is the contract between the pipeline and this service: artifact
filenames are looked up in it rather than hardcoded here, so the pipeline can rename an
output without breaking the sidecar. Everything this module publishes lands in the job's
artifact directory under a stable name, because that name is what the API serves.
"""
from __future__ import annotations

import json
import os
import re
import shutil
from typing import Callable, Dict, List, Optional

from . import holdsio
from .config import Settings
from .imaging import dimensions, write_display_copy

# A view name becomes part of a served artifact filename, so it is constrained rather
# than trusted. The set of names is open on purpose: the display projection is not
# settled, and a new one must not need a change here to be servable.
SAFE_VIEW_NAME = re.compile(r"^[a-z0-9][a-z0-9-]{0,31}$")


class Publisher:
    """Copies artifacts out of the pipeline's work directory under served names."""

    def __init__(self, work_dir: str, artifact_dir: str, settings: Settings):
        self.work_dir = work_dir
        self.artifact_dir = artifact_dir
        self.settings = settings
        os.makedirs(artifact_dir, exist_ok=True)

    def publish(self, name, target: str) -> Optional[str]:
        """Copy an artifact the manifest names to `target`. None if it is not there."""
        if not name:
            return None
        source = os.path.join(self.work_dir, str(name))
        if not os.path.isfile(source):
            return None
        destination = os.path.join(self.artifact_dir, target)
        shutil.copyfile(source, destination)
        return destination

    def display(self, master: Optional[str], name: str) -> str:
        if not master:
            return ""
        write_display_copy(master, os.path.join(self.artifact_dir, name),
                           self.settings.display_max_edge,
                           self.settings.display_jpeg_quality)
        return name

    def read(self, name, reader: Callable[[str], object]):
        if not name:
            return None
        path = os.path.join(self.work_dir, str(name))
        return reader(path) if os.path.isfile(path) else None


def assemble(manifest: Dict, publisher: Publisher,
             photos: List[str]) -> Optional[Dict[str, object]]:
    """The wire result, or None when the pipeline left no usable master behind."""
    artifacts = manifest.get("artifacts") or {}
    flat = publisher.publish(artifacts.get("flat_base"), "flat.jpg")
    natural = publisher.publish(artifacts.get("natural_default"), "natural.jpg")
    if flat is None or natural is None:
        return None
    cameras = publisher.publish(artifacts.get("cameras"), "cameras.json")
    wall = manifest.get("wall") or {}

    return {
        "flatMaster": _ref("flat.jpg", flat),
        "naturalMaster": _ref("natural.jpg", natural),
        "displayFlat": publisher.display(flat, "display-flat.jpg"),
        "displayNatural": publisher.display(natural, "display-natural.jpg"),
        "camerasJson": os.path.basename(cameras) if cameras else "",
        "coordinateConvention": str(manifest.get("coordinate_convention") or ""),
        "wallWidthM": float(wall.get("width_m") or 0.0),
        "wallHeightM": float(wall.get("height_m") or 0.0),
        "curvature": curvature(manifest, publisher),
        "holds": publisher.read(artifacts.get("holds_flat"), holdsio.read_detections),
        "holdsNatural": publisher.read(artifacts.get("holds_natural"),
                                       holdsio.read_detections),
        "carryover": carryover(manifest, publisher),
        "diagnostics": diagnostics(manifest, photos),
    }


def curvature(manifest: Dict, publisher: Publisher) -> Optional[Dict[str, object]]:
    """Every emitted curvature, published so the operator can pick after seeing them."""
    curve = manifest.get("curvature") or {}
    emitted = curve.get("emitted") or {}
    if not emitted:
        return None
    wall = manifest.get("wall") or {}
    curves = []
    for name, entry in emitted.items():
        if not SAFE_VIEW_NAME.match(str(name)):
            continue
        artifact, display = f"natural-{name}.jpg", f"display-natural-{name}.jpg"
        published = publisher.publish(entry.get("image"), artifact)
        if published is None:
            continue
        publisher.display(published, display)
        curves.append({
            "name": name, "artifact": artifact, "display": display,
            "width": int(entry.get("width") or 0),
            "height": int(entry.get("height") or 0),
            "thetaMaxDeg": _optional_float(entry.get("theta_max_deg")),
            "k": _optional_float(entry.get("k")),
            "radiusM": _optional_float(entry.get("radius_m")),
        })
    return {
        "projection": str(curve.get("projection") or ""),
        "default": str(curve.get("default") or ""),
        "requestedThetaMaxDeg": float(curve.get("requested_theta_max_deg") or 0.0),
        "viewDistM": float(wall.get("view_dist_m") or 0.0),
        "eyeFrac": float(wall.get("eye_frac") or 0.0),
        # Gentlest first under a curved projection; a projection with no curvature
        # keeps whatever order it emitted.
        "curves": sorted(curves, key=lambda c: (c["thetaMaxDeg"] is None,
                                                c["thetaMaxDeg"] or 0.0)),
    }


def carryover(manifest: Dict, publisher: Publisher) -> Optional[Dict[str, object]]:
    result = publisher.read((manifest.get("artifacts") or {}).get("carryover"),
                            holdsio.read_carryover)
    if not result:
        return None
    # carryover.json does not restate which hold generation it worked from; the manifest
    # does, and the app needs it to know what this result supersedes.
    result["generation"] = int((manifest.get("carryover") or {}).get("generation") or 0)
    return result


def diagnostics(manifest: Dict, photos: List[str]) -> Dict[str, object]:
    inputs = manifest.get("inputs") or {}
    plane = manifest.get("plane") or {}
    used = [os.path.basename(p) for p in photos]
    kept = set(inputs.get("frames") or [])

    warnings: List[str] = []
    inpainted = int((plane.get("finish") or {}).get("inpainted_px") or 0)
    flat = manifest.get("flat_base") or {}
    area = int(flat.get("width") or 0) * int(flat.get("height") or 0)
    if area and inpainted / area > 0.01:
        warnings.append(
            "Over 1% of the wall had to be filled in because no photo covered it; the "
            "sweep has a gap.")
    detection = manifest.get("detection") or {}
    if detection and not detection.get("final"):
        warnings.append("No holds were detected on the wall.")
    # How hard the sweep had to stretch to reach the reference frame's plane. Above a
    # few times, the composite is still recognisable but visibly smeared at the edges,
    # which is a shooting problem the operator can fix by walking further back — not
    # something the blend can rescue.
    stretch = (manifest.get("registration") or {}).get("reference_area_stretch")
    if isinstance(stretch, (int, float)) and stretch > 4.0:
        warnings.append(
            "No photo faces the wall squarely enough to composite onto cleanly, so the "
            "edges are stretched. Shoot a few frames from straight in front of the wall.")

    return {
        "imagesUsed": used,
        "imagesRejected": [{"name": n, "reason": "no usable overlap with the other photos"}
                           for n in used if kept and n not in kept],
        "referenceFrame": str(inputs.get("reference_frame") or ""),
        # How far the composite's verticals are from vertical, in degrees. The manifest
        # carries the whole line census; this is the one number worth surfacing, and a
        # large value means the plane is skewed however clean the blend looks.
        "straightness": float((plane.get("straightness") or {}).get("vertical_rms_deg") or 0.0),
        "inpaintedPx": inpainted,
        "elapsedSeconds": float(manifest.get("elapsed_seconds") or 0.0),
        "coverageWarnings": warnings,
    }


def read_manifest(work_dir: str) -> Dict:
    try:
        with open(os.path.join(work_dir, "manifest.json"), "r", encoding="utf-8") as handle:
            return json.load(handle)
    except (OSError, json.JSONDecodeError):
        return {}


def _optional_float(value) -> Optional[float]:
    return None if value is None else float(value)


def _ref(artifact: str, path: str) -> Dict[str, object]:
    width, height = dimensions(path)
    return {"artifact": artifact, "width": width, "height": height}
