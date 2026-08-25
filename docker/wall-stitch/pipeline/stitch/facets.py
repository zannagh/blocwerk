#!/usr/bin/env python3
"""Rectify every discovered plane, not just the first one.

`auto_planes.discover` returns a ranked list of planes.  Historically only two were
ever delivered - `planes[0]` as the main span and `planes[1]` as the kickboard - and
anything else the wall happened to be made of was found, accepted, ranked and then
dropped.  On the reference wall that silently discarded the steeper right-hand panel,
which discovery had recovered from photos 4 and 5 with 261 correspondences.

This module rectifies each accepted plane the same way `stitch_planes.kickboard` does
the kickboard, and records enough of its pose for `wall_layout` to place the facets
next to one another afterwards:

* the plane's unit normal in the reference camera,
* the rectifying frame R (columns dx, dy, dz in reference-camera coordinates), whose
  dx is the **main span's** in-plane horizontal.  Sharing that axis is what makes the
  facets rotationally consistent with one another rather than each being level only
  against itself,
* the metric scale, so a canvas pixel means the same distance on every facet.
"""
import os

import cv2
import numpy as np

import plane_support as PS
import stitch_planes as SP
import stitch_wall as S


# A facet whose rectified extent exceeds this at unit scale is being viewed so close to
# edge-on that its own plane's horizon is inside the frame.  Both limits are deliberately
# loose: they exist to stop a degenerate surface allocating an unbounded canvas, not to
# make a resolution judgement.
MAX_UNIT_EXTENT = 40000.0
MAX_ASPECT = 60.0
MAX_FACET_MPX = 40.0

# Grow the correspondence-support markers by this many grid cells before intersecting
# with the plane mask.  It has to bridge the gaps between holds on a real panel (a few
# cells at plane_support.CELL) without reaching across into an adjacent surface.
SUPPORT_BRIDGE_CELLS = 3
# Drop a restricted mask blob carrying fewer than this many correspondence cells: on a
# small facet the flood leaks a stray blob or two onto the floor, each vouched for by a
# single spurious match, and those are exactly the junk regions to shed.
MIN_BLOB_SUPPORT_CELLS = 4


def _support_restricted_masks(plane, images):
    """Clip each of the plane's masks to the region its own correspondences vouch for.

    `plane_support.build_mask` floods a plane's mask outward from its support through
    every cell whose warp photometrically agrees.  For a large, well-sampled surface
    that is what fills the mask; but for a small panel sitting against low-texture
    clutter (an attic floor, a roof soffit, a fingerboard post) the flood leaks across
    the join, because a featureless surface warps onto itself without disagreeing.  The
    correspondences never follow - they stay on the panel - so keep only the mask blob(s)
    the support actually reaches, grown just far enough to close the gaps between holds.
    """
    out = {}
    for n in images:
        grid = plane.grid[n] > 0
        sup = PS.support_counts(plane.points(n), grid.shape) > 0
        sup &= grid
        k = np.ones((2 * SUPPORT_BRIDGE_CELLS + 1, 2 * SUPPORT_BRIDGE_CELLS + 1), np.uint8)
        grown = (cv2.dilate(sup.astype(np.uint8), k) & grid.astype(np.uint8)) > 0
        num, lab = cv2.connectedComponents(grown.astype(np.uint8))
        keep = np.zeros_like(grown)
        for c in range(1, num):
            if int(sup[lab == c].sum()) >= MIN_BLOB_SUPPORT_CELLS:
                keep |= (lab == c)
        if not keep.any():                       # degenerate: fall back to the full mask
            out[n] = plane.masks[n]
        else:
            out[n] = PS.to_full(keep, plane.masks[n].shape)
    return out


