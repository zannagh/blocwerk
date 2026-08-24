"""Coarse global old->new initialisation.

Stage 1 fits a single homography from RootSIFT correspondences. Stage 2 warps the
old photo into the new frame with it and re-matches under a tight proximity gate,
which yields correspondences right out to the edges; a robust bivariate cubic is
then fitted to those. The cubic (not the homography) is what seeds the per-hold
search: it absorbs the old photo's lens distortion, which no homography can.
"""
import json

import cv2
import numpy as np

from hm_common import PolyMap, WALL_JSON


def _rootsift(gray, mask=None, nfeatures=40000):
    sift = cv2.SIFT_create(nfeatures=nfeatures)
    kp, des = sift.detectAndCompute(gray, mask)
    if des is None or len(kp) == 0:
        return [], np.zeros((0, 128), np.float32)
    des = des / (des.sum(1, keepdims=True) + 1e-7)
    return kp, np.sqrt(des).astype(np.float32)


def main_span_mask(shape, exclude=("Segment 2",)):
    h, w = shape[:2]
    with open(WALL_JSON) as fh:
        wall = json.load(fh)
    mask = np.zeros((h, w), np.uint8)
    for seg in wall["segments"]:
        if seg["Name"] in exclude:
            continue
        pts = np.array([[p["Dx"] * w, p["Dy"] * h] for p in seg["Points"]], np.int32)
        cv2.fillPoly(mask, [pts], 255)
    return mask


def side_panel_mask(shape, include=("Segment 2",)):
    h, w = shape[:2]
    with open(WALL_JSON) as fh:
        wall = json.load(fh)
    mask = np.zeros((h, w), np.uint8)
    for seg in wall["segments"]:
        if seg["Name"] in include:
            pts = np.array([[p["Dx"] * w, p["Dy"] * h] for p in seg["Points"]], np.int32)
            cv2.fillPoly(mask, [pts], 255)
    return mask


def _ratio_match(d1, d2, ratio=0.8):
    bf = cv2.BFMatcher()
    pairs = bf.knnMatch(d1, d2, k=2)
    return [a for a, b in pairs if a.distance < ratio * b.distance]


def coarse_homography(old, new, coarse_scale=1 / 3.0):
    """Homography mapping OLD pixels -> FULL-RESOLUTION NEW pixels."""
    news = cv2.resize(new, None, fx=coarse_scale, fy=coarse_scale, interpolation=cv2.INTER_AREA)
    mask = main_span_mask(old.shape)
    k1, d1 = _rootsift(cv2.cvtColor(old, cv2.COLOR_BGR2GRAY), mask)
    k2, d2 = _rootsift(cv2.cvtColor(news, cv2.COLOR_BGR2GRAY))
    good = _ratio_match(d1, d2)
    src = np.float32([k1[g.queryIdx].pt for g in good])
    dst = np.float32([k2[g.trainIdx].pt for g in good])
    h, inl = cv2.findHomography(src, dst, cv2.USAC_MAGSAC, 4.0, maxIters=20000, confidence=0.9999)
    up = np.diag([1 / coarse_scale, 1 / coarse_scale, 1.0])
    return up @ h, int(inl.sum()), len(good)


