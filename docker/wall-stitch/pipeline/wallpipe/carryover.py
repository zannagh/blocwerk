"""Hold CARRYOVER: match the wall's EXISTING holds onto a freshly detected hold set.

Vendored from new-run/exp5/carryover.py in its FIRST-PASS configuration, which is the
one that was validated: colour is a soft tie-break inside the ICP cost only, it never
rejects a geometric match, and there is no CPD stage. The fixed paths and the
hand-placed detail crops are the only things that changed.

    CARRIED_OVER  the hold was found again in the new photo -> its boulders survive
    MISSING       not found                                 -> removed, or missed
    NEW           a detection nothing claimed               -> an added hold to validate

Pipeline:
    coarse homography seed (wallpipe.seed)
      -> TPS-ICP on the hold point sets, with colour as a SOFT term in the ICP cost
      -> moving-least-squares refinement (the new image is a composite, so its geometry
         has seams no single global warp follows)
      -> box-aware one-to-one (Hungarian) assignment on a size-scaled gate
      -> leave-one-out consistency filter

    !! The match distance this reports is NOT a measure of correctness. On a hold field
    !! this dense and this uniform, a wrong alignment produces just as tight a residual
    !! as the right one. `_quality.estimated_precision` is the figure that says how many
    !! of the CARRIED_OVER rows are the right hold; read that before applying anything.
"""
import numpy as np

from . import co_blob, co_color, co_mls, co_patch, seed
from .rbf import RbfField

COLOUR_OK = 22.0         # weighted-Lab distance at which two samples are "the same colour"
P_AGREE_IF_CORRECT = 0.80   # measured ceiling: correct pairs still disagree sometimes
GATE_REL, GATE_MIN, GATE_MAX = 1.0, 80.0, 170.0
CONSISTENCY_PX = 170.0
MLS_SIGMA, MLS_K = 220.0, 14
MLS_TOLS = (220.0, 170.0, 140.0, 120.0, 100.0, 90.0)
ICP_STEPS = ((300.0, 8.0), (220.0, 4.0), (170.0, 2.0), (140.0, 1.0),
             (120.0, 0.5), (100.0, 0.3), (90.0, 0.2))
BLOB_STILL_THERE = 34.0


class WarpMap:
    """Homography plus an optional smooth TPS residual. Old px -> new px."""

    def __init__(self, h, field=None):
        self.h = np.asarray(h, np.float64)
        self.field = field

    def base(self, pts):
        import cv2
        p = np.atleast_2d(np.asarray(pts, np.float64)).astype(np.float32)
        return cv2.perspectiveTransform(
            p.reshape(-1, 1, 2), self.h).reshape(-1, 2).astype(np.float64)

    def __call__(self, pts):
        b = self.base(pts)
        return b + (self.field(np.atleast_2d(pts)) if self.field is not None else 0.0)

    def jacobian(self, pt, eps=4.0):
        p = np.asarray(pt, np.float64).reshape(1, 2)
        b = self(p)[0]
        jx = (self(p + [eps, 0])[0] - b) / eps
        jy = (self(p + [0, eps])[0] - b) / eps
        return np.stack([jx, jy], 1)


def mutual_pairs(p, cen, tol, cost=None):
    d = np.linalg.norm(p[:, None, :] - cen[None, :, :], axis=-1)
    c = d if cost is None else np.where(d <= tol, cost, 1e9)
    a, b = c.argmin(1), c.argmin(0)
    i = np.arange(len(p))
    ok = (b[a] == i) & (d[i, a] <= tol)
    return i[ok], a[ok], d[i[ok], a[ok]]


def tps_icp(h, po, cen, col_old, col_new, log=print):
    """Alternate mutual-NN matching and TPS re-fitting, tightening tol and the ridge.

    Colour enters only as an additive preference in the correspondence cost, so it can
    break a tie between two nearby detections but can never veto a geometric match.
    """
    warp = WarpMap(h)
    a, b = np.ones(3), np.zeros(3)
    cdist, cost = None, None
    for tol, lam in ICP_STEPS:
        for _ in range(2):
            p = warp(po)
            cost = None if cdist is None else (
                np.linalg.norm(p[:, None, :] - cen[None, :, :], axis=-1)
                + 2.0 * np.minimum(cdist, 120.0))
            i, j, d = mutual_pairs(p, cen, tol, cost)
            if len(i) < 30:
                log('    icp tol=%.0f: only %d pairs, stopping' % (tol, len(i)))
                return warp, (a, b)
            a, b = co_color.align(col_old, col_new, (i, j))
            cdist = co_color.distance(col_old, col_new, a, b)
            warp = WarpMap(warp.h, RbfField.fit(po[i], cen[j] - warp.base(po)[i], lam=lam))
        i, j, d = mutual_pairs(warp(po), cen, tol, cost)
        log('    icp tol=%.0f lam=%s: %d/%d pairs, median %.1f px'
            % (tol, lam, len(i), len(po), np.median(d)))
    return warp, (a, b)


