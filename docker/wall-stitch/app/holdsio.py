"""Translation between the HTTP wire shape and the matcher's own JSON files.

The matcher reads a `holds.json` / `wall.json` pair exported from the app's database,
so the sidecar writes that same pair from the `options.holds` payload. The HTTP
contract carries no wall segmentation, so a single full-frame segment is written and
every hold lands on the main span - which is also the only plane the response models.
"""
from __future__ import annotations

import json
import os
from typing import Any, Dict, List

CONVENTION = (
    "All geometry is NORMALISED against the wall photo, per axis and independently: "
    "X = px/imageWidth, Y = py/imageHeight, both 0..1, origin top-left. Radius is "
    "normalised against the longer side. ShapePoints are offsets from (X, Y).")

MAIN_SEGMENT = "Segment 1"
GENERATION = 1


def write_inputs(directory: str, holds: List[Any], wall_angle_degrees: float) -> Dict[str, str]:
    """Writes holds.json and wall.json; returns their paths."""
    os.makedirs(directory, exist_ok=True)
    holds_path = os.path.join(directory, "holds.json")
    wall_path = os.path.join(directory, "wall.json")

    records = [{
        "Id": hold.id,
        "Generation": GENERATION,
        "X": float(hold.x),
        "Y": float(hold.y),
        "Radius": float(hold.radius or 0.0),
        "Category": int(hold.category),
        "Color": hold.color,
        "BoulderLinkCount": int(hold.boulder_link_count),
        "ShapePoints": [{"Dx": float(p.dx), "Dy": float(p.dy)} for p in (hold.shape_points or [])],
    } for hold in holds]

    _dump(holds_path, {
        "_count": len(records),
        "_coordinateConvention": CONVENTION,
        "holds": records,
    })
    _dump(wall_path, {
        "wall": {"Angle": float(wall_angle_degrees), "CurrentGeneration": GENERATION},
        "segments": [{
            "Name": MAIN_SEGMENT,
            "Kind": 0,
            "Yaw": 0,
            "Angle": float(wall_angle_degrees),
            "SortOrder": 0,
            "Points": [{"Dx": 0.0, "Dy": 0.0}, {"Dx": 1.0, "Dy": 0.0},
                       {"Dx": 1.0, "Dy": 1.0}, {"Dx": 0.0, "Dy": 1.0}],
        }],
        "_coordinateConvention": CONVENTION,
    })
    return {"holds": holds_path, "wall": wall_path}


def read_results(path: str) -> List[Dict[str, Any]]:
    """Reads holds-remapped.json into the wire shape (matched | uncertain | missing)."""
    with open(path, "r", encoding="utf-8") as handle:
        document = json.load(handle)

    out: List[Dict[str, Any]] = []
    for record in document.get("holds", []):
        new = record.get("new") or {}
        old = record.get("old") or {}
        out.append({
            "id": str(record.get("Id")),
            "x": float(new.get("X", old.get("X", 0.0)) or 0.0),
            "y": float(new.get("Y", old.get("Y", 0.0)) or 0.0),
            "radius": float(new.get("Radius", old.get("Radius", 0.0)) or 0.0),
            "shapePoints": [{"dx": float(p["Dx"]), "dy": float(p["Dy"])}
                            for p in new.get("ShapePoints", [])] or None,
            # "moved" is a matcher-internal nuance the app does not model: it is a hold
            # that matched confidently but shifted, which is still a match.
            "classification": _classification(record.get("classification")),
            "confidence": float(record.get("confidence") or 0.0),
        })
    return out


def _classification(value: Any) -> str:
    text = str(value or "").lower()
    if text in ("matched", "moved"):
        return "matched"
    if text == "missing":
        return "missing"
    return "uncertain"


def _dump(path: str, payload: Dict[str, Any]) -> None:
    with open(path, "w", encoding="utf-8") as handle:
        json.dump(payload, handle, indent=1)
