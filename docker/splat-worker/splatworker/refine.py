"""Fine alignment of the splat to the wall: a robust plane-ICP on the splat centres.

align.py fits the COLMAP frame to the wall frame on camera centres only (median residual ~20 mm on The
Attic), so the splat's wall surface can float a few centimetres off the modelled facets. Here the
bare wall does the fitting: splat centres near each facet plane (opaque, small splats, inside the
facet's extent) pull the similarity transform so the surface lands ON the planes. Holds and volumes
stick out of the wall, so a trimmed fit with a shrinking gate drops them; the caller may also pass
exclusion polygons (hold footprints) in facet-plane millimetres.

Only small corrections are accepted (see MAX_*): a big correction means the camera alignment itself
is wrong, and the plane fit is then no better placed to know.
"""
import numpy as np

GATES_MM = (60.0, 40.0, 25.0, 15.0, 15.0, 15.0)
MIN_POINTS = 2000
MAX_SPLAT_MM = 40.0          # splats wider than this are fog, not surface
MIN_ALPHA = 0.1           # spz opacity; most splats of a trained scene are faint
EDGE_MM = 60.0               # stay off the facet borders (neighbouring facets, kickboard edge)
SAMPLE = 200_000             # candidate points kept (random subset), for speed
MAX_SHIFT_MM = 80.0
MAX_ROT_DEG = 3.0
MAX_SCALE = 0.03
EVAL_GATE_MM = 50.0          # residual statistics count points within this distance of a plane


def facets_of(doc):
    out = []
    for seg in doc.get("segments", []):
        for f in seg.get("facets", []):
            e = f.get("extentMm")
            if not e:
                continue
            out.append({"id": str(f["id"]), "o": np.array(f["origin"], float), "u": np.array(f["u"], float),
                        "v": np.array(f["v"], float), "n": np.array(f["normal"], float), "e": e})
    return out


def _inside(poly, pts):
    x, y = pts[:, 0], pts[:, 1]
    res = np.zeros(len(pts), bool)
    j = len(poly) - 1
    for i in range(len(poly)):
        (xi, yi), (xj, yj) = poly[i], poly[j]
        res ^= ((yi > y) != (yj > y)) & (x < (xj - xi) * (y - yi) / (yj - yi + 1e-12) + xi)
        j = i
    return res


def _transform(M, p):
    return p @ M[:3, :3].T + M[:3, 3]


def candidates(world, facets, exclude=None, gate=150.0):
    """(facet index per point or -1, signed distance) for points inside a facet's shrunk extent."""
    best = np.full(len(world), -1)
    dist = np.full(len(world), np.inf)
    for k, f in enumerate(facets):
        rel = world - f["o"]
        a, b, d = rel @ f["u"], rel @ f["v"], rel @ f["n"]
        e = f["e"]
        ok = (a > e["aMin"] + EDGE_MM) & (a < e["aMax"] - EDGE_MM) & (b > e["bMin"] + EDGE_MM) & (b < e["bMax"] - EDGE_MM)
        ok &= np.abs(d) < np.minimum(gate, np.abs(dist))
        for poly in (exclude or {}).get(f["id"], []):
            near = ok & (a > poly[:, 0].min()) & (a < poly[:, 0].max()) & (b > poly[:, 1].min()) & (b < poly[:, 1].max())
            if near.any():
                idx = np.nonzero(near)[0]
                ok[idx[_inside(poly, np.c_[a[idx], b[idx]])]] = False
        best[ok], dist[ok] = k, d[ok]
    return best, dist


def residuals(M, xyz, facets, exclude=None):
    """Median |point-to-plane| (mm) of bare-wall points within EVAL_GATE_MM, and per-facet signed medians."""
    idx, d = candidates(_transform(M, xyz), facets, exclude, EVAL_GATE_MM)
    ok = idx >= 0
    per = {f["id"]: round(float(np.median(d[idx == k])), 2) for k, f in enumerate(facets) if (idx == k).sum() > 100}
    return {"medianAbsMm": round(float(np.median(np.abs(d[ok]))), 2) if ok.any() else None,
            "points": int(ok.sum()), "facetOffsetMm": per}


def _rot(w):
    th = np.linalg.norm(w)
    if th < 1e-12:
        return np.eye(3)
    k = w / th
    K = np.array([[0, -k[2], k[1]], [k[2], 0, -k[0]], [-k[1], k[0], 0]])
    return np.eye(3) + np.sin(th) * K + (1 - np.cos(th)) * K @ K