def colour_agreement(i, j, cd, seed_val=11, trials=25):
    """Agreement rate of a match set and the shuffled chance level for that same set."""
    if len(i) < 10:
        return 0.0, 0.0
    rng = np.random.default_rng(seed_val)
    hit = float((cd[i, j] <= COLOUR_OK).mean())
    chance = float(np.mean([(cd[i, rng.permutation(j)] <= COLOUR_OK).mean()
                            for _ in range(trials)]))
    return hit, chance


def precision_from_colour(hit, chance):
    """Fraction of a match set that is actually correct, from its colour agreement.

    observed = f * P(agree | correct) + (1 - f) * P(agree | wrong), and P(agree | wrong)
    is exactly the shuffled chance level, so f falls straight out. Colour is measured
    from each hold's SEGMENTED blob and the two photos' colour marginals are matched by
    quantile mapping, which needs no correspondences -- so this stays an independent
    read even though a colour preference sits inside the ICP cost.
    """
    denom = P_AGREE_IF_CORRECT - chance
    if denom <= 1e-6:
        return float('nan')
    return float(np.clip((hit - chance) / denom, 0.0, 1.0))


def fill(col):
    return np.where(np.isfinite(col), col, np.nanmedian(col, 0))


def align(old, new, po, r_old, cen, r_det, cache_dir=None, seed_h=None, log=print):
    """Everything up to the assignment: seed, ICP, MLS, gate, consistency, precision.

    Returns a dict of intermediates the record builder needs.
    """
    nh, nw = new.shape[:2]
    log('[1] colour signatures (soft tie-break only; never a reject)')
    lab_o, lab_n = co_color.lab(old), co_color.lab(new)
    col_old = co_color.sample(lab_o, po, np.maximum(r_old, 6.0))
    col_new = co_color.sample(lab_n, cen, np.maximum(r_det, 6.0))

    log('[2] coarse homography seed')
    seed_stats = {}
    if seed_h is None:
        seed_h, seed_stats = seed.solve(old, new, po, cen, cache_dir=cache_dir, log=log)

    log('[3] TPS-ICP')
    warp, _ = tps_icp(seed_h, po, cen, col_old, col_new, log=log)

    log('[4] moving-least-squares refinement')
    mls, _, _ = co_mls.refine(warp, po, cen, r_det, MLS_TOLS,
                              sigma=MLS_SIGMA, k=MLS_K, log=log)
    transferred = mls(po)
    scale = np.array([float(np.sqrt(abs(np.linalg.det(mls.jacobian(p))))) for p in po])
    r_pred = np.clip(r_old * scale, 25.0, 900.0)
    in_frame = np.array([(0 <= p[0] < nw and 0 <= p[1] < nh) for p in transferred])

    log('[5] box-aware assignment + consistency filter')
    gate = np.clip(GATE_REL * r_pred, GATE_MIN, GATE_MAX)
    gate[~in_frame] = 0.0
    who, dist = co_mls.hungarian(transferred, cen, r_det, gate)
    resid = co_mls.consistency(po, cen, who, sigma=MLS_SIGMA, k=MLS_K)
    dropped = (who >= 0) & (resid > CONSISTENCY_PX)
    who[dropped] = -1
    dist[dropped] = np.inf
    m = who >= 0
    log('  %d gated matches, %d dropped as inconsistent with their neighbours'
        % (int(m.sum()) + int(dropped.sum()), int(dropped.sum())))
    if m.any():
        log('  match distance px: median %.1f  p90 %.1f  (predicted radius median %.0f px)'
            % (np.median(dist[m]), np.percentile(dist[m], 90), np.median(r_pred)))
    else:
        log('  NO hold matched. Either the new base does not overlap the prior photo, '
            'or the coarse seed search failed; carryover output will be all MISSING.')

    log('[6] independent precision read (segmented-blob colour, quantile-aligned)')
    seg_o = co_blob.refine(lab_o, po, np.maximum(r_old, 6.0))
    seg_n = co_blob.refine(lab_n, cen, np.maximum(r_det, 10.0))
    cs_o = np.where(np.isfinite(seg_o[2]), seg_o[2], col_old)
    cs_n = np.where(np.isfinite(seg_n[2]), seg_n[2], col_new)
    cd = co_color.distance(fill(co_color.quantile_align(cs_o, cs_n)), fill(cs_n),
                           np.ones(3), np.zeros(3))
    hit, chance = colour_agreement(np.where(m)[0], who[m], cd)
    prec = precision_from_colour(hit, chance) if m.any() else 0.0
    log('  colour agreement %.2f vs chance %.2f  ->  ESTIMATED PRECISION ~%.0f%%'
        % (hit, chance, prec * 100))

    log('[7] blobness at the unmatched positions')
    blob = co_patch.blobness_search(lab_n, transferred, r_pred)
    still_there = (~m) & in_frame & (blob >= BLOB_STILL_THERE)

    return dict(mls=mls, transferred=transferred, r_pred=r_pred, in_frame=in_frame,
                who=who, dist=dist, resid=resid, matched=m, blob=blob,
                still_there=still_there, colour_distance=cd, hit=hit, chance=chance,
                precision=prec, seed_stats=seed_stats, seed_h=seed_h)
