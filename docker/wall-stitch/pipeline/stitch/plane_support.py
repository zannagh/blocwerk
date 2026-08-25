#!/usr/bin/env python3
"""Per-image masks for a discovered plane, grown from its own geometric support.

A plane is described by a set of homographies between pairs of the input frames
plus the full-resolution correspondences that support them.  This module turns
that into a boolean mask per frame: "these pixels lie on the plane".

The rule is deliberately conservative and has exactly one premise: a pixel may
only be claimed for the plane if some *other* view could have contradicted it.
Concretely the mask is the connected region grown from the plane's own inlier
support, restricted to the part of the frame that a partner view of the same
plane actually reaches, and blocked wherever a partner view reaches the pixel
and disagrees with it.

That premise is what makes the result trustworthy on an unknown upload, and it
is also the single place where automatic masking is weaker than hand-tracing:
material seen by one camera only is never claimed, because from one view a
climbing wall, the return panel it folds into and the crash mat below it are
all just planar-looking texture.

Deterministic: pure OpenCV/NumPy, no RNG.
"""
import cv2
import numpy as np

CELL = 32                # mask grid, in full-resolution pixels
NCC_WIN = 21             # NCC window at the coarse (half-resolution) scale
NCC_VAR_MIN = 25.0       # local intensity variance below which NCC means nothing
NCC_PIXEL = 0.60         # a pixel agrees with the plane above this NCC
NCC_AGREE = 0.50         # a cell agrees when this fraction of its opinionated px do
NEIGH = 3                # neighbourhood radius, in cells, for the verdict below
NEIGH_OPINION = 0.15     # a neighbourhood counts when this fraction of it has an opinion
NEIGH_AGREE = 0.20       # ... and it is on the plane when this fraction of that agrees
MIN_CELL_CONF = 0.15     # a cell has an opinion when this fraction of it does
CLOSE_CELLS = 6          # morphological close radius, in cells
MIN_COMPONENT = 20       # cells; smaller connected pieces are dropped
HULL_MAX_GROWTH = 1.6    # convex closing may tidy a boundary, not invent a region


def grid_shape(full_shape):
    h, w = full_shape
    return (h + CELL - 1) // CELL, (w + CELL - 1) // CELL


def cell_index(pts, gshape):
    """Map full-resolution points to (row, col) cell indices."""
    gh, gw = gshape
    p = np.asarray(pts, np.float64).reshape(-1, 2)
    jj = np.clip((p[:, 0] / CELL).astype(int), 0, gw - 1)
    ii = np.clip((p[:, 1] / CELL).astype(int), 0, gh - 1)
    return ii, jj


def support_counts(pts, gshape):
    g = np.zeros(gshape, np.int32)
    if len(pts):
        ii, jj = cell_index(pts, gshape)
        np.add.at(g, (ii, jj), 1)
    return g


def _windowed_ncc(a, b):
    """Zero-mean windowed NCC of two float32 images, gated on local texture.

    Where either window is flat the correlation is a ratio of two noise floors
    and says nothing, so it comes back as -1 ("no evidence") rather than as a
    number that happens to be right half the time.
    """
    k = (NCC_WIN, NCC_WIN)
    ma, mb = cv2.boxFilter(a, -1, k), cv2.boxFilter(b, -1, k)
    va = cv2.boxFilter(a * a, -1, k) - ma * ma
    vb = cv2.boxFilter(b * b, -1, k) - mb * mb
    cab = cv2.boxFilter(a * b, -1, k) - ma * mb
    s = cab / np.sqrt(np.maximum(va, 1e-6) * np.maximum(vb, 1e-6))
    s[np.minimum(va, vb) < NCC_VAR_MIN] = -1.0
    return s


def agreement(gray_src, gray_dst, H_src_to_dst, gshape, coarse):
    """Grid verdict of "does this plane explain what is here?", in dst's frame.

    `gray_*` are coarse-scale float32 greys and `H_src_to_dst` is the plane's
    full-resolution homography.  Returns (score, reach): `score` is the fraction
    of opinionated pixels in the cell that agree, or -1 where the cell holds no
    opinion; `reach` says the partner view covers the cell at all.
    """
    gh, gw = gshape
    h, w = gray_dst.shape
    Sc = np.diag([coarse, coarse, 1.0])
    H = Sc @ H_src_to_dst @ np.linalg.inv(Sc)
    warped = cv2.warpPerspective(gray_src, H, (w, h), flags=cv2.INTER_LINEAR, borderValue=0)
    valid = cv2.warpPerspective(np.ones_like(gray_src), H, (w, h),
                                flags=cv2.INTER_NEAREST, borderValue=0) > 0.5
    valid = cv2.erode(valid.astype(np.uint8), np.ones((NCC_WIN, NCC_WIN), np.uint8)) > 0
    s = _windowed_ncc(warped, gray_dst)
    opinion = valid & (s > -1.0)
    agree = opinion & (s > NCC_PIXEL)
    conf = cv2.resize(opinion.astype(np.float32), (gw, gh), interpolation=cv2.INTER_AREA)
    agr = cv2.resize(agree.astype(np.float32), (gw, gh), interpolation=cv2.INTER_AREA)
    reach = cv2.resize(valid.astype(np.float32), (gw, gh), interpolation=cv2.INTER_AREA) > 0.5
    score = np.where(conf >= MIN_CELL_CONF, agr / np.maximum(conf, 1e-6), -1.0)
    return score.astype(np.float32), reach


def _fill_holes(g):
    h, w = g.shape
    canvas = np.zeros((h + 2, w + 2), np.uint8)
    canvas[1:-1, 1:-1] = g.astype(np.uint8)
    cv2.floodFill(canvas, np.zeros((h + 4, w + 4), np.uint8), (0, 0), 2)
    out = g.copy()
    out[canvas[1:-1, 1:-1] == 0] = True
    return out


