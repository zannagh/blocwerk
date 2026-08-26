"""Appearance check: is the OLD hold actually visible at the position we matched it to?

Distance alone cannot decide a match, because the detector's boxes are often small
sub-boxes of a big hold, so a correct match can sit 80 px off the hold's centre while
a wrong match to the neighbour sits 120 px off. This resamples the old hold's patch
through the local affine of the registration and correlates it against the new image
in a SMALL window around the candidate position, which is an evidence source
independent of both the distance and the detector.
"""
import cv2
import numpy as np

WORK_OVERSAMPLE = 1.4
MAX_TPL = 96


def _pair(old, new, jac, po, pn, r_old, slack):
    """Best NCC of the old patch at `po` against the new image around `pn`."""
    base = float(np.sqrt(abs(np.linalg.det(jac))))
    if not np.isfinite(base) or base <= 1e-6:
        return 0.0
    tpl_half = float(np.clip(2.1 * max(r_old, 4.0), 14.0, 150.0))
    best = 0.0
    for sf in (0.9, 1.0, 1.1):
        shrink = WORK_OVERSAMPLE / (base * sf)
        hn = int(round(tpl_half * WORK_OVERSAMPLE))
        if hn > MAX_TPL:
            shrink *= MAX_TPL / hn
            hn = MAX_TPL
        j = jac * sf * shrink
        rad = max(4, int(round(slack * shrink)))
        size = 2 * hn + 1
        m = np.hstack([j, (np.array([hn, hn], np.float64) - j @ np.asarray(po))[:, None]])
        tpl = cv2.warpAffine(old, m, (size, size), flags=cv2.INTER_AREA,
                             borderMode=cv2.BORDER_REFLECT101)
        rhalf = hn + rad
        mr = np.array([[shrink, 0, rhalf - shrink * pn[0]],
                       [0, shrink, rhalf - shrink * pn[1]]])
        reg = cv2.warpAffine(new, mr, (2 * rhalf + 1, 2 * rhalf + 1), flags=cv2.INTER_AREA,
                             borderMode=cv2.BORDER_REFLECT101)
        if tpl.std() < 2.0 or reg.std() < 2.0:
            continue
        resp = cv2.matchTemplate(reg, tpl, cv2.TM_CCOEFF_NORMED)
        best = max(best, float(resp.max()))
    return best


def score(old, new, warp, po, r_old, targets, slack=45.0, log=None):
    """NCC for every (hold, candidate position) pair. `targets` is Nx2 in new px."""
    out = np.zeros(len(po))
    for i in range(len(po)):
        if not np.isfinite(targets[i]).all():
            continue
        out[i] = _pair(old, new, warp.jacobian(po[i]), po[i], targets[i],
                       float(r_old[i]), slack)
        if log and (i + 1) % 150 == 0:
            log(f"    scored {i + 1}/{len(po)}")
    return out


def blobness_search(img_lab, centres, radii, slack=0.9, step=0.45):
    """Best blobness over a small neighbourhood of each centre.

    The transferred position carries the registration error (tens of px here), so
    measuring the disc exactly at it under-reads whenever the hold is really there
    but slightly beside the guess. Taking the best score over a search the size of
    that error makes the presence/absence read comparable between a hold we matched
    and one we did not.
    """
    best = np.zeros(len(centres))
    offs = np.arange(-slack, slack + 1e-9, step)
    for dx in offs:
        for dy in offs:
            shifted = centres + np.stack([dx * radii, dy * radii], 1)
            best = np.maximum(best, blobness(img_lab, shifted, radii))
    return best


def blobness(img_lab, centres, radii, inner=0.7, outer=1.9):
    """How much the disc at each centre stands out from its surrounding annulus.

    Used to tell the two kinds of MISSING apart: if the transferred position now shows
    bare wood the hold was really taken off the wall, whereas a strong blob there means
    the hold is still up and the detector simply missed it.
    """
    h, w = img_lab.shape[:2]
    out = np.zeros(len(centres))
    for i, ((cx, cy), r) in enumerate(zip(centres, radii)):
        ro = max(6.0, outer * r)
        x0, x1 = int(max(0, cx - ro)), int(min(w, cx + ro + 1))
        y0, y1 = int(max(0, cy - ro)), int(min(h, cy + ro + 1))
        if x1 - x0 < 6 or y1 - y0 < 6:
            continue
        patch = img_lab[y0:y1, x0:x1].reshape(-1, 3)
        ys, xs = np.mgrid[y0:y1, x0:x1]
        d = np.sqrt((xs - cx) ** 2 + (ys - cy) ** 2).ravel()
        a = d <= inner * r
        b = (d > 1.25 * r) & (d <= ro)
        if a.sum() < 8 or b.sum() < 20:
            continue
        wgt = np.array([0.5, 1.0, 1.0])
        diff = np.linalg.norm((np.median(patch[a], 0) - np.median(patch[b], 0)) * wgt)
        spread = float(np.median(np.abs(patch[a] - np.median(patch[a], 0)).sum(1)))
        out[i] = diff + 0.35 * spread
    return out
