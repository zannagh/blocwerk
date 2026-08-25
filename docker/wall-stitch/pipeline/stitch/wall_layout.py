#!/usr/bin/env python3
"""Composite every rectified wall surface into the two delivered masters.

Phase 2 recovers a wall as several independently rectified surfaces - the main
span, the kickboard, the right corner facet and the left return - each at its own
density scale and each levelled only against the main span's horizontal.  This
module places them into the two images the app already stores, with no change to
the wire contract (`StitchJobResult` still has exactly `ortho` and `angled`):

* the ORTHO master (`wall-orthophoto.png`) - every surface developed FLAT and laid
  out side by side at ONE unified scale, adjacent along their shared creases.  This
  is the recognition image and the "Flat" view.
* the ANGLED master (`wall-orthophoto-angled.png`) - every surface first put through
  its standing-viewer projection M and then placed to read as if the viewer stood on
  the mat: the span foreshortened vertically, the returns fanning away at the sides.

Standing-viewer projection.  The span's rectifying frame gives the in-plane
horizontal e_r (span x); the wall angle theta gives the true-down e_d.  A surface
whose axes expressed in the span basis are W = span_R^T @ surface_R projects by the
2x2

    M = [[dx . e_r, dy . e_r],
         [dx . e_d, dy . e_d]]        dx, dy = W[:, 0], W[:, 1]

    e_r = (1, 0, 0)        e_d = (0, cos theta, -sin theta)   in span coordinates

which reduces to the two documented special cases: the span (W = I) gives
[[1, 0], [0, cos theta]] - a vertical squash by cos theta - and a return yawed phi
about vertical gives [[cos phi, 0], [0, 1]] - a horizontal squash by cos phi.

Coordinate conventions written to report.json (a point maps ortho -> angled by)

    angled_pt = angled_origin + M @ (ortho_pt - ortho_origin)

where per surface `ortho_origin` / `angled_origin` are the master pixel at that
surface's native (0, 0), and M is the 2x2 above.  The unified rescale factor is the
same in both masters, so it cancels and does not appear in the mapping.
"""
import os

import cv2
import numpy as np


def projection(W, theta):
    """The standing-viewer 2x2 for a surface whose axes in span coords are W."""
    c, s = float(np.cos(theta)), float(np.sin(theta))
    e_r = np.array([1.0, 0.0, 0.0])
    e_d = np.array([0.0, c, -s])
    dx, dy = W[:, 0], W[:, 1]
    return np.array([[dx @ e_r, dy @ e_r],
                     [dx @ e_d, dy @ e_d]])


def yaw_tilt(W):
    """(yaw, tilt) of a surface in degrees, from its normal in the span basis."""
    dz = W[:, 2]
    return (float(np.degrees(np.arctan2(dz[0], dz[2]))),
            float(np.degrees(np.arcsin(np.clip(dz[1], -1, 1)))))


def _content_mask(img):
    return (img.max(axis=2) > 0).astype(np.uint8)


def _upright(img, W, theta):
    """Reparametrise a surface so its image axes run right (e_r) and down (e_d).

    The producers do not all choose the same in-plane handedness: the left return,
    for one, is rectified with its y-axis pointing up.  Flipping the pixels and
    negating the matching frame column is a null operation on the physical surface,
    but it keeps every facet un-mirrored and the right way up under M.
    """
    M = projection(W, theta)
    W = W.copy()
    if M[0, 0] < 0:                              # image x runs against e_r -> mirror it
        img = img[:, ::-1].copy(); W[:, 0] = -W[:, 0]
    if M[1, 1] < 0:                              # image y runs against e_d -> flip it
        img = img[::-1, :].copy(); W[:, 1] = -W[:, 1]
    return img, W


