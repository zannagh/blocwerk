"""One marker id the photos cannot agree on: solve again WITHOUT that marker instead of down-weighting many others.

reject.py removes a detection that contradicts the other photos. It cannot remove a false detection that REPEATS: the
same dark hold with a bright bolt decodes as the same id from neighbouring photos (The Attic, 2026-10-26: id 17, one
2x2 white block, read off holds in ~21 % of its detections, on the same holds across 3-6 neighbouring photos). Those
views agree among themselves, so leave-one-out passes them; the free solve bends the cameras of those photos to fit one
marker at two places, and the down-weighting then flags the innocent markers those cameras see (ten markers, 4.98 px;
without 17: one marker, 3.55 px). Per-photo marker poses do not single the id out either: a 40 px marker's own depth
is metres off, and the bent cameras make every marker in them look misplaced.

What does single it out:
  - many of its OWN views were rejected as inconsistent (at least MIN_REJECTED and REJECTED_SHARE of them): the
    photos split into two places for it, and reject.py may even have kept the false place; or
  - its residual in the FIRST free solve dominates every other marker's (17: 195 px, next 68 px) while the solve shows
    collateral damage (at least MIN_AFFECTED markers down-weighted or with detections rejected); or
  - it has the worst first free-solve residual of a solve too poor to use (reprojection over POOR_PX, the app's gate).
Then:
  1. solve again FROM SCRATCH without any detection of that marker;
  2. keep that solve only when it is better: fewer markers it cannot fit at no worse reprojection, or (a solve too poor
     to use) one under POOR_PX with no more such markers. A wrecked initialisation ranks innocent markers first too;
     leaving one of them out lowers the error but does not make the solve usable, so it is not kept;
  3. repeat up to MAX_ROUNDS times.
The dropped marker is reported once in quality.rejectedObservations (reason REASON, markerDropped).
"""
import copy

import numpy as np

MIN_AFFECTED = 3
MIN_REJECTED = 3
REJECTED_SHARE = 0.25
DOMINANT_X = 2.0
POOR_PX = 4.0
POOR_TRIALS = 3  # markers tried, worst first, on a solve too poor to use
MIN_RMS_PX = 10.0  # a dominant marker must also be far off in absolute terms (a clean capture is ~1-3 px)
MAX_ROUNDS = 2
REASON = "inconsistent-marker"

# A trial solve that fails numerically (or leaves a request the solver refuses) is a failed trial, never a failed solve.
TRIAL_ERRORS = (np.linalg.LinAlgError, ValueError, FloatingPointError)


def affected(sol):
    """Markers the solve could not fit: down-weighted, or with detections rejected as inconsistent."""
    out = set(sol["downweighted"])
    out |= {int(r["id"]) for r in sol["rejected"] if r.get("reason") == "inconsistent-with-other-views"}
    return out


def _views(sol):
    """{marker id: photos it was used or rejected in}."""
    out = {}
    for o in sol["obs"]:
        out[o["id"]] = out.get(o["id"], 0) + 1
    for r in sol["rejected"]:
        out[int(r["id"])] = out.get(int(r["id"]), 0) + 1
    return out


def _split(sol):
    """The marker with the most views rejected as inconsistent, when they are many of its views; else None."""
    views, bad = _views(sol), {}
    for r in sol["rejected"]:
        if r.get("reason") == "inconsistent-with-other-views":
            bad[int(r["id"])] = bad.get(int(r["id"]), 0) + 1
    many = [(n, m) for m, n in bad.items() if n >= MIN_REJECTED and n >= REJECTED_SHARE * views[m]]
    if not many:
        return None
    n, m = max(many)
    return m, f"{n} of its {views[m]} photos contradict the others"


