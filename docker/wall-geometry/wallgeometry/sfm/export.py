"""The solve-sfm result as the SAME wall-geometry document v1 as the marker solve (tools/glyph/wall-geometry.schema.md,
wallgeometry/export.py conventions) with `markers: []`, `idScheme: "plan"` (no ids carry meaning), and:
world.frameSource "features", gravitySource (anchors | anchor-fit | device | declared | floor | cameras), scaleKnown,
scaleSource (anchors | measured | anchor-fit | estimate), anchored (anchor-fit: the anchors' similarity, refused
for the frame by the gate but close enough to measure with); quality.sfm {points, planes, anchors, residuals, gravity,
scale}. Cameras: the photos only (not the video frames, not the anchors), K / dist at the stored resolution.
"""
import numpy as np

from ..export import _l, _r
from ..frame import angles
from .cameras import doc_intrinsics, reprojection_rms


def _facet_doc(F, ang, known):
    return {"id": F["id"], "origin": _l(F["origin"], 2), "u": _l(F["u"], 6), "v": _l(F["v"], 6),
            "normal": _l(F["n"], 6), "measuredAngleDeg": _r(ang["tiltDeg"]) if known else None,
            "yawDeg": _r(ang["yawDeg"]) if known else None,
            "angleToReferenceFacetDeg": _r(ang["angleToReferenceDeg"]),
            "extentMm": {k: round(v, 1) for k, v in F["extent"].items()}, "markerIds": []}


def _segments(sol):
    known = sol["gravity"]["known"]
    normals = {F["id"]: F["n"] for F in sol["facets"]}
    ang = angles(normals, sol["up"], "0")
    out = []
    for F in sol["facets"]:
        f = _facet_doc(F, ang[F["id"]], known)
        out.append({"index": int(F["id"]), "name": "Main surface" if F["id"] == "0" else f"Surface {F['id']}",
                    "declared": False, "declaredAngleDeg": None, "measuredAngleDeg": f["measuredAngleDeg"],
                    "declaredVsMeasuredDeg": None, "angleIsGravityReference": False, "facets": [f]})
    return out


def _cameras(sol):
    model, A, b, s = sol["model"], sol["A"], sol["b"], sol["s"]
    rms = reprojection_rms(model)
    out = []
    for im in model["images"]:
        if im["role"] != "photo":
            continue
        cam = model["cams"][im["cam"]]
        K, dist = doc_intrinsics(cam)
        Rw = im["R"] @ A.T
        out.append({"image": im["stem"], "group": f"sfm-camera-{im['cam']}", "width": cam["width"],
                    "height": cam["height"], "K": _l(K, 3), "dist": _l(dist, 6), "R": _l(Rw, 6),
                    "t": _l(s * im["t"] - Rw @ b, 2), "reprojRmsPx": _r(rms.get(im["name"]))})
    return sorted(out, key=lambda c: c["image"])


def _plane_rows(sol):
    ids = {id(F["plane"]): F["id"] for F in sol["facets"]}
    rows = []
    for k, pl in enumerate(sorted(sol["planes"], key=lambda p: -p["npts"])):
        c = sol["A"] @ pl["c"] + sol["b"]
        rows.append({"index": k, "facet": ids.get(id(pl)), "accepted": pl["accepted"], "reason": pl["reason"],
                     "points": pl["npts"], "areaM2": _r(pl["areaM2"], 2), "rmsMm": _r(pl["rmsMm"], 1),
                     "facing": _r(pl["facing"], 2), "holdHitShare": _r(pl["holdHitShare"], 3),
                     "holdHits": pl.get("holdHits"), "referenceFacet": pl.get("refFacet"),
                     "score": _r(pl["score"], 2), "mergedSlabs": pl.get("members", 1),
                     "normal": _l(sol["A"] @ pl["n"], 4), "centreMm": _l(c, 0),
                     "tiltDeg": _r(np.degrees(np.arcsin(np.clip(-(sol["A"] @ pl["n"]) @ sol["up"], -1, 1))), 2)})
    return rows


