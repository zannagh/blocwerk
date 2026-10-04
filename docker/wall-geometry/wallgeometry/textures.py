"""Per-facet rectified orthophotos ("textures") from the photos + a solved geometry document.

For every facet, a regular grid on the facet plane (a along u, b along v, default 2 mm/px) is filled
from ONE photo per pixel: the photo that sees that spot with the highest resolution and least
obliqueness, score = f * cos(view angle) / distance (image px per mm). No averaging, so protruding
holds are not ghosted. The choice is made on a coarse label grid, cleaned with a mode filter (no
speckle), then upsampled; seams therefore run along cell boundaries.

Pixel convention of every output image: column i, row j (top-left origin) covers plane point
  a = aMin + (i + 0.5) * mmPerPx,   b = bMax - (j + 0.5) * mmPerPx
i.e. +a to the right, +b up, exactly the facet frame of the geometry document.

A photo is not used for a plane point when another facet's region lies between the point and the
camera (occlusion.py: ray vs the occluder's real region, never "behind its infinite plane"). Points no
photo sees are left black; the per-facet coverage mask (`coverage_mask`) marks them 0 so a viewer can
show the plain facet there instead. Holds are not modelled as occluders.

By default (blendViews > 1, see blended.py) every photo is first exposure / white-balance balanced
(exposure.py), and the per-cell choice penalises photos that disagree with what the other photos see
there (consensus.py), so occluders not in the model (roof rafters) are not painted onto the wall;
seams are feathered from the top-N sample slots (blend.py). blendViews = 1 is the plain method above.
Finally facets at the same overhang are evenly shaded to one common level (flatten.py), and every
shared edge is smoothed locally (seams.py) so no hard brightness / colour line shows there.
"""
import math

import cv2
import numpy as np

from . import blend, consensus, exposure, flatten, occlusion, scale, seams, sourcemap, views
from .camera import max_valid_radius2
from . import kernel
from .kernel import MIN_FACING_COS, NEAR_PLANE_MM
from .markercheck import marker_check

DEFAULTS = {"behindOtherFacetMm": NEAR_PLANE_MM, "mmPerPx": 2.0, "maxSidePx": 4096, "extraMarginMm": 100.0,
            "labelCellPx": 8, "modeFilterCells": 5, "imageMarginPx": 16, "jpegQuality": 90, "maskFeatherPx": 4.0,
            "blendMaxBytes": 2.0e9, **blend.BLEND_DEFAULTS, **exposure.GAIN_DEFAULTS,
            **consensus.CONSENSUS_DEFAULTS, **flatten.FLATTEN_DEFAULTS,
            **seams.SEAM_DEFAULTS}


# What a client may set in `options`, with its bounds (everything else in DEFAULTS is internal).
CLIENT_OPTIONS = {"mmPerPx": (0.25, 50.0, float), "maxSidePx": (256, 8192, int),
                  "extraMarginMm": (0.0, 2000.0, float), "jpegQuality": (30, 100, int),
                  "blendViews": (1, 8, int)}


class TextureError(ValueError):
    """Inputs are inconsistent (e.g. a photo's size differs from the solved camera)."""


def validate_params(options):
    """Client options -> clean dict; TextureError on unknown keys or out-of-range / non-finite values."""
    if options is None:
        return {}
    if not isinstance(options, dict):
        raise TextureError("'options' must be a JSON object")
    unknown = set(options) - set(CLIENT_OPTIONS)
    if unknown:
        raise TextureError(f"unknown option(s) {sorted(unknown)}; known: {sorted(CLIENT_OPTIONS)}")
    out = {}
    for k, v in options.items():
        lo, hi, typ = CLIENT_OPTIONS[k]
        ok = isinstance(v, (int, float)) and not isinstance(v, bool) and math.isfinite(v) and lo <= v <= hi
        if not ok or (typ is int and v != int(v)):
            kind = "an integer" if typ is int else "a number"
            raise TextureError(f"options.{k} must be {kind} in [{lo:g}, {hi:g}]")
        out[k] = typ(v)
    return out


def output_pixels(doc, params):
    """Total output pixels the facets of `doc` would take with these params (memory budget check)."""
    p = {**DEFAULTS, **(params or {})}
    return sum(g["W"] * g["H"] for g in (_grid(f, p) for f in _facets(doc)))