def _dominant(sol, doc):
    """The worst markers of the first free solve to try without: the worst one when it dominates the damage; the
    POOR_TRIALS worst when the solve is too poor to use (a wrecked start can rank an innocent marker first)."""
    ranked = sorted(sol.get("marker_rms_first", {}).items(), key=lambda kv: -kv[1])
    if len(ranked) < 2 or ranked[0][1] < MIN_RMS_PX:
        return []
    if len(affected(sol)) >= MIN_AFFECTED and ranked[0][1] >= DOMINANT_X * ranked[1][1]:
        picked = ranked[:1]
    elif doc["quality"]["reprojRmsPx"] > POOR_PX:
        picked = [kv for kv in ranked[:POOR_TRIALS] if kv[1] >= MIN_RMS_PX]
    else:
        return []
    return [(int(m), _why(m, r, ranked)) for m, r in picked]


def _why(m, rms, ranked):
    other = next(v for k, v in ranked if k != m)
    return f"it misses its photos by {rms:.0f} px where the worst other marker misses by {other:.0f} px"


def suspects(sol, doc):
    """[(marker id, why)] of the markers to try the solve without, most suspect first; empty when none is."""
    split = _split(sol)
    return [split] if split else _dominant(sol, doc)


def better(new_sol, new_doc, old_sol, old_doc):
    """The solve without the suspect fits more markers at no worse reprojection, or turns a poor solve usable; never one
    that loses gravity (the marker was half of a level pair, or a reference segment's only one)."""
    if _gravity(old_doc) and not _gravity(new_doc):
        return False
    a_new, a_old = len(affected(new_sol)), len(affected(old_sol))
    r_new, r_old = new_doc["quality"]["reprojRmsPx"], old_doc["quality"]["reprojRmsPx"]
    return (a_new < a_old and r_new <= r_old) or (a_new <= a_old and r_old > POOR_PX >= r_new)


def _gravity(doc):
    return doc.get("world", {}).get("gravityKnown", True)


def without(request_doc, mid):
    """The request without any detection of marker `mid` (a photo left without markers stays, listed as unused)."""
    req = copy.deepcopy(request_doc)
    for p in req["photos"]:
        p["markers"] = [m for m in p["markers"] if m["id"] != mid]
    if req.get("unplannedMarkerIds"):
        req["unplannedMarkerIds"] = [i for i in req["unplannedMarkerIds"] if i != mid]
    return req


def record(request_doc, mid, why, old_doc, new_doc):
    photos = [p["name"] for p in request_doc["photos"] if any(m["id"] == mid for m in p["markers"])]
    before, after = old_doc["quality"]["reprojRmsPx"], new_doc["quality"]["reprojRmsPx"]
    return {"photo": photos[0] if photos else "", "id": int(mid), "views": len(photos), "residualPx": None,
            "thresholdPx": None, "otherViewsResidualPx": None, "reason": REASON, "markerDropped": True,
            "detail": (f"its photos do not agree where it is ({why}): something else on the wall most likely reads "
                       f"as this id. Solved without it: {after:.2f} px instead of {before:.2f} px")}


def run(doc, sol, request_doc, solve_once, progress):
    """(doc, sol, records): the best solve and the markers left out to get it."""
    records = []
    if not request_doc.get("options", {}).get("isolateMarkers", True):
        return doc, sol, records
    for _ in range(MAX_ROUNDS):
        kept = None
        for mid, why in suspects(sol, doc):
            kept = _trial(doc, sol, request_doc, solve_once, progress, mid)
            if kept is not None:
                records.append(record(request_doc, mid, why, doc, kept[0]))
                doc, sol, request_doc = kept
                break
        if kept is None:
            break
    return doc, sol, records


def _trial(doc, sol, request_doc, solve_once, progress, mid):
    """(doc, sol, request) of the solve without marker `mid` when it is better, else None."""
    progress(0.05, f"marker {mid} does not fit its photos; solving again without it")
    trial = without(request_doc, mid)
    try:
        new_doc, new_sol = solve_once(trial)
    except TRIAL_ERRORS as e:
        progress(0.05, f"solving without marker {mid} failed ({type(e).__name__}); keeping the solve with it")
        return None
    return (new_doc, new_sol, trial) if better(new_sol, new_doc, sol, doc) else None
