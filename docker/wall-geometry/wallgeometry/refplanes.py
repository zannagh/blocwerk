"""Gravity references that are not flat, and facet decisions close to the fold threshold.

A segment declared `verticalReference` says "this piece is vertical". When its markers split into
several facets (facets.py: planes > `foldDeg` apart), giving every piece a full, equal vote in the
gravity least squares lets a two-marker end outvote the rest, and the result jumps whenever the fold
crosses the threshold (The Attic's kickboard: 4.97 deg -> one facet, main wall 45.2 deg; 5.33 deg ->
two facets, 46.5 deg). Weighting the piece normals by markers x extent swung it to 44.3 deg instead.

So the declaration is applied to what it is about: the WHOLE segment. For gravity only, the facet
bundle adjustment is re-run once with that segment as one plane (every other facet unchanged); its
normal, rotated into the split solve's frame, is the segment's single gravity constraint. That plane is
what the unsplit solve would have used, so the answer is continuous across the fold threshold, and
it leans towards the better observed pieces by the bundle adjustment's own weighting (markers,
photos, lever arm). The pieces stay separate facets for the geometry and become checks: their lean
against the resulting vertical is reported in `quality.gravityDetail.splitReferences`.
"""
import itertools

import numpy as np

from .facets import angle
from .frame import tilt_from_vertical

BORDERLINE_DEG = 0.5  # a fold within this of `foldDeg` could have gone the other way


def split_reference_segments(members, facet_segment, segments):
    """{segment: [facet ids]} of declared vertical-reference segments that have several facets."""
    out = {}
    for f in sorted(members):
        s = facet_segment[f]
        if s in segments and segments[s].vertical_reference:
            out.setdefault(s, []).append(f)
    return {s: fs for s, fs in out.items() if len(fs) > 1}


def _frame_rotation(cams_to, cams_from):
    """Rotation Q with X_to = Q X_from, from the cameras both solves share (their mean)."""
    acc = sum(cams_to[i][0].T @ cams_from[i][0] for i in cams_to if i in cams_from)
    u, _, vt = np.linalg.svd(acc)
    q = u @ vt
    if np.linalg.det(q) < 0:
        u[:, -1] = -u[:, -1]
        q = u @ vt
    return q


def whole_segment_planes(sol, facet_solve):
    """{segment: {"facets": [...], "normal": n}}: each split reference segment's single-plane normal in
    the frame of `sol` (the split facet solve). Empty (no extra solve) when nothing is split."""
    split = split_reference_segments(sol["members"], sol["facet_segment"], sol["req"].segments)
    if not split:
        return {}
    pieces = {f for fs in split.values() for f in fs}
    merged = {f: ms for f, ms in sol["members"].items() if f not in pieces}
    keys = {}
    for s, fs in split.items():
        keys[s] = f"whole{s}"
        merged[keys[s]] = sorted(m for f in fs for m in sol["members"][f])
    wprob, wx = facet_solve(sol["prob"], sol["x"], merged, sol["free_intr"])
    fp, fx = sol["fprob"], sol["fx"]
    q = _frame_rotation({i: fp.cam(fx, i) for i in fp.images}, {i: wprob.cam(wx, i) for i in wprob.images})
    return {s: {"facets": split[s], "normal": q @ wprob.facet_frame(wx, keys[s])[0][:, 2]} for s in split}


def gravity_refs(ref_facets, normals, facet_segment, planes):
    """[(label, normal)]: a split segment's pieces are replaced by its whole-segment plane."""
    refs = []
    for f in ref_facets:
        seg = facet_segment[f]
        if seg not in planes:
            refs.append((f"facet {f} normal", normals[f]))
        elif f == planes[seg]["facets"][0]:
            refs.append((plane_label(seg, planes[seg]["facets"]), planes[seg]["normal"]))
    return refs


def plane_label(seg, facets):
    return f"segment {seg} plane (facets {'+'.join(facets)})"


def _extent_mm(corners):
    pts = np.vstack(corners)
    c = pts - pts.mean(0)
    _, _, vt = np.linalg.svd(c)
    return float(np.ptp(c @ vt[0]))


