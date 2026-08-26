"""Metric rectification of the mosaic plane.

Vendored from new-run/exp3/rectify.py, plus the driver (`solve`) that the R&D tree
only ever ran by hand: build a coarse unrectified plane, recover the rectifying
homography from it, and normalise the result so it stays in work-pixel scale.

The mosaic inherits the reference frame's tilt, so plywood board seams and panel
joints converge. Recover the two vanishing points from those line families, send
their vanishing line to infinity, then square up the two directions.
"""
import cv2
import numpy as np

from . import compose
from .util import percentile_roi


def segments(gray, min_len_frac=0.02):
    lsd = cv2.createLineSegmentDetector()
    lines = lsd.detect(gray)[0]
    if lines is None:
        return np.zeros((0, 4), np.float32)
    lines = lines.reshape(-1, 4)
    d = np.hypot(lines[:, 2] - lines[:, 0], lines[:, 3] - lines[:, 1])
    return lines[d > min_len_frac * max(gray.shape)]


def vanishing_point(lines):
    """Smallest right singular vector of the stacked line coefficients."""
    p1 = np.c_[lines[:, :2], np.ones(len(lines))]
    p2 = np.c_[lines[:, 2:], np.ones(len(lines))]
    coef = np.cross(p1, p2)
    coef /= np.linalg.norm(coef[:, :2], axis=1, keepdims=True) + 1e-12
    # IRLS: long, well-fitting lines dominate; strays get down-weighted
    length = np.hypot(lines[:, 2] - lines[:, 0], lines[:, 3] - lines[:, 1])
    w = length
    v = None
    for _ in range(12):
        v = np.linalg.svd(coef * w[:, None], full_matrices=False)[2][-1]
        r = np.abs(coef @ v)
        s = np.median(r) + 1e-9
        w = length / (1 + (r / (2.5 * s)) ** 2)
    return v / (np.linalg.norm(v) + 1e-12), w


def split_families(lines, h_tol=30.0, v_tol=30.0):
    ang = np.degrees(np.arctan2(lines[:, 3] - lines[:, 1], lines[:, 2] - lines[:, 0]))
    ang = (ang + 90) % 180 - 90        # -90..90
    return lines[np.abs(ang) < h_tol], lines[np.abs(np.abs(ang) - 90) < v_tol]


def rectifying_homography(gray, aspect=None, roi=None, min_len_frac=0.005, log=print):
    g = gray if roi is None else gray[roi[1]:roi[3], roi[0]:roi[2]]
    segs = segments(g, min_len_frac)
    if roi is not None:
        segs = segs + np.array([roi[0], roi[1], roi[0], roi[1]], np.float32)
    horiz, vert = split_families(segs)
    log('segments %d -> horiz %d vert %d' % (len(segs), len(horiz), len(vert)))
    if len(horiz) < 8 or len(vert) < 8:
        log('WARNING too few line segments to rectify; leaving the plane as-is')
        return np.eye(3), (horiz, vert)
    vh, _ = vanishing_point(horiz)
    vv, _ = vanishing_point(vert)

    linf = np.cross(vh, vv)
    linf = linf / linf[2] if abs(linf[2]) > 1e-12 else linf
    hp = np.array([[1, 0, 0], [0, 1, 0], linf], np.float64)

    # After affine rectification the two families are parallel pencils; map their
    # directions onto the x and y axes.
    dh = hp @ vh
    dv = hp @ vv
    dh = dh[:2] / (np.linalg.norm(dh[:2]) + 1e-12)
    dv = dv[:2] / (np.linalg.norm(dv[:2]) + 1e-12)
    if dh[0] < 0:
        dh = -dh
    if dv[1] < 0:
        dv = -dv
    ha = np.eye(3)
    ha[:2, :2] = np.linalg.inv(np.array([[dh[0], dv[0]], [dh[1], dv[1]]]))
    h = ha @ hp
    if aspect is not None:
        h = np.diag([1.0, 1.0 / aspect, 1.0]) @ h
    return h / h[2, 2], (horiz, vert)


def plausible(rect, w, h, max_aspect_change=2.5, max_area_change=6.0):
    """Sanity-gate a candidate rectification before trusting it.

    A degenerate vanishing-point estimate -- the usual failure on a horizontally
    planked wall, where the "vertical" family is hold edges rather than panel joints
    -- produces a homography that folds or wildly stretches the frame. Both show up as
    an extreme change in the frame quad's aspect or area.
    """
    q = np.array([[[0, 0]], [[w, 0]], [[w, h]], [[0, h]]], np.float32)
    p = cv2.perspectiveTransform(q, rect).reshape(-1, 2)
    if not np.isfinite(p).all():
        return False
    side = [np.linalg.norm(p[(i + 1) % 4] - p[i]) for i in range(4)]
    if min(side) < 1e-6:
        return False
    x, y = p[:, 0], p[:, 1]
    area = abs(0.5 * (np.dot(x, np.roll(y, -1)) - np.dot(y, np.roll(x, -1))))
    aspect = (side[0] / side[1]) / (w / h)
    scale = np.sqrt(area / (w * h))
    return (1 / max_aspect_change <= aspect <= max_aspect_change
            and 1 / max_area_change <= scale <= max_area_change)


