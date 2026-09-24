"""Automatic facet assignment from the free solve, and the facet-constrained BA problem.

Starting point: every marker belongs to its NOMINAL segment (the request's `markerSegments`, else
`id // 6`). Then, using the free-solve
marker poses (no facet constraint yet):

1. SPLIT: a segment whose marker normals disagree is clustered (complete linkage on the normals). A
   split is only accepted when every side has >= `minMarkersPerFacet` markers AND the plane fitted to
   each side differs by more than `foldDeg`. A single odd marker is not a fold: it can just as well
   be a bent or badly observed marker, and one marker cannot tell the two apart.
2. MERGE: markers whose nominal segment is NOT declared in the request (spares reused elsewhere, e.g.
   capture 1's seg-4 fillers 24-27) join the declared facet they are coplanar with: normal within
   `mergeDeg` and every corner within `mergeMm` of that facet's plane. Unmatched ones form their own
   facet. A marker of a declared segment whose NORMAL disagrees with its own facet by more than
   `foldDeg` but which IS coplanar with another facet is moved there. Declared segments are never merged with each other, even when coplanar
   (the owner says they are different pieces); that is reported as a note instead.

Every decision is logged with its numbers in `decisions`.
"""
import itertools

import numpy as np

from .ba import Problem
from .camera import N_INTR

DEFAULTS = {"foldDeg": 5.0, "mergeDeg": 5.0, "mergeMm": 40.0, "minMarkersPerFacet": 2}


def marker_normals(prob, x):
    return {m: prob.marker(x, m)[0][:, 2] for m in prob.mids}


def angle(a, b):
    return float(np.degrees(np.arccos(np.clip(abs(a @ b), -1, 1))))


def fit_plane(points, ref_normal):
    c = points.mean(0)
    _, _, vt = np.linalg.svd(points - c)
    n = vt[2]
    return c, (n if n @ ref_normal > 0 else -n)


def _plane(ms, mw, nm):
    """Plane of a marker set; one marker -> its own plane."""
    ref = np.mean([nm[m] for m in ms], 0)
    if len(ms) == 1:
        return mw[ms[0]].mean(0), nm[ms[0]] / np.linalg.norm(nm[ms[0]])
    return fit_plane(np.vstack([mw[m] for m in ms]), ref)


def _fit_to(ms, plane, mw, nm):
    """(normal angle deg, max |corner offset| mm) of marker set ms against plane."""
    c, n = plane
    own = _plane(ms, mw, nm)[1]
    off = max(float(np.abs((mw[m] - c) @ n).max()) for m in ms)
    return angle(own, n), off


def cluster_normals(ms, nm, thr):
    """Complete-linkage clustering of marker normals at `thr` degrees."""
    clusters = [[m] for m in ms]
    while len(clusters) > 1:
        best = None
        for i, j in itertools.combinations(range(len(clusters)), 2):
            d = max(angle(nm[a], nm[b]) for a in clusters[i] for b in clusters[j])
            if d < thr and (best is None or d < best[0]):
                best = (d, i, j)
        if not best:
            break
        _, i, j = best
        clusters[i] += clusters.pop(j)
    return clusters


def _split(seg, ms, mw, nm, p, log):
    clusters = cluster_normals(ms, nm, p["foldDeg"])
    if len(clusters) == 1:
        return [sorted(ms)]
    small = [c for c in clusters if len(c) < p["minMarkersPerFacet"]]
    if small:
        log.append({"kind": "splitRejected", "segment": seg, "clusters": [sorted(c) for c in clusters],
                    "reason": f"a side has fewer than {p['minMarkersPerFacet']} markers: "
                              f"{[sorted(c) for c in small]} (odd marker, not a measurable fold)"})
        return [sorted(ms)]
    planes = [_plane(c, mw, nm) for c in clusters]
    worst = min(angle(a[1], b[1]) for a, b in itertools.combinations(planes, 2))
    if worst <= p["foldDeg"]:
        log.append({"kind": "splitRejected", "segment": seg, "clusters": [sorted(c) for c in clusters],
                    "reason": f"side planes differ by only {worst:.2f} deg (<= {p['foldDeg']})",
                    "minPlaneAngleDeg": round(worst, 3), "foldDeg": p["foldDeg"]})
        return [sorted(ms)]
    log.append({"kind": "split", "segment": seg, "facets": [sorted(c) for c in clusters],
                "minPlaneAngleDeg": round(worst, 3), "foldDeg": p["foldDeg"]})
    return [sorted(c) for c in sorted(clusters, key=min)]


