"""Per-hold local refinement.

For every live hold the old photo is resampled, through the LOCAL affine (the
Jacobian of the seed map at that hold), into the new photo's geometry, and then
correlated against a bounded window of the orthophoto. Each hold therefore gets
its own displacement, which is the point of the exercise: one global warp cannot
be right everywhere when the source is lens-distorted and the target is rectified.

Matching runs at ~1.4x the OLD photo's local sampling rate rather than at the
orthophoto's native rate. The old photo is the information-limited side; blowing
its patches up to 3x only invents detail and costs time.
"""
import cv2
import numpy as np

WORK_OVERSAMPLE = 1.4   # working resolution relative to the old photo's local rate
MAX_TPL = 96            # template half-size cap in working px (cost bound)
SEARCH_PX = 60          # +/- search radius around the seeded centre, full-res new px
SCALES = (0.94, 1.0, 1.06)


def _subpixel(resp, y, x):
    dy = dx = 0.0
    if 0 < x < resp.shape[1] - 1:
        a, b, c = resp[y, x - 1], resp[y, x], resp[y, x + 1]
        den = a - 2 * b + c
        if abs(den) > 1e-9:
            dx = float(np.clip(0.5 * (a - c) / den, -1, 1))
    if 0 < y < resp.shape[0] - 1:
        a, b, c = resp[y - 1, x], resp[y, x], resp[y + 1, x]
        den = a - 2 * b + c
        if abs(den) > 1e-9:
            dy = float(np.clip(0.5 * (a - c) / den, -1, 1))
    return dy, dx


def _peak_margin(resp, y, x, excl):
    m = resp.copy()
    y0, y1 = max(0, y - excl), min(resp.shape[0], y + excl + 1)
    x0, x1 = max(0, x - excl), min(resp.shape[1], x + excl + 1)
    m[y0:y1, x0:x1] = -2.0
    best = m.max()
    return 1.0 if best <= -2.0 else float(resp[y, x] - best)


def refine_hold(old_img, new_img, seed, po, r_old, search=SEARCH_PX):
    """Correlate one old hold patch into the new orthophoto. All px are full-res."""
    nh, nw = new_img.shape[:2]
    jac = seed.jacobian(po)
    pn = seed(po)[0]
    base_scale = float(np.sqrt(abs(np.linalg.det(jac))))
    out = {"seed": pn.tolist(), "new": pn.tolist(), "ncc": 0.0, "margin": 0.0,
           "scale": base_scale, "offset": 0.0, "at_limit": False, "in_frame": True}
    if not (0 <= pn[0] < nw and 0 <= pn[1] < nh):
        out["in_frame"] = False
        return out

    tpl_half_old = float(np.clip(2.1 * max(r_old, 4.0), 14.0, 150.0))
    best = None
    for sf in SCALES:
        # shrink maps full-res new px -> working px
        shrink = WORK_OVERSAMPLE / (base_scale * sf)
        hn = int(round(tpl_half_old * WORK_OVERSAMPLE))
        if hn > MAX_TPL:
            shrink *= MAX_TPL / hn
            hn = MAX_TPL
        j = jac * sf * shrink
        rad = max(6, int(round(search * shrink)))
        size = 2 * hn + 1
        m = np.hstack([j, (np.array([hn, hn], np.float64) - j @ np.asarray(po))[:, None]])
        tpl = cv2.warpAffine(old_img, m, (size, size), flags=cv2.INTER_AREA,
                             borderMode=cv2.BORDER_REFLECT101)
        rhalf = hn + rad
        mr = np.array([[shrink, 0, rhalf - shrink * pn[0]], [0, shrink, rhalf - shrink * pn[1]]])
        reg = cv2.warpAffine(new_img, mr, (2 * rhalf + 1, 2 * rhalf + 1),
                             flags=cv2.INTER_AREA, borderMode=cv2.BORDER_REFLECT101)
        if tpl.std() < 2.0 or reg.std() < 2.0:
            continue
        resp = cv2.matchTemplate(reg, tpl, cv2.TM_CCOEFF_NORMED)
        _, mx, _, loc = cv2.minMaxLoc(resp)
        x, y = loc
        dy, dx = _subpixel(resp, y, x)
        margin = _peak_margin(resp, y, x, max(3, int(hn * 0.6)))
        off = (np.array([x + dx, y + dy]) - rad) / shrink
        cand = {"ncc": float(mx), "margin": margin, "scale": base_scale * sf,
                "new": (pn + off).tolist(), "offset": float(np.hypot(*off)),
                "at_limit": bool(min(x, y) <= 0 or x >= resp.shape[1] - 1 or y >= resp.shape[0] - 1),
                "in_frame": True, "seed": pn.tolist()}
        if best is None or cand["ncc"] > best["ncc"]:
            best = cand
    return best if best is not None else out


def refine_all(old, new, seed, holds, log=print, search=SEARCH_PX):
    oh, ow = old.shape[:2]
    out = []
    for i, h in enumerate(holds):
        po = (h["X"] * ow, h["Y"] * oh)
        r_old = float(h.get("Radius") or 0.0) * max(ow, oh)
        out.append(refine_hold(old, new, seed, po, r_old, search=search))
        if log and (i + 1) % 150 == 0:
            log(f"    refined {i + 1}/{len(holds)}")
    return out
