"""Coarse old-photo -> new-base homography, the seed the carryover ICP starts from.

Vendored from new-run/exp5/probe.py. The fixed image paths, the fixed new-image size
and the wall-segment mask are now arguments; the search and the scoring are unchanged.

SIFT inlier counts lie under repetitive wood texture, so candidates are scored on an
independent criterion instead: run an ICP between the stored hold centres and the
detections in the new image, and count how many ONE-TO-ONE mutual-nearest pairs
survive a tight tolerance. A degenerate homography that collapses the wall into the
dense hold cluster scores near zero on that, which is the point.
"""
import itertools
import os
import pickle

import cv2
import numpy as np

ICP_TOLS = (500.0, 360.0, 260.0, 190.0, 140.0, 110.0)
TILTS = (1.0, 1.6, 2.4)
PHIS = (0.0, -20.0, 20.0)
COARSE_SCALES = (0.25, 1 / 3.0, 0.45)


def rootsift(gray, mask=None, nfeatures=60000, contrast=0.02, edge=12):
    sift = cv2.SIFT_create(nfeatures=nfeatures, contrastThreshold=contrast,
                           edgeThreshold=edge)
    kp, des = sift.detectAndCompute(gray, mask)
    if des is None or len(kp) == 0:
        return [], np.zeros((0, 128), np.float32)
    des = des / (des.sum(1, keepdims=True) + 1e-7)
    return kp, np.sqrt(des).astype(np.float32)


def tilt_images(img, mask, tilts=(1.0,), phis=(0.0,)):
    """ASIFT-style affine simulation. Yields (warped, mask, A) with A: img -> warped."""
    out = []
    h, w = img.shape[:2]
    for t, phi in itertools.product(tilts, phis):
        a = np.eye(3)
        cur, curm = img, mask
        if phi != 0.0:
            r = cv2.getRotationMatrix2D((w / 2, h / 2), phi, 1.0)
            cs, sn = abs(r[0, 0]), abs(r[0, 1])
            ww, hh = int(h * sn + w * cs), int(h * cs + w * sn)
            r[0, 2] += ww / 2 - w / 2
            r[1, 2] += hh / 2 - h / 2
            cur = cv2.warpAffine(img, r, (ww, hh), flags=cv2.INTER_LINEAR)
            curm = cv2.warpAffine(mask, r, (ww, hh), flags=cv2.INTER_NEAREST)
            a = np.vstack([r, [0, 0, 1]]) @ a
        if t != 1.0:
            cur = cv2.GaussianBlur(cur, (0, 0), 0.8 * np.sqrt(t * t - 1), sigmaY=0.01)
            s = np.array([[1 / t, 0, 0], [0, 1, 0], [0, 0, 1]], np.float64)
            cur = cv2.warpAffine(cur, s[:2], (int(cur.shape[1] / t), cur.shape[0]),
                                 flags=cv2.INTER_AREA)
            curm = cv2.warpAffine(curm, s[:2], (cur.shape[1], cur.shape[0]),
                                  flags=cv2.INTER_NEAREST)
            a = s @ a
        out.append((cur, curm, a))
    return out


def mutual_pairs(p, cen, tol):
    """Mutual-nearest one-to-one pairs within tol. Returns (i_idx, j_idx, dists)."""
    d = np.linalg.norm(p[:, None, :] - cen[None, :, :], axis=-1)
    a, b = d.argmin(1), d.argmin(0)
    i = np.arange(len(p))
    ok = (b[a] == i) & (d[i, a] <= tol)
    return i[ok], a[ok], d[i[ok], a[ok]]


def icp(h, po, cen, tols=ICP_TOLS):
    """Refine a homography by alternating mutual-NN matching and re-fitting."""
    cur = h.copy()
    stats = (0, np.inf)
    for tol in tols:
        p = cv2.perspectiveTransform(po.reshape(-1, 1, 2).astype(np.float32),
                                     cur).reshape(-1, 2)
        i, j, d = mutual_pairs(p, cen, tol)
        if len(i) < 20:
            return cur, stats
        nh, _ = cv2.findHomography(po[i].astype(np.float32), cen[j].astype(np.float32),
                                   cv2.USAC_MAGSAC, tol * 0.5, maxIters=20000,
                                   confidence=0.999)
        if nh is None:
            return cur, stats
        cur = nh / nh[2, 2]
        p = cv2.perspectiveTransform(po.reshape(-1, 1, 2).astype(np.float32),
                                     cur).reshape(-1, 2)
        i, j, d = mutual_pairs(p, cen, tol)
        stats = (len(i), float(np.median(d)) if len(d) else np.inf)
    return cur, stats


def solve(old, new, po, cen, cache_dir=None, old_mask=None, log=print):
    """Search coarse old->new homographies and keep the one the hold ICP likes best."""
    cache = os.path.join(cache_dir, 'seed-candidates.pkl') if cache_dir else None
    if cache and os.path.exists(cache):
        cands = pickle.load(open(cache, 'rb'))
    else:
        oldg = cv2.cvtColor(old, cv2.COLOR_BGR2GRAY)
        mask = old_mask if old_mask is not None else np.full(oldg.shape, 255, np.uint8)
        sims = tilt_images(oldg, mask, tilts=TILTS, phis=PHIS)
        cands = []
        for cs in COARSE_SCALES:
            news = cv2.resize(new, None, fx=cs, fy=cs, interpolation=cv2.INTER_AREA)
            k2, d2 = rootsift(cv2.cvtColor(news, cv2.COLOR_BGR2GRAY))
            bf = cv2.BFMatcher()
            for wi, (wimg, wmask, a) in enumerate(sims):
                k1, d1 = rootsift(wimg, wmask)
                if len(k1) < 50:
                    continue
                knn = bf.knnMatch(d1, d2, k=2)
                for ratio in (0.8, 0.9):
                    good = [m for m, n in knn if m.distance < ratio * n.distance]
                    if len(good) < 30:
                        continue
                    src = np.float32([k1[g.queryIdx].pt for g in good])
                    dst = np.float32([k2[g.trainIdx].pt for g in good])
                    hh, inl = cv2.findHomography(src, dst, cv2.USAC_MAGSAC, 4.0,
                                                 maxIters=50000, confidence=0.9999)
                    if hh is None:
                        continue
                    full = np.diag([1 / cs, 1 / cs, 1.0]) @ hh @ a
                    cands.append((f'cs={cs:.2f} sim={wi} ratio={ratio}',
                                  full / full[2, 2], int(inl.sum()), len(good)))
            log('  coarse scale %.2f: %d candidates so far' % (cs, len(cands)))
        if cache:
            os.makedirs(cache_dir, exist_ok=True)
            pickle.dump(cands, open(cache, 'wb'))
    if not cands:
        raise SystemExit('no coarse old->new homography candidate survived matching')

    scored = []
    for tag, h, inl, _ in cands:
        rh, (n, med) = icp(h, po, cen)
        scored.append((n, med, tag, rh, inl))
    scored.sort(key=lambda s: (-s[0], s[1]))
    n, med, tag, rh, inl = scored[0]
    log('seed %s: %d/%d one-to-one pairs at 110px, median %.1f px'
        % (tag, n, len(po), med))
    return rh, {'tag': tag, 'icp_pairs': int(n), 'icp_median_px': float(med),
                'sift_inliers': int(inl), 'candidates': len(cands)}
