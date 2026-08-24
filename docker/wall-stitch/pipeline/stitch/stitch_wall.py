#!/usr/bin/env python3
"""Dominant-plane orthophoto of a home climbing wall from handheld phone photos.

Pipeline: plumb-line lens calibration -> undistort -> masked SIFT matching ->
plane homographies -> global refinement -> metric rectification from the plane
normal (homography decomposition) + horizontal seam vanishing point ->
resolution-matched canvas -> gain compensation -> graph-cut seams -> multiband blend.

Deterministic: fixed RNG seeds everywhere, no interactive steps.
"""
import argparse, json, os, time
import cv2
import numpy as np
from scipy.optimize import least_squares

import angled_view
import stitch_planes

SEED = 20260822
W0, H0 = 3024, 4032
F35_EQ = 14.0                      # EXIF 35mm-equivalent focal length (iPhone 16 Pro ultra-wide)
SENSOR_DIAG_35MM = 43.2666

# Hand-authored wall polygons on the ORIGINAL full-res frames (see README).
# They select the main climbing span only: no floor, mats, ceiling, beams,
# clutter, and no left/right return panels (which are different planes).
WALL_POLY = {
    "1": [(0, 300), (3010, 300), (3010, 2620), (1150, 2660), (880, 2705), (190, 1000), (0, 520)],
    "2": [(10, 360), (3010, 360), (3010, 2720), (10, 2690)],
    "3": [(10, 320), (2660, 320), (2660, 2700), (10, 2720)],
    "4": [(10, 200), (1520, 200), (1520, 2690), (10, 2690)],
}

# Pushing these polygons up to the *physical* top edge of the plywood (measured
# per image: 1 -> y 225/211 left/right, 3 -> 193/165, 4 -> above the frame;
# 2 already sits on it at y 360) was tried and reverted.  It moves the mosaic's
# top edge up by 54 canvas px but the largest 97%-filled rectangle only gains
# 22 px of that, costs 32 px of width, and degrades the bow of the traced seams
# from 1.13 to 1.61 px and the seam-angle rms from 0.062 to 0.069 deg, because
# the extra material is the blurred, grazing top of 4.jpeg.  The top plank row
# is genuinely below the crop only by ~100 canvas px and the fix for that is a
# reshoot, not a mask.

# The kickboard: the plank strip between the bottom edge of the main span and
# the mat.  Traced per image from the shadow line at its top edge down to the
# mat.  It is NOT coplanar with the main span (see report.json -> kickboard),
# so it is registered, rectified and delivered as its own plane.
KICK_POLY = {
    "1": [(1120, 2712), (3015, 2636), (3015, 2742), (1120, 2790)],
    "2": [(20, 2790), (3010, 2700), (3010, 2862), (20, 2872)],
    "3": [(20, 2728), (2655, 2698), (2655, 2828), (20, 2842)],
    "4": [(20, 2698), (1515, 2702), (1515, 2822), (20, 2792)],
}

# The left return panel, seen only in 1.jpeg, traced on the UNDISTORTED frame
# (it is rasterised directly, not remapped).  Bounded by the crease with the
# main span on the upper right, the panel's free edge on the left and the mat
# on the lower right.
LEFT_POLY_UNDIST = [(150, 1150), (985, 2755), (915, 2830), (690, 3120),
                    (500, 3400), (330, 3690), (140, 3690), (140, 1150)]
# Two points on the crease between the main span and the left return panel,
# read off the LSD segments that lie on it in the undistorted 1.jpeg; only a
# seed, the line is refit robustly at run time.
LEFT_CREASE_SEED = ((172.0, 989.0), (704.0, 2077.0))

IMAGES = ["1", "2", "3", "4"]      # "5" is rejected: see README / report
REF = "2"
PAIRS = [("1", "2"), ("2", "3"), ("3", "4")]   # 1-3 and 2-4 have no usable overlap


def log(*a):
    print(f"[{time.strftime('%H:%M:%S')}]", *a, flush=True)


def intrinsics(w, h, f35=F35_EQ):
    f = f35 * np.hypot(w, h) / SENSOR_DIAG_35MM
    return np.array([[f, 0, w / 2.0], [0, f, h / 2.0], [0, 0, 1.0]])


# --------------------------------------------------------------------------
# 1. plumb-line radial-distortion calibration
# --------------------------------------------------------------------------
def line_chains(gray, min_seg=60, max_gap=220, ang_tol_deg=7.0, perp_tol=12.0,
                min_span=600, max_parab_rms=2.0, min_pts=8):
    """Group LSD segments into chains that plausibly lie on one physical straight edge."""
    lsd = cv2.createLineSegmentDetector()
    segs = lsd.detect(gray)[0]
    if segs is None:
        return []
    s = segs.reshape(-1, 4)
    L = np.hypot(s[:, 2] - s[:, 0], s[:, 3] - s[:, 1])
    s, L = s[L > min_seg], L[L > min_seg]
    n = len(s)
    mid = np.stack([(s[:, 0] + s[:, 2]) / 2, (s[:, 1] + s[:, 3]) / 2], 1)
    d = np.stack([s[:, 2] - s[:, 0], s[:, 3] - s[:, 1]], 1)
    d /= np.linalg.norm(d, axis=1, keepdims=True)
    ang = np.arctan2(d[:, 1], d[:, 0]) % np.pi
    parent = list(range(n))

    def find(a):
        while parent[a] != a:
            parent[a] = parent[parent[a]]
            a = parent[a]
        return a

    for i in range(n):
        for j in range(i + 1, n):
            da = abs(ang[i] - ang[j])
            da = min(da, np.pi - da)
            if da > np.deg2rad(ang_tol_deg):
                continue
            v = mid[j] - mid[i]
            if abs(v[0] * d[i][1] - v[1] * d[i][0]) > perp_tol:
                continue
            if abs(v[0] * d[i][0] + v[1] * d[i][1]) > (L[i] + L[j]) / 2 + max_gap:
                continue
            a, b = find(i), find(j)
            if a != b:
                parent[a] = b
    groups = {}
    for i in range(n):
        groups.setdefault(find(i), []).append(i)
    out = []
    for g in groups.values():
        if len(g) < 3:
            continue
        pts = np.concatenate([s[g][:, :2], s[g][:, 2:]], 0).astype(np.float64)
        if np.linalg.norm(pts.max(0) - pts.min(0)) < min_span or len(pts) < min_pts:
            continue
        c = pts - pts.mean(0)
        _, _, V = np.linalg.svd(c, full_matrices=False)
        t, u = c @ V[0], c @ V[1]
        A = np.stack([t ** 2, t, np.ones_like(t)], 1)
        coef, *_ = np.linalg.lstsq(A, u, rcond=None)
        if np.sqrt(((A @ coef - u) ** 2).mean()) > max_parab_rms:   # not a smooth arc -> bad grouping
            continue
        out.append(pts)
    return out


def calibrate_distortion(paths, K):
    chains = []
    for p in paths:
        g = cv2.imread(p, cv2.IMREAD_GRAYSCALE)
        chains += line_chains(g)

    def resid(k):
        D = np.array([k[0], k[1], 0, 0, 0])
        r = []
        for pts in chains:
            u = cv2.undistortPoints(pts.reshape(-1, 1, 2), K, D, P=K).reshape(-1, 2)
            u = u - u.mean(0)
            _, _, V = np.linalg.svd(u, full_matrices=False)
            r.append(u @ V[1])
        return np.concatenate(r)

    rms0 = float(np.sqrt((resid([0.0, 0.0]) ** 2).mean()))
    sol = least_squares(resid, [0.0, 0.0], method="lm")
    rms1 = float(np.sqrt((sol.fun ** 2).mean()))
    return sol.x, dict(n_chains=len(chains), n_pts=int(sum(len(c) for c in chains)),
                       straightness_rms_px_before=rms0, straightness_rms_px_after=rms1)