def refine(xyz, to_world, doc, alpha=None, size_mm=None, exclude=None, seed=0):
    """Refined 4x4 splat -> world (mm) and a report dict; the input transform when the fit is unusable.

    xyz: splat centres (splat frame), alpha 0..1, size_mm: largest splat axis in mm (both optional),
    exclude: {facet id: [polygon (k,2) in absolute facet-plane mm]} regions to ignore (holds, volumes).
    """
    M0 = np.asarray(to_world, float)
    facets = facets_of(doc)
    keep = np.ones(len(xyz), bool)
    if alpha is not None:
        keep &= alpha >= MIN_ALPHA
    if size_mm is not None:
        keep &= size_mm <= MAX_SPLAT_MM
    pts = xyz[keep]
    if len(pts) > SAMPLE:
        pts = pts[np.random.default_rng(seed).choice(len(pts), SAMPLE, replace=False)]
    sample = pts
    report = {"method": "plane-icp", "before": residuals(M0, sample, facets, exclude)}
    if not facets or report["before"]["points"] < MIN_POINTS:
        return M0, {**report, "applied": False, "reason": "too few wall points"}
    idx0, _ = candidates(_transform(M0, pts), facets, exclude)
    pts = pts[idx0 >= 0]
    M = M0.copy()
    for gate in GATES_MM:
        world = _transform(M, pts)
        idx, d = candidates(world, facets, None, gate)
        ok = idx >= 0
        if ok.sum() < MIN_POINTS:
            break
        p, r, n = world[ok], d[ok], np.array([f["n"] for f in facets])[idx[ok]]
        c = p.mean(0)
        q = p - c
        L = np.sqrt((q ** 2).sum(1).mean())      # lever arm: rotation / scale columns in mm like the shift
        J = np.c_[np.cross(q, n) / L, n]
        # Rigid only: planes barely constrain scale, and a trimmed fit would shrink the scene onto
        # them. Tukey-ish weights on the gate; damping keeps directions no plane constrains at zero.
        w = (1 - (r / gate) ** 2) ** 2
        A = (J * w[:, None]).T @ J
        lam = 1e-4 * np.trace(A) / 6
        x = np.linalg.solve(A + lam * np.eye(6), -(J * w[:, None]).T @ r)
        step = np.eye(4)
        step[:3, :3] = _rot(x[:3] / L)
        step[:3, 3] = c + x[3:6] - step[:3, :3] @ c
        M = step @ M
    report["after"] = residuals(M, sample, facets, exclude)
    delta = M @ np.linalg.inv(M0)
    s = np.cbrt(np.linalg.det(delta[:3, :3]))
    ang = np.degrees(np.arccos(np.clip((np.trace(delta[:3, :3] / s) - 1) / 2, -1, 1)))
    centre = np.mean([f["o"] for f in facets], 0)
    shift = float(np.linalg.norm(_transform(delta, centre[None])[0] - centre))
    report["correction"] = {"shiftMm": round(shift, 2), "rotationDeg": round(float(ang), 3), "scale": round(float(s), 5)}
    worse = report["after"]["medianAbsMm"] is None or report["after"]["medianAbsMm"] > report["before"]["medianAbsMm"]
    if worse or shift > MAX_SHIFT_MM or ang > MAX_ROT_DEG or abs(s - 1) > MAX_SCALE:
        return M0, {**report, "applied": False, "reason": "correction out of bounds or no better"}
    return M, {**report, "applied": True}


def refinement_block(M, report):
    """The additive frame.json field: the refined splat -> world transform (row-major) and its report."""
    return {**report, "toWorldMm": np.asarray(M, float).round(9).tolist()}


def refine_frame(frame, splats, doc):
    """Adds frame["refinement"] for an aligned frame (splatio.Splats in the COLMAP frame); never raises."""
    try:
        size = np.exp(splats.log_scale.max(1)) * float(frame["scaleMmPerUnit"])
        M, report = refine(splats.xyz, frame["toWorldMm"], doc, splats.alpha, size)
        frame["refinement"] = refinement_block(M, report)
    except Exception as e:  # noqa: BLE001 - the fine alignment is an optional extra, never a failed job
        frame["refinement"] = {"method": "plane-icp", "applied": False, "reason": str(e)[:200]}
    return frame
