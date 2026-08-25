#!/usr/bin/env python3
"""Driver for automatic plane discovery: pairs -> observations -> planes -> masks.

See `auto_mask` for the reasoning and `plane_support` for the masking rule.
Everything here is bookkeeping around those two: caching the pairwise matching,
grouping observations into planes, growing a plane into pairs where the first
unmasked RANSAC could not see it, and reporting what was accepted and why.
"""
import os

import cv2
import numpy as np

import auto_mask as AM
import plane_support as PS


def _coarse(img, shape):
    g = cv2.resize(img, (shape[1], shape[0]), interpolation=cv2.INTER_AREA)
    return cv2.cvtColor(g, cv2.COLOR_BGR2GRAY).astype(np.float32)


def pairwise(imgs, names, cache, log, full_shape):
    """Coarse matches plus promoted plane observations for every unordered pair."""
    H0, W0 = full_shape
    cshape = (int(round(H0 * AM.COARSE)), int(round(W0 * AM.COARSE)))
    grays, feats = {}, {}
    for n in names:
        grays[n] = _coarse(imgs[n], cshape)
        fp = os.path.join(cache, f"af_{n}.npz")
        if os.path.exists(fp):
            z = np.load(fp)
            feats[n] = (z["p"], z["d"])
        else:
            feats[n] = AM.detect(grays[n].astype(np.uint8), None, AM.SIFT_N, AM.SIFT_CT)
            np.savez(fp, p=feats[n][0], d=feats[n][1])
    log("auto: coarse features " + ", ".join(f"{n}:{len(feats[n][0])}" for n in names))

    Sc = np.diag([AM.COARSE, AM.COARSE, 1.0])
    matches, obs, stats = {}, [], {}
    for i, a in enumerate(names):
        for b in names[i + 1:]:
            cp = os.path.join(cache, f"ao_{a}_{b}.npz")
            if os.path.exists(cp):
                z = np.load(cp)
                pa, pb = z["pa"], z["pb"]
                got = [dict(H=z[f"H{k}"], pa=z[f"ia{k}"], pb=z[f"ib{k}"], n=int(z[f"n{k}"]),
                            candidates=int(z[f"c{k}"]), rms=float(z[f"r{k}"]))
                       for k in range(int(z["k"]))]
                seeds = [int(v) for v in z["seeds"]]
            else:
                mi = AM.bf_ratio_match(feats[a][1], feats[b][1])
                pa, pb = feats[a][0][mi[:, 0]], feats[b][0][mi[:, 1]]
                raw = AM.sequential_ransac(pa, pb) if len(pa) >= 40 else []
                seeds = [len(s) for _, s in raw]
                got = []
                for Hc, _sel in raw:
                    r = AM.promote(imgs[a], imgs[b], np.linalg.inv(Sc) @ Hc @ Sc)
                    if r is not None:
                        got.append(r)
                got = AM.dedupe(got)
                got.sort(key=lambda o: -o["n"])
                z = dict(pa=pa, pb=pb, k=len(got), seeds=np.array(seeds, int))
                for k, o in enumerate(got):
                    z[f"H{k}"], z[f"ia{k}"], z[f"ib{k}"] = o["H"], o["pa"], o["pb"]
                    z[f"n{k}"], z[f"c{k}"], z[f"r{k}"] = o["n"], o["candidates"], o["rms"]
                np.savez(cp, **z)
            matches[(a, b)] = (pa, pb)
            for o in got:
                o.update(a=a, b=b)
            obs += got
            stats[f"{a}-{b}"] = dict(coarse_matches=int(len(pa)), coarse_seed_inliers=seeds,
                                     planes=len(got), inliers=[o["n"] for o in got],
                                     reproj_rms_px=[round(o["rms"], 2) for o in got])
            log("auto: pair %s-%s  %d coarse matches, seeds %s -> %d planes %s rms %s"
                % (a, b, len(pa), seeds, len(got), [o["n"] for o in got],
                   [round(o["rms"], 2) for o in got]))
    return dict(grays=grays, cshape=cshape, matches=matches, obs=obs, stats=stats)


NORMAL_TOL_DEG = 8.0           # two views of one plane recover the same normal
ADJACENT_CELLS = 10            # ... and their support in the shared camera touches


def plane_normals(H, K, pa):
    """Unit normals of the plane in the FIRST camera of `H`, cheirality-filtered.

    `decomposeHomographyMat` returns four solutions; the two that put the
    matched points behind the plane cannot be what the camera saw.
    """
    n_sol, _Rs, _Ts, Ns = cv2.decomposeHomographyMat(H, K)
    rays = np.hstack([pa, np.ones((len(pa), 1))]) @ np.linalg.inv(K).T
    out = []
    for i in range(n_sol):
        v = np.array(Ns[i]).ravel()
        v = v / np.linalg.norm(v)
        if (rays @ v <= 0).mean() > 0.05:
            continue
        out.append(v)
    return out


