"""Moving-least-squares registration driven by the hold pattern itself.

A single global TPS cannot be right everywhere here: the new image is a COMPOSITE of
several frames, so its geometry has seams, and the wall is not one plane. Instead each
old hold is placed by a weighted affine fitted to its own neighbourhood of already
matched holds. That is local by construction, so a seam only affects the holds beside
it, and it degrades gracefully to the global fit where matches are sparse.
"""
import numpy as np
from scipy.optimize import linear_sum_assignment


class MlsMap:
    """Locally-weighted affine map from control pairs. Old px -> new px."""

    def __init__(self, src, dst, sigma=260.0, k=18, ridge=1e-3):
        self.src = np.asarray(src, np.float64)
        self.dst = np.asarray(dst, np.float64)
        self.sigma = float(sigma)
        self.k = int(min(k, len(src)))
        self.ridge = ridge

    def __call__(self, pts, skip=None):
        """`skip` (per-point control index to exclude) gives leave-one-out predictions."""
        p = np.atleast_2d(np.asarray(pts, np.float64))
        out = np.empty_like(p)
        d2 = ((p[:, None, :] - self.src[None, :, :]) ** 2).sum(-1)
        if skip is not None:
            d2 = d2.copy()
            for n, s in enumerate(skip):
                if s >= 0:
                    d2[n, s] = np.inf
        order = np.argsort(d2, axis=1)[:, : self.k]
        for n in range(len(p)):
            idx = order[n]
            w = np.exp(-d2[n, idx] / (2.0 * self.sigma ** 2)) + 1e-6
            a = np.hstack([self.src[idx], np.ones((len(idx), 1))])
            sw = np.sqrt(w)[:, None]
            m = np.linalg.solve((a * sw).T @ (a * sw) + self.ridge * np.eye(3) * w.sum(),
                                (a * sw).T @ (self.dst[idx] * sw))
            out[n] = np.array([p[n, 0], p[n, 1], 1.0]) @ m
        return out

    def jacobian(self, pt, eps=4.0):
        p = np.asarray(pt, np.float64).reshape(1, 2)
        b = self(p)[0]
        jx = (self(p + [eps, 0])[0] - b) / eps
        jy = (self(p + [0, eps])[0] - b) / eps
        return np.stack([jx, jy], 1)


def effective_distance(pos, cen, r_det, slack=0.55):
    """Centre distance minus the detection's own extent.

    The detector frequently emits a small box on the textured middle of a large hold,
    so its centroid is not the hold's centre. Charging the full centre distance would
    then penalise a correct match on a big hold more than a wrong match on a small
    neighbour. Landing anywhere inside the detection costs nothing.
    """
    d = np.linalg.norm(pos[:, None, :] - cen[None, :, :], axis=-1)
    return np.maximum(d - slack * r_det[None, :], 0.0), d


def hungarian(pos, cen, r_det, gate, extra=None):
    """Gated one-to-one assignment on the box-aware distance."""
    eff, raw = effective_distance(pos, cen, r_det)
    g = np.maximum(gate, 1e-6)[:, None]   # gate 0 means "never match" (e.g. off frame)
    ok = eff <= gate[:, None]
    cost = eff / g + (0.0 if extra is None else extra)
    ri, ci = linear_sum_assignment(np.where(ok, cost, 1e7))
    who = np.full(len(pos), -1)
    dist = np.full(len(pos), np.inf)
    for i, j in zip(ri, ci):
        if ok[i, j]:
            who[i], dist[i] = j, raw[i, j]
    return who, dist


def refine(warp, po, cen, r_det, tols, sigma=260.0, k=18, log=print):
    """Alternate gated assignment and MLS re-fitting, tightening the tolerance."""
    cur = warp
    who = dist = None
    for tol in tols:
        for _ in range(2):
            who, dist = hungarian(cur(po), cen, r_det, np.full(len(po), float(tol)))
            m = who >= 0
            if m.sum() < 25:
                log(f"    mls tol={tol:.0f}: only {int(m.sum())} pairs, stopping")
                return cur, who, dist
            cur = MlsMap(po[m], cen[who[m]], sigma=sigma, k=k)
        who, dist = hungarian(cur(po), cen, r_det, np.full(len(po), float(tol)))
        m = who >= 0
        log(f"    mls tol={tol:.0f}: {int(m.sum())}/{len(po)} pairs,"
            f" median {np.median(dist[m]):.1f} px, p90 {np.percentile(dist[m], 90):.1f} px")
    return cur, who, dist


def consistency(po, cen, who, sigma=300.0, k=16):
    """Leave-one-out residual of every match against its neighbours' displacement.

    A wrong match is a hold pulled onto some other blob; its displacement disagrees
    with what the surrounding, independently matched holds say the wall did there.
    Returns residual px per hold (inf where unmatched).
    """
    m = np.where(who >= 0)[0]
    out = np.full(len(po), np.inf)
    if len(m) < 20:
        return out
    fit = MlsMap(po[m], cen[who[m]], sigma=sigma, k=min(k, len(m) - 1))
    skip = np.arange(len(m))
    pred = fit(po[m], skip=skip)
    out[m] = np.linalg.norm(pred - cen[who[m]], axis=1)
    return out
