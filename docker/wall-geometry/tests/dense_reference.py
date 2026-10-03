"""The texture renderer as it was before views.py: every per-photo array dense over photos x label cells.

Kept only as the reference the sparse renderer must reproduce exactly (test_texture_memory.py). Its own
code is the previous dense scoring, labels, accumulator and exposure fit, plus the later rule that the
consensus label only picks photos with rendered pixels. Everything after the labels is NOT old code: it
calls the current blended._result, blend.finish and source map (sourcemap.drawn_cells), so this module
only proves the sparse views change nothing; regressions in that shared tail are caught by the frozen
output in test_texture_golden.py.
"""
import numpy as np

from wallgeometry import blend, consensus, exposure, flatten, occlusion, scale, seams
from wallgeometry import blended as bl
from wallgeometry import textures as tx


class DenseAccumulator(blend.FacetAccumulator):
    """The old accumulator: weights as one dense (C, ch, cw) array."""

    def __init__(self, g, W_cells, cell, n):
        super().__init__(g, None, cell, n)
        self.W = W_cells

    def weights(self, c):
        return self.W[c] if (self.W[c] > 0).any() else None

    def region(self, c):
        Wc = self.W[c]
        ys, xs = np.nonzero(Wc > 0)
        if ys.size == 0:
            return None
        cy0, cy1 = max(ys.min() - 1, 0), min(ys.max() + 2, Wc.shape[0])
        cx0, cx1 = max(xs.min() - 1, 0), min(xs.max() + 2, Wc.shape[1])
        crop = Wc[cy0:cy1, cx0:cx1]
        cell = self.cell
        up = tx.cv2.resize(crop, ((cx1 - cx0) * cell, (cy1 - cy0) * cell), interpolation=tx.cv2.INTER_LINEAR)
        y0, x0 = cy0 * cell, cx0 * cell
        up = up[:max(0, self.g["H"] - y0), :max(0, self.g["W"] - x0)]
        return y0, x0, up


def cell_scores(f, g, cams, names, p, occs):
    X = blend.cell_points(f, g, p["labelCellPx"], tx._plane_points)
    S = np.stack([tx._score(cams[n], f, X, p["imageMarginPx"]) for n in names])  # (C, ch, cw)
    others = [o for o in occs if o.id != f["id"]]
    flat = X.reshape(-1, 3)
    for k, n in enumerate(names):
        sk = S[k].reshape(-1)
        seen = np.nonzero(sk > 0)[0]
        if seen.size and others:
            centre = -cams[n]["R"].T @ cams[n]["t"]
            sk[seen[occlusion.hidden(centre, others, flat[seen], p["behindOtherFacetMm"])]] = 0
    return X, S


def cell_labels(S, names, p):
    valid = S > 0
    lab = np.where(valid.any(0), S.argmax(0), -1)
    k = int(p["modeFilterCells"])
    if k > 1 and len(names) > 1:
        votes = np.stack([tx.cv2.boxFilter((lab == c).astype(np.float32), -1, (k, k), normalize=False,
                                           borderType=tx.cv2.BORDER_REPLICATE) for c in range(len(names))])
        smax = S.max(0, keepdims=True)
        votes = votes + 0.01 * S / np.where(smax > 0, smax, 1)
        votes[~valid] = -1
        lab = np.where(valid.any(0), votes.argmax(0), -1)
    return lab


def pair_offsets(lg, min_overlap):
    V = ~np.isnan(lg[..., 0])
    counts = V.astype(np.float32) @ V.T.astype(np.float32)
    pairs = []
    C = lg.shape[0]
    for i in range(C):
        for j in range(i + 1, C):
            if counts[i, j] < min_overlap:
                continue
            m = V[i] & V[j]
            pairs.append((i, j, np.median(lg[i, m] - lg[j, m], axis=0), int(m.sum())))
    return pairs


def fit_gains(cells, min_overlap, prior_luma, prior_chroma):
    col = np.concatenate([c.reshape(c.shape[0], -1, 3) for c in cells], axis=1)
    lg = exposure.log_samples(col)
    pairs = pair_offsets(lg, min_overlap)
    gains, ref = exposure.solve_gains(lg.shape[0], pairs, prior_luma, prior_chroma)
    return gains, ref, len(pairs)