def _warp(img, L):
    """Warp `img` by the 2x2 linear map L about the origin, cropped to its content.

    Returns (warped_bgr, warped_mask, m) where a native point p lands at L @ p - m
    in the returned image; m already folds in the crop to the content bounding box,
    so black margins do not push adjacent surfaces apart at their shared crease.
    """
    h, w = img.shape[:2]
    corners = np.array([[0, 0], [w, 0], [w, h], [0, h]], float).T
    out = L @ corners
    m = out.min(1)
    A = np.array([[L[0, 0], L[0, 1], -m[0]],
                  [L[1, 0], L[1, 1], -m[1]]])
    ow = max(1, int(np.ceil(out[0].max() - m[0])))
    oh = max(1, int(np.ceil(out[1].max() - m[1])))
    warped = cv2.warpAffine(img, A, (ow, oh), flags=cv2.INTER_LANCZOS4)
    mask = cv2.warpAffine(_content_mask(img) * 255, A, (ow, oh), flags=cv2.INTER_NEAREST)
    ys, xs = np.nonzero(mask)
    if len(ys):
        y0, y1, x0, x1 = int(ys.min()), int(ys.max()) + 1, int(xs.min()), int(xs.max()) + 1
        warped, mask = warped[y0:y1, x0:x1], mask[y0:y1, x0:x1]
        m = m + np.array([x0, y0], float)
    return warped, mask, m