def _normal_gap(u_list, v_list):
    if not u_list or not v_list:
        return 180.0
    return min(float(np.degrees(np.arccos(np.clip(abs(u @ v), -1, 1))))
               for u in u_list for v in v_list)


def _adjacent(gi, gj, r=ADJACENT_CELLS):
    k = np.ones((2 * r + 1, 2 * r + 1), np.uint8)
    return bool((cv2.dilate(gi.astype(np.uint8), k) & gj.astype(np.uint8)).any())


def cluster(obs, gshape, K):
    """Union-find over observations that are the same physical plane.

    Two criteria, both positive evidence:
      * they share a camera and their support covers the same part of it
        (opaque surfaces do not overlap, so co-located support means one plane);
      * they share a camera, the plane normal each recovers in that camera is
        the same, and their support in it touches.
    The second is what links a plane across a strip panorama, where two views of
    it meet the middle frame on opposite sides and barely co-locate.  On the
    reference set the two views of the main span agree to 0.58 deg while every
    main-span/kickboard pairing is 42-49 deg apart, so the two surfaces separate
    with a wide margin.  The residual assumption is that two *parallel* planes at
    different depths are not both visible in one camera; that case would need the
    plane distance as well, and is reported rather than silently handled."""
    sup, nrm = [], []
    for o in obs:
        sup.append({o["a"]: PS.support_counts(o["pa"], gshape) > 0,
                    o["b"]: PS.support_counts(o["pb"], gshape) > 0})
        nrm.append({o["a"]: plane_normals(o["H"], K, o["pa"]),
                    o["b"]: plane_normals(np.linalg.inv(o["H"]), K, o["pb"])})
    parent = list(range(len(obs)))

    def find(x):
        while parent[x] != x:
            parent[x] = parent[parent[x]]
            x = parent[x]
        return x

    links = []
    for i in range(len(obs)):
        for j in range(i + 1, len(obs)):
            best, where = 0.0, None
            for cam in set(sup[i]) & set(sup[j]):
                gi, gj = sup[i][cam], sup[j][cam]
                u = (gi | gj).sum()
                v = float((gi & gj).sum()) / u if u else 0.0
                if v > best:
                    best, where = v, cam
            why = None
            if best >= AM.IOU_SAME_PLANE:
                why = "support IoU %.2f in camera %s" % (best, where)
            else:
                for cam in set(sup[i]) & set(sup[j]):
                    gap = _normal_gap(nrm[i][cam], nrm[j][cam])
                    if gap < NORMAL_TOL_DEG and _adjacent(sup[i][cam], sup[j][cam]):
                        why = "normals agree to %.2f deg in camera %s" % (gap, cam)
                        break
            if why:
                links.append(dict(i=i, j=j, why=why))
                ri, rj = find(i), find(j)
                if ri != rj:
                    parent[ri] = rj
    groups = {}
    for i in range(len(obs)):
        groups.setdefault(find(i), []).append(i)
    out = sorted((sorted(v) for v in groups.values()),
                 key=lambda g: -sum(obs[i]["n"] for i in g))
    return out, links


class Plane:
    """A discovered plane: homographies and correspondences per pair, masks per image."""

    def __init__(self, rank):
        self.rank = rank
        self.H, self.corr = {}, {}
        self.grid, self.masks = {}, {}

    @property
    def images(self):
        return sorted({n for pair in self.H for n in pair})

    @property
    def support(self):
        return int(sum(len(v[0]) for v in self.corr.values()))

    def add(self, key, H, pa, pb):
        if key in self.corr and len(self.corr[key][0]) >= len(pa):
            return False
        self.H[key], self.corr[key] = H, (pa, pb)
        return True

    def points(self, name):
        out = [self.corr[k][0 if k[0] == name else 1] for k in self.corr if name in k]
        return np.vstack(out) if out else np.zeros((0, 2))

    def compute_support(self, gshape):
        self.sup = {n: PS.support_counts(self.points(n), gshape) for n in self.images}

    def build_masks(self, grays, gshape, coarse, blocked=None):
        self.grid = {}
        for n in self.images:
            scores, reaches = [], []
            for (a, b), H in self.H.items():
                if n == b:
                    src, Hd = a, H
                elif n == a:
                    src, Hd = b, np.linalg.inv(H)
                else:
                    continue
                s, r = PS.agreement(grays[src], grays[n], Hd, gshape, coarse)
                scores.append(s)
                reaches.append(r)
            self.grid[n] = PS.build_mask(self.sup[n], scores, reaches,
                                         None if blocked is None else blocked.get(n))