def _prepare(facets, cams, names, p, occs):
    jobs = []
    for f in facets:
        g = tx._grid(f, p)
        X, S = cell_scores(f, g, cams, names, p, occs)
        W = blend.view_weights(S, int(p["blendViews"]), float(p["blendSharpness"]))
        acc = DenseAccumulator(g, W, p["labelCellPx"], int(p["blendViews"]))
        cells = np.full((len(names),) + S.shape[1:] + (3,), np.nan, np.float32)
        jobs.append({"f": f, "g": g, "X": X, "S": S, "acc": acc, "cells": cells})
    return jobs


def _sample_gain_cells(img, cam, c, jobs):
    small = blend.shrink(img, bl.GAIN_DOWNSCALE)
    for j in jobs:
        seen = j["S"][c] > 0
        if seen.any():
            col = blend.sample_cells(small, bl.GAIN_DOWNSCALE, cam, j["X"], tx.project)
            col[~seen] = np.nan
            j["cells"][c] = col


def _label(j, gains, names, p):
    if p["blendMode"] != "select":
        return None
    S = consensus.penalised_scores(j["S"], j["cells"], gains, p) if p["exposureBalance"] else j["S"]
    S = np.where(j["acc"].W > 0, S, 0)  # only photos with rendered pixels (the consensus-pick fix)
    cells = cell_labels(S, names, {**p, "modeFilterCells": p["selectModeFilterCells"]})
    return tx._upsample(cells, j["g"], p["labelCellPx"])


def _render_blended(doc, load_photo, cams, names, facets, p, occs):
    jobs = _prepare(facets, cams, names, p, occs)
    balance = bool(p["exposureBalance"])
    for c, n in enumerate(names):
        if not any((j["acc"].W[c] > 0).any() or (balance and (j["S"][c] > 0).any()) for j in jobs):
            continue
        cam = cams[n]
        img = tx._checked_photo(load_photo, n, cam)
        if balance:
            _sample_gain_cells(img, cam, c, jobs)
        for j in jobs:
            bl._render_slots(img, cam, c, j, p)
    gains, ref, npairs = np.ones((len(names), 3)), None, 0
    if balance:
        gains, ref, npairs = fit_gains([j["cells"] for j in jobs], int(p["gainMinOverlapCells"]),
                                       float(p["gainPriorLuma"]), float(p["gainPriorChroma"]))
    report = bl._gain_report(gains, ref, npairs, names)
    return [bl._result(doc, j, gains, names, p, _label(j, gains, names, p)) | {"exposure": report}
            for j in jobs]


def _render_single(doc, load_photo, cams, names, facets, p, occs):
    jobs = []
    for f in facets:
        g = tx._grid(f, p)
        _, S = cell_scores(f, g, cams, names, p, occs)
        jobs.append({"views": _DenseLabels(cell_labels(S, names, p)), "f": f, "g": g})
    return tx._render_single(doc, load_photo, cams, names, jobs, p, None)


class _DenseLabels:
    """Stands in for views.FacetViews in textures._render_single: labels already chosen densely."""

    def __init__(self, cells):
        self.cells = cells

    def labels(self, *args):
        return self.cells


def render_textures(doc, load_photo, available, params=None):
    p = scale.at_resolution({**tx.DEFAULTS, **(params or {})}, params)
    cams = {c["image"]: tx._cam(c) for c in doc.get("cameras", []) if c["image"] in available}
    names = sorted(cams)
    facets = list(tx._facets(doc))
    if int(p["blendViews"]) > 1:
        results = _render_blended(doc, load_photo, cams, names, facets, p, tx.occluders(facets, doc))
    else:
        results = _render_single(doc, load_photo, cams, names, facets, p, tx.occluders(facets, doc))
    fid = {f["id"]: f for f in facets}
    if p["flattenShading"]:
        shading = flatten.flatten(results, fid, p)
        for r in results:
            if r["facet"] in shading:
                r["shading"] = shading[r["facet"]]
    if p["seamHarmonise"] and len(results) > 1:
        report = seams.harmonise(results, fid, p)
        for r in results:
            r["seams"] = {k: v for k, v in report.items() if r["facet"] in k.split("-")}
    return results
