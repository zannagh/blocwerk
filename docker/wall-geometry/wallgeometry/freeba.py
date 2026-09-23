"""Cameras, initial intrinsics, and the free (marker-pose) bundle adjustment."""
import numpy as np

from .ba import Problem, pack_free, unpack_free
from .initpose import chain, chain_fixed, ippe_candidates, select_branches

# A lens group needs this many photos before principal point and distortion are identifiable;
# smaller groups only get f free (the 1-photo 28 mm group of capture 1 is the motivating case).
FULL_INTR_MIN_PHOTOS = 3
FULL_INTR = [0, 1, 2, 3, 4]  # f, dx, dy, k1, k2 (k3 stays 0)
# Weak priors (mean, sigma): principal point near centre, distortion small. Data dominate.
FULL_PRIOR = {1: (0.0, 60.0), 2: (0.0, 60.0), 3: (0.0, 0.5), 4: (0.0, 0.5)}
ROBUST = {"loss": "soft_l1", "f_scale": 2.0}
PORTRAIT_SIGN = 1  # see camera.py; the fit is insensitive to it


def build_cameras(req, psign=PORTRAIT_SIGN):
    cams, intr, count = {}, {}, {}
    for p in req.photos:
        if not p["markers"]:
            continue
        g = p["group"]
        cams[p["name"]] = {"group": g, "w": p["width"], "h": p["height"], "psign": psign}
        count[g] = count.get(g, 0) + 1
        if g not in intr:
            intr[g] = np.array([p["focal_px"], 0, 0, 0, 0, 0], float)
    free_intr = {g: (FULL_INTR if n >= FULL_INTR_MIN_PHOTOS else [0]) for g, n in count.items()}
    prior = {g: (FULL_PRIOR if n >= FULL_INTR_MIN_PHOTOS else {}) for g, n in count.items()}
    return cams, intr, free_intr, prior


def free_mask(prob, free_intr=None):
    m = np.ones(prob.n, dtype=bool)
    for g in prob.groups:
        m[prob.gi[g]:prob.gi[g] + 6] = False
        for j in (free_intr or {}).get(g, []):
            m[prob.gi[g] + j] = True
    return m


def pick_root(obs):
    """Gauge photo: most markers, then most co-visible marker links to other photos, then name."""
    by_img = {}
    for o in obs:
        by_img.setdefault(o["image"], set()).add(o["id"])

    def score(img):
        links = sum(len(by_img[img] & s) for k, s in by_img.items() if k != img)
        return (len(by_img[img]), links, img)
    return max(by_img, key=score)


def run_free(obs, cams, intr, free_intr, prior, obj, root=None, log=None):
    """Initialise + free BA. Returns (prob, x, obs, unreached_images)."""
    ippe_candidates(obs, cams, intr)
    select_branches(obs, {o["id"]: o.get("seg", o["id"] // 6) for o in obs})  # nominal segment votes
    root = root or pick_root(obs)
    cam_pose, mk_pose = chain(obs, cams, intr, root)
    unreached = sorted(set(cams) - set(cam_pose))
    obs = [o for o in obs if o["image"] in cam_pose and o["id"] in mk_pose]
    cams = {k: v for k, v in cams.items() if k in cam_pose}
    groups = sorted({c["group"] for c in cams.values()})
    mids = sorted(mk_pose)
    prob = Problem(obs, cams, groups, root, mids, obj, prior=prior)
    x = pack_free(prob, {g: intr[g] for g in groups}, cam_pose, mk_pose)
    best = None
    # alternate: poses-only BA, then re-choose every marker's IPPE branch against ALL cameras
    for _ in range(3):
        x, _ = prob.solve(x, free_mask(prob), loss="linear", max_nfev=300)
        cost = float(np.sum(prob.residuals(x) ** 2))
        if best is None or cost < best[0]:
            best = (cost, x.copy())
        i2, cp2, _ = unpack_free(prob, x)
        _, mp3 = chain_fixed(obs, cams, i2, cp2)
        x = pack_free(prob, i2, cp2, mp3)
    x = best[1]
    x, _ = prob.solve(x, free_mask(prob, free_intr), loss="linear", max_nfev=300)
    x, _ = prob.solve(x, free_mask(prob, free_intr), max_nfev=300, **ROBUST)
    return prob, x, obs, unreached


def per_obs_err(prob, x):
    px = prob.project_all(x)
    return np.sqrt(np.sum((px - prob.o_px) ** 2, axis=2))  # (n_obs, 4) px


def rms(e, obs, max_sigma=1.0):
    """Pixel RMS over corners with sigma <= max_sigma (synthetic / down-weighted corners excluded)."""
    keep = np.stack([o["sigma"] <= max_sigma for o in obs])
    return float(np.sqrt(np.mean(e[keep] ** 2))) if keep.any() else float("nan")


def per_marker_rms(prob, x, obs):
    e = per_obs_err(prob, x)
    acc = {}
    for k, o in enumerate(obs):
        real = o["sigma"] < 10.0
        acc.setdefault(o["id"], []).extend(e[k][real])
    return {m: float(np.sqrt(np.mean(np.square(v)))) for m, v in acc.items() if v}