GROW_MIN_SEED = 20         # coarse inliers needed to try a restricted re-search
GROW_CANDIDATES = 3        # ... and how many surfaces to peel out of the restriction
GROW_MIN_COLOCATED = 0.3   # a grown fit must land where the plane already is


def rebuild_all(planes, grays, gshape, coarse):
    """Masks for every plane at once, so each can block on the others' inliers.

    A plane may not close its mask across a cell that a different plane's own
    correspondences occupy.  On the reference set that band of kickboard inliers
    is what stops the main span's mask from closing over the crash mat below it.
    """
    for p in planes:
        p.compute_support(gshape)
    k = np.ones((5, 5), np.uint8)
    for p in planes:
        blocked = {}
        for n in p.images:
            other = [q.sup[n] > 0 for q in planes if q is not p and n in q.sup]
            if other:
                blocked[n] = cv2.dilate(np.any(other, 0).astype(np.uint8), k) > 0
        p.build_masks(grays, gshape, coarse, blocked)


def _inside(pts, grid, scale=1.0):
    """Which of these points fall in a grid mask.  `scale` converts the points
    to full resolution first (the discovery matches live at the coarse scale)."""
    ii, jj = PS.cell_index(np.asarray(pts) / scale, grid.shape)
    return grid[ii, jj]


def merge_planes(planes, log):
    """Collapse planes that turn out to be the same surface.

    Discovery can find one physical plane twice - once from each side of a
    strip panorama - without any single pair witnessing both.  Growth then
    pushes each copy into the other's pairs, and there the two finally meet:
    if their homographies for a shared pair explain each other's
    correspondences, they are one plane."""
    out = list(planes)
    changed = True
    while changed and len(out) > 1:
        changed = False
        for i in range(len(out)):
            for j in range(i + 1, len(out)):
                p, q = out[i], out[j]
                shared = set(p.H) & set(q.H)
                if not shared:
                    continue
                ok = [k for k in shared
                      if np.median(AM.transfer_err(*q.corr[k], p.H[k])) < AM.DUP_MEDIAN_PX
                      and np.median(AM.transfer_err(*p.corr[k], q.H[k])) < AM.DUP_MEDIAN_PX]
                if len(ok) < len(shared) or not ok:
                    continue
                for k in q.H:
                    if k in p.corr:
                        pa = np.vstack([p.corr[k][0], q.corr[k][0]])
                        pb = np.vstack([p.corr[k][1], q.corr[k][1]])
                        H, sel = AM._refit(pa, pb, p.H[k], AM.FULL_THRESH)
                        if len(sel) >= AM.MIN_OBS_INLIERS:
                            p.H[k], p.corr[k] = H, (pa[sel], pb[sel])
                            continue
                    if len(q.corr[k][0]) > len(p.corr.get(k, ([],))[0]):
                        p.H[k], p.corr[k] = q.H[k], q.corr[k]
                log("auto: merged plane %d into %d (agree on %s)"
                    % (q.rank, p.rank, ["%s-%s" % k for k in ok]))
                out.pop(j)
                changed = True
                break
            if changed:
                break
    return out


