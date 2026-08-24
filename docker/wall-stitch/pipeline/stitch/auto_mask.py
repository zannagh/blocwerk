#!/usr/bin/env python3
"""Automatic plane discovery and per-image masking for an arbitrary photo set.

Replaces the hand-traced `WALL_POLY` / `KICK_POLY` / `LEFT_POLY_UNDIST` polygons
of `stitch_wall.py` with masks derived from the geometry of the upload alone, so
the pipeline can run on photos of a wall nobody has traced.

The chain of reasoning:

1. **Match everything, mask nothing.**  RootSIFT and exact brute-force ratio
   matching at half resolution, for every unordered pair of inputs.  No prior
   about where the wall is.

2. **Sequential MAGSAC per pair.**  Each accepted homography is one *plane
   observation*: by construction its inliers lie on a common plane in the scene.
   Ceiling, rafters, crash mats, floor and clutter are not on the wall, so they
   land in other observations or in none.

3. **Promote each seed to full resolution.**  A coarse seed is warped onto its
   partner and re-matched at full resolution under a proximity gate.  This is
   what makes the discovery precise: at half resolution the keypoint noise is
   about 1.2 px, so a single plane splits into several indistinguishable
   sub-fits; after promotion the main span of the reference set fits one
   homography to 1.1 px over ~1000 correspondences and the duplicates collapse.

4. **Identity across pairs by support overlap.**  Opaque surfaces occupy
   disjoint regions of an image, so two observations that share a camera and
   cover the same region of it are the same physical plane.  Union-find over
   support IoU turns per-pair observations into global planes - no plane
   normals, no decomposition ambiguity.

5. **Grow each plane.**  Every pair is revisited with its raw matches restricted
   to the plane's current masks.  That is how a plane reaches a pair where the
   first, unmasked RANSAC could not see it: on the reference photo set the main
   span in pair 3-4 is outvoted by the strongly textured kickboard until the
   kickboard has been masked away.

6. **Accept on support, then de-overlap.**  Planes clear a support and coverage
   bar; only accepted planes compete for contested cells.

Deterministic: no FLANN (its kd-tree seed is unreachable from Python and injects
run-to-run jitter), fixed `cv2.setRNGSeed`, all intermediates cached.
"""
import os

import cv2
import numpy as np

import plane_support as PS

COARSE = 0.5                # working scale for discovery
SIFT_CT = 0.012             # SIFT contrast threshold at the coarse scale
SIFT_N = 40000
RATIO = 0.85                # Lowe ratio for the discovery matcher
COARSE_THRESH = 4.0         # px at the coarse scale; ~3.5 sigma of keypoint noise
FULL_THRESH = 2.0           # px at full resolution, after promotion
PROX = 60.0                 # promotion proximity gate, full-resolution px
SEQ_MAX_PLANES = 6
MIN_SEED_INLIERS = 25       # a coarse seed needs at least this many
MIN_OBS_INLIERS = 60        # a promoted observation needs at least this many
DUP_MEDIAN_PX = 3.0         # two observations are the same plane fit below this
IOU_SAME_PLANE = 0.25       # support IoU in a shared camera -> same plane
MIN_PLANE_IMAGE_FRAC = 0.02 # a plane must cover 2% of a frame to count it
MIN_PLANE_INLIERS = 150     # ... and carry this much total support
GROW_ROUNDS = 3


# ---------------------------------------------------------------- features
def rootsift(des):
    des = des / (des.sum(1, keepdims=True) + 1e-7)
    return np.sqrt(des).astype(np.float32)


def detect(gray, mask=None, nfeatures=0, ct=0.02):
    sift = cv2.SIFT_create(nfeatures=nfeatures, contrastThreshold=ct, edgeThreshold=12)
    kp, de = sift.detectAndCompute(gray, mask)
    if de is None or len(kp) < 8:
        return np.zeros((0, 2)), np.zeros((0, 128), np.float32)
    return np.array([k.pt for k in kp], np.float64), rootsift(de)


def bf_ratio_match(d1, d2, ratio=RATIO):
    """Exact brute force, deliberately not FLANN: FLANN is not seedable here."""
    if len(d1) < 2 or len(d2) < 2:
        return np.zeros((0, 2), int)
    m = cv2.BFMatcher(cv2.NORM_L2).knnMatch(d1, d2, k=2)
    return np.array([[a.queryIdx, a.trainIdx] for a, b in m
                     if a.distance < ratio * b.distance], int).reshape(-1, 2)


