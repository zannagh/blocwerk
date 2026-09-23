"""Consensus-guided view choice: penalise photos that disagree with what most photos see.

Every photo that sees a label cell has a low-resolution colour there (exposure-corrected). The
per-cell median over all of them is the consensus; a photo whose colour is far from it (in Lab,
averaged over a few neighbouring cells, minus the typical distance of all photos there) most likely has something in front of the wall there: a
roof rafter, a rope, a person. Its score is scaled down by 1 / (1 + (dE / T)^2)^2, so the single-photo
choice (crisp, no ghosting of protruding holds) goes to the best photo that shows the wall itself.
"""
import warnings

import cv2
import numpy as np

CONSENSUS_DEFAULTS = {"consensusDeltaE": 10.0, "consensusBlurCells": 3, "consensusReliefDeltaE": 10.0}


def cells_lab(cells, gains):
    """(C, ch, cw, 3) BGR low-res colours (NaN = unseen) -> Lab (C, ch, cw, 3), NaN kept."""
    col = cells * np.asarray(gains, np.float32)[:, None, None, :]
    seen = ~np.isnan(col[..., 0])
    C, ch, cw, _ = col.shape
    src = np.clip(np.nan_to_num(col) / 255.0, 0, 1).astype(np.float32).reshape(C * ch, cw, 3)
    lab = cv2.cvtColor(src, cv2.COLOR_BGR2Lab).reshape(C, ch, cw, 3)
    lab[~seen] = np.nan
    return lab


def deviation(lab, blur_cells, relief):
    """Per photo and cell: Lab distance to the all-photo median, box-averaged over seen cells."""
    seen = ~np.isnan(lab[..., 0])
    if not seen.any():
        return np.zeros(lab.shape[:3], np.float32)
    count = seen.sum(0)
    med = np.full(lab.shape[1:], np.nan, np.float32)
    many = count >= 3  # with fewer photos there is no majority to trust
    if many.any():
        med[many] = np.nanmedian(lab[:, many], axis=0)
    d = np.sqrt(np.nansum((lab - med[None]) ** 2, -1)).astype(np.float32)
    ok = seen & many[None]
    d[~ok] = 0
    if blur_cells > 1:
        k = (blur_cells, blur_cells)
        for c in range(d.shape[0]):
            m = ok[c].astype(np.float32)
            num = cv2.boxFilter(d[c] * m, -1, k, normalize=False, borderType=cv2.BORDER_REPLICATE)
            den = cv2.boxFilter(m, -1, k, normalize=False, borderType=cv2.BORDER_REPLICATE)
            d[c] = np.where(m > 0, num / np.maximum(den, 1e-6), 0)
    # minus the typical disagreement at that spot (up to `relief`): where every photo disagrees
    # (parallax on a protruding volume) nobody is penalised much, only the photo that stands out
    with warnings.catch_warnings():
        warnings.simplefilter("ignore", RuntimeWarning)  # cells nobody sees
        typical = np.nanmedian(np.where(ok, d, np.nan), axis=0)
    return np.clip(d - np.minimum(np.nan_to_num(typical), relief)[None], 0, None) * ok


def penalised_scores(S, cells, gains, p):
    """Scores S (C, ch, cw) scaled down where a photo disagrees with the consensus."""
    d = deviation(cells_lab(cells, gains), int(p["consensusBlurCells"]),
                  float(p["consensusReliefDeltaE"]))
    return S / (1.0 + (d / float(p["consensusDeltaE"])) ** 2) ** 2