def _positions(order, sizes, attaches):
    """Top-left of each surface relative to the anchor at (0, 0).

    Sides abut the anchor's edges and stack outward if more than one shares a side;
    everything is top-aligned except the bottom strip, which centres under the span.
    """
    anchor = next(n for n in order if attaches[n] == "anchor")
    aw, ah = sizes[anchor]
    pos = {anchor: (0, 0)}
    right, left, bottom = aw, 0, ah
    for n in order:
        if n == anchor:
            continue
        w, h = sizes[n]
        a = attaches[n]
        if a == "right":
            pos[n] = (right, 0); right += w
        elif a == "left":
            left -= w; pos[n] = (left, 0)
        elif a == "bottom":
            pos[n] = ((aw - w) // 2, bottom); bottom += h
        else:                                   # unknown: drop to the right, harmless
            pos[n] = (right, 0); right += w
    return pos


def _paint(canvas, warped, mask, off):
    """Copy `warped` (where `mask`) onto `canvas` at integer offset `off`, clipped."""
    x0, y0 = int(off[0]), int(off[1])
    H, W = canvas.shape[:2]
    h, w = warped.shape[:2]
    sx0, sy0 = max(0, -x0), max(0, -y0)
    dx0, dy0 = max(0, x0), max(0, y0)
    ww, hh = min(w - sx0, W - dx0), min(h - sy0, H - dy0)
    if ww <= 0 or hh <= 0:
        return
    sub = warped[sy0:sy0 + hh, sx0:sx0 + ww]
    m = mask[sy0:sy0 + hh, sx0:sx0 + ww] > 0
    dst = canvas[dy0:dy0 + hh, dx0:dx0 + ww]
    dst[m] = sub[m]


def compose(surfaces, theta, unified_scale, mode):
    """Lay every surface out in one master. mode is 'ortho' (flat) or 'angled'.

    Returns (canvas_bgr, meta) where meta[name] carries the placement of that surface
    in this master: `origin` (master pixel at the surface's native 0,0), `bbox`,
    and (angled only) the 2x2 `M`.
    """
    warps, sizes, attaches, order = {}, {}, {}, []
    for s in surfaces:
        f = unified_scale / s["scale"]
        L = f * np.eye(2) if mode == "ortho" else f * projection(s["W"], theta)
        img, mask, m = _warp(s["image"], L)
        warps[s["name"]] = (img, mask, m, L)
        sizes[s["name"]] = (img.shape[1], img.shape[0])
        attaches[s["name"]] = s["attach"]
        order.append(s["name"])

    pos = _positions(order, sizes, attaches)
    xs = [pos[n][0] for n in order] + [pos[n][0] + sizes[n][0] for n in order]
    ys = [pos[n][1] for n in order] + [pos[n][1] + sizes[n][1] for n in order]
    gx, gy = min(xs), min(ys)
    W = int(max(xs) - gx); H = int(max(ys) - gy)
    canvas = np.zeros((H, W, 3), np.uint8)

    meta = {}
    # Paint the sides first and the anchor (span) last, so the span's own edges win
    # at every seam rather than being overwritten by a neighbour's black border.
    for n in sorted(order, key=lambda k: attaches[k] == "anchor"):
        img, mask, m, L = warps[n]
        off = (pos[n][0] - gx, pos[n][1] - gy)
        _paint(canvas, img, mask, off)
        origin = [float(off[0] - m[0]), float(off[1] - m[1])]
        entry = dict(origin=origin,
                     bbox=[int(off[0]), int(off[1]), sizes[n][0], sizes[n][1]])
        meta[n] = entry
    return canvas, meta


def _placements(surfaces, theta, s0, om, am):
    """Per-surface placement metadata for report.json (Phase 5 consumes this)."""
    out = {}
    for s in surfaces:
        n = s["name"]
        M = projection(s["W"], theta)
        yaw, tilt = yaw_tilt(s["W"])
        out[n] = dict(
            attach=s["attach"], native_scale=float(s["scale"]),
            unified_scale=s0, rescale_factor=float(s0 / s["scale"]),
            yaw_vs_span_deg=yaw, tilt_vs_span_deg=tilt,
            M=[[float(M[0, 0]), float(M[0, 1])], [float(M[1, 0]), float(M[1, 1])]],
            ortho=dict(origin=om[n]["origin"], bbox=om[n]["bbox"]),
            angled=dict(origin=am[n]["origin"], bbox=am[n]["bbox"]),
            well_constrained=bool(s.get("well_constrained", True)))
        if s.get("note"):
            out[n]["note"] = s["note"]
    return out


def build(surfaces, theta, report, work, png_compression, log=print):
    """Composite the ortho and angled masters and record the placement metadata.

    Overwrites `06-final/wall-orthophoto.png` and `-angled.png` in place, so the two
    composited masters flow into the app's existing `ortho` / `angled` artifact slots
    unchanged.  `surfaces` is a list of dicts: name, image (BGR), scale (px/unit), W
    (3x3 axes in span coords), attach ('anchor' | 'left' | 'right' | 'bottom').
    """
    # Upright every surface once, so its native coordinates are identical in both
    # masters (the ortho->angled mapping depends on that) and no facet is mirrored.
    surfaces = [dict(s, **dict(zip(("image", "W"),
                                   _upright(s["image"], s["W"], theta))))
                for s in surfaces]
    s0 = float(next(s["scale"] for s in surfaces if s["attach"] == "anchor"))
    ortho, om = compose(surfaces, theta, s0, "ortho")
    angled, am = compose(surfaces, theta, s0, "angled")

    fin = os.path.join(work, "06-final")
    cv2.imwrite(os.path.join(fin, "wall-orthophoto.png"), ortho,
                [cv2.IMWRITE_PNG_COMPRESSION, png_compression])
    cv2.imwrite(os.path.join(fin, "wall-orthophoto-angled.png"), angled,
                [cv2.IMWRITE_PNG_COMPRESSION, png_compression])

    report["layout"] = dict(
        unified_scale_px_per_unit=s0,
        wall_angle_deg=float(np.degrees(theta)),
        projection="M = [[dx.e_r, dy.e_r],[dx.e_d, dy.e_d]]; e_r=(1,0,0), "
                   "e_d=(0,cos t,-sin t) in span coords; dx,dy = span_R^T @ surface_R "
                   "columns 0,1",
        coordinate_convention=(
            "Both masters share each surface's flat (native px) coordinate.  In the "
            "ORTHO master the surface is placed at unified scale with M = I; in the "
            "ANGLED master it is placed through M.  The unified rescale is identical "
            "in both, so it cancels in the mapping below."),
        ortho_to_angled=("angled_pt = angled.origin + M @ (ortho_pt - ortho.origin), "
                         "per surface; origins are the master pixel at that surface's "
                         "native (0,0)."),
        ortho_master=dict(width=int(ortho.shape[1]), height=int(ortho.shape[0]),
                          file="wall-orthophoto.png"),
        angled_master=dict(width=int(angled.shape[1]), height=int(angled.shape[0]),
                           file="wall-orthophoto-angled.png"),
        surfaces=_placements(surfaces, theta, s0, om, am))
    log("layout: ortho master %dx%d, angled master %dx%d, %d surfaces (%s)"
        % (ortho.shape[1], ortho.shape[0], angled.shape[1], angled.shape[0],
           len(surfaces), ", ".join(s["name"] for s in surfaces)))
    return ortho, angled
