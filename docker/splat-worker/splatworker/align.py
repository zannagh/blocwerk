"""COLMAP frame -> the wall-geometry frame (ArUco solver), by a similarity transform on camera centres.

The geometry document (tools/glyph/wall-geometry.schema.md) has per camera `R` (row-major 3x3) and
`t` in millimetres, world = x right along the reference facet, y into the wall, z up. The viewer
frame used in frame.json is three.js-style metres: X right, Y up, Z out of the wall, origin = the
centre of the reference facet (segments[0].facets[0]).
"""
import numpy as np

from computejobs.child import JobError

# world (mm; x right, y into wall, z up) -> viewer (m; X right, Y up, Z out of the wall)
WORLD_TO_VIEWER = np.array([[1, 0, 0], [0, 0, 1], [0, -1, 0]], float) / 1000.0


def umeyama(A, B):
    """s, R, t minimising |s R A + t - B| (rows are points)."""
    ma, mb = A.mean(0), B.mean(0)
    U, S, Vt = np.linalg.svd((B - mb).T @ (A - ma))
    D = np.eye(3)
    D[2, 2] = np.sign(np.linalg.det(U @ Vt)) or 1.0
    R = U @ D @ Vt
    var = ((A - ma) ** 2).sum()
    if var <= 0:
        raise JobError("align", "the registered cameras all sit at one point: cannot align")
    s = float(np.trace(np.diag(S) @ D) / var)
    return s, R, mb - s * R @ ma


def geometry_centres(doc):
    out = {}
    for c in doc["cameras"]:
        R = np.array(c["R"], float).reshape(3, 3)
        out[c["image"]] = -R.T @ np.array(c["t"], float)
    return out


def facet_corners(doc):
    pts = []
    for seg in doc.get("segments", []):
        for f in seg.get("facets", []):
            e, o = f.get("extentMm"), np.array(f.get("origin", [0, 0, 0]), float)
            if not e:
                continue
            u, v = np.array(f["u"], float), np.array(f["v"], float)
            for a in (e["aMin"], e["aMax"]):
                for b in (e["bMin"], e["bMax"]):
                    pts.append(o + a * u + b * v)
    return np.array(pts)


def reference_centre(doc):
    try:
        f = doc["segments"][0]["facets"][0]
        e = f["extentMm"]
        return (np.array(f["origin"], float) + np.array(f["u"], float) * (e["aMin"] + e["aMax"]) / 2
                + np.array(f["v"], float) * (e["bMin"] + e["bMax"]) / 2)
    except (KeyError, IndexError, TypeError):
        return np.zeros(3)


def align(colmap_centres, doc, margin_mm):
    """colmap_centres {image stem: centre}. Returns the frame dict (matrix + crop + residuals)."""
    geo = geometry_centres(doc)
    names = sorted(set(colmap_centres) & set(geo))
    if len(names) < 3:
        raise JobError("align", f"only {len(names)} registered photo(s) have a camera in the geometry "
                                "document (need 3; photo file names must equal the cameras' 'image')")
    A = np.array([colmap_centres[n] for n in names])
    B = np.array([geo[n] for n in names])
    s, R, t = umeyama(A, B)
    res = np.linalg.norm((s * (R @ A.T)).T + t - B, axis=1)
    to_world = np.eye(4)
    to_world[:3, :3], to_world[:3, 3] = s * R, t
    centre = reference_centre(doc)
    to_viewer = np.eye(4)
    to_viewer[:3, :3] = WORLD_TO_VIEWER @ (s * R)
    to_viewer[:3, 3] = WORLD_TO_VIEWER @ (t - centre)
    corners = facet_corners(doc)
    if len(corners):
        lo, hi = corners.min(0) - margin_mm, corners.max(0) + margin_mm
        box = np.array([[x, y, z] for x in (lo[0], hi[0]) for y in (lo[1], hi[1]) for z in (lo[2], hi[2])])
        v = (WORLD_TO_VIEWER @ (box - centre).T).T
        crop = [v.min(0).round(4).tolist(), v.max(0).round(4).tolist()]
    else:
        crop = None
    return {
        "aligned": True, "units": "m",
        "matrix": to_viewer.T.flatten().round(9).tolist(),  # column-major (three.js Matrix4.fromArray)
        "toViewer": to_viewer.round(9).tolist(),             # row-major, same transform
        "toWorldMm": to_world.round(9).tolist(),             # splat coords -> geometry world (mm)
        "scaleMmPerUnit": s,
        "crop": crop,
        "alignment": {"cameras": len(names), "residualMmMedian": round(float(np.median(res)), 2),
                      "residualMmMax": round(float(res.max()), 2),
                      "perCameraMm": {n: round(float(r), 2) for n, r in zip(names, res)}},
    }


def unaligned_frame(xyz):
    """No geometry: identity transform, crop = robust bounding box of the splat centres."""
    lo, hi = np.percentile(xyz, 1, axis=0), np.percentile(xyz, 99, axis=0)
    pad = (hi - lo) * 0.15
    return {"aligned": False, "units": "colmap (arbitrary scale)",
            "matrix": np.eye(4).flatten().tolist(), "toViewer": np.eye(4).tolist(), "toWorldMm": None,
            "scaleMmPerUnit": None, "crop": [(lo - pad).round(4).tolist(), (hi + pad).round(4).tolist()],
            "alignment": None}