class Facet:
    """One rectified surface, plus where it sits relative to the main span."""

    def __init__(self, key, rank, image, normal, R, scale, images):
        self.key = key                 # "main-span", "facet-2", ...
        self.rank = rank               # discovery rank
        self.image = image             # BGR, fronto-parallel to this surface
        self.normal = np.asarray(normal, float)
        self.R = np.asarray(R, float)  # rectifying frame in reference-camera coords
        self.scale = float(scale)      # canvas px per unit at the reference depth
        self.images = list(images)     # which photos contributed

    @property
    def size(self):
        return self.image.shape[1], self.image.shape[0]

    def pose_report(self, span_R):
        """Orientation relative to the main span, in degrees."""
        W = span_R.T @ self.R                       # facet axes in span coordinates
        dz = W[:, 2]
        return dict(
            images=self.images, rank=self.rank,
            width=int(self.image.shape[1]), height=int(self.image.shape[0]),
            scale=self.scale,
            yaw_vs_span_deg=float(np.degrees(np.arctan2(dz[0], dz[2]))),
            tilt_vs_span_deg=float(np.degrees(np.arcsin(np.clip(dz[1], -1, 1)))),
            dihedral_to_span_deg=float(np.degrees(np.arccos(np.clip(abs(dz[2]), -1, 1)))),
        )


def _register(plane, K, log):
    """Chain the plane's own pairwise homographies onto one reference frame.

    Returns (Hs, ref, images) or None when the plane's registration graph does not
    connect - which is not a failure, only a surface too weakly seen to place.
    """
    images = [n for n in plane.images if n in plane.masks]
    if len(images) < 2:
        return None
    pairs = [k for k in plane.H if k[0] in images and k[1] in images]
    if not pairs:
        return None
    # the frame that shares the most pairs makes the shallowest chain
    ref = max(images, key=lambda n: sum(1 for k in pairs if n in k))
    Hs = S.chain_to_ref({k: plane.H[k] for k in pairs}, ref, images)
    if len(Hs) < len(images):
        # keep whatever connected component the reference belongs to
        images = sorted(Hs)
        if len(images) < 2:
            return None
    corr = {k: plane.corr[k] for k in pairs if k[0] in images and k[1] in images}
    if corr:
        Hs, _ = S.refine(Hs, corr, ref, images)
    return Hs, ref, images


