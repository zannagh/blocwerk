"""Last robustness stage: a solved model that is physically implausible is solved AGAIN without its worst offenders.

reject.py removes a false detection that contradicts the other photos. It cannot remove one that the other photos
agree with: DICT_4X4_50 decodes ids on holds, and the same hold decodes the same id from neighbouring photos (The
Attic, 2026-09-29: a ring hold as id 17, a purple hold as id 37). Such detections corrupt the free solve's
initialisation, reject.py then removes them together with dozens of real ones, and the re-solve that starts from the
corrupted poses stays wrong: main wall 17 deg slab (declared 45 deg overhang), markers 52 m too big, a camera 881 km
away. The corrupted solve still ranks the false detections at the top of its residuals, though.

So when a model fails a plausibility check (the same limits as the app's activation gate, minus reprojection):
  1. take the worst offenders: the detections reject.py removed, worst residual first (then the worst remaining
     observations), at most DROP_SHARE of the observations per round, and each implausibly far camera's worst one;
  2. solve again FROM SCRATCH without them (a fresh initialisation, not a warm start);
  3. repeat up to MAX_ROUNDS times; the most plausible model wins.
Dropped detections are reported in quality.rejectedObservations with reason "implausible-model". A photo is never
dropped as a whole (never its last detection), so it stays in the capture and in the solve if it keeps markers.
"""
import copy
import math

import numpy as np

MAX_CAMERA_DISTANCE_MM = 50_000.0
MAX_SIDE_MEAN_SHARE = 0.05
MAX_SIDE_RMS_SHARE = 0.08
MAX_ANGLE_OFF_DEG = 10.0
MAX_ROUNDS = 2
DROP_SHARE = 0.02
MIN_DROP = 3
REASON = "implausible-model"


def _marker_centres(doc):
    return [np.mean(np.asarray(m["cornersWorldMm"], float), axis=0)
            for m in doc.get("markers", []) if m.get("cornersWorldMm")]


def far_cameras(doc):
    """(image, distance mm) of every camera farther than MAX_CAMERA_DISTANCE_MM from the nearest marker."""
    centres = _marker_centres(doc)
    out = []
    for c in doc.get("cameras", []) if centres else []:
        R, t = np.asarray(c["R"], float).reshape(3, 3), np.asarray(c["t"], float)
        centre = -R.T @ t
        d = min(float(np.linalg.norm(centre - m)) for m in centres)
        if not d <= MAX_CAMERA_DISTANCE_MM:
            out.append((c["image"], d))
    return sorted(out, key=lambda x: -x[1] if math.isfinite(x[1]) else -math.inf)


def problems(doc):
    """Plain-language reasons the model is implausible; empty when it is plausible."""
    out = []
    size = doc.get("markerSizeMm") or 125.0
    checks = doc.get("quality", {}).get("checks", {})
    mean, rms = checks.get("markerSideMeanErrMm"), checks.get("markerSideRmsErrMm")
    if mean is not None and abs(mean) > MAX_SIDE_MEAN_SHARE * size:
        out.append(f"markers measure {mean:+.1f} mm off their printed size on average")
    elif rms is not None and rms > MAX_SIDE_RMS_SHARE * size:
        out.append(f"marker sizes spread by {rms:.1f} mm")
    if doc.get("world", {}).get("gravityKnown", True):
        for s in doc.get("segments", []):
            declared = s.get("declaredAngleDeg")
            if declared is None and s.get("angleIsGravityReference"):
                declared = 0.0
            for f in s.get("facets", []) if declared is not None else []:
                a = f.get("measuredAngleDeg")
                if a is not None and abs(a - declared) > MAX_ANGLE_OFF_DEG:
                    out.append(f"{s.get('name') or 'segment ' + str(s['index'])} (facet {f['id']}) measures "
                               f"{a:.1f} deg, declared {declared:g} deg")
    far = far_cameras(doc)
    if far:
        out.append(f"{len(far)} camera(s) implausibly far from the wall ({far[0][0]}: {far[0][1] / 1000:.0f} m)")
    return out


def _counts(request_doc, dropped):
    per_photo = {}
    for p in request_doc["photos"]:
        per_photo[p["name"]] = sum(1 for m in p["markers"] if (p["name"], m["id"]) not in dropped)
    return per_photo


def offenders(doc, sol, request_doc, dropped):
    """The next round's worst offenders as {(photo, id): residualPx}, never a photo's last detection."""
    n = sum(len(p["markers"]) for p in request_doc["photos"])
    budget = max(MIN_DROP, int(math.ceil(DROP_SHARE * n)))
    ranked = [((r["photo"], int(r["id"])), float(r.get("residualPx") or 0.0))
              for r in sorted(sol["rejected"], key=lambda r: -(r.get("residualPx") or 0.0))]
    err = sol["err_facet"]
    rms = [float(np.sqrt(np.mean(np.square(e)))) for e in err]
    by_rms = sorted(range(len(sol["obs"])), key=lambda k: -rms[k])
    ranked += [((sol["obs"][k]["image"], int(sol["obs"][k]["id"])), rms[k]) for k in by_rms]
    far = {img for img, _ in far_cameras(doc)}
    worst_far = {}
    for k in by_rms:
        img = sol["obs"][k]["image"]
        if img in far and img not in worst_far:
            worst_far[img] = ((img, int(sol["obs"][k]["id"])), rms[k])
    left = _counts(request_doc, dropped)
    pick = {}
    for key, r in list(worst_far.values()) + ranked:
        if len(pick) >= budget + len(worst_far):
            break
        if key in pick or key in dropped or left.get(key[0], 0) <= 1:
            continue
        pick[key] = r
        left[key[0]] -= 1
    return pick


def without(request_doc, dropped):
    """The request with the dropped (photo, id) detections removed."""
    req = copy.deepcopy(request_doc)
    for p in req["photos"]:
        p["markers"] = [m for m in p["markers"] if (p["name"], m["id"]) not in dropped]
    return req


def record(key, residual, why, round_no):
    return {"photo": key[0], "id": key[1], "views": None, "residualPx": round(residual, 2), "thresholdPx": None,
            "otherViewsResidualPx": None, "reason": REASON, "markerDropped": False,
            "detail": "; ".join(why)[:300], "round": round_no}