def _shares(dev):
    """Lever rule: the whole plane lies between the pieces; the closer a piece, the larger its share."""
    inv = np.array([1.0 / max(d, 1e-3) for d in dev])
    return inv / inv.sum()


def _decided_fold(decisions, seg):
    return next((d["minPlaneAngleDeg"] for d in decisions if d["kind"] == "split" and d["segment"] == seg), None)


def split_report(sol, planes, up):
    """quality.gravityDetail.splitReferences (up None: gravity unknown, no lean). `foldDeg` is the angle
    the split was decided on (free solve); `pieceNormalsAngleDeg` the largest one in the final solve."""
    n_obs = {}
    for o in sol["obs"]:
        n_obs[o["id"]] = n_obs.get(o["id"], 0) + 1
    out = []
    for seg, pl in sorted(planes.items()):
        fs, nw = pl["facets"], pl["normal"]
        dev = [angle(sol["normals"][f], nw) for f in fs]
        pieces = []
        for f, d, w in zip(fs, dev, _shares(dev)):
            ms = sol["members"][f]
            pieces.append({"facet": f, "markerIds": sorted(ms), "observations": sum(n_obs.get(m, 0) for m in ms),
                           "extentMm": round(_extent_mm([sol["corners_ba"][m] for m in ms]), 1),
                           "angleToSegmentPlaneDeg": round(d, 3), "share": round(float(w), 3),
                           "leanDeg": None if up is None else round(tilt_from_vertical(sol["normals"][f], up), 3)})
        final = max(angle(sol["normals"][a], sol["normals"][b]) for a, b in itertools.combinations(fs, 2))
        decided = _decided_fold(sol["decisions"], seg)
        out.append({"segment": seg, "name": sol["req"].segment_name(seg),
                    "foldDeg": round(final if decided is None else decided, 3),
                    "pieceNormalsAngleDeg": round(final, 3), "method": "whole-segment plane", "pieces": pieces})
    return out


def _split_text(r):
    ps = sorted(r["pieces"], key=lambda p: -p["share"])
    main, rest = ps[0], ps[1:]
    others = "; ".join(f"markers {p['markerIds']} (facet {p['facet']})" for p in rest)
    text = (f"{r['name']} is declared vertical but is not flat: {others} lie at {r['foldDeg']:.1f}° to "
            f"markers {main['markerIds']} (facet {main['facet']}). Gravity was taken from the whole "
            f"segment's plane, which follows mainly the better-supported part {main['markerIds']} "
            f"({main['share'] * 100:.0f} %).")
    if all(p["leanDeg"] is not None for p in ps):
        leans = ", ".join(f"facet {p['facet']} {p['leanDeg']:+.1f}°" for p in ps)
        text += f" Against that vertical the pieces lean {leans} (+ = overhang)."
    return text


def borderline_decisions(decisions):
    """Split / rejected-split decisions whose plane angle is within BORDERLINE_DEG of foldDeg."""
    return [{"segment": d["segment"], "kind": d["kind"], "minPlaneAngleDeg": d["minPlaneAngleDeg"],
             "foldDeg": d["foldDeg"]}
            for d in decisions if d.get("minPlaneAngleDeg") is not None and d.get("foldDeg") is not None
            and abs(d["minPlaneAngleDeg"] - d["foldDeg"]) <= BORDERLINE_DEG]


def _borderline_text(b, name):
    verdict = "split it into facets" if b["kind"] == "split" else "kept it as one facet"
    return (f"{name}: the facet decision is borderline. Its parts differ by {b['minPlaneAngleDeg']:.2f}° "
            f"against the {b['foldDeg']:g}° fold threshold, so the solver {verdict}, but one or two more or "
            f"different photos can tip it the other way.")


def warnings(sol):
    """Human-readable checks for quality.checks.warnings."""
    out = [_split_text(r) for r in sol["gravity"].get("splitReferences", [])]
    out += [_borderline_text(b, sol["req"].segment_name(b["segment"])) for b in borderline_decisions(sol["decisions"])]
    return out