def _span_fill(g, opinion):
    """Claim cells nobody could verify that lie *between* claimed cells on a row.

    A frame's own top corner is often outside every partner view, so the mask
    would otherwise be notched wherever the partners' coverage parts around it.
    Only cells with no opinion at all are filled, so this never overrides
    evidence, and the span is bounded by the mask itself, so it never reaches
    past the surface.
    """
    out = g.copy()
    for i in range(g.shape[0]):
        on = np.flatnonzero(g[i])
        if len(on) < 2:
            continue
        lo, hi = on[0], on[-1]
        out[i, lo:hi + 1] |= ~opinion[i, lo:hi + 1]
    return out


def build_mask(sup, scores, reaches, blocked=None):
    """Grid mask for one frame.

    `sup` counts the plane's inliers per cell; `scores` / `reaches` are one
    `agreement()` result per partner view of the plane; `blocked` marks cells a
    different plane's own inliers claim.

    The verdict is taken over a small neighbourhood rather than per cell.  Cell
    by cell the evidence is salt and pepper - a 1 px misregistration destroys the
    correlation on a hold's edge while leaving its neighbour intact - but over a
    5x5 window the surfaces separate cleanly: on the wall a quarter to a half of
    the opinionated cells agree, on the ceiling beam essentially none do, and a
    crash mat is a wide expanse of grey that holds no opinion at all and so is
    never claimed.  The result is closed, largest-component filtered and
    hole-filled, which is what carries the mask across bare plywood.
    """
    gshape = sup.shape
    seed = sup > 0
    if not seed.any():
        return np.zeros(gshape, bool)
    reach = np.zeros(gshape, bool)
    best = np.full(gshape, -1.0, np.float32)
    for s, r in zip(scores, reaches):
        reach |= r
        best = np.maximum(best, s)
    opinion = reach & (best >= 0.0)
    box = (2 * NEIGH + 1, 2 * NEIGH + 1)
    den = cv2.boxFilter(opinion.astype(np.float32), -1, box)
    num = cv2.boxFilter((opinion & (best >= NCC_AGREE)).astype(np.float32), -1, box)
    verdict = (den >= NEIGH_OPINION) & (num / np.maximum(den, 1e-6) >= NEIGH_AGREE)
    core = seed | verdict
    if blocked is not None:
        core &= ~blocked
    k = np.ones((2 * CLOSE_CELLS + 1, 2 * CLOSE_CELLS + 1), np.uint8)
    region = cv2.morphologyEx(core.astype(np.uint8), cv2.MORPH_CLOSE, k) > 0
    region &= ~(opinion & ~verdict) | seed
    if blocked is not None:
        region &= ~blocked | seed
    nlab, lab, stats, _ = cv2.connectedComponentsWithStats(region.astype(np.uint8), 8)
    keep = np.zeros(gshape, bool)
    for i in range(1, nlab):
        comp = lab == i
        if (comp & seed).any() and stats[i, cv2.CC_STAT_AREA] >= MIN_COMPONENT:
            keep |= comp
    if not keep.any():
        return keep
    keep = _fill_holes(keep)
    # A plane's visible region is convex in the image unless something occludes
    # it, and the occluders are precisely what the evidence already marks: cells
    # a partner view contradicts, and cells another plane's own inliers claim.
    # Taking the convex hull and cutting those back out squares up the boundary
    # that the cell grid and the salt-and-pepper correlation leave ragged, which
    # is what the downstream largest-filled-rectangle crop is sensitive to.
    pts = np.column_stack(np.nonzero(keep))[:, ::-1].astype(np.int32)
    hull = np.zeros(gshape, np.uint8)
    cv2.fillConvexPoly(hull, cv2.convexHull(pts), 1)
    grown = (hull > 0) & ~(opinion & ~verdict)
    if blocked is not None:
        grown &= ~blocked
    nlab, lab, _st, _c = cv2.connectedComponentsWithStats(grown.astype(np.uint8), 8)
    out = np.zeros(gshape, bool)
    for i in range(1, nlab):
        comp = lab == i
        if (comp & keep).sum() >= 0.25 * keep.sum():
            out |= comp
    out |= keep
    if out.sum() > HULL_MAX_GROWTH * keep.sum():
        out = keep          # tidy-up only: a hull that big is a different shape
    return _span_fill(_fill_holes(out), opinion)


def resolve_overlaps(masks_by_plane, sup_by_plane):
    """Give a contested cell to the plane whose inliers dominate around it."""
    if len(masks_by_plane) < 2:
        return masks_by_plane
    k = np.ones((9, 9), np.float32)
    dens = [cv2.filter2D(s.astype(np.float32), -1, k) for s in sup_by_plane]
    stack = np.stack(dens)
    win = stack.argmax(0)
    tie = stack.max(0) <= 0
    out = []
    for i, m in enumerate(masks_by_plane):
        contested = m & (np.sum(np.stack(masks_by_plane), 0) > 1)
        keep = m & (~contested | (win == i) | tie)
        out.append(keep)
    return out


def to_full(g, full_shape):
    h, w = full_shape
    return cv2.resize(g.astype(np.uint8) * 255, (w, h), interpolation=cv2.INTER_NEAREST)


def outline(mask_full):
    """Outer contour of a full-resolution mask, as an Nx2 float array."""
    cnts, _ = cv2.findContours((mask_full > 0).astype(np.uint8), cv2.RETR_EXTERNAL,
                               cv2.CHAIN_APPROX_SIMPLE)
    if not cnts:
        return np.zeros((0, 2), np.float64)
    c = max(cnts, key=cv2.contourArea)
    return c.reshape(-1, 2).astype(np.float64)