# --------------------------------------------------------------------------
# 2. features / pairwise homographies
# --------------------------------------------------------------------------
def rootsift(des):
    des = des / (des.sum(1, keepdims=True) + 1e-7)
    return np.sqrt(des).astype(np.float32)


def detect(img, mask):
    sift = cv2.SIFT_create(contrastThreshold=0.02, edgeThreshold=12)
    kp, des = sift.detectAndCompute(cv2.cvtColor(img, cv2.COLOR_BGR2GRAY), mask)
    return np.array([k.pt for k in kp], np.float64), rootsift(des)


def match(d1, d2, ratio=0.85):
    bf = cv2.BFMatcher(cv2.NORM_L2)
    m = bf.knnMatch(d1, d2, k=2)
    return np.array([[a.queryIdx, a.trainIdx] for a, b in m if a.distance < ratio * b.distance], int)


def match_pair(ia, ma, ib, mb, coarse=0.5, prox=60.0):
    """Two-stage plane matching.

    Stage A: RootSIFT at half resolution -> coarse homography (MAGSAC).
    Stage B: warp A into B with that homography (viewpoints now nearly identical),
             re-detect at full resolution inside the overlap only, match with a
             proximity gate, and refine.  Returns full-res correspondences in the
             ORIGINAL frames of A and B plus the refined homography A->B.
    """
    S = np.diag([coarse, coarse, 1.0])
    sa = cv2.resize(ia, None, fx=coarse, fy=coarse, interpolation=cv2.INTER_AREA)
    sb = cv2.resize(ib, None, fx=coarse, fy=coarse, interpolation=cv2.INTER_AREA)
    qa = cv2.resize(ma, None, fx=coarse, fy=coarse, interpolation=cv2.INTER_NEAREST)
    qb = cv2.resize(mb, None, fx=coarse, fy=coarse, interpolation=cv2.INTER_NEAREST)
    pa, da = detect(sa, qa)
    pb, db = detect(sb, qb)
    mi = match(da, db)
    if len(mi) < 20:
        return None
    H0_, inl0 = cv2.findHomography(pa[mi[:, 0]], pb[mi[:, 1]], cv2.USAC_MAGSAC, 3.0,
                                   maxIters=50000, confidence=0.9999)
    if H0_ is None or inl0.sum() < 20:
        return None
    Hf = np.linalg.inv(S) @ H0_ @ S
    h, w = ib.shape[:2]
    wa = cv2.warpPerspective(ia, Hf, (w, h), flags=cv2.INTER_LINEAR)
    wm = cv2.warpPerspective(ma, Hf, (w, h), flags=cv2.INTER_NEAREST)
    ov = cv2.bitwise_and(wm, mb)
    if ov.sum() == 0:
        return None
    pw, dw = detect(wa, ov)
    pb2, db2 = detect(ib, ov)
    mj = match(dw, db2, 0.9)
    if len(mj) < 20:
        return None
    P, Q = pw[mj[:, 0]], pb2[mj[:, 1]]
    keep = np.linalg.norm(P - Q, axis=1) < prox
    P, Q = P[keep], Q[keep]
    if len(P) < 20:
        return None
    H2, inl = cv2.findHomography(P, Q, cv2.USAC_MAGSAC, 2.0, maxIters=50000, confidence=0.9999)
    inl = inl.ravel().astype(bool)
    Pa = cv2.perspectiveTransform(P.reshape(-1, 1, 2), np.linalg.inv(Hf)).reshape(-1, 2)
    H = H2 @ Hf
    err = np.linalg.norm(cv2.perspectiveTransform(Pa[inl].reshape(-1, 1, 2), H).reshape(-1, 2) - Q[inl], axis=1)
    return dict(H=H, pa=Pa[inl], pb=Q[inl], n_coarse=int(inl0.sum()), n_stageb=int(len(mj)),
                n_inl=int(inl.sum()), rms=float(np.sqrt((err ** 2).mean())),
                overlap_mpx=float(ov.sum() / 255 / 1e6))


# --------------------------------------------------------------------------
# 3. global refinement of all homographies to the reference frame
# --------------------------------------------------------------------------
def refine(Hs, corr, ref, names):
    free = [n for n in names if n != ref]
    idx = {n: i for i, n in enumerate(free)}
    x0 = np.concatenate([(Hs[n] / Hs[n][2, 2]).ravel()[:8] for n in free])

    def unpack(x):
        out = {ref: np.eye(3)}
        for n in free:
            out[n] = np.append(x[idx[n] * 8:idx[n] * 8 + 8], 1.0).reshape(3, 3)
        return out

    def res(x):
        Hm = unpack(x)
        r = []
        for (a, b), (pa, pb) in corr.items():
            A, B = Hm[a], Hm[b]
            qa = cv2.perspectiveTransform(pa.reshape(-1, 1, 2), A).reshape(-1, 2)
            qb = cv2.perspectiveTransform(pb.reshape(-1, 1, 2), B).reshape(-1, 2)
            r.append((qa - qb).ravel())
        return np.concatenate(r)

    r0 = res(x0)
    sol = least_squares(res, x0, method="trf", loss="huber", f_scale=3.0,
                        xtol=1e-12, ftol=1e-12, max_nfev=300, verbose=0)
    stats = dict(rms_before=float(np.sqrt((r0 ** 2).mean()) * np.sqrt(2)),
                 rms_after=float(np.sqrt((sol.fun ** 2).mean()) * np.sqrt(2)))
    return unpack(sol.x), stats


# --------------------------------------------------------------------------
# 4. metric rectification
# --------------------------------------------------------------------------
def plane_normal(H_ab, K, pts_a):
    """Normal of the plane in camera A coords, from the A->B homography."""
    n_sol, Rs, Ts, Ns = cv2.decomposeHomographyMat(np.linalg.inv(H_ab), K)
    # decomposeHomographyMat expects H mapping B->A for normals in A; filter by visibility
    keep = cv2.filterHomographyDecompByVisibleRefpoints(
        Rs, Ns, pts_a.reshape(-1, 1, 2), pts_a.reshape(-1, 1, 2))
    cands = [np.array(Ns[i]).ravel() for i in (keep.ravel() if keep is not None else range(n_sol))]
    cands = [c / np.linalg.norm(c) for c in cands]
    cands = [c if c[2] > 0 else -c for c in cands]      # point away from camera (into the wall)
    return cands


def frame_from_normal(n_wall, theta, ref_dx=None):
    """Orthonormal camera frame looking along the wall normal, rotated by theta in-plane."""
    dz = n_wall / np.linalg.norm(n_wall)
    if ref_dx is None:
        a = np.array([1.0, 0.0, 0.0])
        if abs(a @ dz) > 0.9:
            a = np.array([0.0, 1.0, 0.0])
        ref_dx = a - (a @ dz) * dz
        ref_dx /= np.linalg.norm(ref_dx)
    e2 = np.cross(dz, ref_dx)
    dx = np.cos(theta) * ref_dx + np.sin(theta) * e2
    dy = np.cross(dz, dx)
    return np.stack([dx, dy, dz], 1), ref_dx