class _Assigner:
    """State of one facet assignment: facets {fid: (segment, [ids])} and the decision log."""

    def __init__(self, prob, x, declared, params, suspect, nominal_of=None):
        self.p = {**DEFAULTS, **(params or {})}
        self.mw, self.nm = prob.marker_world(x), marker_normals(prob, x)
        self.declared, self.suspect, self.log = declared, set(suspect), []
        self.nominal_of = nominal_of or (lambda m: m // 6)
        nominal = {}
        for m in prob.mids:
            nominal.setdefault(self.nominal_of(m), []).append(m)
        self.facets = {}
        for seg in sorted(nominal):
            parts = _split(seg, nominal[seg], self.mw, self.nm, self.p, self.log)
            for k, part in enumerate(parts):
                self.facets[str(seg) if len(parts) == 1 else f"{seg}{'abcdefgh'[k]}"] = (seg, part)
        self.declared_fids = [f for f, (s, _) in self.facets.items() if s in declared]

    def _trusted(self, ms):
        return [m for m in ms if m not in self.suspect] or ms

    def best_host(self, ms, exclude):
        best = None
        for f in self.declared_fids:
            host = [m for m in self.facets[f][1] if m not in ms]
            if f in exclude or not host:
                continue
            a, off = _fit_to(ms, _plane(self._trusted(host), self.mw, self.nm), self.mw, self.nm)
            if a < self.p["mergeDeg"] and off < self.p["mergeMm"] and (best is None or off < best[2]):
                best = (f, a, off)
        return best

    def merge_undeclared(self):
        """Undeclared nominal segments: adopt into a coplanar declared facet."""
        for f in [f for f, (s, _) in self.facets.items() if s not in self.declared]:
            ms = self.facets[f][1]
            host = self.best_host(ms, exclude={f})
            if not host:
                self.log.append({"kind": "undeclaredFacet", "facet": f, "markers": ms,
                                 "reason": "not coplanar with any declared facet"})
                continue
            hf, a, off = host
            self.facets[hf] = (self.facets[hf][0], sorted(self.facets[hf][1] + ms))
            del self.facets[f]
            self.log.append({"kind": "merge", "markers": ms, "fromNominalSegment": int(self.nominal_of(ms[0])),
                             "intoFacet": hf, "normalAngleDeg": round(a, 3), "maxOffsetMm": round(off, 2)})

    def move_misfits(self):
        """Declared markers whose normal disagrees with their own facet but fits another one.

        Only the NORMAL can overrule a declared id: offsets against a plane fitted to one or two other
        markers are too sensitive to extrapolation and to a single bad marker."""
        for f in list(self.facets):
            seg, ms = self.facets[f]
            if seg not in self.declared or len(ms) < 2:
                continue
            for m in list(ms):
                rest = [k for k in self.facets[f][1] if k != m]
                if not rest:
                    continue
                a_own, off_own = _fit_to([m], _plane(self._trusted(rest), self.mw, self.nm), self.mw, self.nm)
                host = self.best_host([m], exclude={f}) if a_own > self.p["foldDeg"] else None
                if not host:
                    continue
                hf, a, off = host
                self.facets[f] = (seg, rest)
                self.facets[hf] = (self.facets[hf][0], sorted(self.facets[hf][1] + [m]))
                self.log.append({"kind": "move", "marker": m, "fromFacet": f, "intoFacet": hf,
                                 "ownFitDeg": round(a_own, 3), "ownOffsetMm": round(off_own, 2),
                                 "normalAngleDeg": round(a, 3), "maxOffsetMm": round(off, 2)})

    def coplanar_notes(self):
        """Facets of different segments that are coplanar (kept apart on purpose)."""
        for f, g in itertools.combinations(sorted(self.facets), 2):
            if self.facets[f][0] == self.facets[g][0]:
                continue
            a, off = _fit_to(self.facets[f][1], _plane(self.facets[g][1], self.mw, self.nm), self.mw, self.nm)
            if a < self.p["mergeDeg"] and off < self.p["mergeMm"]:
                self.log.append({"kind": "coplanarNote", "facets": [f, g], "normalAngleDeg": round(a, 3),
                                 "maxOffsetMm": round(off, 2),
                                 "note": "coplanar within thresholds; kept separate (different segments)"})


def assign_facets(prob, x, declared, params=None, suspect=(), nominal_of=None):
    """Return (members {facetId: [ids]}, facet_segment {facetId: segment}, decisions).

    `suspect` markers (down-weighted outliers) never define a plane when others are available.
    `nominal_of(id)` gives a marker's nominal segment (default: the legacy `id // 6`).
    """
    a = _Assigner(prob, x, declared, params, suspect, nominal_of)
    a.merge_undeclared()
    a.move_misfits()
    a.coplanar_notes()
    members = {f: sorted(ms) for f, (_, ms) in a.facets.items()}
    return members, {f: s for f, (s, _) in a.facets.items()}, a.log


def coplanarity(prob, x, members):
    """Per facet: RMS / max corner distance (mm) to the best-fit plane, max marker-normal tilt."""
    mw, nm = prob.marker_world(x), marker_normals(prob, x)
    out = {}
    for fid, ms in members.items():
        ms = [m for m in ms if m in mw]
        if len(ms) < 2:
            out[fid] = {"markers": len(ms), "rmsMm": None, "maxMm": None, "maxNormalDevDeg": None}
            continue
        c, n = fit_plane(np.vstack([mw[m] for m in ms]), np.mean([nm[m] for m in ms], 0))
        d = np.concatenate([(mw[m] - c) @ n for m in ms])
        out[fid] = {"markers": len(ms), "rmsMm": float(np.sqrt(np.mean(d ** 2))),
                    "maxMm": float(np.abs(d).max()),
                    "maxNormalDevDeg": max(angle(nm[m], n) for m in ms)}
    return out


def build_facet_problem(prob_free, x_free, members):
    """Facet problem initialised from the free solution (plane fit, in-plane marker poses)."""
    mw, nm = prob_free.marker_world(x_free), marker_normals(prob_free, x_free)
    facets = {}
    for fid, ms in members.items():
        ms = [m for m in ms if m in mw]
        c, n = _plane(ms, mw, nm)
        ref = np.array([0, 0, 1.0]) if abs(n[2]) < 0.9 else np.array([1.0, 0, 0])
        u = np.cross(ref, n)
        u /= np.linalg.norm(u)
        v = np.cross(n, u)
        facets[fid] = {"F0": np.column_stack([u, v, n]), "p0": c, "members": ms}
    mids = [m for f in facets.values() for m in f["members"]]
    prob = Problem(prob_free.obs, prob_free.cams, prob_free.groups, prob_free.root,
                   [m for m in prob_free.mids if m in mids], prob_free.obj, facets=facets,
                   prior=prob_free.prior)
    x = np.zeros(prob.n)
    ni = N_INTR * len(prob_free.groups)
    x[:ni] = x_free[:ni]
    for img in prob.free_imgs:
        x[prob.ci[img]:prob.ci[img] + 6] = x_free[prob_free.ci[img]:prob_free.ci[img] + 6]
    for m in prob.mids:
        f = facets[prob.fid_of[m]]
        R, t = prob_free.marker(x_free, m)
        F0, p0 = f["F0"], f["p0"]
        a, b = (t - p0) @ F0[:, 0], (t - p0) @ F0[:, 1]
        psi = np.arctan2(R[:, 0] @ F0[:, 1], R[:, 0] @ F0[:, 0])
        x[prob.mi[m]:prob.mi[m] + 3] = [a, b, psi]
    return prob, x