def guided_correspondences(old, new, h_full, work_scale=0.5, gate=120.0, ratio=0.85):
    """Re-match old-warped-into-new against new at `work_scale`, gated by proximity."""
    down = np.diag([work_scale, work_scale, 1.0])
    h_w = down @ h_full
    nw = int(round(new.shape[1] * work_scale))
    nh = int(round(new.shape[0] * work_scale))
    warped = cv2.warpPerspective(old, h_w, (nw, nh), flags=cv2.INTER_LANCZOS4)
    cover = cv2.warpPerspective(
        main_span_mask(old.shape), h_w, (nw, nh), flags=cv2.INTER_NEAREST)
    news = cv2.resize(new, (nw, nh), interpolation=cv2.INTER_AREA)

    k1, d1 = _rootsift(cv2.cvtColor(warped, cv2.COLOR_BGR2GRAY), cover, nfeatures=60000)
    k2, d2 = _rootsift(cv2.cvtColor(news, cv2.COLOR_BGR2GRAY), cover, nfeatures=60000)
    good = _ratio_match(d1, d2, ratio)
    p1 = np.float32([k1[g.queryIdx].pt for g in good])
    p2 = np.float32([k2[g.trainIdx].pt for g in good])
    keep = np.linalg.norm(p1 - p2, axis=1) <= gate
    p1, p2 = p1[keep], p2[keep]
    # warped-frame -> original old pixels
    inv = np.linalg.inv(h_w)
    src_old = cv2.perspectiveTransform(p1.reshape(-1, 1, 2), inv).reshape(-1, 2)
    dst_new = p2 / work_scale
    return src_old, dst_new


class SeedMap:
    """Homography + robust polynomial residual correction. Old pixels -> new pixels.

    A projective map is rational, so a bare polynomial fits it badly; conversely a
    homography cannot express the old photo's lens distortion. Composing the two
    gives a map that is projectively correct in the large and free-form in the small.
    """

    def __init__(self, h_full, poly):
        self.h = np.asarray(h_full, np.float64)
        self.poly = poly

    def __call__(self, pts):
        p = np.atleast_2d(np.asarray(pts, np.float64)).astype(np.float32)
        base = cv2.perspectiveTransform(p.reshape(-1, 1, 2), self.h).reshape(-1, 2)
        return base.astype(np.float64) + (self.poly(p) if self.poly is not None else 0.0)

    def jacobian(self, pt, eps=4.0):
        p = np.asarray(pt, np.float64).reshape(1, 2)
        base = self(p)[0]
        jx = (self(p + [eps, 0])[0] - base) / eps
        jy = (self(p + [0, eps])[0] - base) / eps
        return np.stack([jx, jy], 1)


def build_global_map(old, new, degree=3, log=print):
    h_full, n_inl, n_good = coarse_homography(old, new)
    log(f"  stage 1 homography: {n_inl}/{n_good} RANSAC inliers")
    src, dst = guided_correspondences(old, new, h_full)
    log(f"  stage 2 guided correspondences: {len(src)}")
    base = cv2.perspectiveTransform(src.reshape(-1, 1, 2), h_full).reshape(-1, 2)
    hres = np.linalg.norm(base - dst, axis=1)
    delta = dst - base
    keep = np.ones(len(src), bool)
    for _ in range(4):
        poly = PolyMap.fit(src[keep], delta[keep], degree=degree, huber=6.0)
        r = np.linalg.norm(base + poly(src) - dst, axis=1)
        keep = r < max(12.0, 3.0 * np.median(r[keep]))
    seed = SeedMap(h_full, poly)
    res = np.linalg.norm(seed(src[keep]) - dst[keep], axis=1)
    log(f"  robust trim kept {int(keep.sum())}/{len(src)} correspondences")
    stats = {
        "homography_inliers": n_inl,
        "guided_correspondences": int(len(src)),
        "homography_residual_px": {
            "median": float(np.median(hres)), "p90": float(np.percentile(hres, 90))},
        "seedmap_residual_px": {
            "median": float(np.median(res)), "p90": float(np.percentile(res, 90))},
        "correspondences_kept": int(keep.sum()),
        "degree": degree,
    }
    log("  homography residual  med %.1f px  p90 %.1f px" % (
        stats["homography_residual_px"]["median"], stats["homography_residual_px"]["p90"]))
    log("  seed map  residual  med %.1f px  p90 %.1f px" % (
        stats["seedmap_residual_px"]["median"], stats["seedmap_residual_px"]["p90"]))
    return seed, h_full, (src[keep], dst[keep]), stats
