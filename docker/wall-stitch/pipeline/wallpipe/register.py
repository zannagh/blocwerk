"""Homography graph over the frame set.

Vendored from new-run/exp3/hgraph.py. The algorithm is unchanged; the module-level
SRC/OUT/CACHE path constants are gone -- every path is now an argument.

The wall is a plane, so a homography between any two frames that both see it is
exact -- no rotation-only or affine approximation needed, and no bowl. This module
does features -> pairwise H -> spanning tree -> global refinement, and caches
everything so the expensive matching runs once.
"""
import heapq
import itertools
import os
import pickle

import cv2
import numpy as np


def features(paths, cache_dir, work_mpx=1.5, nfeat=12000, log=print):
    """SIFT features per frame at a common working resolution.

    Returns (dict path -> (points, descriptors), scale, work_w, work_h).
    """
    key = os.path.join(cache_dir, 'feat-%d-%.2f-%d.pkl' % (len(paths), work_mpx, nfeat))
    if os.path.exists(key):
        return pickle.load(open(key, 'rb'))
    sift = cv2.SIFT_create(nfeatures=nfeat)
    out = {}
    scale, small = None, None
    for p in paths:
        img = cv2.imread(p, cv2.IMREAD_GRAYSCALE)
        if img is None:
            raise SystemExit('could not read image: %s' % p)
        scale = min(1.0, np.sqrt(work_mpx * 1e6 / img.size))
        small = cv2.resize(img, None, fx=scale, fy=scale, interpolation=cv2.INTER_AREA)
        kp, des = sift.detectAndCompute(small, None)
        out[p] = (np.array([k.pt for k in kp], np.float32), des)
        log('  feat %s: %d' % (os.path.basename(p), len(kp)))
    res = (out, scale, small.shape[1], small.shape[0])
    os.makedirs(cache_dir, exist_ok=True)
    pickle.dump(res, open(key, 'wb'))
    return res


def pair_homographies(paths, feats, cache_dir, ratio=0.75, min_inl=40, reproj=3.0,
                      log=print):
    key = os.path.join(cache_dir, 'pairs-%d-%.2f-%d.pkl' % (len(paths), ratio, min_inl))
    if os.path.exists(key):
        return pickle.load(open(key, 'rb'))
    flann = cv2.FlannBasedMatcher(dict(algorithm=1, trees=5), dict(checks=64))
    pairs = {}
    for a, b in itertools.combinations(paths, 2):
        pa, da = feats[a]
        pb, db = feats[b]
        if da is None or db is None:
            continue
        m = flann.knnMatch(da, db, k=2)
        good = [(x.queryIdx, x.trainIdx) for x, y in m if x.distance < ratio * y.distance]
        if len(good) < min_inl:
            continue
        src = pa[[g[0] for g in good]]
        dst = pb[[g[1] for g in good]]
        h, mask = cv2.findHomography(src, dst, cv2.USAC_MAGSAC, reproj,
                                     maxIters=20000, confidence=0.9999)
        if h is None:
            continue
        inl = mask.ravel().astype(bool)
        if inl.sum() < min_inl:
            continue
        pairs[(a, b)] = dict(H=h, src=src[inl], dst=dst[inl], n=int(inl.sum()))
        log('  pair %s-%s: %d/%d inliers'
            % (os.path.basename(a), os.path.basename(b), inl.sum(), len(good)))
    os.makedirs(cache_dir, exist_ok=True)
    pickle.dump(pairs, open(key, 'wb'))
    return pairs


def spanning_tree(paths, pairs, ref):
    """Max-inlier spanning tree; returns H_i mapping frame i -> ref plane."""
    adj = {n: [] for n in paths}
    for (a, b), d in pairs.items():
        adj[a].append((b, d['n'], np.linalg.inv(d['H'])))
        adj[b].append((a, d['n'], d['H']))
    hs = {ref: np.eye(3)}
    frontier = [(-n, ref, nb, step) for nb, n, step in adj[ref]]
    heapq.heapify(frontier)
    while frontier:
        _, src, nb, step = heapq.heappop(frontier)
        if nb in hs:
            continue
        m = hs[src] @ step
        hs[nb] = m / m[2, 2]
        for nb2, n2, step2 in adj[nb]:
            if nb2 not in hs:
                heapq.heappush(frontier, (-n2, nb, nb2, step2))
    return hs


