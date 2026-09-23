"""Camera model: pinhole + radial distortion, intrinsics shared per lens, orientation-aware.

Intrinsics per group are stored in the *sensor* (landscape) frame as
    [f, dx, dy, k1, k2, k3]
with (dx, dy) the principal-point offset from the image centre in px. A portrait photo is the
landscape sensor rotated by 90 deg; radial terms and f are rotation-invariant, the principal-point
offset is rotated: CW (s=+1) -> (-dy, dx), CCW (s=-1) -> (dy, -dx).
"""
import numpy as np
from scipy.spatial.transform import Rotation

N_INTR = 6
INTR_NAMES = ["f", "dx", "dy", "k1", "k2", "k3"]


def rotmat(rvecs):
    return Rotation.from_rotvec(np.atleast_2d(rvecs)).as_matrix()


def image_pp(intr, w, h, portrait_sign):
    """Principal point (cx, cy) in the image's own pixel frame."""
    dx, dy = intr[1], intr[2]
    if w < h:  # portrait
        s = portrait_sign
        dx, dy = -s * dy, s * dx
    return (w - 1) / 2.0 + dx, (h - 1) / 2.0 + dy


def distort_norm(xn, yn, k):
    r2 = xn * xn + yn * yn
    d = 1 + k[0] * r2 + k[1] * r2 * r2 + k[2] * r2 * r2 * r2
    return xn * d, yn * d


def project(pc, intr, w, h, portrait_sign):
    """pc: (...,3) camera-frame points -> (...,2) pixels."""
    z = pc[..., 2]
    xn, yn = pc[..., 0] / z, pc[..., 1] / z
    xd, yd = distort_norm(xn, yn, intr[3:6])
    cx, cy = image_pp(intr, w, h, portrait_sign)
    return np.stack([intr[0] * xd + cx, intr[0] * yd + cy], axis=-1)


def undistort_norm(xd, yd, k, iters=30):
    """Invert the radial model by fixed-point iteration (normalized coords)."""
    xn, yn = xd.copy(), yd.copy()
    for _ in range(iters):
        r2 = xn * xn + yn * yn
        d = 1 + k[0] * r2 + k[1] * r2 * r2 + k[2] * r2 * r2 * r2
        xn, yn = xd / d, yd / d
    return xn, yn


def undistort_px(px, intr, w, h, portrait_sign):
    """Pixels -> undistorted pixels of an ideal pinhole with the same f and pp."""
    cx, cy = image_pp(intr, w, h, portrait_sign)
    f = intr[0]
    xd, yd = (px[..., 0] - cx) / f, (px[..., 1] - cy) / f
    xn, yn = undistort_norm(xd, yd, intr[3:6])
    return np.stack([xn * f + cx, yn * f + cy], axis=-1)


def K_matrix(intr, w, h, portrait_sign):
    cx, cy = image_pp(intr, w, h, portrait_sign)
    return np.array([[intr[0], 0, cx], [0, intr[0], cy], [0, 0, 1.0]])


def corner_distortion_px(intr, w, h, portrait_sign):
    """|distorted - ideal| at the image corners: how far a straight-line pinhole would be off."""
    cx, cy = image_pp(intr, w, h, portrait_sign)
    f = intr[0]
    corners = np.array([[0, 0], [w - 1, 0], [w - 1, h - 1], [0, h - 1]], dtype=float)
    xd, yd = (corners[:, 0] - cx) / f, (corners[:, 1] - cy) / f
    xn, yn = undistort_norm(xd, yd, intr[3:6])
    shift = np.hypot((xn - xd) * f, (yn - yd) * f)
    return float(shift.max()), float(shift.mean())


def exif_focal_px(f35, w, h):
    """35mm-equivalent focal is defined on the diagonal (43.27 mm)."""
    return f35 / 43.2666 * np.hypot(w, h)
