"""One full solve: free BA -> outlier down-weighting -> facet assignment -> facet BA -> frame."""
import numpy as np

from .facets import assign_facets, build_facet_problem, coplanarity, marker_normals
from .freeba import ROBUST, build_cameras, free_mask, per_marker_rms, per_obs_err, rms, run_free
from .frame import angles, camera_up_vote, gravity, pseudo_up
from .request import observations

# A marker whose free-solve RMS exceeds both of these is treated as physically suspect (bent, not
# flat, partly occluded) and down-weighted: sigma = its RMS / the median marker RMS (capped).
OUTLIER_MIN_PX = 3.0
OUTLIER_X_MEDIAN = 4.0
OUTLIER_MAX_SIGMA = 10.0


def _noop(*_):
    pass


def downweight_outliers(prob, x, obs, free_intr):
    pm = per_marker_rms(prob, x, obs)
    med = float(np.median(list(pm.values())))
    flagged = {}
    for m, r in pm.items():
        if r > OUTLIER_MIN_PX and r > OUTLIER_X_MEDIAN * med:
            s = float(min(OUTLIER_MAX_SIGMA, r / med))
            flagged[m] = {"freeRmsPx": round(r, 3), "medianRmsPx": round(med, 3), "sigmaPx": round(s, 3)}
            for o in obs:
                if o["id"] == m:
                    o["sigma"] = np.maximum(o["sigma"], s)
    if flagged:
        prob._obs_arrays()
        x, _ = prob.solve(x, free_mask(prob, free_intr), max_nfev=300, **ROBUST)
    return x, flagged


def reference_facet(members, facet_segment, segments):
    """World-x facet: lowest declared non-reference segment's biggest facet; else the biggest facet."""
    cands = [s for s in sorted(segments) if not segments[s].vertical_reference]
    for s in cands:
        fs = [f for f, seg in facet_segment.items() if seg == s]
        if fs:
            return max(fs, key=lambda f: (len(members[f]), f))
    return max(members, key=lambda f: (len(members[f]), f))


def facet_solve(prob, x, members, free_intr):
    fprob, fx = build_facet_problem(prob, x, members)
    fx, _ = fprob.solve(fx, free_mask(fprob, free_intr), loss="linear", max_nfev=300)
    fx, _ = fprob.solve(fx, free_mask(fprob, free_intr), max_nfev=300, **ROBUST)
    return fprob, fx


def solve_structure(req, progress=_noop, drop_image=None, members=None):
    """Everything up to (not including) gravity. Returns the solution dict."""
    obs = observations(req)
    if drop_image:
        obs = [o for o in obs if o["image"] != drop_image]
    cams, intr, free_intr, prior = build_cameras(req)
    cams = {k: v for k, v in cams.items() if k != drop_image}
    obj = {o["id"]: o["obj"] for o in obs}
    progress(0.05, "free bundle adjustment")
    prob, x, obs, unreached = run_free(obs, cams, intr, free_intr, prior, obj)
    progress(0.45, "outlier check")
    flagged = {}
    if req.options.get("autoDownweight", True):
        x, flagged = downweight_outliers(prob, x, obs, free_intr)
    declared = set(req.segments)
    decisions = []
    facet_segment = None
    if members is None:
        progress(0.55, "facet assignment")
        members, facet_segment, decisions = assign_facets(prob, x, declared, req.options.get("facets"),
                                                          suspect=set(flagged), nominal_of=req.segment_of)
    members = {k: [m for m in v if m in prob.mids] for k, v in members.items()}
    members = {k: v for k, v in members.items() if v}
    if facet_segment is None:
        facet_segment = {f: int(f.rstrip("abcdefgh")) for f in members}
    progress(0.65, "facet bundle adjustment")
    fprob, fx = facet_solve(prob, x, members, free_intr)
    fmw = fprob.marker_world(fx)
    return {
        "req": req, "obs": obs, "cams": prob.cams, "prob": prob, "x": x, "fprob": fprob, "fx": fx,
        "members": members, "facet_segment": facet_segment, "decisions": decisions,
        "downweighted": flagged, "unreached": unreached, "free_intr": free_intr,
        "free_normals": marker_normals(prob, x),
        "coplanarity_free": coplanarity(prob, x, members),
        "rms_free": rms(per_obs_err(prob, x), obs),
        "rms_facet": rms(per_obs_err(fprob, fx), obs),
        "err_facet": per_obs_err(fprob, fx),
        "normals": {fid: fprob.facet_frame(fx, fid)[0][:, 2] for fid in fprob.facets},
        "corners_ba": fmw, "centres": {m: c.mean(0) for m, c in fmw.items()},
    }


def apply_gravity(sol, level_pairs=None):
    """Gravity + reference facet + angles. Cheap: can be re-run with different level pairs."""
    req = sol["req"]
    level_pairs = req.level_pairs if level_pairs is None else level_pairs
    normals, members, fseg = sol["normals"], sol["members"], sol["facet_segment"]
    ref_facets = [f for f in sorted(members) if fseg[f] in req.segments
                  and req.segments[fseg[f]].vertical_reference]
    cams_ba = {img: sol["fprob"].cam(sol["fx"], img) for img in sol["fprob"].images}
    cam_up = camera_up_vote(cams_ba)
    up, ginfo = gravity(normals, sol["centres"], ref_facets, level_pairs, cam_up)
    ref = reference_facet(members, fseg, req.segments)
    ginfo["known"] = up is not None
    if up is None:
        up = pseudo_up(normals[ref], cam_up)
    sol.update({"up": up, "gravity": ginfo, "ref_facet": ref, "ref_facets_gravity": ref_facets,
                "level_pairs": list(level_pairs), "cams_ba": cams_ba,
                "angles": angles(normals, up, ref)})
    return sol


def solve(req, progress=_noop):
    sol = apply_gravity(solve_structure(req, progress))
    progress(0.8, "gravity and world frame")
    return sol
