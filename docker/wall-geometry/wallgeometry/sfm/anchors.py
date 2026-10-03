"""Anchors: photos of the active capture, reconstructed with the new ones, tie the model to the wall frame.

1. A similarity (robust Umeyama) from the anchors' model camera centres to their camera centres in the reference
   geometry document. Gate: >= 6 anchors kept, rms <= 35 mm, each <= 80 mm. Phase 0 fitted 14.5-18.8 mm rms, but
   measured 22-28 mm of pure solver disagreement between two reconstructions of the same anchors, and the first real
   re-capture fitted 15/15 anchors at 29.9 mm rms / 68.1 mm max. This module is the only place the gate lives: the
   app just reads `world.anchored`. A fit the gate refuses but within SCALE_RMS_MM still gives scale and gravity
   (solve.py), not the frame.
2. A rigid point-to-plane ICP of the sparse points onto the reference facets (the scale stays the anchors': a
   similarity ICP collapses it), with splat-worker refine.py's schedule and limits: a correction beyond 80 mm or
   3 deg means the anchors themselves are wrong, and the anchoring fails (Phase 0: 0.3 deg, 20-29 mm).
"""
import itertools

import numpy as np

MIN_ANCHORS = 6
MAX_RMS_MM = 35.0
MAX_SINGLE_MM = 80.0
SCALE_RMS_MM = 60.0  # a refused fit this close still measures scale and "up" far better than any estimate
GATES_MM = (60.0, 40.0, 25.0, 15.0, 15.0, 15.0)
EDGE_MM = 60.0
MIN_ICP_POINTS = 300
MAX_SHIFT_MM = 80.0
MAX_ROT_DEG = 3.0
EVAL_GATE_MM = 50.0
NORMAL_COS = np.cos(np.radians(25.0))
MIN_THRESHOLD_MM = 5.0  # an anchor within this of the fit is never an outlier (noise-free data)


def umeyama(src, dst):
    """dst ~ s R src + t."""
    mu_s, mu_d = src.mean(0), dst.mean(0)
    xs, xd = src - mu_s, dst - mu_d
    U, D, Vt = np.linalg.svd(xd.T @ xs / len(src))
    E = np.eye(3)
    if np.linalg.det(U) * np.linalg.det(Vt) < 0:
        E[2, 2] = -1
    R = U @ E @ Vt
    s = (D * np.diag(E)).sum() / (xs ** 2).sum() * len(src)
    return s, R, mu_d - s * R @ mu_s


def _lmeds_start(src, dst, max_sets=500, seed=0):
    """Inliers of the best least-median-of-squares similarity over 3-anchor subsets (all or max_sets random)."""
    n = len(src)
    rng = np.random.default_rng(seed)
    sets = list(itertools.combinations(range(n), 3)) if n <= 16 else [rng.choice(n, 3, replace=False)
                                                                       for _ in range(max_sets)]
    best, best_med = None, np.inf
    for idx in sets:
        idx = list(idx)
        if np.linalg.matrix_rank(src[idx] - src[idx].mean(0), tol=1e-9) < 2:
            continue
        s, R, t = umeyama(src[idx], dst[idx])
        r = np.linalg.norm(src @ (s * R).T + t - dst, axis=1)
        if np.median(r) < best_med:
            best, best_med = r, np.median(r)
    keep = best <= max(3 * 1.4826 * best_med, MIN_THRESHOLD_MM) if best is not None else None
    return keep if keep is not None and keep.sum() >= 3 else np.ones(n, bool)


def robust_umeyama(src, dst, iters=5, k=3.0):
    """Similarity dst ~ s R src + t robust to a few wrong anchors: LMedS start, then iterative trimming at
    k x 1.4826 x the median residual."""
    keep = _lmeds_start(src, dst) if len(src) >= 4 else np.ones(len(src), bool)
    for _ in range(iters):
        s, R, t = umeyama(src[keep], dst[keep])
        r = np.linalg.norm(src @ (s * R).T + t - dst, axis=1)
        new = r < max(k * 1.4826 * np.median(r[keep]), MIN_THRESHOLD_MM)
        if new.sum() < 3 or (new == keep).all():
            break
        keep = new
    return s, R, t, r, keep


