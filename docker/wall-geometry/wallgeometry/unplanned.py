"""Markers the plan does not list: stuck on the wall after the plan was printed (same dictionary, same size).

The app sends an unplanned id (`unplannedMarkerIds`) only when several photos decode it (a hold read as an id is
mostly dropped before, by the detector's quiet-zone check). The solve keeps it only when it confirms a real marker on
the wall: still seen in >= MIN_VIEWS photos after the false-detection checks, not down-weighted as an outlier
(reprojection within the capture's normal bounds), and adopted by facet assignment into a declared facet it lies on
(normal < mergeDeg, every corner < mergeMm off the plane; facets.py). An unplanned marker never defines a facet of
its own and never gets a planned-position prior. Anything else is dropped with all its detections, reported once
per marker in quality.rejectedObservations (reason REASON), and the structure is solved again without it.
"""
import dataclasses

REASON = "unplanned-misfit"
MIN_VIEWS = 2


def misfits(req, obs, members, facet_segment, downweighted):
    """{marker id: why} of the unplanned markers the solve does not confirm."""
    if not req.unplanned:
        return {}
    views = {}
    for o in obs:
        views[o["id"]] = views.get(o["id"], 0) + 1
    placed = {m for f, ms in members.items() if facet_segment.get(f) in req.segments for m in ms}
    out = {}
    for m in sorted(req.unplanned & set(views)):
        if views[m] < MIN_VIEWS:
            out[m] = f"seen in only {views[m]} photo after the false-detection checks"
        elif m in downweighted:
            out[m] = "it reprojects far worse than the other markers"
        elif m not in placed:
            out[m] = "it does not lie on any solved surface"
    return out


def without(req, ids):
    """The parsed request without any detection of `ids`."""
    photos = [{**p, "markers": [m for m in p["markers"] if m["id"] not in ids]} for p in req.photos]
    return dataclasses.replace(req, photos=photos)


def records(req, off):
    """One rejectedObservations record per dropped unplanned marker (its first photo names it)."""
    out = []
    for m, why in sorted(off.items()):
        photos = [p["name"] for p in req.photos if any(k["id"] == m for k in p["markers"])]
        out.append({"photo": photos[0] if photos else "", "id": int(m), "views": len(photos), "residualPx": None,
                    "thresholdPx": None, "otherViewsResidualPx": None, "reason": REASON, "markerDropped": True,
                    "detail": f"not in the marker plan and {why}"})
    return out
