"""World-frame conversion and the wall-geometry document (contract: tools/glyph/wall-geometry.schema.md)."""
import numpy as np

from .camera import K_matrix
from .frame import facet_axes, world_transform
from .refplanes import borderline_decisions, warnings

EXTENT_MARGIN_MM = 50.0
UP = np.array([0.0, 0, 1])


def to_world(sol):
    """World frame: z = up, x along the reference facet; origin = reference facet's origin O."""
    R = world_transform(sol["normals"][sol["ref_facet"]], sol["up"])
    W0 = lambda X: np.asarray(X) @ R.T  # no shift yet
    facets = {}
    for fid, ms in sol["members"].items():
        F, p = sol["fprob"].facet_frame(sol["fx"], fid)
        n = R @ F[:, 2]
        u, v = facet_axes(n, UP)
        pw = W0(p)
        ab = {m: np.stack([(W0(sol["corners_ba"][m]) - pw) @ u, (W0(sol["corners_ba"][m]) - pw) @ v], 1)
              for m in ms}
        allab = np.vstack(list(ab.values()))
        amin, bmin = allab.min(0)
        span = allab.max(0) - allab.min(0)
        facets[fid] = {"origin": pw + amin * u + bmin * v, "u": u, "v": v, "normal": n,
                       "ab": {m: a - [amin, bmin] for m, a in ab.items()},
                       "extent": {"aMin": -EXTENT_MARGIN_MM, "aMax": float(span[0] + EXTENT_MARGIN_MM),
                                  "bMin": -EXTENT_MARGIN_MM, "bMax": float(span[1] + EXTENT_MARGIN_MM)}}
    shift = facets[sol["ref_facet"]]["origin"].copy()
    for f in facets.values():
        f["origin"] = f["origin"] - shift
    o_ba = R.T @ shift  # world origin, BA frame
    corners = {m: W0(c) - shift for m, c in sol["corners_ba"].items()}
    cams = {}
    for img, (Rc, tc) in sol["cams_ba"].items():
        Rw = Rc @ R.T
        t = Rc @ o_ba + tc
        cams[img] = {"R": Rw, "t": t, "centre": -Rw.T @ t}
    sol["world"] = {"R": R, "facets": facets, "corners": corners, "cams": cams}
    return sol


def _l(a, nd=3):
    return [round(float(v), nd) for v in np.ravel(a)]


def _r(v, nd=3):
    return None if v is None else round(float(v), nd) + 0.0  # no "-0.0"


def _gravity_text(sol):
    g = sol["gravity"]
    if not g["known"]:
        return "unknown"
    return "least squares over " + ", ".join(g["constraints"])


def _segments(sol):
    req, wd, ang, fseg = sol["req"], sol["world"], sol["angles"], sol["facet_segment"]
    known = sol["gravity"]["known"]
    out = []
    for seg in sorted(set(fseg.values()) | set(req.segments)):
        fids = sorted(f for f, s in fseg.items() if s == seg)
        decl = req.segments.get(seg)
        facets = []
        for f in fids:
            fw = wd["facets"][f]
            facets.append({"id": f, "origin": _l(fw["origin"], 2), "u": _l(fw["u"], 6), "v": _l(fw["v"], 6),
                           "normal": _l(fw["normal"], 6),
                           "measuredAngleDeg": _r(ang[f]["tiltDeg"]) if known else None,
                           "yawDeg": _r(ang[f]["yawDeg"]) if known else None,
                           "angleToReferenceFacetDeg": _r(ang[f]["angleToReferenceDeg"]),
                           "extentMm": {k: round(v, 1) for k, v in fw["extent"].items()},
                           "markerIds": sorted(sol["members"][f])})
        measured = facets[0]["measuredAngleDeg"] if len(facets) == 1 else None
        declared = decl.declared_angle_deg if decl else None
        out.append({"index": seg, "name": req.segment_name(seg), "declared": decl is not None,
                    "declaredAngleDeg": declared, "measuredAngleDeg": measured,
                    "declaredVsMeasuredDeg": (_r(measured - declared) if measured is not None
                                              and declared is not None else None),
                    "angleIsGravityReference": bool(decl and decl.vertical_reference and known),
                    "facets": facets})
    return out


def _markers(sol, per_mk, measured):
    wd, req = sol["world"], sol["req"]
    fid_of = {m: f for f, ms in sol["members"].items() for m in ms}
    n_obs = {}
    for o in sol["obs"]:
        n_obs[o["id"]] = n_obs.get(o["id"], 0) + 1
    syn = {o["id"]: o["synthetic_corners"] for o in sol["obs"] if o["synthetic"]}
    out = []
    for m in sorted(fid_of):
        f = fid_of[m]
        rec = {"id": m, "segment": sol["facet_segment"][f], "nominalSegment": req.segment_of(m), "role": req.role_of(m),
               "facet": f, "sizeMm": req.marker_size(m),
               "cornersPlaneMm": [_l(p, 2) for p in wd["facets"][f]["ab"][m]],
               "cornersWorldMm": [_l(p, 2) for p in wd["corners"][m]],
               "observations": n_obs.get(m, 0),
               "reprojRmsPx": _r(per_mk.get(m, {}).get("rmsPx")),
               "measuredSideMm": _r(measured[m]["sideMm"], 1) if m in measured else None,
               "measuredSidePhotos": measured[m]["photos"] if m in measured else None,
               "synthetic": m in syn, "syntheticCorners": syn.get(m, [])}
        if m in sol["downweighted"]:
            rec["downweightedSigmaPx"] = sol["downweighted"][m]["sigmaPx"]
        out.append(rec)
    return out


