"""Second robustness stage: REMOVE observations that are false detections, then re-solve.

Down-weighting (solver.downweight_outliers) is right for a marker that is physically imperfect (bent,
lifted) and looks equally wrong in every photo. It is wrong for a false detection: a hold, a shadow
or a grazing blur the detector read as a marker id in ONE photo. Down-weighted, such an observation
still drags its facet plane (one false id 17 on a black hold moved a facet by 267 mm). Here each
observation of the free (marker-pose) solve is tested:

1. its reprojection RMS against a MAD threshold over all observations, and
2. for a marker seen in >= 3 photos: where its OTHER photos place it (marker pose re-fitted from
   them alone, cameras fixed), projected into this photo. A physically bent marker fails in every
   photo alike; a false detection fails only in its own photo.

A marker's last two views need strong evidence (a 2-view marker cannot say which view is wrong
unless one is far off and the other clean); a marker seen in one photo that fails is dropped.
Never more than MAX_REJECT_FRACTION of the observations are removed, and never a photo's last one.
"""
import numpy as np
from scipy.optimize import least_squares
from scipy.spatial.transform import Rotation

from .camera import project

MAD_K = 10.0  # threshold = median + MAD_K * robust sigma (MAD * 1.4826) of the per-observation RMS
MIN_THRESHOLD_PX = 4.0  # ... but never below this: a clean capture has a tiny MAD
TWO_VIEW_X = 3.0  # a 2-view marker: the bad view must exceed 3x the threshold, the other be clean
MAX_REJECT_FRACTION = 0.10
REAL_SIGMA = 10.0  # corners with a REQUEST sigma >= this are synthetic / not measured


def _real(o):
    """Measured corners, judged by the sigma the request gave (not the down-weighting's)."""
    return o.get("sigma0", o["sigma"]) < REAL_SIGMA


def obs_rms(err, obs):
    """Per-observation RMS (px) over its measured corners; nan when it has none."""
    out = np.full(len(obs), np.nan)
    for k, o in enumerate(obs):
        real = _real(o)
        if real.any():
            out[k] = float(np.sqrt(np.mean(err[k][real] ** 2)))
    return out


def threshold(r):
    v = r[np.isfinite(r)]
    med = float(np.median(v))
    mad = 1.4826 * float(np.median(np.abs(v - med)))
    return max(MIN_THRESHOLD_PX, med + MAD_K * mad), med, mad


def _cam_params(prob, x, img):
    R, t = prob.cam(x, img)
    c = prob.cams[img]
    return R, t, prob.intr(x, c["group"]), c["w"], c["h"], c["psign"]


def _project_marker(prob, x, img, rvec, t, m):
    Rc, tc, it, w, h, ps = _cam_params(prob, x, img)
    Rm = Rotation.from_rotvec(rvec).as_matrix()
    P = prob.obj[m] @ Rm.T + t
    return project(P @ Rc.T + tc, it, w, h, ps)


def loo_residual(prob, x, obs, k):
    """RMS (px) of observation k against the marker pose fitted from its OTHER observations only."""
    m = obs[k]["id"]
    others = [j for j, o in enumerate(obs) if o["id"] == m and j != k]
    R0, t0 = prob.marker(x, m)
    z0 = np.r_[Rotation.from_matrix(R0).as_rotvec(), t0]

    def fun(z):
        res = []
        for j in others:
            real = _real(obs[j])
            d = _project_marker(prob, x, obs[j]["image"], z[:3], z[3:], m) - obs[j]["corners"]
            res.append((d * real[:, None]).ravel())
        return np.concatenate(res)

    z = least_squares(fun, z0, loss="soft_l1", f_scale=2.0, x_scale="jac", max_nfev=200).x
    real = _real(obs[k])
    d = _project_marker(prob, x, obs[k]["image"], z[:3], z[3:], m) - obs[k]["corners"]
    e = np.hypot(d[:, 0], d[:, 1])[real]
    return float(np.sqrt(np.mean(e ** 2))) if e.size else float("nan")


def _candidate(prob, x, obs, k, r, views, thr):
    """Rejection record for observation k, or None when the evidence is not strong enough."""
    o = obs[k]
    n = len(views[o["id"]])
    rec = {"photo": o["image"], "id": int(o["id"]), "views": n, "residualPx": round(float(r[k]), 2),
           "thresholdPx": round(thr, 2), "otherViewsResidualPx": None}
    if n == 1:
        return {**rec, "reason": "single-view-misfit", "markerDropped": True}
    if n == 2:
        other = next(j for j in views[o["id"]] if j != k)
        if r[k] <= TWO_VIEW_X * thr or not r[other] <= thr:
            return None
    loo = loo_residual(prob, x, obs, k)
    rec["otherViewsResidualPx"] = round(loo, 2)
    others = [r[j] for j in views[o["id"]] if j != k and np.isfinite(r[j])]
    # the other views must agree among themselves much better than with this one
    if not (loo > thr and (not others or loo > 3.0 * float(np.median(others)))):
        return None
    return {**rec, "reason": "inconsistent-with-other-views", "markerDropped": False}


def find_rejections(prob, x, obs, err):
    """Observations to remove: list of (obs index, record), worst first, capped."""
    r = obs_rms(err, obs)
    if np.isfinite(r).sum() < 8:
        return []
    thr, _, _ = threshold(r)
    views, per_img = {}, {}
    for k, o in enumerate(obs):
        views.setdefault(o["id"], []).append(k)
        per_img[o["image"]] = per_img.get(o["image"], 0) + 1
    out = []
    for k in np.argsort(-np.nan_to_num(r, nan=-1.0)):
        if not r[k] > thr:
            break
        rec = _candidate(prob, x, obs, int(k), r, views, thr)
        if rec is not None:
            out.append((int(k), rec))
    cap = int(MAX_REJECT_FRACTION * len(obs))
    kept = []
    for k, rec in out:
        img = obs[k]["image"]
        if len(kept) >= cap or per_img[img] <= 1:
            continue
        per_img[img] -= 1
        kept.append((k, rec))
    return kept