def normalise_scale(rect, ref_quad):
    """Keep the rectified plane at roughly work-pixel scale and positive orientation.

    The raw rectifying homography is only defined up to scale, and it routinely comes
    back scaled by 1e-3 or mirrored, which would make every downstream pixel budget
    meaningless. Pin it so the reference frame keeps its own area and handedness.
    """
    q = np.asarray(ref_quad, np.float32).reshape(-1, 1, 2)
    w = cv2.perspectiveTransform(q, rect).reshape(-1, 2)
    src = np.asarray(ref_quad, np.float64).reshape(-1, 2)

    def area(p):
        x, y = p[:, 0], p[:, 1]
        return 0.5 * (np.dot(x, np.roll(y, -1)) - np.dot(y, np.roll(x, -1)))

    a_src, a_dst = area(src), area(w)
    if abs(a_dst) < 1e-12:
        return rect
    s = np.sqrt(abs(a_src / a_dst))
    flip = -1.0 if a_src * a_dst < 0 else 1.0
    return np.diag([s * flip, s, 1.0]) @ rect


def solve(hs, wscale, paths, work_w, work_h, probe_mpx=12.0, aspect=None, log=print):
    """Auto-rectify: coarse plane -> vanishing points -> squared-up plane.

    Replaces the hand-run rectification of the R&D tree, which produced a one-off
    H-rect.npy for one wall. Returns (rect 3x3 in work pixels, roi in rectified work
    pixels, probe image).
    """
    raw_roi = percentile_roi(compose.frame_quads(hs, paths, work_w, work_h))
    rw, rh = raw_roi[2] - raw_roi[0], raw_roi[3] - raw_roi[1]
    k = float(min(1.0, np.sqrt(probe_mpx * 1e6 / max(rw * rh, 1.0))))
    log('probe plane roi (work px) %s at %.3f' % (np.round(raw_roi, 1).tolist(), k))
    probe = compose.build(hs, wscale, paths, wscale * k, roi=raw_roi,
                          max_mpx=probe_mpx * 4, log=log)[0]
    gray = cv2.cvtColor(probe, cv2.COLOR_BGR2GRAY)
    gray = cv2.createCLAHE(2.5, (16, 16)).apply(gray)
    rect_probe, _ = rectifying_homography(gray, aspect=aspect, log=log)
    if not plausible(rect_probe, probe.shape[1], probe.shape[0]):
        log('WARNING auto-rectification is degenerate (the vertical line family on a '
            'horizontally-planked wall is not a real parallel pencil); leaving the '
            'reference frame to define the plane. Pass --rect-matrix to supply one.')
        rect_probe = np.eye(3)

    # rectifying_homography works in probe pixels; conjugate it back into work pixels,
    # where every other homography in the pipeline lives. A work-plane point P lands at
    # probe pixel k*P - k*roi0.
    w2p = np.array([[k, 0, -k * raw_roi[0]], [0, k, -k * raw_roi[1]], [0, 0, 1]],
                   np.float64)
    rect = np.linalg.inv(w2p) @ rect_probe @ w2p
    rect = normalise_scale(rect / rect[2, 2],
                           [[0, 0], [work_w, 0], [work_w, work_h], [0, work_h]])
    rect = rect / rect[2, 2]

    rect_hs = {n: rect @ hs[n] for n in paths}
    roi = percentile_roi(compose.frame_quads(rect_hs, paths, work_w, work_h))
    log('rectified roi (work px) %s' % (np.round(roi, 1).tolist(),))
    return rect, roi, probe


def straightness(gray):
    """Angle statistics of the two line families -- the flatness check for a base."""
    segs = segments(gray, 0.03)
    horiz, vert = split_families(segs, 12.0, 12.0)
    # both families need the same +-90 wrap: a segment detected end-for-end comes back
    # at ~180 deg and would otherwise dominate the rms
    ah = np.degrees(np.arctan2(horiz[:, 3] - horiz[:, 1], horiz[:, 2] - horiz[:, 0]))
    ah = (ah + 90) % 180 - 90
    av = np.degrees(np.arctan2(vert[:, 3] - vert[:, 1], vert[:, 2] - vert[:, 0]))
    av = (av + 90) % 180 - 90
    return dict(n_horizontal=len(horiz), n_vertical=len(vert),
                horizontal_mean_deg=float(np.mean(ah)) if len(ah) else None,
                horizontal_rms_deg=float(np.sqrt(np.mean(ah ** 2))) if len(ah) else None,
                vertical_mean_deg=float(np.mean(av)) if len(av) else None,
                vertical_rms_deg=float(np.sqrt(np.mean(av ** 2))) if len(av) else None)
