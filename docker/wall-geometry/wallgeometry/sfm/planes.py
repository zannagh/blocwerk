"""Planes in the sparse points: sequential RANSAC with local sampling and normal-consistent inliers, Tukey IRLS,
the largest spatially connected part, and the merge of near-parallel slabs (Phase 0 `b_facets.py`, The Attic:
main wall 0.10-0.37 deg, side panel 0.13-0.48 deg from the marker model).

Local sampling (3 points within `radius`) finds small facets too; normal consistency (per-point PCA normal
within 25 deg of the plane's) keeps an infinite 45 deg plane from eating a band of every fold it crosses.
Units: whatever the points are in; the caller passes tolerances in the same unit.
"""
import numpy as np
from scipy.spatial import cKDTree

NORMAL_COS = np.cos(np.radians(25.0))
SCORE_SAMPLE = 20000  # RANSAC hypotheses are counted on at most this many remaining points


def point_normals(pts, k=20):
    """Per-point unit normals from the local PCA of k neighbours."""
    k = min(k, len(pts))
    _, nb = cKDTree(pts).query(pts, k=k)
    q = pts[nb] - pts[nb].mean(1, keepdims=True)
    return np.linalg.svd(q, full_matrices=False)[2][:, 2, :]


def lsq_plane(pts, w=None):
    w = np.ones(len(pts)) if w is None else w
    c = (pts * w[:, None]).sum(0) / w.sum()
    return c, np.linalg.svd((pts - c) * np.sqrt(w)[:, None], full_matrices=False)[2][2]


def irls(pts, c, n, tol, iters=10):
    """Tukey-biweight refit of a plane (scale 2.5 tol, points beyond 3 tol ignored)."""
    for _ in range(iters):
        r = (pts - c) @ n
        sel = np.abs(r) < 3 * tol
        if sel.sum() < 3:
            break
        w = np.clip(1 - (r[sel] / (2.5 * tol)) ** 2, 0, None) ** 2
        c2, n2 = lsq_plane(pts[sel], w + 1e-9)
        c, n = c2, (n2 if n2 @ n >= 0 else -n2)
    return c, n


def basis(n):
    """Two unit in-plane axes of a plane with normal n."""
    a = np.cross(n, [0, 0, 1.0])
    if np.linalg.norm(a) < 0.1:
        a = np.cross(n, [1.0, 0, 0])
    a /= np.linalg.norm(a)
    return a, np.cross(n, a)


def plane_ab(pts, c, n):
    e1, e2 = basis(n)
    d = pts - c
    return np.stack([d @ e1, d @ e2], 1)


def largest_component(pts, inl, c, n, cell):
    """The biggest 8-connected group of occupied grid cells (in the plane) of the inliers."""
    idx = np.flatnonzero(inl)
    if not len(idx):
        return inl
    keys = np.floor(plane_ab(pts[idx], c, n) / cell).astype(np.int64)
    cells, inv = np.unique(keys, axis=0, return_inverse=True)
    inv = inv.ravel()
    lookup = {tuple(k): i for i, k in enumerate(cells)}
    label = np.full(len(cells), -1)
    sizes = []
    for start in range(len(cells)):
        if label[start] >= 0:
            continue
        label[start], stack = len(sizes), [start]
        while stack:
            k = stack.pop()
            x, y = cells[k]
            for dx in (-1, 0, 1):
                for dy in (-1, 0, 1):
                    j = lookup.get((x + dx, y + dy))
                    if j is not None and label[j] < 0:
                        label[j] = len(sizes)
                        stack.append(j)
        sizes.append(0)
    counts = np.bincount(label[inv], minlength=len(sizes))
    out = np.zeros_like(inl)
    out[idx[label[inv] == int(np.argmax(counts))]] = True
    return out


