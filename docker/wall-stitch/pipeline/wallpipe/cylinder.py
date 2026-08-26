"""Stage 2: analytic vertical-axis cylindrical remap of the FLAT base.

Vendored from new-run/exp7/cylinder.py; the only edit is dropping its import of a
module-level logger, so this file has no path dependencies.

The base is already a fronto-parallel development of the wall, so the curve is a
pure image warp -- no camera model, no rotational warper, and therefore no bowl.
The map is separable in the source column u, which is what keeps every vertical
in the picture vertical:

    theta = u * 2 * theta_max                    u in [-0.5, 0.5]
    Xs    = sin(theta) / (1 - k cos(theta))      horizontal display position
    S     = (1 - k) / (1 - k cos(theta))         vertical scale about eye height

beta lerps the whole thing back towards the flat base for the gentler strengths.

NORMALISATION. exp3 rescaled Xs so the ends stayed at the frame edges, which magnified
the centre horizontally by 1.26x at the gentle setting while leaving the vertical scale
there at 1.0 -- a 26% anamorphic widening of the whole middle of the wall, i.e. the
"morphed to be wide" look. Normalise on the CENTRE derivative instead: dX/du = S = 1 at
theta = 0, so the centre is untouched and isotropic, and the ends foreshorten
horizontally, which is what a receding wall actually does. The picture therefore gets
NARROWER than the flat base, never wider: the natural is w * 2 * Xb[-1] wide.
"""
import cv2
import numpy as np


def geometry(theta_max_deg, wall_width_m, view_dist_m):
    """Treat the developed wall as an arc of that angular width, seen from
    view_dist_m in front of its nearest point."""
    tm = np.radians(theta_max_deg)
    r = wall_width_m / (2 * tm)
    d = r + view_dist_m
    k = r / d
    if k >= np.cos(tm):
        raise ValueError('k=%.3f must stay below cos(theta_max)=%.3f: the wall is too '
                         'wide or the viewer too close for this curvature'
                         % (k, np.cos(tm)))
    return tm, k, r


def profile(theta_max_deg, k, beta, samples=8193):
    """(u, Xb, Sb): source column, display position, vertical scale, over u in
    [-0.5, 0.5]. Xb is normalised so dXb/du = 1 at the centre."""
    tm = np.radians(theta_max_deg)
    u = np.linspace(-0.5, 0.5, samples)
    th = u * 2 * tm
    xs = np.sin(th) / (1 - k * np.cos(th)) * (1 - k) / (2 * tm)
    s = (1 - k) / (1 - k * np.cos(th))
    return u, (1 - beta) * u + beta * xs, (1 - beta) + beta * s


def maps(w, h, theta_max_deg, k, beta, eye_frac=0.5):
    """Inverse remap grids for a w x h flat base. The output is w * 2 * Xb[-1] wide."""
    u, xb, sb = profile(theta_max_deg, k, beta)
    e = float(xb[-1])
    w_out = max(2, int(round(w * 2 * e)))
    xs_out = ((np.arange(w_out) + 0.5) / w_out - 0.5) * 2 * e
    # xb is monotonic in u, so the inverse is a straight interpolation
    u_of_x = np.interp(xs_out, xb, u)
    s_of_x = np.interp(u_of_x, u, sb)

    yc = h * eye_frac
    src_x = (u_of_x + 0.5) * w
    ys = np.arange(h)[:, None].astype(np.float32)
    map_x = np.repeat(src_x[None, :].astype(np.float32), h, axis=0)
    map_y = (yc + (ys - yc) / s_of_x[None, :]).astype(np.float32)
    return map_x, map_y


def minification(w, theta_max_deg, k, beta):
    """Source pixels consumed per output pixel, per output column."""
    mx, _ = maps(w, 2, theta_max_deg, k, beta)
    return np.maximum(np.abs(np.gradient(mx[0])), 1e-3)


def warp(img, theta_max_deg, k, beta, eye_frac=0.5, mask=None, levels=5):
    """The ends of the curve minify by 3-5x, so sampling the full-resolution
    base there would alias badly. Sample a mip pyramid instead and blend the two
    bracketing levels, as texture hardware does."""
    h, w = img.shape[:2]
    mxf, myf = maps(w, h, theta_max_deg, k, beta, eye_frac)
    d = minification(w, theta_max_deg, k, beta)
    # vertical minification tracks the horizontal one closely; take the larger
    _, my2 = maps(w, 3, theta_max_deg, k, beta, eye_frac)
    d = np.maximum(d, np.abs(my2[2] - my2[0]) / 2.0)
    lod = np.clip(np.log2(d), 0, levels - 1)

    pyr = [img]
    for _ in range(levels - 1):
        pyr.append(cv2.pyrDown(pyr[-1]))

    # Each level is only needed over a couple of column runs, so remap just those
    # runs -- sampling all levels over the whole frame would cost several GB.
    acc = np.zeros((h, mxf.shape[1], img.shape[2]), np.float32)
    for level in range(levels):
        wgt = np.clip(1.0 - np.abs(lod - level), 0, 1).astype(np.float32)
        idx = np.where(wgt > 0)[0]
        if len(idx) == 0:
            continue
        s = 1.0 / (2 ** level)
        for run in np.split(idx, np.where(np.diff(idx) > 1)[0] + 1):
            a, b = run[0], run[-1] + 1
            sm = cv2.remap(pyr[level], (mxf[:, a:b] * s).astype(np.float32),
                           (myf[:, a:b] * s).astype(np.float32),
                           cv2.INTER_LINEAR, borderMode=cv2.BORDER_REPLICATE)
            acc[:, a:b] += sm.astype(np.float32) * wgt[None, a:b, None]
            del sm
    out = np.clip(acc, 0, 255).astype(np.uint8)
    del acc

    mo = None
    if mask is not None:
        mo = cv2.remap(mask, mxf, myf, cv2.INTER_NEAREST, borderMode=cv2.BORDER_CONSTANT)
        out[mo == 0] = 0
    return out, mo


def forward_lookup(w, theta_max_deg, k, beta, eye_frac):
    """Flat -> natural column and vertical-scale lookups, for mapping POINTS.

    Returns (src_x, s_of_x): for output column i, src_x[i] is the flat column it
    samples and s_of_x[i] the vertical scale applied about eye height. There are
    fewer output columns than flat columns -- the curve foreshortens.
    """
    mx, my = maps(w, 3, theta_max_deg, k, beta, eye_frac)
    src_x = mx[0].astype(np.float64)
    s_of_x = 1.0 / np.maximum(my[1] - my[0], 1e-9)
    return src_x, s_of_x
