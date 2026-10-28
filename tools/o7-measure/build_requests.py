"""Turn one exported capture (export.sql) into the wall-geometry requests the app would send.

    python3 build_requests.py capture.json OUT_DIR

Writes OUT_DIR/solve.json (as CaptureComputeDocuments.BuildSolveRequest: plan scheme, declared segments, every photo with
a usable marker), OUT_DIR/geometry.json (the model without carried facets, as SubmitTexturesAsync) and
OUT_DIR/photos.json ({camera name: stored file} for the photos the model solved). Plan fields come from the solved model
(markerSegments, markerSizeOverridesMm), which the app copied from the same plan; unplanned markers are left out.
"""
import json
import os
import sys

QUALITIES = {  # TextureQualityPresets.ToOptionsJson with the default settings
    "standard": None,
    "high": {"mmPerPx": 1.5, "maxSidePx": 8192, "blendViews": 6},
    "maximum": {"mmPerPx": 1.25, "maxSidePx": 8192, "blendViews": 4},
}


def photo_name(index):
    return f"p{index:02d}"


def camera_group(p):
    group, f35 = p.get("cameraGroup"), p.get("focal35mm")
    if group is None or f35 is None:
        return group
    return f"{group}|f35={round(f35, 1):g}"  # C# "0.#"


def solve_request(capture):
    model, decl = capture["model"], capture["declarations"] or {}
    planned = {int(k) for k in model["markerSegments"]}
    photos = []
    for p in capture["photos"]:
        markers = [m for m in (p["markers"] or []) if m.get("ignored") is None and m["id"] in planned]
        if not markers or p.get("focal35mm") is None:
            continue
        photos.append({"name": photo_name(p["index"]), "width": p["width"], "height": p["height"],
                       "focal35mm": p["focal35mm"], "cameraGroup": camera_group(p),
                       "markers": [{"id": m["id"], "corners": m["corners"], "refined": True,
                                    "synthetic": [bool(m.get("synthetic"))] * 4} for m in markers]})
    segments = [s for s in decl.get("segments", []) if s.get("declaredAngleDeg") is not None or s.get("verticalReference")]
    return {"markerSizeMm": model["markerSizeMm"], "dictionary": model.get("dictionary", "DICT_4X4_50"), "idScheme": "plan",
            "markerSizeOverridesMm": model.get("markerSizeOverridesMm") or {}, "markerSegments": model["markerSegments"],
            "segments": sorted(segments, key=lambda s: s["index"]), "levelPairs": decl.get("levelPairs") or [],
            "photos": photos, "options": {"validate": False}}


def texture_geometry(model):
    carried = set(((model.get("quality") or {}).get("registration") or {}).get("carriedFacets") or [])
    doc = json.loads(json.dumps(model))
    for s in doc.get("segments", []):
        s["facets"] = [f for f in s.get("facets", []) if f.get("id") not in carried]
    return doc


def main(src, out):
    with open(src) as fh:
        capture = json.load(fh)
    os.makedirs(out, exist_ok=True)
    geometry = texture_geometry(capture["model"])
    cameras = {c["image"] for c in geometry["cameras"]}
    photos = {photo_name(p["index"]): p["storedPath"] for p in capture["photos"] if photo_name(p["index"]) in cameras}
    solve = solve_request(capture)
    for name, doc in (("solve.json", solve), ("geometry.json", geometry), ("photos.json", photos),
                      ("qualities.json", QUALITIES)):
        with open(os.path.join(out, name), "w") as fh:
            json.dump(doc, fh)
    print(f"solve: {len(solve['photos'])} photos, "
          f"{sum(len(p['markers']) for p in solve['photos'])} observations; textures: {len(photos)} photos")


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2])