def _cameras(sol, per_img):
    fp, fx, wd = sol["fprob"], sol["fx"], sol["world"]
    cams = []
    for img in fp.images:
        c = sol["cams"][img]
        intr = fp.intr(fx, c["group"])
        cams.append({"image": img, "group": c["group"], "width": c["w"], "height": c["h"],
                     "K": _l(K_matrix(intr, c["w"], c["h"], c["psign"]), 3),
                     "dist": _l([intr[3], intr[4], 0, 0, intr[5]], 6),
                     "R": _l(wd["cams"][img]["R"], 6), "t": _l(wd["cams"][img]["t"], 2),
                     "reprojRmsPx": _r(per_img.get(img, {}).get("rmsPx"))})
    return cams


def _level_checks(sol):
    out = []
    for a, b in sol["level_pairs"]:
        c = sol["world"]["corners"]
        if a in c and b in c:
            out.append({"pair": [a, b], "heightDiffMm": _r(c[a].mean(0)[2] - c[b].mean(0)[2], 2)})
    return out


def build_document(sol, checks):
    """checks: dict from validation (per_image, per_marker, side, distortion, optional loo)."""
    req, g = sol["req"], sol["gravity"]
    segs = _segments(sol)
    decl_checks = {str(s["index"]): s["declaredVsMeasuredDeg"] for s in segs
                   if s["declaredVsMeasuredDeg"] is not None and not s["angleIsGravityReference"]}
    q = {"reprojRmsPx": _r(sol["rms_facet"]), "reprojRmsPxFree": _r(sol["rms_free"]),
         "gravity": _gravity_text(sol),
         "gravityDetail": {k: v for k, v in g.items() if k != "known"},
         "checks": {"declaredVsMeasuredDeg": decl_checks,
                    "markerSideRmsErrMm": _r(checks["side"]["rmsErrMm"]),
                    "markerSideMeanErrMm": _r(checks["side"]["meanErrMm"]),
                    "levelPairs": _level_checks(sol),
                    "borderlineFacetDecisions": borderline_decisions(sol["decisions"]),
                    "warnings": warnings(sol)},
         "facetDecisions": sol["decisions"],
         "downweightedMarkers": {str(k): v for k, v in sol["downweighted"].items()},
         "rejectedObservations": sol.get("rejected", []),
         "unusedPhotos": sol["unreached"],
         "intrinsics": checks["distortion"],
         "coplanarityFreeSolveMm": {k: _r(v["rmsMm"], 2) for k, v in sol["coplanarity_free"].items()},
         "cornerSource": "as supplied by the client (expected: edge-line refined, see refine.py)"}
    if "referenceNormalsAngleDeg" in g:
        q["n1n2AngleDeg"] = _r(g["referenceNormalsAngleDeg"])
    if "0" in decl_checks:
        q["checks"]["seg0DeclaredVsMeasuredDeg"] = decl_checks["0"]
    if checks.get("loo"):
        q["leaveOnePhotoOut"] = checks["loo"]
    origin = (f"facet {sol['ref_facet']} origin: the bottom-left of its markers' bounding box in its own "
              "plane (a = along u, b = along v); x = horizontal along that facet pointing right as you "
              "face it, y = z × x (into the wall), z = up")
    if not g["known"]:
        origin += ". GRAVITY UNKNOWN: z is the reference facet's in-plane 'up' (the facet is treated as "\
                  "vertical), so absolute angles are not measured; mm are still valid"
    doc = {"version": 1, "units": "mm", "dictionary": req.dictionary, "idScheme": req.id_scheme,
           "markerSizeMm": req.marker_size_mm,
           "world": {"origin": origin, "up": [0, 0, 1], "gravityKnown": g["known"],
                     "referenceFacet": sol["ref_facet"]},
           "segments": segs, "markers": _markers(sol, checks["per_marker"], checks["side"].get("measured", {})),
           "cameras": _cameras(sol, checks["per_image"]), "quality": q}
    if req.size_overrides_mm:
        doc["markerSizeOverridesMm"] = {str(k): v for k, v in req.size_overrides_mm.items()}
    if req.marker_segments:
        doc["markerSegments"] = {str(k): v for k, v in sorted(req.marker_segments.items())}
    return doc
