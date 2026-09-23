"""Gravity, the world frame, and measured angles.

Gravity is a least-squares unit vector `up` that is perpendicular to
  * the normal of every facet of a segment declared `verticalReference`, and
  * the centre-to-centre direction of every declared `levelPairs` marker pair.
i.e. the eigenvector of the smallest eigenvalue of sum(w_i * d_i d_i^T) over those unit directions
(all weights 1). With exactly two non-parallel vertical references and no level pairs this is exactly
up ∝ n1 × n2. Sign: the solved cameras' image-up directions vote (people hold phones upright).

Without enough constraints (< 2 independent directions) gravity is UNKNOWN: the frame then treats the
reference facet as if it were vertical, so millimetres stay valid but every angle is only relative.
"""
import numpy as np

MIN_REF_ANGLE_DEG = 20.0  # two reference directions closer than this do not determine gravity


def _unit(v):
    return v / np.linalg.norm(v)


def camera_up_vote(cams_ba):
    """Sum of every camera's image-up direction (-y of the camera) in the BA frame."""
    return np.sum([-R.T[:, 1] for R, _ in cams_ba.values()], 0)


def gravity(normals, centres, ref_facets, level_pairs, cam_up):
    """Returns (up or None, info dict)."""
    dirs, used = [], []
    for f in ref_facets:
        dirs.append(normals[f])
        used.append(f"facet {f} normal")
    missing = []
    for a, b in level_pairs:
        if a in centres and b in centres:
            dirs.append(_unit(centres[a] - centres[b]))
            used.append(f"level pair {a}-{b}")
        else:
            missing.append([a, b])
    info = {"constraints": used, "levelPairsMissing": missing}
    if len(dirs) < 2:
        info["reason"] = "fewer than two gravity constraints"
        return None, info
    D = np.array(dirs)
    # independence: largest pairwise angle among constraint directions
    cross = max(np.linalg.norm(np.cross(a, b)) for i, a in enumerate(D) for b in D[i + 1:])
    if np.degrees(np.arcsin(min(1.0, cross))) < MIN_REF_ANGLE_DEG:
        info["reason"] = f"gravity constraints are (nearly) parallel (< {MIN_REF_ANGLE_DEG} deg apart)"
        return None, info
    w, V = np.linalg.eigh(D.T @ D)
    up = V[:, 0]
    if up @ cam_up < 0:
        up = -up
    info["residualDeg"] = {u: round(float(np.degrees(np.arcsin(np.clip(abs(d @ up), 0, 1)))), 4)
                           for u, d in zip(used, D)}
    if len(ref_facets) == 2:
        n1, n2 = normals[ref_facets[0]], normals[ref_facets[1]]
        info["referenceNormalsAngleDeg"] = float(np.degrees(np.arccos(np.clip(n1 @ n2, -1, 1))))
    return up, info


def pseudo_up(n_ref, cam_up):
    """Gravity unknown: the in-plane direction of the reference facet closest to the camera-up vote."""
    v = cam_up - (cam_up @ n_ref) * n_ref
    if np.linalg.norm(v) < 1e-9:
        v = np.cross(n_ref, [1.0, 0, 0])
    return _unit(v)


def world_transform(n_ref, up):
    """R (rows = world axes in BA frame): x = horizontal along the reference facet, pointing right as
    you face it; y = z × x (into the wall); z = up."""
    x = np.cross(up, n_ref)
    if np.linalg.norm(x) < 1e-9:
        x = np.cross(up, [1.0, 0, 0]) if abs(up[0]) < 0.9 else np.cross(up, [0, 1.0, 0])
    x = _unit(x)
    y = np.cross(up, x)
    return np.vstack([x, y, up])


def facet_axes(n, up):
    """u horizontal (right as you face the facet), v up the surface, n = u × v out of the wall."""
    u = np.cross(up, n)
    if np.linalg.norm(u) < 1e-9:  # horizontal facet: fall back to world x
        u = np.array([1.0, 0, 0])
    u = _unit(u)
    v = np.cross(n, u)
    return u, v


def tilt_from_vertical(n, up):
    """Positive = overhang (normal points down), negative = slab."""
    return float(np.degrees(np.arcsin(np.clip(-n @ up, -1, 1))))


def yaw_rel(n, n_ref, up):
    """Rotation about up of n's horizontal projection relative to n_ref's (deg, CCW from above)."""
    def horiz(a):
        h = a - (a @ up) * up
        nh = np.linalg.norm(h)
        return h / nh if nh > 1e-9 else None
    a, b = horiz(n_ref), horiz(n)
    if a is None or b is None:
        return None
    return float(np.degrees(np.arctan2(np.cross(a, b) @ up, a @ b)))


def angles(normals, up, ref_fid):
    return {fid: {"tiltDeg": tilt_from_vertical(n, up), "yawDeg": yaw_rel(n, normals[ref_fid], up),
                  "angleToReferenceDeg": float(np.degrees(np.arccos(np.clip(n @ normals[ref_fid], -1, 1))))}
            for fid, n in normals.items()}