def seam_azimuth(segs, wts, n_wall, K, trunc_deg=4.0):
    """In-plane rotation that makes the plank-seam family horizontal.

    The plane normal is already fixed by the homography decomposition, so this is
    a 1-DOF problem: far better conditioned than a free 2-DOF vanishing point,
    which on this wall would have to be fitted to a handful of near-parallel
    chains.  Cost is a truncated (redescending) weighted angular error, so grain
    lines, hold edges and chains from other line families cannot pull it.
    """
    P = np.concatenate([segs[:, :2], segs[:, 2:]], 0).reshape(-1, 1, 2).astype(np.float64)
    Kin = np.linalg.inv(K)
    trunc = np.deg2rad(trunc_deg)

    def cost(theta):
        R, _ = frame_from_normal(n_wall, theta, cost.ref)
        Q = cv2.perspectiveTransform(P, K @ R.T @ Kin).reshape(2, -1, 2)
        d = Q[1] - Q[0]
        a = np.arctan2(d[:, 1], d[:, 0])
        a = np.abs((a + np.pi / 2) % np.pi - np.pi / 2)      # distance to horizontal
        return float((wts * np.minimum(a, trunc)).sum()), a

    _, cost.ref = frame_from_normal(n_wall, 0.0)
    grid = np.deg2rad(np.arange(-89.0, 90.0, 0.25))
    vals = np.array([cost(t)[0] for t in grid])
    t0 = grid[int(np.argmin(vals))]
    fine = np.linspace(t0 - np.deg2rad(0.3), t0 + np.deg2rad(0.3), 121)
    t = fine[int(np.argmin([cost(x)[0] for x in fine]))]
    _, a = cost(t)
    support = a < np.deg2rad(1.0)
    R, _ = frame_from_normal(n_wall, t, cost.ref)
    return R, float(np.degrees(t)), support, np.degrees(a)