def _cam(c):
    K = np.array(c["K"], float).reshape(3, 3)
    d = np.array(c["dist"], float)
    k = (d[0], d[1], d[4] if len(d) > 4 else 0.0)
    p = tuple(d[i] if len(d) > i else 0.0 for i in (2, 3))  # missing terms are 0, as SolvedCamera.D(i)
    return {"K": K, "k": k, "p": p, "r2max": max_valid_radius2(k),
            "R": np.array(c["R"], float).reshape(3, 3), "t": np.array(c["t"], float),
            "w": int(c["width"]), "h": int(c["height"])}


def project(cam, X):
    """World points (...,3) -> (pixels (...,2), depth (...), camera-frame points), with k1, k2, p1, p2, k3
    and skew as SolvedCamera.Project (C#; agrees to ~1 ulp). Past the radial fold (r2 > max_valid_radius2,
    the same cut-off as C#) a point gets a pixel far outside the image. p = 0 and skew = 0 add exact zeros."""
    Xc = X @ cam["R"].T + cam["t"]
    z = Xc[..., 2]
    zs = np.where(z > 1e-6, z, 1e-6)
    xn, yn = Xc[..., 0] / zs, Xc[..., 1] / zs
    r2 = xn * xn + yn * yn
    k1, k2, k3 = cam["k"]
    p1, p2 = cam.get("p", (0.0, 0.0))
    d = 1 + k1 * r2 + k2 * r2 * r2 + k3 * r2 * r2 * r2
    tx = 2 * p1 * xn * yn + p2 * (r2 + 2 * xn * xn)
    ty = p1 * (r2 + 2 * yn * yn) + 2 * p2 * xn * yn
    K = cam["K"]
    px = np.stack([K[0, 0] * xn * d + K[0, 0] * tx + K[0, 1] * (yn * d + ty) + K[0, 2],
                   K[1, 1] * yn * d + K[1, 1] * ty + K[1, 2]], -1)
    px[r2 > cam.get("r2max", np.inf)] = -1e6
    return px, z, Xc


def _facets(doc):
    for s in doc["segments"]:
        for f in s["facets"]:
            yield f


def _grid(f, p):
    e = f["extentMm"]
    m = p["extraMarginMm"]
    a0, a1, b0, b1 = e["aMin"] - m, e["aMax"] + m, e["bMin"] - m, e["bMax"] + m
    res = max(p["mmPerPx"], max(a1 - a0, b1 - b0) / p["maxSidePx"])
    W, H = max(1, int(math.ceil((a1 - a0) / res))), max(1, int(math.ceil((b1 - b0) / res)))
    return {"aMin": a0, "bMax": b1, "res": res, "W": W, "H": H,
            "bounds": {"aMin": round(a0, 2), "aMax": round(a0 + W * res, 2),
                       "bMin": round(b1 - H * res, 2), "bMax": round(b1, 2)}}


def _plane_points(f, g, cols, rows):
    """World points for pixel centres (cols, rows arrays, broadcastable)."""
    O, u, v = (np.array(f[k], float) for k in ("origin", "u", "v"))
    a = g["aMin"] + (cols + 0.5) * g["res"]
    b = g["bMax"] - (rows + 0.5) * g["res"]
    return O + a[..., None] * u + b[..., None] * v


def _score(cam, f, X, margin):
    """Image px per plane mm at X (0 where the camera cannot see it)."""
    px, z, Xc = project(cam, X)
    n = np.array(f["normal"], float)
    centre = -cam["R"].T @ cam["t"]
    ray = centre - X
    dist = np.linalg.norm(ray, axis=-1)
    cos = (ray @ n) / np.maximum(dist, 1e-9)
    ok = (z > 1e-3) & (cos > MIN_FACING_COS)
    ok &= (px[..., 0] >= margin) & (px[..., 0] <= cam["w"] - 1 - margin)
    ok &= (px[..., 1] >= margin) & (px[..., 1] <= cam["h"] - 1 - margin)
    return np.where(ok, cam["K"][0, 0] * cos / np.maximum(dist, 1e-9), 0.0)


def occluders(facets, doc):
    """Every facet as a view blocker (occlusion.py), built once per render."""
    return occlusion.occluders(facets, doc.get("markers", []), kernel.extent_excluded(doc))