def grow(planes, disc, gshape, log):
    """Revisit every pair with its raw matches restricted to a plane's mask."""
    for it in range(AM.GROW_ROUNDS):
        added = []
        for p in planes:
            for (a, b), (pa, pb) in disc["matches"].items():
                if (a, b) in p.H:
                    continue
                ga, gb = p.grid.get(a), p.grid.get(b)
                if ga is None and gb is None:
                    continue
                # Exclusion, not inclusion: the plane is looked for in whatever
                # the *other* discovered surfaces do not already own.  Requiring
                # the points to fall inside this plane's current mask would be
                # circular - the mask is exactly what is missing in this pair.
                sel = np.ones(len(pa), bool)
                for q in planes:
                    if q is p:
                        continue
                    if q.grid.get(a) is not None:
                        sel &= ~_inside(pa, q.grid[a], AM.COARSE)
                    if q.grid.get(b) is not None:
                        sel &= ~_inside(pb, q.grid[b], AM.COARSE)
                idx = np.flatnonzero(sel)
                if len(idx) < 4 * AM.MIN_SEED_INLIERS:
                    continue
                # A restricted, targeted search, so the seed bar is lower than in
                # the blind first pass: full-resolution promotion and the
                # co-location check below are what actually validate the find.
                found = AM.sequential_ransac(pa, pb, subset=idx, max_planes=GROW_CANDIDATES,
                                             min_inliers=GROW_MIN_SEED)
                Sc = np.diag([AM.COARSE, AM.COARSE, 1.0])
                shared = [c for c in (a, b) if c in p.grid]
                r = None
                for Hc, _sel in found:
                    cand = AM.promote(disc["imgs"][a], disc["imgs"][b],
                                      np.linalg.inv(Sc) @ Hc @ Sc)
                    if cand is None:
                        continue
                    if shared and not any(
                            _inside(cand["pa"] if c == a else cand["pb"],
                                    p.grid[c]).mean() > GROW_MIN_COLOCATED
                            for c in shared):
                        continue              # landed on somebody else's surface
                    if r is None or cand["n"] > r["n"]:
                        r = cand
                if r is None:
                    continue
                if p.add((a, b), r["H"], r["pa"], r["pb"]):
                    added.append("%s-%s:%d(p%d)" % (a, b, r["n"], p.rank))
        merged = merge_planes(planes, log)
        if not added and len(merged) == len(planes):
            break
        if added:
            log("auto: grew into %s" % added)
        planes[:] = merged
        rebuild_all(planes, disc["grays"], gshape, AM.COARSE)


def discover(imgs, names, cache, log, full_shape, K):
    """Full discovery.  Returns accepted planes (ranked) plus diagnostics."""
    gshape = PS.grid_shape(full_shape)
    disc = pairwise(imgs, names, cache, log, full_shape)
    disc["imgs"], disc["full_shape"] = imgs, full_shape
    groups, links = cluster(disc["obs"], gshape, K)

    planes = []
    for gi, g in enumerate(groups):
        p = Plane(gi)
        for i in sorted(g, key=lambda i: -disc["obs"][i]["n"]):
            o = disc["obs"][i]
            p.add((o["a"], o["b"]), o["H"], o["pa"], o["pb"])
        planes.append(p)
    rebuild_all(planes, disc["grays"], gshape, AM.COARSE)
    grow(planes, disc, gshape, log)

    acc, rejected = [], []
    for p in planes:
        big = [n for n in p.images if p.grid[n].mean() >= AM.MIN_PLANE_IMAGE_FRAC]
        why = None
        if p.support < AM.MIN_PLANE_INLIERS:
            why = "support %d < %d correspondences" % (p.support, AM.MIN_PLANE_INLIERS)
        elif len(big) < 2:
            why = "covers %d%% of only %d frame(s)" % (100 * AM.MIN_PLANE_IMAGE_FRAC, len(big))
        if why:
            rejected.append(dict(support=p.support, images=p.images, reason=why))
        else:
            acc.append(p)
    acc.sort(key=lambda p: (-sum(p.grid[n].mean() for n in p.images), -p.support))
    for k, p in enumerate(acc):
        p.rank = k
    for n in names:
        owners = [p for p in acc if n in p.grid]
        if len(owners) > 1:
            fixed = PS.resolve_overlaps([p.grid[n] for p in owners], [p.sup[n] for p in owners])
            for p, g in zip(owners, fixed):
                p.grid[n] = g
    for p in acc:
        p.masks = {n: PS.to_full(p.grid[n], full_shape) for n in p.images
                   if p.grid[n].mean() >= AM.MIN_PLANE_IMAGE_FRAC}
        log("auto: plane %d support=%d rms=%.2f images=%s area%%=%s pairs=%s"
            % (p.rank, p.support,
               float(np.mean([np.sqrt((AM.transfer_err(*p.corr[k], p.H[k]) ** 2).mean())
                              for k in p.H])),
               sorted(p.masks), {n: round(100 * float((p.masks[n] > 0).mean()), 1)
                                 for n in sorted(p.masks)},
               ["%s-%s" % k for k in sorted(p.H)]))
    return acc, dict(pairs=disc["stats"], links=links, rejected_planes=rejected)


def overlay(img, masks, colors):
    """Diagnostic: dim everything off-plane, tint each discovered plane."""
    ov = img.copy()
    any_m = np.zeros(img.shape[:2], bool)
    for i, m in enumerate(masks):
        if m is None:
            continue
        sel = m > 0
        any_m |= sel
        ov[sel] = (0.65 * ov[sel] + 0.35 * np.array(colors[i % len(colors)], np.float32)).astype(np.uint8)
    ov[~any_m] = (ov[~any_m] * 0.25).astype(np.uint8)
    return ov
