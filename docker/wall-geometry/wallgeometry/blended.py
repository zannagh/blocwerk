"""The default texture renderer: exposure-balanced, robust multi-view blend (blend.py, exposure.py).

One pass over the photos: each is loaded once, sampled at low resolution on every facet's label cells
(for the exposure gains) and rendered at full resolution into the facet's sample slots wherever it is
among the top-N views. Then the gains are fitted and every facet is combined in row tiles.
"""
import numpy as np

from . import blend, consensus, exposure
from . import textures as tx

GAIN_DOWNSCALE = 8


def _prepare(facets, cams, names, p):
    jobs = []
    for f in facets:
        g = tx._grid(f, p)
        X, S = tx._cell_scores(f, g, cams, names, p, [o for o in facets if o is not f])
        W = blend.view_weights(S, int(p["blendViews"]), float(p["blendSharpness"]))
        acc = blend.FacetAccumulator(g, W, p["labelCellPx"], int(p["blendViews"]))
        cells = np.full((len(names),) + S.shape[1:] + (3,), np.nan, np.float32)
        jobs.append({"f": f, "g": g, "X": X, "S": S, "acc": acc, "cells": cells})
    return jobs


def _render_slots(img, cam, c, job, p):
    """Photo c's view of one facet into the facet's sample slots, weighted and border-feathered."""
    acc, f, g = job["acc"], job["f"], job["g"]
    region = acc.region(c)
    if region is None:
        return
    y0, x0, up = region
    h, w = up.shape
    cols, rows = np.meshgrid(np.arange(x0, x0 + w, dtype=np.float64), np.arange(y0, y0 + h, dtype=np.float64))
    px, z, _ = tx.project(cam, tx._plane_points(f, g, cols, rows))
    px = np.clip(px, -1e6, 1e6)
    mx, my = px[..., 0].astype(np.float32), px[..., 1].astype(np.float32)
    patch = tx.cv2.remap(img, mx, my, tx.cv2.INTER_LINEAR, borderMode=tx.cv2.BORDER_CONSTANT)
    inside = (mx >= 0) & (mx <= cam["w"] - 1) & (my >= 0) & (my <= cam["h"] - 1) & (z > 0)
    wt = up * blend.border_ramp(mx, my, cam["w"], cam["h"], p["borderRampPx"]) * inside
    acc.add(c, y0, x0, patch, wt)


def _sample_gain_cells(img, cam, c, jobs):
    small = blend.shrink(img, GAIN_DOWNSCALE)
    for j in jobs:
        seen = j["S"][c] > 0
        if seen.any():
            col = blend.sample_cells(small, GAIN_DOWNSCALE, cam, j["X"], tx.project)
            col[~seen] = np.nan
            j["cells"][c] = col


def render(doc, load_photo, cams, names, facets, p, progress):
    jobs = _prepare(facets, cams, names, p)
    balance = bool(p["exposureBalance"])
    for c, n in enumerate(names):
        if progress:
            progress(0.9 * c / len(names), f"rendering from {n}")
        if not any((j["acc"].W[c] > 0).any() or (balance and (j["S"][c] > 0).any()) for j in jobs):
            continue
        cam = cams[n]
        img = tx._checked_photo(load_photo, n, cam)
        if balance:
            _sample_gain_cells(img, cam, c, jobs)
        for j in jobs:
            _render_slots(img, cam, c, j, p)
        del img
    gains, ref, npairs = np.ones((len(names), 3)), None, 0
    if balance:
        gains, ref, npairs = exposure.fit_gains([j["cells"] for j in jobs], int(p["gainMinOverlapCells"]))
    if progress:
        progress(0.92, "blending")
    report = _gain_report(gains, ref, npairs, names)
    return [_result(doc, j, gains, names, p, _label(j, gains, names, p)) | {"exposure": report} for j in jobs]


def _label(j, gains, names, p):
    """Consensus-penalised single-photo choice (full-res photo index map), or None in blend mode."""
    if p["blendMode"] != "select":
        return None
    S = consensus.penalised_scores(j["S"], j["cells"], gains, p) if p["exposureBalance"] else j["S"]
    return tx._labels(S, j["g"], names, {**p, "modeFilterCells": p["selectModeFilterCells"]})


def _result(doc, j, gains, names, p, label):
    out, filled, kept = blend.finish(j["acc"], gains, p, label)
    g = j["g"]
    tot = kept.sum()
    used = {names[k]: round(float(kept[k] / tot), 4) for k in range(len(names)) if tot > 0 and kept[k] > 0}
    return {"facet": j["f"]["id"], "image": out, "mask": tx.coverage_mask(filled, p["maskFeatherPx"]),
            "mmPerPx": g["res"], "bounds": g["bounds"], "widthPx": g["W"], "heightPx": g["H"],
            "photosUsed": used, "coverage": round(float(filled.mean()), 4),
            "markerCheck": tx.marker_check(out, j["f"], doc, g["res"], g)}


def _gain_report(gains, ref, npairs, names):
    """Small summary for the manifest (gains as RGB)."""
    return {"reference": None if ref is None else names[ref], "pairs": npairs,
            "gainMin": [round(float(v), 3) for v in gains.min(0)[::-1]],
            "gainMax": [round(float(v), 3) for v in gains.max(0)[::-1]]}
