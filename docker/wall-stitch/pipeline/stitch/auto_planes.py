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


LINK_MIN_INLIERS = 60          # composite-homography link: absolute support
LINK_MIN_FRAC = 0.06           # ... and this fraction of the third pair's matches


def _composite(k1, H1, k2, H2):
    """Chain two plane observations that share one camera into the homography
    between the two cameras they do NOT share.  If both observations really are
    the same physical plane, that composite is that plane's homography for the
    third pair and the third pair's own matches will vote for it."""
    (a, b), (c, d) = k1, k2
    if b == c and a != d:
        return H2 @ H1, (a, d)
    if a == c and b != d:
        return H2 @ np.linalg.inv(H1), (b, d)
    if b == d and a != c:
        return np.linalg.inv(H2) @ H1, (a, c)
    if a == d and b != c:
        return H1 @ H2, (c, b)
    return None, None


def _votes(matches, pair, Hc):
    """How many of a pair's raw coarse matches a candidate homography explains."""
    if pair in matches:
        pa, pb = matches[pair]
    elif (pair[1], pair[0]) in matches:
        pb, pa = matches[(pair[1], pair[0])]
        Hc = np.linalg.inv(Hc)
    else:
        return 0, 0
    return int((AM.transfer_err(pa, pb, Hc) < AM.COARSE_THRESH).sum()), len(pa)


def cluster(obs, gshape, matches):
    """Union-find over observations that are the same physical plane.

    Two criteria, both positive evidence:
      * they share a camera and their support covers the same part of it
        (opaque surfaces do not overlap, so co-located support means one plane);
      * they share a camera and the homography chained through it is voted for
        by the raw matches of the pair they do not share.
    The second is what links a plane across a strip panorama, where two views of
    it meet the middle frame on opposite sides and never co-locate."""
    sup = []
    for o in obs:
        sup.append({o["a"]: PS.support_counts(o["pa"], gshape) > 0,
                    o["b"]: PS.support_counts(o["pb"], gshape) > 0})
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
            why, n, tot = None, 0, 0
            if best >= AM.IOU_SAME_PLANE:
                why = "support IoU %.2f in camera %s" % (best, where)
            else:
                Sc = np.diag([AM.COARSE, AM.COARSE, 1.0])
                comp, pair = _composite((obs[i]["a"], obs[i]["b"]), obs[i]["H"],
                                        (obs[j]["a"], obs[j]["b"]), obs[j]["H"])
                if comp is not None:
                    n, tot = _votes(matches, pair, Sc @ comp @ np.linalg.inv(Sc))
                    if n >= LINK_MIN_INLIERS and n >= LINK_MIN_FRAC * max(tot, 1):
                        why = "chained through %s-%s: %d/%d matches" % (*pair, n, tot)
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

    def build_masks(self, grays, gshape, coarse):
        self.grid, self.sup = {}, {}
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
            self.sup[n] = PS.support_counts(self.points(n), gshape)
            self.grid[n] = PS.build_mask(self.sup[n], scores, reaches)


def _inside(pts, grid):
    ii, jj = PS.cell_index(pts, grid.shape)
    return grid[ii, jj]


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
                sel = np.ones(len(pa), bool)
                if ga is not None:
                    sel &= _inside(pa, ga)
                if gb is not None:
                    sel &= _inside(pb, gb)
                for q in planes:                       # not somebody else's surface
                    if q is p:
                        continue
                    if q.grid.get(a) is not None:
                        sel &= ~_inside(pa, q.grid[a])
                    if q.grid.get(b) is not None:
                        sel &= ~_inside(pb, q.grid[b])
                idx = np.flatnonzero(sel)
                if len(idx) < 4 * AM.MIN_SEED_INLIERS:
                    continue
                found = AM.sequential_ransac(pa, pb, subset=idx, max_planes=1)
                if not found:
                    continue
                Hc = found[0][0]
                Sc = np.diag([AM.COARSE, AM.COARSE, 1.0])
                r = AM.promote(disc["imgs"][a], disc["imgs"][b], np.linalg.inv(Sc) @ Hc @ Sc,
                               PS.to_full(ga, disc["full_shape"]) if ga is not None else None,
                               PS.to_full(gb, disc["full_shape"]) if gb is not None else None)
                if r is None:
                    continue
                if p.add((a, b), r["H"], r["pa"], r["pb"]):
                    added.append("%s-%s:%d(p%d)" % (a, b, r["n"], p.rank))
        if not added:
            break
        log("auto: grew into %s" % added)
        for p in planes:
            p.build_masks(disc["grays"], gshape, AM.COARSE)


def discover(imgs, names, cache, log, full_shape):
    """Full discovery.  Returns accepted planes (ranked) plus diagnostics."""
    gshape = PS.grid_shape(full_shape)
    disc = pairwise(imgs, names, cache, log, full_shape)
    disc["imgs"], disc["full_shape"] = imgs, full_shape
    groups, links = cluster(disc["obs"], gshape, disc["matches"])

    planes = []
    for gi, g in enumerate(groups):
        p = Plane(gi)
        for i in sorted(g, key=lambda i: -disc["obs"][i]["n"]):
            o = disc["obs"][i]
            p.add((o["a"], o["b"]), o["H"], o["pa"], o["pb"])
        p.build_masks(disc["grays"], gshape, AM.COARSE)
        planes.append(p)
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