def reference_stretch(paths, pairs, ref, work_w, work_h):
    """p90 of |log(warped area / source area)|, exponentiated: how hard the sweep has
    to stretch to reach `ref`'s plane. 1.0 is a perfectly fronto-parallel sweep."""
    hs = spanning_tree(paths, pairs, ref)
    if len(hs) < len(paths):
        return float('inf')
    corners = np.array([[0, 0], [work_w, 0], [work_w, work_h], [0, work_h]],
                       np.float32).reshape(-1, 1, 2)
    source_area = float(work_w * work_h)
    ratios = []
    for h in hs.values():
        q = cv2.perspectiveTransform(corners, h).reshape(-1, 2)
        area = abs(float(cv2.contourArea(q.astype(np.float32))))
        ratios.append(abs(np.log(max(area, 1.0) / source_area)))
    return float(np.exp(np.percentile(ratios, 90)))


def pick_reference(paths, pairs, work_w, work_h, log=print):
    """The frame whose own plane the whole sweep projects onto most gently.

    The reference frame IS the mosaic plane, so choosing it badly is not a cosmetic
    matter: a frame angled steeply across the wall sends its neighbours off towards
    their vanishing line, the canvas explodes to hundreds of megapixels, the compose
    scale collapses to fit the cap, and the result is a radial smear rather than a wall.
    The middle frame of the sweep is arbitrary and lands there routinely.

    Score each candidate by how much the sweep has to stretch to reach its plane: the
    90th percentile of |log(warped area / source area)| over the frames, which punishes
    the extreme frames a mean would hide. Every candidate reuses the pairwise
    homographies, so this costs one spanning tree per frame and nothing else.
    """
    best, best_score = None, np.inf
    for candidate in paths:
        score = reference_stretch(paths, pairs, candidate, work_w, work_h)
        if score < best_score:
            best, best_score = candidate, score
    if best is None:
        return paths[len(paths) // 2], float('inf')
    log('reference %s: p90 area stretch %.2fx' % (os.path.basename(best), best_score))
    return best, best_score


def refine(paths, pairs, h0, ref, work_w, work_h, iters=400, f_scale=1.5, log=print):
    """Global refinement of all 8-dof homographies with the reference pinned.

    Two things matter for this to converge: the residual is the symmetric transfer
    error measured in each frame's OWN pixels (measuring it in the reference plane
    lets the stretched far frames dominate), and the parameters are normalised to
    [-1,1] (raw homography entries span 1e-4 to 1e3, which the solver cannot scale).

    Returns (homographies, stats).
    """
    from scipy.optimize import least_squares
    from scipy.sparse import lil_matrix
    n_mat = np.array([[2.0 / work_w, 0, -1], [0, 2.0 / work_h, -1], [0, 0, 1]])
    n_inv = np.linalg.inv(n_mat)
    free = [n for n in paths if n != ref and n in h0]
    pos = {n: i for i, n in enumerate(free)}

    def norm(hm):
        g = n_mat @ hm @ n_inv
        return g / g[2, 2]

    x0 = np.concatenate([norm(h0[n]).ravel()[:8] for n in free])
    use = [(a, b, d) for (a, b), d in pairs.items() if a in h0 and b in h0]
    pts = {}
    for a, b, d in use:
        pts[(a, b)] = (
            cv2.perspectiveTransform(d['src'].reshape(-1, 1, 2), n_mat).reshape(-1, 2),
            cv2.perspectiveTransform(d['dst'].reshape(-1, 1, 2), n_mat).reshape(-1, 2))

    def mats(x):
        m = {ref: np.eye(3)}
        for n in free:
            m[n] = np.append(x[pos[n] * 8:pos[n] * 8 + 8], 1.0).reshape(3, 3)
        return m

    def resid(x):
        m = mats(x)
        r = []
        for a, b, d in use:
            pa, pb = pts[(a, b)]
            hba = np.linalg.inv(m[b]) @ m[a]
            r.append((cv2.perspectiveTransform(pa.reshape(-1, 1, 2), hba).reshape(-1, 2)
                      - pb).ravel())
            r.append((cv2.perspectiveTransform(pb.reshape(-1, 1, 2),
                                               np.linalg.inv(hba)).reshape(-1, 2)
                      - pa).ravel())
        return np.concatenate(r) * (work_w / 2.0)     # back to pixel units

    n0 = len(resid(x0))
    log('refining %d params over %d residuals' % (len(x0), n0))
    sp = lil_matrix((n0, len(x0)), dtype=np.uint8)
    off = 0
    for a, b, d in use:
        k = len(d['src']) * 4
        for n in (a, b):
            if n in pos:
                sp[off:off + k, pos[n] * 8:pos[n] * 8 + 8] = 1
        off += k
    res = least_squares(resid, x0, jac_sparsity=sp, method='trf', loss='huber',
                        f_scale=f_scale, max_nfev=iters, verbose=0, x_scale='jac',
                        ftol=1e-10, xtol=1e-10)
    r0, r1 = resid(x0), res.fun
    stats = {
        'transfer_rms_px_before': float(np.sqrt(np.mean(r0 ** 2))),
        'transfer_rms_px_after': float(np.sqrt(np.mean(r1 ** 2))),
        'transfer_median_abs_px_before': float(np.median(np.abs(r0))),
        'transfer_median_abs_px_after': float(np.median(np.abs(r1))),
        'nfev': int(res.nfev),
    }
    log('transfer rms %.2f -> %.2f px, median |e| %.2f -> %.2f, nfev %d'
        % (stats['transfer_rms_px_before'], stats['transfer_rms_px_after'],
           stats['transfer_median_abs_px_before'], stats['transfer_median_abs_px_after'],
           stats['nfev']))
    m = mats(res.x)
    return {n: (lambda g: g / g[2, 2])(n_inv @ m[n] @ n_mat) for n in m}, stats


def run(paths, cache_dir, ref=None, work_mpx=1.5, nfeat=12000, log=print):
    """Full registration: features -> pairs -> spanning tree -> global refinement.

    Returns (homographies keyed by path, work_scale, work_w, work_h, ref, stats).
    """
    log('features for %d frames at %.2f Mpx' % (len(paths), work_mpx))
    feats, scale, ww, wh = features(paths, cache_dir, work_mpx, nfeat, log)
    log('work scale %.4f -> %dx%d' % (scale, ww, wh))
    pairs = pair_homographies(paths, feats, cache_dir, log=log)
    log('%d connected pairs' % len(pairs))
    if not pairs:
        raise SystemExit('no frame pair matched: the sweep is not overlapping enough')
    stretch = None
    if ref:
        stretch = reference_stretch(paths, pairs, ref, ww, wh)
    else:
        ref, stretch = pick_reference(paths, pairs, ww, wh, log=log)
    h0 = spanning_tree(paths, pairs, ref)
    missing = [p for p in paths if p not in h0]
    if missing:
        log('WARNING %d frames unreachable from the reference, dropped: %s'
            % (len(missing), ', '.join(os.path.basename(p) for p in missing)))
    kept = [p for p in paths if p in h0]
    hs, stats = refine(kept, pairs, h0, ref, ww, wh, log=log)
    stats['reference_area_stretch'] = stretch
    stats['frames_used'] = len(kept)
    stats['frames_dropped'] = [os.path.basename(p) for p in missing]
    return hs, scale, ww, wh, ref, stats