def _residuals(sol):
    out = {}
    for F in sol["facets"]:
        d = (F["P"] - F["c"]) @ F["n"]
        out[F["id"]] = {"points": int(len(d)), "rmsMm": _r(np.sqrt((d ** 2).mean()), 2),
                        "medianAbsMm": _r(np.median(np.abs(d)), 2)}
    return out


def _warnings(sol):
    w = []
    if not sol["scale"]["known"]:
        w.append("scale is an estimate (camera height or camera distance prior, about +-10-15 %): "
                 "measure one distance or add anchors for exact millimetres")
    if not sol["gravity"]["known"]:
        w.append("gravity unknown: the reference facet is treated as vertical, angles are not measured")
    a = sol["anchors"]
    if a is not None and not a.get("ok") and a.get("usedFor"):
        w.append(f"anchors not used for the frame: {a.get('reason')}; their fit gave the {a['usedFor']}")
    elif a is not None and not a.get("ok"):
        w.append(f"anchors not used: {a.get('reason')}")
    return w


def _quality(sol):
    m, pts, g = sol["model"], sol["pts"], sol["gravity"]
    registered = {im["stem"] for im in m["images"]}
    asked = set(sol["req"].gravity) | set(sol["req"].holds)
    return {"reprojRmsPx": _r(np.sqrt((m["err"] ** 2).mean())) if len(m["err"]) else None,
            "gravity": {"device": "device accelerometer", "declared": "declared segment angles",
                        "floor": "floor plane", "anchors": "anchors (the reference model's gravity)",
                        "anchor-fit": "anchor fit (the reference model's gravity, frame refused)"}
            .get(g["source"], "unknown") if g["known"] else "unknown",
            "gravityDetail": {k: v for k, v in g.items() if k != "known"},
            "checks": {"declaredVsMeasuredDeg": {}, "warnings": _warnings(sol)},
            "facetDecisions": [], "unusedPhotos": sorted(asked - registered),
            "sfm": {"points": {"total": pts["total"], "used": int(len(pts["Q"])),
                               "medianNearestCameraMm": _r(pts["spread"] * sol["s"], 1),
                               "tolMm": _r(0.012 * pts["spread"] * sol["s"], 2), "holdRays": sol["rays"]},
                    "images": {r: sum(1 for im in m["images"] if im["role"] == r) for r in ("photo", "frame", "anchor")},
                    "planes": _plane_rows(sol), "anchors": sol["anchors"], "residuals": _residuals(sol),
                    "gravity": {"source": g["source"], "known": g["known"]},
                    "scale": {k: (_r(v, 6) if isinstance(v, float) else v) for k, v in sol["scale"].items()}}}


def build_sfm_document(sol):
    req, g, sc = sol["req"], sol["gravity"], sol["scale"]
    anchored = bool(sol["anchors"] and sol["anchors"].get("ok"))
    origin = ("the reference document's world (anchored)" if anchored else
              "facet 0 origin: the bottom-left of its surface's 1-99 % box in its own plane (a = along u, b = along "
              "v); x = horizontal along that facet pointing right as you face it, y = z × x (into the wall), z = up")
    if not g["known"] and not anchored:
        origin += ". GRAVITY UNKNOWN: z is the reference facet's in-plane 'up' (the facet is treated as vertical)"
    world = {"origin": origin, "up": _l(sol["up"], 6), "gravityKnown": g["known"], "referenceFacet": "0",
             "frameSource": "features", "gravitySource": g["source"], "scaleKnown": sc["known"],
             "scaleSource": sc["source"], "anchored": anchored}
    return {"version": 1, "units": "mm", "dictionary": req.dictionary, "idScheme": "plan",
            "markerSizeMm": req.marker_size_mm, "world": world, "segments": _segments(sol), "markers": [],
            "cameras": _cameras(sol), "quality": _quality(sol)}
