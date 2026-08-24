"""Robust smooth deformation field over the per-hold correspondences.

The per-hold matcher is confident for most holds and useless for some (soft bottom
third, holds that were re-set, holds occluded in one photo). A thin-plate spline is
fitted to the confident ones only, with iterative outlier rejection, and then used
to place everything else. Every hold therefore gets a defensible position, while
the confident ones keep their own locally-measured one.
"""
import numpy as np

from hm_common import RbfField


def fit_field(src_old, residual, weights=None, lam=0.05, reject_iters=4, log=print):
    """Fit residual(old_px) -> (dx, dy) in new px, rejecting outliers iteratively."""
    keep = np.ones(len(src_old), bool)
    field = None
    for it in range(reject_iters):
        if keep.sum() < 12:
            break
        field = RbfField.fit(src_old[keep], residual[keep], lam=lam)
        r = np.linalg.norm(field(src_old) - residual, axis=1)
        med = np.median(r[keep])
        mad = np.median(np.abs(r[keep] - med)) + 1e-6
        thresh = max(15.0, med + 4.0 * 1.4826 * mad)
        newkeep = r <= thresh
        if log:
            log(f"    field iter {it + 1}: kept {int(newkeep.sum())}/{len(src_old)}"
                f"  (median residual {med:.1f} px, cut {thresh:.1f} px)")
        if newkeep.sum() == keep.sum() and np.array_equal(newkeep, keep):
            keep = newkeep
            break
        keep = newkeep
    if field is None:
        field = RbfField.fit(src_old, residual, lam=lam)
        keep = np.ones(len(src_old), bool)
    return field, keep