def _hypothesis(pts, normals, idx, tree, rng, tol, radius, iters):
    """Best (point, normal, count) of `iters` locally sampled plane hypotheses over the points idx."""
    score_idx = idx if len(idx) <= SCORE_SAMPLE else rng.choice(idx, SCORE_SAMPLE, replace=False)
    P, N = pts[score_idx], normals[score_idx]
    seeds = rng.choice(len(idx), iters)
    nbrs = tree.query_ball_point(pts[idx[seeds]], r=radius)
    best, best_n = None, 0
    for it in range(iters):
        if len(nbrs[it]) < 3:
            continue
        smp = pts[idx[rng.choice(nbrs[it], 3, replace=False)]]
        n = np.cross(smp[1] - smp[0], smp[2] - smp[0])
        if np.linalg.norm(n) < 1e-12:
            continue
        n /= np.linalg.norm(n)
        cnt = int(((np.abs((P - smp[0]) @ n) < tol) & (np.abs(N @ n) > NORMAL_COS)).sum())
        if cnt > best_n:
            best, best_n = (smp[0], n), cnt
    return best, best_n * len(idx) / len(score_idx)


def fit_planes(pts, normals, tol, radius, cell, rng, min_pts=150, max_planes=30, iters=1500, attempts=100):
    """Sequential RANSAC -> [{"c", "n", "inl" (bool over pts)}], biggest first. A hypothesis whose connected
    part is too small (scattered clutter) costs an attempt, not a plane."""
    remaining = np.ones(len(pts), bool)
    out = []
    for _ in range(attempts):
        if len(out) >= max_planes:
            break
        idx = np.flatnonzero(remaining)
        if len(idx) < min_pts:
            break
        best, count = _hypothesis(pts, normals, idx, cKDTree(pts[idx]), rng, tol, radius, iters)
        if best is None or count < min_pts:
            break
        cand = idx[np.abs(normals[idx] @ best[1]) > NORMAL_COS]  # refit on the orientation-consistent points only
        c, n = irls(pts[cand], best[0], best[1], tol)
        on = remaining & (np.abs((pts - c) @ n) < tol) & (np.abs(normals @ n) > NORMAL_COS)
        inl = largest_component(pts, on, c, n, cell)
        if inl.sum() < min_pts:  # only scattered bits: never propose this plane again
            remaining &= ~on
            continue
        c, n = irls(pts[inl], c, n, tol)
        out.append({"c": c, "n": n, "inl": inl})
        remaining &= ~inl
    return out


def _touch(pts, a, b, gap):
    """Do the footprints (in a's plane) of planes a and b come within `gap` of each other?"""
    A, B = plane_ab(pts[a["inl"]], a["c"], a["n"]), plane_ab(pts[b["inl"]], a["c"], a["n"])
    return cKDTree(A).query(B[:: max(1, len(B) // 500)], distance_upper_bound=gap)[0].min() < gap


def merge_parallel(planes, pts, max_deg=3.0, max_off=30.0, gap=300.0):
    """Near-parallel slabs within max_off of a bigger plane whose footprints touch are one surface (hold
    layers); the merged plane keeps the biggest member's fit (the surface has the most points)."""
    cos = np.cos(np.radians(max_deg))
    groups = []
    for pl in sorted(planes, key=lambda p: -p["inl"].sum()):
        for gr in groups:
            h = gr[0]
            if abs(pl["n"] @ h["n"]) > cos and abs((pl["c"] - h["c"]) @ h["n"]) < max_off and _touch(pts, h, pl, gap):
                gr.append(pl)
                break
        else:
            groups.append([pl])
    out = []
    for gr in groups:
        inl = np.zeros(len(pts), bool)
        for pl in gr:
            inl |= pl["inl"]
        out.append({"c": gr[0]["c"], "n": gr[0]["n"], "inl": inl, "members": len(gr)})
    return out


def refine(planes, pts, normals, tol):
    """The final fit of each plane: Tukey IRLS at tol / 2 on its orientation-consistent inliers."""
    for pl in planes:
        sel = pl["inl"] & (np.abs(normals @ pl["n"]) > NORMAL_COS)
        if sel.sum() >= 3:
            pl["c"], pl["n"] = irls(pts[sel], pl["c"], pl["n"], tol / 2, iters=15)
    return planes