def fit_anchors(model, anchors, reference):
    """{"ok", "s", "R", "t", report fields}: the anchor similarity model -> reference world (mm)."""
    ref = {c["image"]: c for c in reference["cameras"]}
    stems, src, dst = [], [], []
    for im in model["images"]:
        if im["role"] == "anchor" and im["stem"] in anchors:
            c = ref[anchors[im["stem"]]]
            R, t = np.array(c["R"], float).reshape(3, 3), np.array(c["t"], float)
            stems.append(im["stem"])
            src.append(im["C"])
            dst.append(-R.T @ t)
    out = {"ok": False, "requested": len(anchors), "registered": len(stems)}
    if len(stems) < 3:
        return {**out, "reason": f"{len(stems)} anchors registered (need {MIN_ANCHORS})"}
    s, R, t, r, keep = robust_umeyama(np.array(src), np.array(dst))
    rms, worst = float(np.sqrt((r[keep] ** 2).mean())), float(r[keep].max())
    out.update(s=s, R=R, t=t, inliers=int(keep.sum()), rmsMm=round(rms, 2), maxMm=round(worst, 2),
               residualsMm={st: round(float(x), 1) for st, x in zip(stems, r)},
               outliers=[st for st, k in zip(stems, keep) if not k])
    if keep.sum() < MIN_ANCHORS:
        out["reason"] = f"{int(keep.sum())} consistent anchors (need {MIN_ANCHORS})"
    elif rms > MAX_RMS_MM or worst > MAX_SINGLE_MM:
        out["reason"] = (f"anchor residual {rms:.1f} mm rms / {worst:.1f} mm max "
                         f"(limits {MAX_RMS_MM:g} / {MAX_SINGLE_MM:g})")
        out["scaleOk"] = rms <= SCALE_RMS_MM
    else:
        out["ok"] = True
    return out


def reference_facets(doc):
    out = []
    for seg in doc.get("segments", []):
        for f in seg.get("facets", []):
            out.append({"o": np.array(f["origin"], float), "u": np.array(f["u"], float),
                        "v": np.array(f["v"], float), "n": np.array(f["normal"], float), "e": f["extentMm"]})
    return out


def _candidates(P, N, facets, gate):
    """(facet index or -1, signed distance) of points inside a facet's shrunk extent, near it, parallel to it."""
    best, dist = np.full(len(P), -1), np.full(len(P), np.inf)
    for k, f in enumerate(facets):
        rel = P - f["o"]
        a, b, d = rel @ f["u"], rel @ f["v"], rel @ f["n"]
        e = f["e"]
        ok = (a > e["aMin"] + EDGE_MM) & (a < e["aMax"] - EDGE_MM) & (b > e["bMin"] + EDGE_MM) & (b < e["bMax"] - EDGE_MM)
        ok &= (np.abs(d) < np.minimum(gate, np.abs(dist))) & (np.abs(N @ f["n"]) > NORMAL_COS)
        best[ok], dist[ok] = k, d[ok]
    return best, dist


def _rot(w):
    th = np.linalg.norm(w)
    if th < 1e-12:
        return np.eye(3)
    k = w / th
    K = np.array([[0, -k[2], k[1]], [k[2], 0, -k[0]], [-k[1], k[0], 0]])
    return np.eye(3) + np.sin(th) * K + (1 - np.cos(th)) * K @ K


def _median_abs(P, N, facets):
    idx, d = _candidates(P, N, facets, EVAL_GATE_MM)
    return (float(np.median(np.abs(d[idx >= 0]))) if (idx >= 0).any() else None), int((idx >= 0).sum())


def plane_icp(P, N, facets):
    """(R, t) rigid correction of world points P (normals N) onto the facets, and its report."""
    before, n_before = _median_abs(P, N, facets)
    report = {"beforeMedianAbsMm": before, "points": n_before}
    if n_before < MIN_ICP_POINTS:
        return np.eye(3), np.zeros(3), {**report, "applied": False, "reason": "too few wall points"}
    Rt, tt = np.eye(3), np.zeros(3)
    for gate in GATES_MM:
        W, M = P @ Rt.T + tt, N @ Rt.T
        idx, r = _candidates(W, M, facets, gate)
        ok = idx >= 0
        if ok.sum() < MIN_ICP_POINTS:
            break
        p, r, n = W[ok], r[ok], np.array([f["n"] for f in facets])[idx[ok]]
        c = p.mean(0)
        q = p - c
        L = np.sqrt((q ** 2).sum(1).mean())
        J = np.c_[np.cross(q, n) / L, n]
        w = (1 - (r / gate) ** 2) ** 2
        A = (J * w[:, None]).T @ J
        x = np.linalg.solve(A + 1e-4 * np.trace(A) / 6 * np.eye(6), -(J * w[:, None]).T @ r)
        dR = _rot(x[:3] / L)
        Rt, tt = dR @ Rt, dR @ (tt - c) + c + x[3:6]
    after, _ = _median_abs(P @ Rt.T + tt, N @ Rt.T, facets)
    centre = np.mean([f["o"] for f in facets], 0)
    shift = float(np.linalg.norm(Rt @ centre + tt - centre))
    angle = float(np.degrees(np.arccos(np.clip((np.trace(Rt) - 1) / 2, -1, 1))))
    report.update(afterMedianAbsMm=after, shiftMm=round(shift, 2), rotationDeg=round(angle, 3))
    if shift > MAX_SHIFT_MM or angle > MAX_ROT_DEG:
        return None, None, {**report, "applied": False, "reason": "correction beyond 80 mm / 3 deg"}
    if after is None or after > before:
        return np.eye(3), np.zeros(3), {**report, "applied": False, "reason": "no better"}
    return Rt, tt, {**report, "applied": True}