# ---------------------------------------------------------------- plane fitting
def transfer_err(pa, pb, H):
    if not len(pa):
        return np.zeros(0)
    return np.linalg.norm(
        cv2.perspectiveTransform(pa.reshape(-1, 1, 2), H).reshape(-1, 2) - pb, axis=1)


def _refit(pa, pb, H, thresh, iters=6):
    """Guided refit: re-select the points the current H explains, fit again."""
    prev = -1
    for _ in range(iters):
        sel = np.flatnonzero(transfer_err(pa, pb, H) < thresh)
        if len(sel) < 12 or len(sel) == prev:
            break
        prev = len(sel)
        H2, _ = cv2.findHomography(pa[sel], pb[sel], cv2.USAC_MAGSAC, thresh,
                                   maxIters=50000, confidence=0.9999)
        if H2 is None:
            break
        H = H2
    return H, np.flatnonzero(transfer_err(pa, pb, H) < thresh)


def sequential_ransac(pa, pb, thresh=COARSE_THRESH, min_inliers=MIN_SEED_INLIERS,
                      max_planes=SEQ_MAX_PLANES, subset=None):
    """Peel one plane at a time off the correspondence set."""
    idx0 = np.arange(len(pa)) if subset is None else np.asarray(subset)
    rem, out = idx0, []
    for _ in range(max_planes):
        if len(rem) < 4 * min_inliers:
            break
        H, inl = cv2.findHomography(pa[rem], pb[rem], cv2.USAC_MAGSAC, thresh,
                                    maxIters=100000, confidence=0.9999)
        if H is None or inl is None:
            break
        hit = rem[inl.ravel().astype(bool)]
        if len(hit) < min_inliers:
            break
        H, sel = _refit(pa, pb, H, thresh)
        sel = np.intersect1d(sel, idx0)
        if len(sel) >= min_inliers and not any(
                len(np.intersect1d(sel, o[1])) > 0.5 * min(len(sel), len(o[1])) for o in out):
            out.append((H, sel))
        rem = np.setdiff1d(rem, np.union1d(hit, sel))
    out.sort(key=lambda o: -len(o[1]))
    return out


def promote(ia, ib, Hf, mask_a=None, mask_b=None):
    """Re-match a coarse seed at full resolution through its own warp."""
    h, w = ib.shape[:2]
    src_mask = np.full(ia.shape[:2], 255, np.uint8) if mask_a is None else mask_a
    wa = cv2.warpPerspective(ia, Hf, (w, h), flags=cv2.INTER_LINEAR)
    ov = cv2.warpPerspective(src_mask, Hf, (w, h), flags=cv2.INTER_NEAREST)
    if mask_b is not None:
        ov = cv2.bitwise_and(ov, mask_b)
    if ov.sum() == 0:
        return None
    pw, dw = detect(cv2.cvtColor(wa, cv2.COLOR_BGR2GRAY), ov)
    pb2, db2 = detect(cv2.cvtColor(ib, cv2.COLOR_BGR2GRAY), ov)
    mj = bf_ratio_match(dw, db2, 0.9)
    if len(mj) < 20:
        return None
    P, Q = pw[mj[:, 0]], pb2[mj[:, 1]]
    keep = np.linalg.norm(P - Q, axis=1) < PROX
    P, Q = P[keep], Q[keep]
    if len(P) < 20:
        return None
    H2, inl = cv2.findHomography(P, Q, cv2.USAC_MAGSAC, FULL_THRESH,
                                 maxIters=50000, confidence=0.9999)
    if H2 is None or inl is None:
        return None
    inl = inl.ravel().astype(bool)
    Pa = cv2.perspectiveTransform(P.reshape(-1, 1, 2), np.linalg.inv(Hf)).reshape(-1, 2)
    H = H2 @ Hf
    e = transfer_err(Pa[inl], Q[inl], H)
    if len(e) < MIN_OBS_INLIERS:
        return None
    return dict(H=H, pa=Pa[inl], pb=Q[inl], n=int(inl.sum()),
                candidates=int(len(P)), rms=float(np.sqrt((e ** 2).mean())))


def dedupe(obs):
    """Drop observations that are just another fit of a plane already listed."""
    out = []
    for o in obs:
        if any(np.median(transfer_err(o["pa"], o["pb"], k["H"])) < DUP_MEDIAN_PX for k in out):
            continue
        out.append(o)
    return out
