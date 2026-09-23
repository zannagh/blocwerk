"""Checks: per-image/marker RMS, independent marker-side triangulation, optional leave-one-out."""
import numpy as np

from .camera import corner_distortion_px, image_pp, undistort_px


def per_image_marker_rms(sol):
    e, obs = sol["err_facet"], sol["obs"]
    per_img, per_mk = {}, {}
    for k, o in enumerate(obs):
        real = o["sigma"] < 10.0  # everything but synthetic corners
        per_img.setdefault(o["image"], []).extend(e[k][real])
        per_mk.setdefault(o["id"], []).extend(e[k][real])
    f = lambda d: {k: {"n": len(v), "rmsPx": float(np.sqrt(np.mean(np.square(v)))) if v else None}
                   for k, v in sorted(d.items())}
    return f(per_img), f(per_mk)


def distortion(sol):
    fp, fx, cams = sol["fprob"], sol["fx"], sol["cams"]
    out = {}
    for g in fp.groups:
        img = next(i for i, c in cams.items() if c["group"] == g)
        c = cams[img]
        mx, _ = corner_distortion_px(fp.intr(fx, g), c["w"], c["h"], c["psign"])
        out[g] = {"intr": [round(float(v), 6) for v in fp.intr(fx, g)], "cornerShiftMaxPx": round(mx, 2),
                  "images": sum(1 for cc in cams.values() if cc["group"] == g),
                  "freeParams": sol["free_intr"].get(g, [])}
    return out


def _triangulate(rays):
    A, b = np.zeros((3, 3)), np.zeros(3)
    for o, d in rays:
        P = np.eye(3) - np.outer(d, d)
        A += P
        b += P @ o
    return np.linalg.solve(A, b)


def side_check(sol):
    """Triangulate every corner seen >= 2x with the solved cameras FIXED; compare sides with the
    declared marker size. No marker-size prior enters the triangulation: an independent scale check."""
    fp, fx, cams, req = sol["fprob"], sol["fx"], sol["cams"], sol["req"]
    by_id = {}
    for o in sol["obs"]:
        if not o["synthetic"] and o["id"] in fp.mids:
            by_id.setdefault(o["id"], []).append(o)
    errs, per = [], {}
    for m, ol in by_id.items():
        if len(ol) < 2:
            continue
        pts = []
        for ci in range(4):
            rays = []
            for o in ol:
                c = cams[o["image"]]
                intr = fp.intr(fx, c["group"])
                R, t = fp.cam(fx, o["image"])
                u = undistort_px(o["corners"][ci:ci + 1], intr, c["w"], c["h"], c["psign"])[0]
                cx, cy = image_pp(intr, c["w"], c["h"], c["psign"])
                d = R.T @ np.array([(u[0] - cx) / intr[0], (u[1] - cy) / intr[0], 1.0])
                rays.append((-R.T @ t, d / np.linalg.norm(d)))
            pts.append(_triangulate(rays))
        pts = np.array(pts)
        sides = np.linalg.norm(pts - np.roll(pts, -1, 0), axis=1)
        per[m] = [round(float(s), 2) for s in sides]
        errs.extend(sides - req.marker_size(m))
    if not errs:
        return {"markers": 0, "rmsErrMm": None, "meanErrMm": None, "perMarker": {}}
    errs = np.array(errs)
    return {"markers": len(per), "rmsErrMm": float(np.sqrt(np.mean(errs ** 2))),
            "meanErrMm": float(errs.mean()), "perMarker": per}


def leave_one_out(sol, progress=None):
    """Re-solve without each photo (facets fixed), report the reference facet's tilt spread."""
    from .solver import apply_gravity, solve_structure
    rows = {}
    imgs = sorted(sol["cams"])
    ref = sol["ref_facet"]
    for k, img in enumerate(imgs):
        if progress:
            progress(k / len(imgs), f"leave-one-out {img}")
        s = apply_gravity(solve_structure(sol["req"], drop_image=img, members=sol["members"]),
                          sol["level_pairs"])
        a = s["angles"].get(ref, {})
        rows[img] = {"refTiltDeg": a.get("tiltDeg"), "rmsPx": s["rms_facet"],
                     "markersLost": sorted(set(sol["prob"].mids) - set(s["prob"].mids))}
    t = np.array([r["refTiltDeg"] for r in rows.values() if r["refTiltDeg"] is not None])
    return {"referenceFacet": ref, "rows": rows,
            "min": float(t.min()) if t.size else None, "max": float(t.max()) if t.size else None,
            "std": float(t.std()) if t.size else None}