def detail_check(res, imgs, Htot, masks, med, offset, work, patch=760):
    """Native-resolution side-by-side: source crop (left) vs. orthophoto crop (right).

    The patch is chosen automatically as the location where the reference image
    samples the wall most densely inside the delivered crop, so it shows the
    best-case fidelity of the mosaic against the raw sensor data.
    """
    ox, oy = offset
    Hr, Wr = res.shape[:2]
    # texture score at the density-grid resolution: prefer a patch with holds,
    # bolt holes and grain over a blank stretch of plywood
    g = cv2.cvtColor(res, cv2.COLOR_BGR2GRAY).astype(np.float32)
    gs = cv2.resize(g, (max(1, Wr // 16), max(1, Hr // 16)), interpolation=cv2.INTER_AREA)
    lap = np.abs(cv2.Laplacian(gs, cv2.CV_32F))
    tex = cv2.blur(lap, (patch // 32 | 1, patch // 32 | 1))
    tex = tex / (tex.max() + 1e-6)
    best = None
    for n in imgs:
        d = density_map(Htot[n], masks[n], (H0, W0),
                        (Wr + ox, Hr + oy), stride=16)
        d = d[oy // 16:, ox // 16:]
        d = d[:max(1, Hr // 16), :max(1, Wr // 16)]
        # keep away from the crop border so a full patch fits
        m = patch // (2 * 16) + 2
        if d.shape[0] <= 2 * m or d.shape[1] <= 2 * m:
            continue
        dd = d.copy()
        t = tex[:dd.shape[0], :dd.shape[1]]
        dd = dd[:t.shape[0], :t.shape[1]] * (0.15 + t)
        dd = dd.copy(); dd[:m] = 0; dd[-m:] = 0; dd[:, :m] = 0; dd[:, -m:] = 0
        j = np.unravel_index(np.argmax(dd), dd.shape)
        if best is None or dd[j] > best[0]:
            best = (float(dd[j]), n, int(j[1] * 16), int(j[0] * 16))
    if best is None:
        return
    _, n, cx, cy = best
    x0 = int(np.clip(cx - patch // 2, 0, Wr - patch)); y0 = int(np.clip(cy - patch // 2, 0, Hr - patch))
    ortho = res[y0:y0 + patch, x0:x0 + patch].copy()
    # same physical patch in the untouched, undistorted source frame
    corners = np.array([[x0, y0], [x0 + patch, y0], [x0 + patch, y0 + patch], [x0, y0 + patch]],
                       np.float64) + [ox, oy]
    sp = cv2.perspectiveTransform(corners.reshape(-1, 1, 2), np.linalg.inv(Htot[n])).reshape(-1, 2)
    sx0, sy0 = np.floor(sp.min(0)).astype(int)
    sx1, sy1 = np.ceil(sp.max(0)).astype(int)
    sx0, sy0 = max(sx0, 0), max(sy0, 0); sx1, sy1 = min(sx1, W0), min(sy1, H0)
    srcc = imgs[n][sy0:sy1, sx0:sx1].copy()          # NATIVE pixels, no resampling
    h = max(srcc.shape[0], ortho.shape[0])
    def pad(a):
        out = np.zeros((h, a.shape[1], 3), np.uint8)
        out[:a.shape[0]] = a
        return out
    bar = 40
    vis = np.hstack([pad(srcc), np.zeros((h, 12, 3), np.uint8), pad(ortho)])
    vis = np.vstack([np.zeros((bar, vis.shape[1], 3), np.uint8), vis])
    cv2.putText(vis, "SOURCE %s.jpeg, native pixels (%dx%d)" % (n, srcc.shape[1], srcc.shape[0]),
                (10, 27), cv2.FONT_HERSHEY_SIMPLEX, 0.62, (255, 255, 255), 1, cv2.LINE_AA)
    cv2.putText(vis, "ORTHOPHOTO, same patch, 100%% (%dx%d)" % (patch, patch),
                (srcc.shape[1] + 24, 27), cv2.FONT_HERSHEY_SIMPLEX, 0.62, (255, 255, 255), 1, cv2.LINE_AA)
    cv2.imwrite(os.path.join(work, "06-final", "detail-check.jpg"), vis,
                [cv2.IMWRITE_JPEG_QUALITY, 96])


def best_rect(valid, min_frac=0.97):
    """Largest axis-aligned rectangle whose pixels are at least `min_frac` valid.

    Requiring 100% valid would force a tiny rectangle, because the union of four
    keystoned footprints is a hexagon; allowing a few percent of unfilled corner
    lets the crop keep almost all of the wall.
    """
    h, w = valid.shape
    C = np.zeros((h + 1, w), np.int32)
    C[1:] = np.cumsum(valid.astype(np.int32), 0)
    best = (0, 0, 0, 0, 0)
    for y0 in range(h):
        for y1 in range(y0 + 1, h + 1):
            hgt = y1 - y0
            if hgt * w <= best[0]:
                continue
            ok = (C[y1] - C[y0]) >= min_frac * hgt
            if not ok.any():
                continue
            idx = np.flatnonzero(np.diff(np.concatenate(([0], ok.view(np.int8), [0]))))
            starts, ends = idx[0::2], idx[1::2]
            k = int(np.argmax(ends - starts))
            run = int(ends[k] - starts[k])
            area = hgt * run
            if area > best[0]:
                best = (area, y0, y1 - 1, int(starts[k]), int(ends[k]) - 1)
    return best


# --------------------------------------------------------------------------
# 5. verification: does the result actually read as flat?
# --------------------------------------------------------------------------
def trace_seams(img):
    """Sub-pixel trace of the horizontal plank / panel seams across the orthophoto."""
    g = cv2.cvtColor(img, cv2.COLOR_BGR2GRAY).astype(np.float32)
    hsv = cv2.cvtColor(img, cv2.COLOR_BGR2HSV)
    Hc, Wc = g.shape
    wood = (g > 0) & (hsv[:, :, 1] < 90) & (hsv[:, :, 2] > 70)      # bare plywood, holds excluded
    wood = cv2.erode(wood.astype(np.uint8), np.ones((9, 9), np.uint8)).astype(bool)
    b = cv2.GaussianBlur(g, (0, 0), 1.2)
    k = np.array([-1, -1, -1, -1, 0, 2, 4, 2, 0, -1, -1, -1, -1], np.float32)
    k -= k.mean(); k /= np.abs(k).sum()
    resp = -cv2.filter2D(b, cv2.CV_32F, k.reshape(-1, 1))           # bright on dark horizontal lines
    R = np.where(wood, resp, np.nan)
    prof = np.nan_to_num(np.nanmean(np.where(wood, resp, np.nan), axis=1))
    prof = cv2.GaussianBlur(prof.reshape(-1, 1), (0, 0), 3).ravel()
    thr = np.percentile(prof, 70)
    peaks = [y for y in range(20, Hc - 20)
             if prof[y] > thr and prof[y] == prof[max(0, y - 30):y + 31].max()]
    peaks = [q for i, q in enumerate(peaks) if i == 0 or q - peaks[i - 1] > 50]
    Rf = np.nan_to_num(R, nan=-1e6)

    def sample(y_at, halfwin):
        xs, ys = [], []
        for x in range(0, Wc - 60, 30):
            yc = int(round(y_at(x + 30)))
            lo, hi = max(0, yc - halfwin), min(Hc, yc + halfwin + 1)
            if hi - lo < 5:
                continue
            wb = wood[lo:hi, x:x + 60]
            if wb.mean() < 0.55:
                continue
            col = np.nanmean(np.where(wb, Rf[lo:hi, x:x + 60], np.nan), axis=1)
            if not np.isfinite(col).any():
                continue
            j = int(np.nanargmax(col))
            if col[j] < 0.4:
                continue
            d = 0.0
            if 0 < j < len(col) - 1:
                den = col[j - 1] - 2 * col[j] + col[j + 1]
                if den != 0:
                    d = float(np.clip(0.5 * (col[j - 1] - col[j + 1]) / den, -1, 1))
            xs.append(x + 30.0); ys.append(lo + j + d)
        return np.array(xs), np.array(ys)

    def robust_line(xs, ys):
        keep = np.ones(len(xs), bool)
        c = np.array([0.0, ys.mean()])
        for _ in range(6):
            A = np.stack([xs[keep], np.ones(keep.sum())], 1)
            c = np.linalg.lstsq(A, ys[keep], rcond=None)[0]
            r = ys - (c[0] * xs + c[1])
            sd = 1.4826 * np.median(np.abs(r[keep] - np.median(r[keep]))) + 1e-6
            nk = np.abs(r - np.median(r[keep])) < 3 * sd
            if nk.sum() < 15:
                break
            keep = nk
        return c, keep

    seams = []
    for y0 in peaks:
        xs, ys = sample(lambda x: y0, 22)
        if len(xs) < 20:
            continue
        c, keep = robust_line(xs, ys)
        if keep.sum() < 20 or not np.all(np.isfinite(c)):
            continue
        xs2, ys2 = sample(lambda x: c[0] * x + c[1], 7)   # second pass, tight window
        if len(xs2) < 20:
            continue
        c2, keep2 = robust_line(xs2, ys2)
        if keep2.sum() < 20 or not np.all(np.isfinite(c2)):
            continue
        r = ys2 - (c2[0] * xs2 + c2[1])
        X, Y = xs2[keep2], ys2[keep2]
        quad = np.polyfit(X, Y, 2)
        bow = abs(quad[0]) * ((X.max() - X.min()) / 2.0) ** 2      # sagitta of the quadratic term
        seams.append(dict(slope=float(c2[0]), icpt=float(c2[1]), n=int(keep2.sum()),
                          span=float(X.max() - X.min()), x0=float(X.min()), x1=float(X.max()),
                          rms=float(np.sqrt((r[keep2] ** 2).mean())),
                          maxdev=float(np.abs(r[keep2]).max()), bow=float(bow)))
    return seams


def measure_rectification(img, min_span_frac=0.55, min_pts=25):
    """Trace the plank seams in a finished orthophoto and score its flatness.

    Returns the metric dict plus the traced seams, so callers can both report
    the numbers and draw them.  Used for the main span, for the kickboard and
    (on a transposed image) for the left return panel's board joints.
    """
    Hc, Wc = img.shape[:2]
    seams = trace_seams(img)
    longs = [s for s in seams if s["span"] > min_span_frac * Wc and s["n"] >= min_pts]
    if not longs:
        return None, None
    a = np.degrees(np.arctan([s["slope"] for s in longs]))
    m = dict(n_seams=len(longs),
             angle_mean_deg=float(a.mean()), angle_rms_deg=float(np.sqrt((a ** 2).mean())),
             angle_max_abs_deg=float(np.abs(a).max()),
             parallelism_spread_deg=float(a.max() - a.min()),
             straightness_rms_px=float(np.mean([s["rms"] for s in longs])),
             straightness_max_px=float(np.max([s["maxdev"] for s in longs])),
             bow_sagitta_median_px=float(np.median([s["bow"] for s in longs])),
             bow_sagitta_max_px=float(np.max([s["bow"] for s in longs])))
    # keystone: spacing between neighbouring seams at 15% and 85% of the width
    ls = sorted(longs, key=lambda s: s["icpt"])
    xl, xr = 0.15 * Wc, 0.85 * Wc
    ratios = []
    for i in range(len(ls) - 1):
        A, B = ls[i], ls[i + 1]
        if min(A["x0"], B["x0"]) > xl or max(A["x1"], B["x1"]) < xr:
            continue
        dl = (B["slope"] * xl + B["icpt"]) - (A["slope"] * xl + A["icpt"])
        dr = (B["slope"] * xr + B["icpt"]) - (A["slope"] * xr + A["icpt"])
        if dl > 20 and dr > 20:
            ratios.append(dr / dl)
    if ratios:
        m["keystone_pitch_ratio_right_over_left"] = dict(
            n=len(ratios), median=float(np.median(ratios)),
            p10=float(np.percentile(ratios, 10)), p90=float(np.percentile(ratios, 90)))
    return m, longs


def log_rectification(tag, m):
    log("%s: %d seams | angle rms %.3f deg, max %.3f deg, spread %.3f deg | "
        "straightness rms %.2f px, bow median %.2f px | keystone pitch ratio %.4f"
        % (tag, m["n_seams"], m["angle_rms_deg"], m["angle_max_abs_deg"],
           m["parallelism_spread_deg"], m["straightness_rms_px"], m["bow_sagitta_median_px"],
           m.get("keystone_pitch_ratio_right_over_left", {}).get("median", float("nan"))))


def grid_overlay(img, longs, m, path, what="plank seams"):
    """Draw a true horizontal/vertical grid plus the fitted seam lines."""
    Hc, Wc = img.shape[:2]
    sc = min(1.0, 2600.0 / Wc)
    gc = cv2.resize(img, None, fx=sc, fy=sc, interpolation=cv2.INTER_AREA)
    gh, gw = gc.shape[:2]
    grid_px = int(round(Wc / 24.0 / 50.0) * 50)   # ~24 columns, round number of canvas px
    step = max(8, int(round(grid_px * sc)))
    ov = gc.copy()
    for i, x in enumerate(range(0, gw, step)):
        cv2.line(ov, (x, 0), (x, gh), (0, 0, 0), 3, cv2.LINE_AA)
        cv2.line(ov, (x, 0), (x, gh), (60, 200, 255), 1 + 1 * (i % 5 == 0), cv2.LINE_AA)
    for i, y in enumerate(range(0, gh, step)):
        cv2.line(ov, (0, y), (gw, y), (0, 0, 0), 3, cv2.LINE_AA)
        cv2.line(ov, (0, y), (gw, y), (60, 255, 255), 1 + 1 * (i % 5 == 0), cv2.LINE_AA)
    gc = cv2.addWeighted(gc, 0.35, ov, 0.65, 0)
    for s in longs:                                   # traced seams, drawn as fitted straight lines
        y1 = int(round((s["slope"] * s["x0"] + s["icpt"]) * sc))
        y2 = int(round((s["slope"] * s["x1"] + s["icpt"]) * sc))
        cv2.line(gc, (int(s["x0"] * sc), y1), (int(s["x1"] * sc), y2), (255, 0, 255), 1, cv2.LINE_AA)
    txt = ("rectification check - yellow: true horizontal/vertical grid (%d px)   "
           "magenta: straight lines fitted to the traced %s   "
           "seam angle rms %.3f deg, max %.3f deg"
           % (grid_px, what, m["angle_rms_deg"], m["angle_max_abs_deg"]))
    bar_h = 34
    gc = np.vstack([np.zeros((bar_h, gw, 3), np.uint8), gc])
    cv2.putText(gc, txt, (12, 23), cv2.FONT_HERSHEY_SIMPLEX, 0.55, (255, 255, 255), 1, cv2.LINE_AA)
    cv2.imwrite(path, gc, [cv2.IMWRITE_JPEG_QUALITY, 92])


def verify(img, report, work, pano_path, jpeg_quality):
    Hc, Wc = img.shape[:2]
    m, longs = measure_rectification(img)
    report["rectification_check"] = m
    log_rectification("rectification check", m)
    grid_overlay(img, longs, m, os.path.join(work, "06-final", "grid-check.jpg"))

    # ---- before / after comparison -----------------------------------------
    pano = cv2.imread(pano_path)
    if pano is None:
        log("comparison skipped: cannot read %s" % pano_path)
        report["comparison"] = "skipped: reference panorama not readable at " + pano_path
        return
    Wv = 2400
    p = cv2.resize(pano, (Wv, int(round(pano.shape[0] * Wv / pano.shape[1]))), interpolation=cv2.INTER_AREA)
    o = cv2.resize(img, (Wv, int(round(Hc * Wv / Wc))), interpolation=cv2.INTER_AREA)
    bar = 54
    def label(im_, t1, t2, col):
        pad = np.zeros((bar, Wv, 3), np.uint8); pad[:] = col
        cv2.putText(pad, t1, (16, 36), cv2.FONT_HERSHEY_SIMPLEX, 1.0, (255, 255, 255), 2, cv2.LINE_AA)
        cv2.putText(pad, t2, (16 + 640, 36), cv2.FONT_HERSHEY_SIMPLEX, 0.62, (235, 235, 235), 1, cv2.LINE_AA)
        return np.vstack([pad, im_])
    top = label(p, "BEFORE", "iPhone sweep panorama - cylindrical projection, wall bowed", (32, 32, 130))
    bot = label(o, "AFTER", "plane-homography orthophoto - fronto-parallel, no bow", (32, 110, 32))
    cv2.imwrite(os.path.join(work, "06-final", "comparison.jpg"),
                np.vstack([top, np.zeros((10, Wv, 3), np.uint8), bot]),
                [cv2.IMWRITE_JPEG_QUALITY, 90])


def density_map(Hcanvas, mask_src, shape_src, canvas_wh, stride=8, e=8.0):
    """sqrt(|det J|) of canvas->source, i.e. source pixels sampled per canvas pixel."""
    Wc, Hc = canvas_wh
    gx, gy = np.meshgrid(np.arange(0, Wc, stride, dtype=np.float64),
                         np.arange(0, Hc, stride, dtype=np.float64))
    gp = np.stack([gx.ravel(), gy.ravel()], 1)
    Hi = np.linalg.inv(Hcanvas)
    f = lambda P: cv2.perspectiveTransform(P.reshape(-1, 1, 2), Hi).reshape(-1, 2)
    p0, px, py = f(gp), f(gp + [e, 0]), f(gp + [0, e])
    det = np.abs(((px[:, 0] - p0[:, 0]) * (py[:, 1] - p0[:, 1]) -
                  (py[:, 0] - p0[:, 0]) * (px[:, 1] - p0[:, 1]))) / (e * e)
    hs, ws = shape_src
    ok = (p0[:, 0] >= 0) & (p0[:, 0] < ws - 1) & (p0[:, 1] >= 0) & (p0[:, 1] < hs - 1)
    d = np.zeros(len(gp))
    ii = np.flatnonzero(ok)
    d[ii] = np.sqrt(det[ii])
    d[ii] *= (mask_src[p0[ii, 1].astype(int), p0[ii, 0].astype(int)] > 0)
    return d.reshape(gx.shape)


# --------------------------------------------------------------------------
# main
# --------------------------------------------------------------------------
def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--src", default="/Users/patrickweindl/Desktop/wall-photos")
    ap.add_argument("--work", default="/Users/patrickweindl/Desktop/wall-photos/work")
    ap.add_argument("--cache", default=None)
    ap.add_argument("--jpeg-quality", type=int, default=95)
    ap.add_argument("--density-percentile", type=float, default=90.0,
                    help="output scale = this percentile of the best-source sampling density "
                         "over the main span (90 = only the best-sampled 10%% of the wall is "
                         "resampled below its native rate; lower values trade detail for size)")
    ap.add_argument("--wall-angle", default=None, metavar="DEG",
                    help="tilt past vertical of the main span, used for the extra "
                         "'keep the wall angle' projection (angled view = ortho with "
                         "the vertical axis scaled by cos(angle)).  A number in degrees, "
                         "'measured' to use the pipeline's own facet angle, or omitted "
                         "for the stored wall Angle from work/holds/wall.json (else 45)")
    ap.add_argument("--focal-scale", type=float, default=1.0,
                    help="scale the EXIF focal length; used to probe how well the "
                         "rectification is constrained (1.0 = nominal)")
    args = ap.parse_args()
    cv2.setRNGSeed(SEED)
    np.random.seed(SEED)
    src, work = args.src, args.work
    cache = args.cache or os.path.join(work, ".cache")
    for d in ["01-undistorted", "02-matches", "03-masks", "04-registered", "05-seams", "06-final"]:
        os.makedirs(os.path.join(work, d), exist_ok=True)
    os.makedirs(cache, exist_ok=True)
    report = {}

    K = intrinsics(W0, H0, F35_EQ * args.focal_scale)
    report["focal_px"] = float(K[0, 0])

    # ---- stage 1: distortion -------------------------------------------------
    cpath = os.path.join(cache, "dist.json")
    if os.path.exists(cpath):
        dist = json.load(open(cpath))
    else:
        log("plumb-line distortion calibration")
        k, st = calibrate_distortion([f"{src}/{n}.jpeg" for n in IMAGES + ["5"]], K)
        dist = dict(k1=float(k[0]), k2=float(k[1]), **st)
        json.dump(dist, open(cpath, "w"), indent=1)
    report["distortion"] = dist
    D = np.array([dist["k1"], dist["k2"], 0, 0, 0])
    log("k1=%.5f k2=%.5f  line straightness rms %.2f -> %.2f px"
        % (dist["k1"], dist["k2"], dist["straightness_rms_px_before"], dist["straightness_rms_px_after"]))

    mapx, mapy = cv2.initUndistortRectifyMap(K, D, None, K, (W0, H0), cv2.CV_32FC1)
    imgs, masks, masks_kick = {}, {}, {}

    def warp_mask(poly):
        m0 = np.zeros((H0, W0), np.uint8)
        cv2.fillPoly(m0, [np.array(poly, np.int32)], 255)
        return (cv2.remap(m0, mapx, mapy, cv2.INTER_NEAREST) > 127).astype(np.uint8) * 255

    for n in IMAGES:
        up = os.path.join(cache, f"u{n}.png")
        if os.path.exists(up):
            u = cv2.imread(up)
        else:
            u = cv2.remap(cv2.imread(f"{src}/{n}.jpeg"), mapx, mapy, cv2.INTER_LANCZOS4)
            cv2.imwrite(up, u)
        imgs[n] = u
        masks[n] = warp_mask(WALL_POLY[n])
        masks_kick[n] = warp_mask(KICK_POLY[n])
        cv2.imwrite(os.path.join(work, "01-undistorted", f"{n}.jpg"),
                    cv2.resize(u, (1500, 2000)), [cv2.IMWRITE_JPEG_QUALITY, 88])
        ov = u.copy()
        dim = (masks[n] == 0) & (masks_kick[n] == 0)
        ov[dim] = (ov[dim] * 0.25).astype(np.uint8)
        ov[masks_kick[n] > 0, 2] = 255                          # kickboard: its own plane
        cv2.imwrite(os.path.join(work, "03-masks", f"{n}.jpg"),
                    cv2.resize(ov, (1500, 2000)), [cv2.IMWRITE_JPEG_QUALITY, 82])
    log("undistorted + masked")

    # ---- stage 2: features & pairwise homographies ---------------------------
    Hpair, corr = {}, {}
    for a, b in PAIRS:
        cp = os.path.join(cache, f"m{a}{b}.npz")
        if os.path.exists(cp):
            z = np.load(cp); r = dict(H=z["H"], pa=z["pa"], pb=z["pb"], n_coarse=int(z["nc"]),
                                      n_stageb=int(z["ns"]), n_inl=int(z["ni"]), rms=float(z["rms"]),
                                      overlap_mpx=float(z["ov"]))
        else:
            r = match_pair(imgs[a], masks[a], imgs[b], masks[b])
            if r is None:
                log(f"  pair {a}-{b}: FAILED"); continue
            np.savez(cp, H=r["H"], pa=r["pa"], pb=r["pb"], nc=r["n_coarse"], ns=r["n_stageb"],
                     ni=r["n_inl"], rms=r["rms"], ov=r["overlap_mpx"])
        Hpair[(a, b)] = r["H"]
        corr[(a, b)] = (r["pa"], r["pb"])
        log(f"  pair {a}-{b}: coarse {r['n_coarse']} inl -> guided {r['n_inl']}/{r['n_stageb']} inl, "
            f"reproj rms {r['rms']:.2f} px, overlap {r['overlap_mpx']:.2f} Mpx")
        report.setdefault("pairs", {})[f"{a}-{b}"] = dict(
            coarse_inliers=r["n_coarse"], guided_matches=r["n_stageb"], inliers=r["n_inl"],
            reproj_rms_px=r["rms"], overlap_mpx=r["overlap_mpx"])
        sc = 0.25
        va = cv2.resize(imgs[a], None, fx=sc, fy=sc); vb = cv2.resize(imgs[b], None, fx=sc, fy=sc)
        vis = np.hstack([va, vb])
        rng = np.random.default_rng(SEED)
        sel = rng.choice(len(r["pa"]), size=min(200, len(r["pa"])), replace=False)
        for i in sel:
            pp = tuple((r["pa"][i] * sc).astype(int))
            qq = tuple((r["pb"][i] * sc + [va.shape[1], 0]).astype(int))
            cv2.line(vis, pp, qq, (0, 255, 0), 1)
            cv2.circle(vis, pp, 2, (0, 0, 255), -1); cv2.circle(vis, qq, 2, (0, 0, 255), -1)
        f = 2000.0 / vis.shape[1]
        cv2.imwrite(os.path.join(work, "02-matches", f"{a}-{b}.jpg"),
                    cv2.resize(vis, None, fx=f, fy=f), [cv2.IMWRITE_JPEG_QUALITY, 80])

    # chain to reference
    Hs = {REF: np.eye(3)}
    Hs["1"] = Hpair[("1", "2")]
    Hs["3"] = np.linalg.inv(Hpair[("2", "3")])
    Hs["4"] = Hs["3"] @ np.linalg.inv(Hpair[("3", "4")])
    Hs, rst = refine(Hs, corr, REF, IMAGES)
    log("global refinement: symmetric transfer rms %.2f -> %.2f px" % (rst["rms_before"], rst["rms_after"]))
    report["registration"] = rst

    # ---- planarity diagnostic ------------------------------------------------
    resid_map = {}
    for (a, b), (pa, pb) in corr.items():
        qa = cv2.perspectiveTransform(pa.reshape(-1, 1, 2), Hs[a]).reshape(-1, 2)
        qb = cv2.perspectiveTransform(pb.reshape(-1, 1, 2), Hs[b]).reshape(-1, 2)
        resid_map[f"{a}-{b}"] = dict(
            n=len(pa), rms=float(np.sqrt((np.linalg.norm(qa - qb, axis=1) ** 2).mean())),
            p95=float(np.percentile(np.linalg.norm(qa - qb, axis=1), 95)))
    report["planarity_residuals_ref_px"] = resid_map

    # ---- stage 3: metric rectification --------------------------------------
    # normal of the wall plane in the REF camera frame, from ref<->neighbour homographies
    cand_sets = []
    for other in ["1", "3", "4"]:
        H_ref_to_other = np.linalg.inv(Hs[other])   # Hs[x] maps image x -> ref frame
        n_sol, Rs, Ts, Ns = cv2.decomposeHomographyMat(H_ref_to_other, K)
        cands = []
        for i in range(n_sol):
            v = np.array(Ns[i]).ravel(); v = v / np.linalg.norm(v)
            if v[2] < 0:
                v = -v
            cands.append(v)
        cand_sets.append((other, cands))
    # pick the normal consistent across all neighbours
    base = cand_sets[0][1]
    best, bestcost = None, 1e9
    for c in base:
        cost, picks = 0.0, [c]
        for _, cs in cand_sets[1:]:
            d = min(np.degrees(np.arccos(np.clip(c @ x, -1, 1))) for x in cs)
            pick = min(cs, key=lambda x: np.degrees(np.arccos(np.clip(c @ x, -1, 1))))
            picks.append(pick); cost += d
        if cost < bestcost:
            bestcost, best, bestpicks = cost, c, picks
    n_wall = np.mean(bestpicks, 0); n_wall /= np.linalg.norm(n_wall)
    spread = [float(np.degrees(np.arccos(np.clip(n_wall @ p, -1, 1)))) for p in bestpicks]
    report["plane_normal_ref_cam"] = [float(x) for x in n_wall]
    report["plane_normal_spread_deg"] = spread
    log("plane normal %s ; per-pair spread %s deg"
        % (np.round(n_wall, 4).tolist(), np.round(spread, 2).tolist()))

    # ---- in-plane orientation from the plank seams --------------------------
    # Long straight edges on the wall, chained across the holds that occlude them,
    # collected from every image and mapped into the REF frame.
    segs_ref, wts_ref = [], []
    for n in IMAGES:
        g = cv2.cvtColor(imgs[n], cv2.COLOR_BGR2GRAY)
        for pts in line_chains(g, min_seg=35, max_gap=350, ang_tol_deg=6.0, perp_tol=10.0,
                               min_span=320, max_parab_rms=2.5, min_pts=6):
            ii = np.clip(pts[:, 1].astype(int), 0, H0 - 1)
            jj = np.clip(pts[:, 0].astype(int), 0, W0 - 1)
            if masks[n][ii, jj].mean() < 250:          # must lie entirely on the wall
                continue
            c = pts.mean(0)
            _, _, V = np.linalg.svd(pts - c, full_matrices=False)
            t = (pts - c) @ V[0]
            e1, e2 = c + V[0] * t.min(), c + V[0] * t.max()
            q = cv2.perspectiveTransform(np.array([[e1], [e2]]), Hs[n]).reshape(-1, 2)
            segs_ref.append([q[0, 0], q[0, 1], q[1, 0], q[1, 1]])
            wts_ref.append(float(np.linalg.norm(e2 - e1)))
    segs_ref = np.array(segs_ref); wts_ref = np.array(wts_ref)
    R, theta_deg, support, ang_res = seam_azimuth(segs_ref, wts_ref, n_wall, K)
    log("seam chains on the wall: %d | in-plane rotation %.3f deg | %d chains within 1 deg "
        "of horizontal after rectification (weighted length %.0f%% of total)"
        % (len(segs_ref), theta_deg, int(support.sum()),
           100 * wts_ref[support].sum() / wts_ref.sum()))
    report["seam_chains"] = dict(
        n=int(len(segs_ref)), in_plane_rotation_deg=theta_deg,
        n_within_1deg=int(support.sum()),
        weighted_support_frac=float(wts_ref[support].sum() / wts_ref.sum()),
        residual_deg_p50=float(np.median(ang_res[support])) if support.any() else None,
        residual_deg_p90=float(np.percentile(ang_res[support], 90)) if support.any() else None)

    # ---- canvas extent ------------------------------------------------------
    Hrect0 = K @ R.T @ np.linalg.inv(K)
    Htot = {n: Hrect0 @ Hs[n] for n in IMAGES}
    corners = [cv2.perspectiveTransform(np.array(WALL_POLY[n], np.float64).reshape(-1, 1, 2),
                                        Htot[n]).reshape(-1, 2) for n in IMAGES]
    allc = np.concatenate(corners)
    lo, hi = allc.min(0), allc.max(0)

    # ---- resolution policy: never upsample past the true source sampling rate ----
    prov_w = int(np.ceil(hi[0] - lo[0])); prov_h = int(np.ceil(hi[1] - lo[1]))
    T0 = np.array([[1, 0, -lo[0]], [0, 1, -lo[1]], [0, 0, 1.0]])
    dmaps = [density_map(T0 @ Htot[n], masks[n], (H0, W0), (prov_w, prov_h), stride=8)
             for n in IMAGES]
    stack = np.stack(dmaps)
    best_dens = stack.max(0)
    cov = best_dens > 0
    med = float(np.percentile(best_dens[cov], args.density_percentile))
    q = np.percentile(best_dens[cov], [5, 25, 50, 75, 95])
    ratio = best_dens[cov] / med
    log("best-source sampling density (source px per canvas px at unit scale): "
        "p5 %.2f p25 %.2f median %.2f p75 %.2f p95 %.2f  -> scale %.3f (p%.0f)"
        % (*q, med, args.density_percentile))
    report["resolution"] = dict(
        density_percentiles=dict(zip(["p5", "p25", "p50", "p75", "p95"], [float(x) for x in q])),
        percentile=args.density_percentile,
        chosen_scale_source_px_per_canvas_px=med,
        frac_of_span_downsampled=float((ratio > 1.0).mean()),
        worst_downsampling_factor=float(ratio.max()))

    S = np.array([[med, 0, -med * lo[0]], [0, med, -med * lo[1]], [0, 0, 1.0]])
    Htot = {n: S @ Htot[n] for n in IMAGES}
    Wc = int(np.ceil((hi[0] - lo[0]) * med)); Hc = int(np.ceil((hi[1] - lo[1]) * med))
    log("canvas %dx%d" % (Wc, Hc))

    res, res_mask = composite(imgs, masks, IMAGES, Htot, Wc, Hc, work, report, "")
    Hc0, Wc0 = res.shape[:2]

    full = res.copy()
    ys, xs = np.nonzero(res_mask)
    full = full[ys.min():ys.max() + 1, xs.min():xs.max() + 1]

    res, crop_box = crop_usable(res, res_mask, masks, Htot, IMAGES, work, report)
    x0, y0 = crop_box[0], crop_box[1]
    Hc, Wc = res.shape[:2]

    png = os.path.join(work, "06-final", "wall-orthophoto.png")
    jpg = os.path.join(work, "06-final", "wall-orthophoto.jpg")
    fullpng = os.path.join(work, "06-final", "wall-orthophoto-full.png")
    cv2.imwrite(png, res, [cv2.IMWRITE_PNG_COMPRESSION, 9])
    cv2.imwrite(jpg, res, [cv2.IMWRITE_JPEG_QUALITY, args.jpeg_quality])
    cv2.imwrite(fullpng, full, [cv2.IMWRITE_PNG_COMPRESSION, 9])
    report["output"] = dict(width=Wc, height=Hc,
                            png_bytes=os.path.getsize(png), jpg_bytes=os.path.getsize(jpg),
                            uncropped_width=int(full.shape[1]), uncropped_height=int(full.shape[0]),
                            uncropped_png_bytes=os.path.getsize(fullpng))

    # ---- detail check: native source crop vs. the same patch in the orthophoto
    detail_check(res, imgs, Htot, masks, med, (x0, y0), work)

    verify(res, report, work, os.path.join(src, "panoramic.jpeg"), args.jpeg_quality)

    np.savez(os.path.join(cache, "geom.npz"), **{f"H{n}": Htot[n] for n in IMAGES},
             R=R, K=K, D=D, S=S, med=med)
    json.dump(report, open(os.path.join(work, "06-final", "report.json"), "w"), indent=1)

    # ---- the two off-plane surfaces the main span cannot carry --------------
    ctx = dict(imgs=imgs, K=K, cache=cache, work=work, args=args, report=report,
               Hs=Hs, R=R, n_wall=n_wall, masks_kick=masks_kick, main_scale=med)
    stitch_planes.kickboard(ctx)
    stitch_planes.left_return(ctx)

    # ---- the same wall, kept at its angle instead of flattened --------------
    angled_view.emit(work, args.wall_angle, report, args.jpeg_quality, log)

    json.dump(report, open(os.path.join(work, "06-final", "report.json"), "w"), indent=1)
    log("report ->", os.path.join(work, "06-final", "report.json"))


def composite(imgs, masks, names, Htot, Wc, Hc, work, report, tag):
    """Exposure-compensate, graph-cut a seam, then composite at full resolution.

    `tag` prefixes the diagnostic file names and the report keys so the same
    machinery can be run for the main span and for the kickboard plane.
    """
    # ---- low-resolution planning: exposure gains + graph-cut seams ---------
    sc = min(1.0, 1800.0 / max(Wc, Hc))
    lw, lh = int(round(Wc * sc)), int(round(Hc * sc))
    Sl = np.diag([sc, sc, 1.0])
    lowim, lowmask = [], []
    for n in names:
        T = Sl @ Htot[n]
        lowim.append(cv2.warpPerspective(imgs[n], T, (lw, lh), flags=cv2.INTER_AREA
                                         if False else cv2.INTER_LINEAR))
        m = cv2.warpPerspective(masks[n], T, (lw, lh), flags=cv2.INTER_NEAREST)
        lowmask.append(cv2.erode(m, np.ones((3, 3), np.uint8)))
    zero = [np.array((0, 0), int) for _ in names]
    comp = cv2.detail_ChannelsCompensator()
    comp.feed(zero, lowim, lowmask)
    gains = [np.array(g).reshape(-1)[:3].astype(np.float32) for g in comp.getMatGains()]
    log("exposure gains (B,G,R): " + " | ".join(
        "%s %.3f/%.3f/%.3f" % (n, *gains[i]) for i, n in enumerate(names)))
    report[tag + "exposure_gains"] = {n: [float(x) for x in gains[i]] for i, n in enumerate(names)}

    # prefer, per canvas pixel, the source that samples the wall most densely;
    # the graph cut then routes the seam freely inside a 15% tolerance band.
    dm = [density_map(Htot[n], masks[n], (H0, W0), (Wc, Hc), stride=8) for n in names]
    dbest = np.stack(dm).max(0)
    prefm = []
    for i, n in enumerate(names):
        pref = ((dm[i] >= 0.85 * dbest) & (dm[i] > 0)).astype(np.uint8) * 255
        pref = cv2.resize(pref, (lw, lh), interpolation=cv2.INTER_NEAREST)
        cand = cv2.bitwise_and(lowmask[i], pref)
        prefm.append(cand if cand.sum() > 0.02 * max(lowmask[i].sum(), 1) else lowmask[i])
    finder = cv2.detail_GraphCutSeamFinder("COST_COLOR_GRAD")
    seam = finder.find([im.astype(np.float32) * gains[i] for i, im in enumerate(lowim)],
                       zero, [cv2.UMat(m.copy()) for m in prefm])
    seam = [(m.get() if isinstance(m, cv2.UMat) else np.asarray(m)) for m in seam]

    vis = np.zeros((lh, lw, 3), np.uint8)
    cols = [(0, 0, 255), (0, 255, 0), (255, 0, 0), (0, 255, 255)]
    for i, sm in enumerate(seam):
        vis[sm > 0] = (0.5 * np.array(cols[i]) + 0.5 * lowim[i][sm > 0]).astype(np.uint8)
    cv2.imwrite(os.path.join(work, "05-seams", tag + "seams.jpg"), vis,
                [cv2.IMWRITE_JPEG_QUALITY, 88])
    for i, n in enumerate(names):
        v = lowim[i].copy(); v[lowmask[i] == 0] //= 4
        cv2.imwrite(os.path.join(work, "04-registered", f"{tag}{n}.jpg"),
                    cv2.resize(v, None, fx=min(1, 2000 / lw), fy=min(1, 2000 / lw)),
                    [cv2.IMWRITE_JPEG_QUALITY, 82])

    # Feather weights derived from the seam labels: a ~24 px transition at full
    # resolution.  Outside that band every output pixel is exactly one warped
    # source pixel, so no high frequency is lost to blending.
    FEATHER_FULL = 24.0
    f_low = max(2.0, FEATHER_FULL * sc)
    wlow = []
    for sm in seam:
        d = cv2.distanceTransform((sm > 0).astype(np.uint8), cv2.DIST_L2, 5)
        wlow.append((np.minimum(d, f_low) + 1e-3 * (sm > 0)).astype(np.float32))

    # ---- full-resolution strip composite ------------------------------------
    del dm, dbest, prefm
    res = np.zeros((Hc, Wc, 3), np.uint8)
    res_mask = np.zeros((Hc, Wc), np.uint8)
    STRIP = 512
    for r0 in range(0, Hc, STRIP):
        r1 = min(r0 + STRIP, Hc)
        acc = np.zeros((r1 - r0, Wc, 3), np.float32)
        wsum = np.zeros((r1 - r0, Wc), np.float32)
        for i, n in enumerate(names):
            T = np.array([[1, 0, 0], [0, 1, -r0], [0, 0, 1.0]]) @ Htot[n]
            mk = cv2.warpPerspective(masks[n], T, (Wc, r1 - r0), flags=cv2.INTER_NEAREST)
            if mk.max() == 0:
                continue
            im_ = cv2.warpPerspective(imgs[n], T, (Wc, r1 - r0), flags=cv2.INTER_LANCZOS4)
            M = np.array([[1.0 / sc, 0, 0], [0, 1.0 / sc, -r0]], np.float32)
            w = cv2.warpAffine(wlow[i], M, (Wc, r1 - r0), flags=cv2.INTER_LINEAR)
            w[mk == 0] = 0
            acc += w[:, :, None] * (im_.astype(np.float32) * gains[i])
            wsum += w
        ok = wsum > 0
        out = np.zeros_like(acc)
        out[ok] = acc[ok] / wsum[ok][:, None]
        res[r0:r1] = np.clip(out, 0, 255).astype(np.uint8)
        res_mask[r0:r1] = ok.astype(np.uint8) * 255
    log("composited %dx%d" % (Wc, Hc))
    return res, res_mask


def crop_usable(res, res_mask, masks, Htot, names, work, report, tag="",
                min_ratio=0.12, min_fill=0.97):
    """Largest axis-aligned rectangle of covered, adequately-sampled canvas.

    A canvas pixel is usable when it is covered AND its best source samples the
    wall at no worse than `min_ratio` of the best sampling anywhere on the
    surface; below that the material is grazing-angle smear, not detail.  The
    threshold is deliberately relative to the best sampling on the surface and
    not to the chosen output scale, so that legitimately lower-resolution parts
    (the bottom of the overhang, seen at a grazing angle from every camera) are
    kept rather than cropped away.
    """
    Hc0, Wc0 = res.shape[:2]
    ST = 32
    dg = np.stack([density_map(Htot[n], masks[n], (H0, W0), (Wc0, Hc0), stride=ST)
                   for n in names]).max(0)
    dg = dg / max(np.percentile(dg[dg > 0], 95), 1e-6)
    cg = res_mask[::ST, ::ST][:dg.shape[0], :dg.shape[1]] > 0
    usable = cg & (dg[:cg.shape[0], :cg.shape[1]] >= min_ratio)
    usable = cv2.morphologyEx(usable.astype(np.uint8), cv2.MORPH_OPEN, np.ones((3, 3), np.uint8)) > 0
    cv2.imwrite(os.path.join(work, "05-seams", tag + "usable.png"), usable.astype(np.uint8) * 255)
    area, gy0, gy1, gx0, gx1 = best_rect(usable, min_fill)
    y0, y1, x0, x1 = gy0 * ST, min((gy1 + 1) * ST, Hc0), gx0 * ST, min((gx1 + 1) * ST, Wc0)
    out = res[y0:y1, x0:x1]
    Hc, Wc = out.shape[:2]
    log("crop: %dx%d from canvas %dx%d (%.0f%% of the usable area retained)"
        % (Wc, Hc, Wc0, Hc0, 100.0 * area / max(usable.sum(), 1)))
    filled = float((res_mask[y0:y1, x0:x1] > 0).mean())
    report[tag + "crop"] = dict(x0=int(x0), y0=int(y0), width=int(Wc), height=int(Hc),
                                canvas_width=int(Wc0), canvas_height=int(Hc0),
                                min_sampling_ratio_kept=min_ratio, min_fill=min_fill,
                                actual_filled_fraction=filled)
    log("crop is %.2f%% filled" % (100 * filled))

    # ---- coverage / effective-resolution profile of the delivered crop ------
    sub = dg[gy0:gy1 + 1, gx0:gx1 + 1]
    sm = sub > 0
    rows = sub.shape[0]
    nb = max(1, min(8, rows))
    prof = []
    for i in range(nb):
        band = sub[i * rows // nb:(i + 1) * rows // nb]
        b = band[band > 0]
        prof.append(float(np.median(b)) if b.size else None)
    who = np.stack([density_map(Htot[n], masks[n], (H0, W0), (Wc0, Hc0), stride=ST)
                    for n in names]).argmax(0)[gy0:gy1 + 1, gx0:gx1 + 1]
    report[tag + "coverage"] = dict(
        rel_to_best_percentiles=dict(zip(["p5", "p25", "p50", "p75", "p95"],
                                         [float(v) for v in np.percentile(sub[sm], [5, 25, 50, 75, 95])])),
        top_to_bottom_median_profile=prof,
        frac_below_30pct_of_best=float((sub[sm] < 0.30).mean()),
        best_source_share={n: float(((who == i) & sm).mean()) for i, n in enumerate(names)})
    ok = [v for v in prof if v is not None]
    log("coverage: effective resolution falls %.2f (top) -> %.2f (bottom) relative to the "
        "best-sampled region; %.0f%% of the crop is below 30%% of best"
        % (ok[0], ok[-1], 100 * report[tag + "coverage"]["frac_below_30pct_of_best"]))
    return out, (x0, y0, x1, y1)


if __name__ == "__main__":
    main()
