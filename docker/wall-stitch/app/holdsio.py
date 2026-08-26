"""Translation between the HTTP wire shape and the pipeline's own JSON files.

The pipeline's carryover stage reads the app's holds export verbatim - the same
`holds.json` shape the database dumps - so the sidecar writes that file from the
`options.holds` payload and reads back `holds-flat.json` (what the detector found) and
`carryover.json` (what became of the wall's existing holds).

Every coordinate on both sides of this module is normalised against the FLAT master,
per axis and independently, with Radius against the longer side.
"""
from __future__ import annotations

import json
import os
from typing import Any, Dict, List, Optional

CONVENTION = (
    "All geometry is NORMALISED against the wall photo, per axis and independently: "
    "X = px/imageWidth, Y = py/imageHeight, both 0..1, origin top-left. Radius is "
    "normalised against the longer side. ShapePoints are offsets from (X, Y).")

GENERATION = 1


def write_prior_holds(directory: str, holds: List[Any]) -> str:
    """Writes the prior-holds export the carryover stage reads. Returns its path."""
    os.makedirs(directory, exist_ok=True)
    path = os.path.join(directory, "holds.json")
    records = [{
        "Id": hold.id,
        "Generation": int(getattr(hold, "generation", 0) or GENERATION),
        "X": float(hold.x),
        "Y": float(hold.y),
        "Radius": float(hold.radius or 0.0),
        "Category": int(hold.category),
        "Color": hold.color,
        "BoulderLinkCount": int(hold.boulder_link_count),
        "ShapePoints": [{"Dx": float(p.dx), "Dy": float(p.dy)}
                        for p in (hold.shape_points or [])],
    } for hold in holds]
    _dump(path, {"_count": len(records), "_coordinateConvention": CONVENTION,
                 "holds": records})
    return path


def read_detections(path: str) -> List[Dict[str, Any]]:
    """`holds-flat.json` -> the wire shape. Boxes become centre + radius."""
    document = _load(path)
    out: List[Dict[str, Any]] = []
    for row in document.get("holds", []):
        out.append({
            "id": str(row.get("id")),
            "x": float(row.get("x") or 0.0),
            "y": float(row.get("y") or 0.0),
            # The detector emits a box; the app models a disc. Half the longer side is
            # the radius that encloses it, which is what the pipeline's own carryover
            # stage uses too, so both hold lists agree on what "radius" means.
            "radius": max(float(row.get("w") or 0.0), float(row.get("h") or 0.0)) / 2.0,
            "confidence": float(row.get("confidence") or 0.0),
        })
    return out


def read_carryover(path: str) -> Optional[Dict[str, Any]]:
    """`carryover.json` -> the wire shape, split into carried / missing / new."""
    document = _load(path)
    if not document:
        return None

    carried: List[Dict[str, Any]] = []
    missing: List[Dict[str, Any]] = []
    for record in document.get("holds", []):
        row = _carried(record)
        (carried if row["classification"] == "CARRIED_OVER" else missing).append(row)

    new = [{
        "detectionId": str(d.get("detection_id")),
        "x": float((d.get("new") or {}).get("X") or 0.0),
        "y": float((d.get("new") or {}).get("Y") or 0.0),
        "radius": float((d.get("new") or {}).get("Radius") or 0.0),
        "confidence": float(d.get("confidence") or 0.0),
        "likelyDuplicate": bool(d.get("likely_duplicate_of_carried_hold")),
    } for d in document.get("new_detections", [])]

    quality = document.get("_quality") or {}
    return {
        "generation": int(document.get("_generation") or 0),
        "counts": {str(k): int(v) for k, v in (document.get("_counts") or {}).items()},
        "countsBoulderLinked": {
            str(k): int(v) for k, v in (document.get("_counts_boulder_linked") or {}).items()},
        "estimatedPrecision": float(quality.get("estimated_precision") or 0.0),
        # Carried straight through rather than summarised: it is the pipeline's own
        # standing caveat that this match must not be applied without a human looking
        # at the overlay, and the app decides what to do with it.
        "blocker": str(document.get("_blocker") or ""),
        "carried": carried,
        "missing": missing,
        "new": new,
    }


def _carried(record: Dict[str, Any]) -> Dict[str, Any]:
    new = record.get("new") or {}
    points = [{"dx": float(p["Dx"]), "dy": float(p["Dy"])}
              for p in (new.get("ShapePoints") or [])]
    return {
        "id": str(record.get("Id")),
        "x": float(new.get("X") or 0.0),
        "y": float(new.get("Y") or 0.0),
        "radius": float(new.get("Radius") or 0.0),
        "shapePoints": points or None,
        "classification": str(record.get("classification") or "MISSING"),
        "matchedDetectionId": _optional_str(record.get("matched_detection_id")),
        "matchDistancePx": _optional_float(record.get("match_distance_px")),
        "colourAgrees": record.get("colour_agrees"),
        "boulderLinkCount": int(record.get("BoulderLinkCount") or 0),
        "inFrame": bool(record.get("in_frame", True)),
        "reason": str(record.get("reason") or ""),
    }


def _optional_str(value: Any) -> Optional[str]:
    return None if value is None else str(value)


def _optional_float(value: Any) -> Optional[float]:
    return None if value is None else float(value)


def _load(path: str) -> Dict[str, Any]:
    try:
        with open(path, "r", encoding="utf-8") as handle:
            return json.load(handle)
    except (OSError, json.JSONDecodeError):
        return {}


def _dump(path: str, payload: Dict[str, Any]) -> None:
    with open(path, "w", encoding="utf-8") as handle:
        json.dump(payload, handle, indent=1)
