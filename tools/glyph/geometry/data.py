"""Capture 1 -> solve request document (the same JSON the app POSTs to the wall-geometry service).

Everything wall-specific lives in THIS file as request data (segment names, declared angles, which
segments are vertical references). The solver itself (docker/wall-geometry/wallgeometry) has no
knowledge of this wall.
"""
import json
import os

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
GLYPH = os.path.dirname(HERE)
OUT = os.path.join(HERE, "out")
REFINED_PATH = os.path.join(OUT, "refined_corners.json")

MARKER_MM = 125.0

# The 21 markers physically on the wall (owner-verified). Anything else is a false positive; the app
# does the equivalent filtering (0..35, no duplicates per photo) before it sends a request.
WALL_IDS = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 12, 14, 15, 24, 25, 26, 27, 31, 32, 33]

# What the owner declares. Seg 4 is deliberately NOT declared: its spare markers 24-27 sit on seg 0's
# plane and the solver must find that by itself.
SEGMENTS = [
    {"index": 0, "name": "main wall", "declaredAngleDeg": 45.0, "verticalReference": False},
    {"index": 1, "name": "kickboard", "declaredAngleDeg": 0.0, "verticalReference": True},
    {"index": 2, "name": "left triangle", "declaredAngleDeg": 0.0, "verticalReference": True},
    {"index": 5, "name": "right cornered piece", "declaredAngleDeg": None, "verticalReference": False},
]


def load_exif():
    with open(os.path.join(GLYPH, "exif.json")) as fh:
        rows = json.load(fh)
    out = {}
    for r in rows:
        name = r["SourceFile"].split(".")[0]
        f35 = float(str(r["FocalLengthIn35mmFormat"]).split()[0])
        lens = "ultra-wide" if f35 < 20 else "main"
        out[name] = {"f35": f35, "w": int(r["ImageWidth"]), "h": int(r["ImageHeight"]),
                     "group": f"{r.get('Model', 'camera')}|{lens}"}
    return out


def _raw_observations(include_id1=True):
    with open(os.path.join(GLYPH, "detections.json")) as fh:
        det = json.load(fh)["detections"]
    obs = [{"image": d["image"], "id": int(d["id"]), "corners": np.array(d["corners"], float),
            "synthetic": [False] * 4}
           for d in det if not d.get("rejected_manual") and d["id"] in WALL_IDS]
    if include_id1:
        with open(os.path.join(GLYPH, "id1_recovered.json")) as fh:
            r = json.load(fh)
        obs.append({"image": r["image"], "id": int(r["id"]), "corners": np.array(r["corners"], float),
                    "synthetic": [n in r["synthetic_corners"] for n in ["TL", "TR", "BR", "BL"]]})
    return obs


def apply_refinement(obs):
    """Swap in edge-refined corners; computed once from the PNGs (refine.py) and cached in out/."""
    cache = {}
    if os.path.exists(REFINED_PATH):
        with open(REFINED_PATH) as fh:
            cache = json.load(fh)
    dirty = False
    for o in obs:
        key = f"{o['image']}:{o['id']}"
        if key not in cache:
            from refine import refine_corners
            side = float(np.mean(np.linalg.norm(o["corners"] - np.roll(o["corners"], -1, 0), axis=1)))
            c, shift, ok = refine_corners(o["image"], o["corners"], side)
            cache[key] = {"raw": o["corners"].tolist(), "refined": c.tolist(),
                          "shift": shift.tolist(), "ok": ok.tolist()}
            dirty = True
        o["corners"] = np.array(cache[key]["refined"])
    if dirty:
        os.makedirs(OUT, exist_ok=True)
        with open(REFINED_PATH, "w") as fh:
            json.dump(cache, fh, indent=1)


def build_request(include_id1=True, refined=True, level_pairs=None, validate=False):
    """The capture-1 request document (see docker/wall-geometry/README.md for the contract)."""
    obs = _raw_observations(include_id1)
    if refined:
        apply_refinement(obs)
    exif = load_exif()
    photos = {}
    for o in obs:
        e = exif[o["image"]]
        p = photos.setdefault(o["image"], {
            "name": o["image"], "width": e["w"], "height": e["h"], "focal35mm": e["f35"],
            "cameraGroup": e["group"], "markers": []})
        p["markers"].append({"id": o["id"], "corners": np.round(o["corners"], 4).tolist(),
                             "refined": refined, "synthetic": o["synthetic"]})
    doc = {"markerSizeMm": MARKER_MM, "dictionary": "DICT_4X4_50", "idScheme": "segment*6+role",
           "segments": SEGMENTS, "photos": [photos[k] for k in sorted(photos)],
           "options": {"validate": validate}}
    if level_pairs:
        doc["levelPairs"] = [list(p) for p in level_pairs]
    return doc
