"""COLMAP camera models (SIMPLE_PINHOLE, PINHOLE, SIMPLE_RADIAL, RADIAL, OPENCV): projection, pixel rays, and
the geometry document's K / dist (OpenCV convention: pixel centres at integers, dist = k1, k2, p1, p2, k3)."""
import numpy as np


def coefficients(cam):
    """(fx, fy, cx, cy, k1, k2, p1, p2) in COLMAP's pixel convention."""
    m, p = cam["model"], cam["params"]
    if m == 0:
        return p[0], p[0], p[1], p[2], 0.0, 0.0, 0.0, 0.0
    if m == 1:
        return p[0], p[1], p[2], p[3], 0.0, 0.0, 0.0, 0.0
    if m == 2:
        return p[0], p[0], p[1], p[2], p[3], 0.0, 0.0, 0.0
    if m == 3:
        return p[0], p[0], p[1], p[2], p[3], p[4], 0.0, 0.0
    return p[0], p[1], p[2], p[3], p[4], p[5], p[6], p[7]


def _distort(cam, u, v):
    _, _, _, _, k1, k2, p1, p2 = coefficients(cam)
    r2 = u * u + v * v
    radial = k1 * r2 + k2 * r2 * r2
    du = u * radial + 2 * p1 * u * v + p2 * (r2 + 2 * u * u)
    dv = v * radial + 2 * p2 * u * v + p1 * (r2 + 2 * v * v)
    return u + du, v + dv


def project(cam, R, t, X):
    """(n, 2) pixels (OpenCV convention) and depth of world points X (n, 3)."""
    fx, fy, cx, cy = coefficients(cam)[:4]
    Xc = X @ R.T + t
    z = Xc[:, 2]
    zs = np.where(np.abs(z) < 1e-12, 1e-12, z)
    u, v = _distort(cam, Xc[:, 0] / zs, Xc[:, 1] / zs)
    return np.stack([fx * u + cx - 0.5, fy * v + cy - 0.5], 1), z


def pixel_rays(cam, xy):
    """Undistorted normalised camera rays (n, 3) of pixels (n, 2) in the OpenCV convention."""
    fx, fy, cx, cy = coefficients(cam)[:4]
    xy = np.asarray(xy, float).reshape(-1, 2)
    ud, vd = (xy[:, 0] + 0.5 - cx) / fx, (xy[:, 1] + 0.5 - cy) / fy
    u, v = ud.copy(), vd.copy()
    for _ in range(20):  # fixed point: distort(u, v) = (ud, vd)
        du, dv = _distort(cam, u, v)
        u, v = u - (du - ud), v - (dv - vd)
    return np.stack([u, v, np.ones_like(u)], 1)


def world_rays(image, cam, xy):
    """(centre (3,), unit directions (n, 3)) of pixels of an image, in the model frame."""
    d = pixel_rays(cam, xy) @ image["R"]  # R.T @ ray, row-wise
    return image["C"], d / np.linalg.norm(d, axis=1)[:, None]


def doc_intrinsics(cam):
    """(K 3x3 row-major list, dist [k1, k2, p1, p2, k3]) for the geometry document."""
    fx, fy, cx, cy, k1, k2, p1, p2 = coefficients(cam)
    K = [fx, 0.0, cx - 0.5, 0.0, fy, cy - 0.5, 0.0, 0.0, 1.0]
    return K, [k1, k2, p1, p2, 0.0]


def reprojection_rms(model):
    """{image name: RMS px of its observations} with the model's own cameras and points."""
    ids, xyz = model["ids"], model["xyz"]
    order = np.argsort(ids)
    out = {}
    for im in model["images"]:
        obs = im["obs"][im["obs"]["p"] >= 0]
        if not len(obs):
            continue
        pos = np.searchsorted(ids[order], obs["p"])
        ok = (pos < len(ids)) & (ids[order][np.minimum(pos, len(ids) - 1)] == obs["p"])
        if not ok.any():
            continue
        X = xyz[order[pos[ok]]]
        px, _ = project(model["cams"][im["cam"]], im["R"], im["t"], X)
        d = px - np.c_[obs["x"][ok] - 0.5, obs["y"][ok] - 0.5]
        out[im["name"]] = float(np.sqrt((d ** 2).sum(1).mean()))
    return out