def _cell_views(f, g, cams, names, p, occs):
    """Per label cell: world points X (ch, cw, 3) and every photo's score, kept sparse per photo
    (views.FacetViews, field "S"); a photo scores 0 where another facet's region (`occs`, the other
    facets' occlusion.Occluder) blocks its view."""
    X = blend.cell_points(f, g, p["labelCellPx"], _plane_points)
    others = [o for o in occs if o.id != f["id"]]
    flat = X.reshape(-1, 3)
    fv = views.FacetViews(X.shape[:2])
    for k, n in enumerate(names):
        s = _score(cams[n], f, X, p["imageMarginPx"])  # (ch, cw): one photo at a time
        sk = s.reshape(-1)
        seen = np.nonzero(sk > 0)[0]
        if seen.size and others:
            centre = -cams[n]["R"].T @ cams[n]["t"]
            sk[seen[occlusion.hidden(centre, others, flat[seen], p["behindOtherFacetMm"])]] = 0
        fv.add(k, s)
    return X, fv


def _upsample(lab, g, cell):
    """Per-cell photo choice (views.cell_labels) -> the full texture grid."""
    ch, cw = lab.shape
    full = cv2.resize(lab.astype(np.int16), (cw * cell, ch * cell), interpolation=cv2.INTER_NEAREST)
    return full[:g["H"], :g["W"]]


def _render_part(img, cam, f, g, mask, out, filled):
    ys, xs = np.nonzero(mask)
    if ys.size == 0:
        return
    y0, y1, x0, x1 = ys.min(), ys.max() + 1, xs.min(), xs.max() + 1
    cols, rows = np.meshgrid(np.arange(x0, x1, dtype=np.float64), np.arange(y0, y1, dtype=np.float64))
    px, z, _ = project(cam, _plane_points(f, g, cols, rows))
    px = np.clip(px, -1e6, 1e6)
    mx, my = px[..., 0].astype(np.float32), px[..., 1].astype(np.float32)
    patch = cv2.remap(img, mx, my, cv2.INTER_LINEAR, borderMode=cv2.BORDER_CONSTANT)
    inside = (mx >= 0) & (mx <= cam["w"] - 1) & (my >= 0) & (my <= cam["h"] - 1) & (z > 0)
    m = mask[y0:y1, x0:x1] & inside & ~filled[y0:y1, x0:x1]
    out[y0:y1, x0:x1][m] = patch[m]
    filled[y0:y1, x0:x1] |= m


def render_textures(doc, load_photo, available, params=None, progress=None):
    """doc: geometry document; load_photo(name) -> BGR uint8 image; available: photo names.

    Returns a list of {facet, image (BGR), mask (uint8, see coverage_mask), source (sourcemap.py), mmPerPx,
    bounds, widthPx, heightPx, photosUsed, coverage, markerCheck} (+ exposureGains when balanced).
    blendViews > 1 (default): robust multi-view blend (see blend.py); 1: the single best photo per pixel.
    """
    p = scale.at_resolution({**DEFAULTS, **(params or {})}, params)  # physical sizes kept at any mmPerPx
    cams = {c["image"]: _cam(c) for c in doc.get("cameras", []) if c["image"] in available}
    if not cams:
        raise TextureError("none of the uploaded photos matches a camera of the geometry document")
    names = sorted(cams)
    facets = list(_facets(doc))
    occs = occluders(facets, doc)
    jobs = _score_facets(facets, cams, names, p, occs)
    p = fit_blend_views(jobs, p)
    if int(p["blendViews"]) > 1:
        from . import blended  # imports this module
        results = blended.render(doc, load_photo, cams, names, jobs, p, progress)
    else:
        results = _render_single(doc, load_photo, cams, names, jobs, p, progress)
    fid = {f["id"]: f for f in facets}
    if p["flattenShading"]:
        shading = flatten.flatten(results, fid, p)
        for r in results:
            if r["facet"] in shading:
                r["shading"] = shading[r["facet"]]
    if p["seamHarmonise"] and len(results) > 1:
        report = seams.harmonise(results, fid, p, {o.id: o for o in occs})
        for r in results:
            r["seams"] = {k: v for k, v in report.items() if r["facet"] in k.split("-")}
    return results


def _checked_photo(load_photo, n, cam):
    img = load_photo(n)
    if img.shape[1] != cam["w"] or img.shape[0] != cam["h"]:
        raise TextureError(f"photo {n} is {img.shape[1]}x{img.shape[0]} but was solved as "
                           f"{cam['w']}x{cam['h']} (orientation or resizing mismatch)")
    return img


