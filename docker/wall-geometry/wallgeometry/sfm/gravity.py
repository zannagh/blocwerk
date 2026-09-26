"""Gravity of a feature model, first source that works: the phones' accelerometer (device) -> declared segment
angles -> the floor plane -> the cameras' image-up (a prior: gravityKnown false).

Device convention (Phase 0, The Attic: 0.18-0.30 deg from the marker gravity): Apple's AccelerationVector is
UP (-gravity, ~1 g) in the device frame +X left, +Y bottom, +Z into the face. In the camera frame of the STORED
upright image (x right, y down, z forward), chosen by the stored aspect and the vector itself (never by the
EXIF Orientation tag: iPhone 16 Pro HEICs are stored turned and tagged 1):
  portrait  (h > w): up_cam = (-aX,  aY, aZ); upside down (aY > 0): ( aX, -aY, aZ)
  landscape (w >= h): up_cam = ( aY,  aX, aZ) when aX < 0;    else  (-aY, -aX, aZ)
"""
import numpy as np

MIN_G, MAX_G = 0.7, 1.3        # a vector outside this (in g) is a phone in motion: ignored
MIN_DEVICE_PHOTOS = 3
HORIZONTAL_DEG = 20.0          # a plane whose normal is within this of up is horizontal
ASSIGN_DEG = 12.0              # a plane takes a declared angle within this of its tilt under the prior
MIN_SPREAD_DEG = 20.0          # declared planes must span at least this much (else up is undetermined)
MAX_DECLARED_RESIDUAL_DEG = 3.0
MAX_DECLARED_SHIFT_DEG = 15.0


def unit(v):
    v = np.asarray(v, float)
    return v / np.linalg.norm(v)


def device_up_cam(a, width, height):
    """Up in the stored image's camera frame from the accelerometer vector a = (aX, aY, aZ)."""
    ax, ay, az = a
    if height > width:
        v = (-ax, ay, az) if ay <= 0 else (ax, -ay, az)
    else:
        v = (ay, ax, az) if ax <= 0 else (-ay, -ax, az)
    return unit(v)


def robust_mean(U, iters=5):
    """IRLS (~L1 on the angle) mean direction of unit vectors, dropping > max(3 x median, 2 deg)."""
    m = unit(U.mean(0))
    e = np.zeros(len(U))
    for _ in range(iters):
        e = np.degrees(np.arccos(np.clip(U @ m, -1, 1)))
        w = 1 / np.maximum(e, 0.3)
        w[e > max(3 * np.median(e), 2.0)] = 0
        m = unit((U * w[:, None]).sum(0))
    return m, e


def device_up(model, vectors):
    """(up in the model frame or None, info). vectors: {stem: (aX, aY, aZ)} of the photos."""
    ups, skipped = [], 0
    for im in model["images"]:
        a = vectors.get(im["stem"]) if im["role"] == "photo" else None
        if a is None:
            continue
        if not MIN_G <= np.linalg.norm(a) <= MAX_G:
            skipped += 1
            continue
        cam = model["cams"][im["cam"]]
        ups.append(im["R"].T @ device_up_cam(a, cam["width"], cam["height"]))
    info = {"photos": len(ups), "skippedMoving": skipped}
    if len(ups) < MIN_DEVICE_PHOTOS:
        info["reason"] = f"device gravity for {len(ups)} registered photos (need {MIN_DEVICE_PHOTOS})"
        return None, info
    m, e = robust_mean(np.array(ups))
    info.update(perPhotoMedianDeg=round(float(np.median(e)), 3), perPhotoP90Deg=round(float(np.percentile(e, 90)), 3))
    return m, info


def camera_up(model):
    """The cameras' image-up vote (people hold phones upright), unit, model frame."""
    v = [-im["R"][1] for im in model["images"] if im["role"] != "anchor"]
    return unit(np.sum(v, 0))


def is_horizontal(n, up, deg=HORIZONTAL_DEG):
    return abs(n @ up) > np.cos(np.radians(deg))


def floor_plane(planes, pts, up0, cam_centres, spread):
    """The biggest horizontal plane (under the prior up0) below the median camera by > 0.2 spread, with its
    normal pointing up; None if there is none."""
    h_cam = np.median(cam_centres @ up0)
    best = None
    for pl in planes:
        if not is_horizontal(pl["n"], up0) or pl["c"] @ up0 > h_cam - 0.2 * spread:
            continue
        if best is None or pl["inl"].sum() > best["inl"].sum():
            best = pl
    if best is None:
        return None
    return {**best, "n": best["n"] if best["n"] @ up0 > 0 else -best["n"]}


def _solve_up(normals, sines, up0, iters=30):
    """Unit up with n_i . up = -sin(a_i) in least squares (Gauss-Newton on the sphere from up0)."""
    up = unit(up0)
    for _ in range(iters):
        e1 = unit(np.cross(up, [1.0, 0, 0] if abs(up[0]) < 0.9 else [0, 1.0, 0]))
        B = np.stack([e1, np.cross(up, e1)], 1)
        r = normals @ up + sines
        J = normals @ B
        step = np.linalg.lstsq(J, -r, rcond=None)[0]
        up = unit(up + B @ step)
        if np.linalg.norm(step) < 1e-10:
            break
    return up


def _spread(N):
    return max((np.degrees(np.arccos(np.clip(abs(a @ b), 0, 1))) for i, a in enumerate(N) for b in N[i + 1:]),
               default=0.0)


def _assign(candidates, hints, up0):
    """[(candidate index, hint)]: each plane takes the declared angle nearest its tilt under up0 (< ASSIGN_DEG)."""
    angles = [(h.declared_angle_deg, h) for h in hints if h.declared_angle_deg is not None]
    used = []
    for k, pl in enumerate(candidates):
        tilt = np.degrees(np.arcsin(np.clip(-pl["n"] @ up0, -1, 1)))
        best = min(angles, key=lambda a: abs(a[0] - tilt), default=None)
        if best is not None and abs(best[0] - tilt) < ASSIGN_DEG:
            used.append((k, best[1]))
    return used


def declared_up(candidates, hints, up0):
    """(up or None, info) from the declared angles: the assigned planes (biggest first, normals toward the
    cameras) solve up; the worst-fitting one is dropped while a residual exceeds MAX_DECLARED_RESIDUAL_DEG
    (rafters at 37 deg take a 45 deg hint under a rough prior); >= 2 planes spanning >= MIN_SPREAD_DEG remain."""
    used = _assign(candidates, hints, up0)
    info = {"assigned": {str(k): h.index for k, h in used}, "dropped": []}
    while True:
        N = np.array([candidates[k]["n"] for k, _ in used]) if used else np.zeros((0, 3))
        if len(used) < 2 or _spread(N) < MIN_SPREAD_DEG:
            info["reason"] = "fewer than two declared planes at different angles fit"
            return None, info
        sines = np.sin(np.radians([h.declared_angle_deg for _, h in used]))
        up = _solve_up(N, sines, up0)
        res = np.degrees(np.abs(np.arcsin(np.clip(-(N @ up), -1, 1)) - np.arcsin(sines)))
        if res.max() <= MAX_DECLARED_RESIDUAL_DEG:
            break
        info["dropped"].append(str(used.pop(int(np.argmax(res)))[0]))
    shift = np.degrees(np.arccos(np.clip(up @ unit(up0), -1, 1)))
    info.update(residualDeg=[round(float(r), 3) for r in res], shiftFromPriorDeg=round(float(shift), 2))
    if shift > MAX_DECLARED_SHIFT_DEG:
        info["reason"] = "the declared angles do not fit the planes"
        return None, info
    return up, info