def rectify(plane, ctx, key, h_ref, percentile, log):
    """Rectify and composite one discovered plane.  None when it cannot be placed."""
    K, work, report = ctx["K"], ctx["work"], ctx["report"]
    imgs = ctx["imgs"]
    got = _register(plane, K, log)
    if got is None:
        log("%s: registration graph does not connect, skipped" % key)
        return None
    Hs, ref, images = got

    others = [n for n in images if n != ref]
    if not others:
        return None
    normal, spread = SP.plane_normal_from_pairs(Hs, K, ref, others,
                                                ref_points=plane.points(ref))

    # dx from the main span's horizontal, projected into this plane.  When the surface
    # is close to edge-on to that direction the projection is ill-conditioned, so fall
    # back to the plane's own best in-plane approximation of it.
    if abs(float(h_ref @ normal)) > 0.98:
        log("%s: main-span horizontal is almost normal to this surface; "
            "using its own frame" % key)
        R = SP.frame_from(normal, np.cross(normal, np.array([0.0, 1.0, 0.0])))
    else:
        R = SP.frame_from(normal, h_ref)

    # Clip the flood-filled masks back to what the correspondences vouch for, so a small
    # panel's rectification is not swamped by attic floor / roof / post the flood leaked
    # onto (see _support_restricted_masks).  The normal above is already clean - it comes
    # from the correspondences, not the mask - so this only bounds the extent and the
    # composited pixels.
    masks = _support_restricted_masks(plane, images)
    Htot = {n: K @ R.T @ np.linalg.inv(K) @ Hs[n] for n in images}
    extent = {n: S.mask_outline(masks[n]) for n in images}
    corners = np.concatenate([
        cv2.perspectiveTransform(extent[n].reshape(-1, 1, 2), Htot[n]).reshape(-1, 2)
        for n in images])

    # Guard the unit-scale extent BEFORE anything is sized from it.  A surface seen at a
    # grazing angle rectifies to an enormous canvas - the horizon of its own plane runs
    # off to infinity - and `scale_for` would then build a density grid over that whole
    # extent and hang.  A facet this foreshortened carries no recoverable detail anyway.
    if not np.all(np.isfinite(corners)):
        log("%s: rectified extent is not finite (surface is edge-on), skipped" % key)
        return None
    span = corners.max(0) - corners.min(0)
    aspect = float(max(span) / max(min(span), 1e-9))
    if max(span) > MAX_UNIT_EXTENT or aspect > MAX_ASPECT:
        log("%s: rectifies to %.0fx%.0f at unit scale (aspect %.0f:1) - too foreshortened "
            "to deliver, skipped" % (key, span[0], span[1], aspect))
        report.setdefault("facets", {}).setdefault(key, {}).update(
            skipped=True, reason="seen too close to edge-on to rectify",
            unit_extent=[float(span[0]), float(span[1])], aspect=aspect)
        return None

    med, lo, hi, rstats = SP.scale_for(Htot, masks, images, corners, percentile)
    # ... and cap the pixel canvas the same way the main span is capped.
    mpx = (hi[0] - lo[0]) * med * (hi[1] - lo[1]) * med / 1e6
    if mpx > MAX_FACET_MPX:
        factor = float(np.sqrt(MAX_FACET_MPX / mpx))
        log("%s: canvas would be %.1f Mpx, scaling down by %.3f" % (key, mpx, factor))
        med *= factor
    Sm = np.array([[med, 0, -med * lo[0]], [0, med, -med * lo[1]], [0, 0, 1.0]])
    Htot = {n: Sm @ Htot[n] for n in images}
    Wc = int(np.ceil((hi[0] - lo[0]) * med))
    Hc = int(np.ceil((hi[1] - lo[1]) * med))
    if Wc < 16 or Hc < 16:
        log("%s: rectifies to %dx%d, too small to deliver" % (key, Wc, Hc))
        return None
    log("%s: canvas %dx%d at scale %.3f from %s" % (key, Wc, Hc, med, images))

    tag = key + "-"
    res, res_mask = S.composite(imgs, masks, images, Htot, Wc, Hc, work, report, tag)
    ys, xs = np.nonzero(res_mask)
    if not len(ys):
        return None
    res = res[ys.min():ys.max() + 1, xs.min():xs.max() + 1].copy()

    rep = report.setdefault("facets", {}).setdefault(key, {})
    rep["resolution"] = rstats
    rep["plane_normal_ref_cam"] = [float(x) for x in normal]
    rep["plane_normal_spread_deg"] = spread
    rep["registered_from"] = dict(reference=ref, pairs=["%s-%s" % k for k in sorted(
        k for k in plane.H if k[0] in images and k[1] in images)])
    return Facet(key, plane.rank, res, normal, R, med, images)


def rectify_all(planes, ctx, span_facet, percentile, log, skip_ranks=()):
    """Every accepted plane except the ones already delivered by name."""
    h_ref = np.asarray(ctx["R"])[:, 0]
    out = []
    for p in planes:
        if p.rank in skip_ranks:
            continue
        f = rectify(p, ctx, "facet-%d" % p.rank, h_ref, percentile, log)
        if f is not None:
            out.append(f)
    span_R = np.asarray(span_facet.R)
    rep = ctx["report"].setdefault("facets", {})
    for f in out:
        rep.setdefault(f.key, {}).update(f.pose_report(span_R))
        log("%s: %dx%d, yaw %.1f deg, tilt %.1f deg vs the main span"
            % (f.key, f.image.shape[1], f.image.shape[0],
               rep[f.key]["yaw_vs_span_deg"], rep[f.key]["tilt_vs_span_deg"]))
    return out