def _score_facets(facets, cams, names, p, occs):
    jobs = []
    for f in facets:
        g = _grid(f, p)
        X, fv = _cell_views(f, g, cams, names, p, occs)
        jobs.append({"f": f, "g": g, "X": X, "views": fv})
    return jobs


def blend_bytes(jobs, p):
    """Peak memory of the multi-view blend: the sample slots ((blendViews + 2) x 7 bytes per output pixel),
    every photo's view crops (views.BYTES_PER_CELL per photo and label cell it sees) and the int16 map of
    who painted each pixel of one facet at a time (plus its per-cell reshape, sourcemap.drawn_cells)."""
    pixels = [j["g"]["W"] * j["g"]["H"] for j in jobs]
    return ((int(p["blendViews"]) + 2) * 7 * sum(pixels) + 4 * max(pixels, default=0)
            + views.BYTES_PER_CELL * sum(j["views"].cells() for j in jobs))


def fit_blend_views(jobs, p):
    """p with `blendViews` lowered until the multi-view blend fits `blendMaxBytes` (down to 2 views; 1 =
    the plain single-photo render when even that does not fit). A fine texture of a big wall used to fall
    straight back to the single-photo render, which has no exposure balance and no seam blending."""
    n = int(p["blendViews"])
    while n > 1 and blend_bytes(jobs, {**p, "blendViews": n}) > p["blendMaxBytes"]:
        n -= 1
    return {**p, "blendViews": n}


def _render_single(doc, load_photo, cams, names, jobs, p, progress):
    for j in jobs:
        g = j["g"]
        j["cells"] = j.pop("views").labels("S", int(p["modeFilterCells"]))
        j.update(lab=_upsample(j["cells"], g, p["labelCellPx"]), out=np.zeros((g["H"], g["W"], 3), np.uint8),
                 filled=np.zeros((g["H"], g["W"]), bool))
    for k, n in enumerate(names):
        if progress:
            progress(k / len(names), f"rendering from {n}")
        if not any((j["lab"] == k).any() for j in jobs):
            continue
        cam = cams[n]
        img = _checked_photo(load_photo, n, cam)
        for j in jobs:
            _render_part(img, cam, j["f"], j["g"], j["lab"] == k, j["out"], j["filled"])
        del img
    results = []
    for j in jobs:
        lab, g = j["lab"], j["g"]
        used = {names[k]: round(float((lab == k).mean()), 4) for k in range(len(names)) if (lab == k).any()}
        results.append({"facet": j["f"]["id"], "image": j["out"],
                        "mask": coverage_mask(j["filled"], p["maskFeatherPx"]), "mmPerPx": g["res"], "bounds": g["bounds"],
                        "widthPx": g["W"], "heightPx": g["H"], "photosUsed": used,
                        "coverage": round(float(j["filled"].mean()), 4),
                        "source": sourcemap.from_cells(j["cells"], names, g, p["labelCellPx"]),
                        "markerCheck": marker_check(j["out"], j["f"], doc, g["res"], g)})
    return results


def coverage_mask(filled, feather_px=DEFAULTS["maskFeatherPx"]):
    """Photo coverage as an 8-bit alpha mask, same grid as the image: 0 where no photo was drawn
    (outside every photo, behind another facet), 255 inside, ramping linearly over `feather_px`
    pixels INSIDE the covered area so the seam to the plain facet is soft and the black fill never
    bleeds through (bilinear sampling or JPEG ringing along the edge)."""
    m = filled.astype(np.uint8) * 255
    if feather_px <= 0 or not filled.any():
        return m
    dist = cv2.distanceTransform(m, cv2.DIST_L2, 3)
    return np.clip(dist / float(feather_px) * 255.0, 0, 255).astype(np.uint8)


def encode_png(image):
    """Lossless PNG at the strongest deflate level (a coverage mask is a few large flat regions)."""
    ok, buf = cv2.imencode(".png", image, [cv2.IMWRITE_PNG_COMPRESSION, 9])
    if not ok:
        raise TextureError("PNG encoding failed")
    return buf.tobytes()


def encode_jpeg(image, quality=DEFAULTS["jpegQuality"]):
    ok, buf = cv2.imencode(".jpg", image, [cv2.IMWRITE_JPEG_QUALITY, int(quality)])
    if not ok:
        raise TextureError("JPEG encoding failed")
    return buf.tobytes()
