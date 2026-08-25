#!/usr/bin/env python3
"""The "keep the wall angle" projection, derived from the orthophoto master.

The orthophoto is fronto-parallel: it is the wall surface itself, so a hold h
metres up the plywood sits h metres up the image.  A climber standing on the
mat and looking horizontally does not see that.  For a facet tilted theta past
vertical, a point h along the surface projects to a height h*cos(theta) on the
retina, so:

    angled view = orthophoto, VERTICAL axis scaled by cos(theta),
                  horizontal axis untouched.

Always derived by DOWNSCALING the full-resolution ortho master (INTER_AREA).
Going the other way would upsample and invent detail.

Because the app normalises hold geometry per axis and independently
(X = px/width, Y = px/height, aspect NOT preserved), a pure vertical scale is
invisible in normalised space: (y*c)/(H*c) = y/H.  `angle-check.jpg` and the
`hold_invariance` block in report.json check that on the real pixels.
"""
import json
import os

import cv2
import numpy as np

# Facets that carry a meaningful vertical foreshortening, in the order they are
# emitted.  (plane key, ortho file, angled stem, segments.json facet id)
FACETS = [
    ("main-span", "wall-orthophoto.png", "wall-orthophoto-angled", "F0"),
    ("kickboard", "wall-orthophoto-kickboard.png", "wall-orthophoto-kickboard-angled", "F1"),
]

# The left return panel is a near-perpendicular side wall (measured yaw 89.7 deg
# to the main span).  Its rectified image is a view along ITS own normal, which
# is roughly horizontal and roughly parallel to the wall face - i.e. a direction
# the head-on viewer never looks along.  Seen head-on from the mat the panel is
# edge-on: it foreshortens in the HORIZONTAL axis, all the way to a line, and
# the vertical axis is the one that stays true.  A cos(theta) squash of its
# vertical axis (theta = 2.97 deg, factor 0.9987) would be both a no-op and a
# lie about which axis is compressed, so no angled left-return image is emitted.
LEFT_RETURN_SKIP = (
    "left-return is a near-perpendicular side wall (yaw 89.7 deg to the main span, "
    "own tilt 2.97 deg past vertical).  Under a head-on horizontal line of sight it "
    "foreshortens horizontally - edge-on, towards a line - not vertically, so scaling "
    "its vertical axis by cos(2.97 deg) = 0.9987 would be a visually null but "
    "geometrically misleading image.  Skipped deliberately; the fronto-parallel "
    "rectification remains the only honest projection of that panel."
)

FONT = cv2.FONT_HERSHEY_SIMPLEX


def _measured(work):
    """Pipeline-measured facet angles past vertical, by segments.json facet id."""
    p = os.path.join(work, "segments", "segments.json")
    if not os.path.exists(p):
        return {}
    segs = json.load(open(p))["segments"]
    return {s["id"]: (float(s["angle_deg"]), s.get("angle_uncertainty_deg")) for s in segs}


def _stored(work):
    """The wall's stored Angle, as the app models it."""
    p = os.path.join(work, "holds", "wall.json")
    if not os.path.exists(p):
        return None
    return float(json.load(open(p))["wall"]["Angle"])


def resolve_theta(spec, work):
    """--wall-angle -> (theta_deg, human-readable source)."""
    meas = _measured(work)
    if spec is None or str(spec).lower() == "stored":
        a = _stored(work)
        if a is None:
            return 45.0, "default (no work/holds/wall.json; fell back to 45)"
        return a, "stored (work/holds/wall.json -> wall.Angle)"
    if str(spec).lower() == "measured":
        a, u = meas.get("F0", (45.0, None))
        return a, ("measured (work/segments/segments.json -> F0 main span, %.2f +- %s deg)"
                   % (a, "%.2f" % u if u else "n/a"))
    return float(spec), "cli (--wall-angle %s)" % spec


def squash(img, c):
    """Vertical-only downscale.  INTER_AREA: correct filter for shrinking."""
    h, w = img.shape[:2]
    hn = max(1, int(round(h * c)))
    if hn == h:
        return img.copy(), hn
    return cv2.resize(img, (w, hn), interpolation=cv2.INTER_AREA), hn


# --------------------------------------------------------------------------
# hold overlay (the invariance demonstration)
# --------------------------------------------------------------------------
def _holds(work):
    """Transferred holds in the SINGLE whole-wall coordinate space.

    Recognition now emits every hold normalised 0..1 against the ONE natural master
    (top-left origin), with no per-plane split - `new.X`/`new.Y` land directly on the
    natural/angled master. The old per-plane filter (`plane == "main-span"`) is gone:
    all records share one surface. Returns (records, is_whole_wall); the whole-wall flag
    lets the caller avoid drawing these natural-master coordinates onto the fronto-parallel
    ortho, where they would not line up.
    """
    p = os.path.join(work, "holds-match", "holds-remapped.json")
    if not os.path.exists(p):
        return [], False
    doc = json.load(open(p))
    whole_wall = "_surface" in doc  # new single-surface format carries `_surface`
    recs = [r for r in doc.get("holds", []) if r.get("new")]
    return recs, whole_wall


def draw_holds(img, holds, c=1.0, col=(60, 235, 60)):
    """Draw every hold from its NORMALISED coordinates onto whatever image.

    The centre is X*W, Y*H - no knowledge of which projection this is.  The
    radius convention (R*max(W,H), a pixel circle) is not a per-axis quantity,
    so on the angled image the marker is drawn as an ellipse squashed by the
    same c: that is what a circle on the wall surface actually looks like.
    """
    out = img.copy()
    h, w = out.shape[:2]
    for r in holds:
        n = r["new"]
        cx, cy = int(round(n["X"] * w)), int(round(n["Y"] * h))
        rx = max(2, int(round(float(n.get("Radius") or 0) * max(w, int(round(h / c))))))
        ry = max(2, int(round(rx * c)))
        cv2.ellipse(out, (cx, cy), (rx, ry), 0, 0, 360, col, 3, cv2.LINE_AA)
        cv2.drawMarker(out, (cx, cy), (40, 210, 255), cv2.MARKER_CROSS, 9, 2, cv2.LINE_AA)
    return out


def ncc(a, b):
    a = a.astype(np.float64).ravel(); b = b.astype(np.float64).ravel()
    a -= a.mean(); b -= b.mean()
    d = np.sqrt((a * a).sum() * (b * b).sum())
    return float((a * b).sum() / d) if d > 1e-9 else 0.0


def invariance(ortho, angled, holds, c, half=70):
    """Numeric proof: the SAME normalised coordinate hits the same wall feature.

    For each hold, crop the ortho around X*Wo,Y*Ho, squash that crop by c, and
    correlate it with the angled image cropped around X*Wa,Y*Ha.  If the
    invariant holds the two patches are the same picture.
    """
    Ho, Wo = ortho.shape[:2]
    Ha, Wa = angled.shape[:2]
    hy = max(2, int(round(half * c)))
    go, sc = cv2.cvtColor(ortho, cv2.COLOR_BGR2GRAY), []
    ga = cv2.cvtColor(angled, cv2.COLOR_BGR2GRAY)
    dy = []
    for r in holds:
        n = r["new"]
        xo, yo = int(round(n["X"] * Wo)), int(round(n["Y"] * Ho))
        xa, ya = int(round(n["X"] * Wa)), int(round(n["Y"] * Ha))
        if not (half <= xo < Wo - half and half <= yo < Ho - half):
            continue
        if not (half <= xa < Wa - half and hy <= ya < Ha - hy):
            continue
        po = go[yo - half:yo + half, xo - half:xo + half]
        po = cv2.resize(po, (2 * half, 2 * hy), interpolation=cv2.INTER_AREA)
        sc.append(ncc(po, ga[ya - hy:ya + hy, xa - half:xa + half]))
        dy.append(abs(ya - yo * (Ha / Ho)))
    if not sc:
        return {}
    sc = np.array(sc)
    return dict(n_holds_tested=int(sc.size),
                patch_ncc=dict(min=float(sc.min()), p05=float(np.percentile(sc, 5)),
                               median=float(np.median(sc)), mean=float(sc.mean())),
                frac_ncc_above_0p90=float((sc > 0.90).mean()),
                max_centre_offset_px_vs_pure_vertical_scale=float(max(dy)),
                verdict=("normalised hold coordinates are invariant: the same (X,Y) "
                         "lands on the same hold in both projections"
                         if float(np.median(sc)) > 0.9 else
                         "INVARIANT VIOLATED - investigate before using the angled view"))


# --------------------------------------------------------------------------
# figures
# --------------------------------------------------------------------------
def _fit(img, width):
    """Downscale to `width`, never up.  INTER_AREA: the correct shrink filter."""
    if img.shape[1] <= width:
        return img
    return cv2.resize(img, (width, max(1, int(round(img.shape[0] * width / img.shape[1])))),
                      interpolation=cv2.INTER_AREA)


def _panel(img, title, sub, col, width):
    # The bar takes the panel's ACTUAL width, not the requested one: `_fit` never
    # upscales, so a master narrower than `width` keeps its own size and a bar built at
    # `width` would not stack with it.
    im = _fit(img, width)
    w = im.shape[1]
    bar = np.zeros((54, w, 3), np.uint8); bar[:] = col
    cv2.putText(bar, title, (16, 37), FONT, 1.0, (255, 255, 255), 2, cv2.LINE_AA)
    cv2.putText(bar, sub, (16 + min(380, max(0, w - 340)), 36), FONT, 0.6,
                (235, 235, 235), 1, cv2.LINE_AA)
    return np.vstack([bar, im])


def _pad_to(img, width):
    """Left-align onto a `width`-wide canvas.  Panels can differ in width once `_fit`
    declines to upscale, and vstack needs them identical."""
    if img.shape[1] >= width:
        return img[:, :width]
    out = np.zeros((img.shape[0], width, 3), np.uint8)
    out[:, :img.shape[1]] = img
    return out


def _stack(parts, width):
    w = max([p.shape[1] for p in parts] + [1])
    gap = np.zeros((12, w, 3), np.uint8)
    out = []
    for p in parts:
        out += [_pad_to(p, w), gap]
    return np.vstack(out[:-1])


def _details(ortho, angled, holds, c, box=430):
    """Native-resolution crops of the same three holds in both projections."""
    Ho, Wo = ortho.shape[:2]; Ha, Wa = angled.shape[:2]
    hb = max(2, int(round(box * c / 2)))
    picks, tiles = [], []
    for frac in (0.22, 0.5, 0.78):
        cand = [r for r in holds if abs(r["new"]["X"] - frac) < 0.06
                and 0.25 < r["new"]["Y"] < 0.75]
        if cand:
            picks.append(min(cand, key=lambda r: abs(r["new"]["X"] - frac)))
    for r in picks:
        n = r["new"]
        xo, yo = int(n["X"] * Wo), int(n["Y"] * Ho)
        xa, ya = int(n["X"] * Wa), int(n["Y"] * Ha)
        xo = min(max(xo, box // 2), Wo - box // 2); yo = min(max(yo, box // 2), Ho - box // 2)
        xa = min(max(xa, box // 2), Wa - box // 2); ya = min(max(ya, hb), Ha - hb)
        a = ortho[yo - box // 2:yo + box // 2, xo - box // 2:xo + box // 2]
        b = angled[ya - hb:ya + hb, xa - box // 2:xa + box // 2]
        pad = np.zeros((box - b.shape[0], b.shape[1], 3), np.uint8)
        tile = np.hstack([a, np.zeros((box, 8, 3), np.uint8), np.vstack([b, pad])])
        cv2.putText(tile, "ortho", (8, 26), FONT, 0.7, (60, 235, 60), 2, cv2.LINE_AA)
        cv2.putText(tile, "angled", (box + 16, 26), FONT, 0.7, (40, 210, 255), 2, cv2.LINE_AA)
        tiles.append(tile)
    return np.hstack(tiles) if tiles else None


def angle_check(work, ortho, angled, holds, c, theta, width=2200):
    # Draw on the DOWNSCALED panels, not on full-resolution copies of both masters.
    # `_panel` shrinks to `width` regardless, so drawing first only bought a pair of
    # ~110 MB and ~80 MB copies and then threw the pixels away.  Hold markers come
    # from normalised coordinates, so they land identically at either size; they are
    # relatively thicker on the panel, which is what a review figure wants anyway.
    o = draw_holds(_fit(ortho, width), holds, 1.0)
    a = draw_holds(_fit(angled, width), holds, c)
    parts = [_panel(o, "ORTHO", "fronto-parallel %dx%d - holds drawn from normalised X,Y"
                    % (ortho.shape[1], ortho.shape[0]), (32, 110, 32), width),
             _panel(a, "ANGLED", "vertical x cos(%.2f deg)=%.4f -> %dx%d - SAME normalised X,Y"
                    % (theta, c, angled.shape[1], angled.shape[0]), (130, 70, 24), width)]
    # The detail tiles are native-resolution crops by definition, so they are taken
    # from the masters rather than from the panels.
    det = _details(ortho, angled, holds, c)
    if det is not None:
        parts.append(_panel(det, "DETAIL", "same holds, native resolution, both projections",
                            (60, 40, 110), width))
    out = _stack(parts, width)
    p = os.path.join(work, "06-final", "angle-check.jpg")
    cv2.imwrite(p, out, [cv2.IMWRITE_JPEG_QUALITY, 92])
    return p


def side_by_side(work, ortho, angled, c, theta, width=2200):
    parts = [_panel(ortho, "ORTHO", "fronto-parallel: the wall surface, %dx%d"
                    % (ortho.shape[1], ortho.shape[0]), (32, 110, 32), width),
             _panel(angled, "ANGLED", "as seen head-on: vertical x %.4f = cos(%.2f deg), %dx%d"
                    % (c, theta, angled.shape[1], angled.shape[0]), (130, 70, 24), width)]
    p = os.path.join(work, "06-final", "angled-vs-ortho.jpg")
    cv2.imwrite(p, _stack(parts, width), [cv2.IMWRITE_JPEG_QUALITY, 92])
    return p


# --------------------------------------------------------------------------
def emit(work, spec, report, jpeg_quality=95, log=print, ortho=None,
         png_compression=3):
    theta, source = resolve_theta(spec, work)
    meas = _measured(work)
    fin = os.path.join(work, "06-final")
    info = dict(convention="angled view = orthophoto with the VERTICAL axis scaled by "
                           "cos(theta) and the horizontal axis unchanged; theta is the "
                           "facet's tilt past vertical.  Always DOWNSCALED (INTER_AREA) "
                           "from the full-resolution ortho master, never upscaled from it.",
                wall_angle_deg=theta, wall_angle_source=source,
                scale_factor=float(np.cos(np.radians(theta))), planes={})
    main = None
    for plane, src, stem, fid in FACETS:
        p = os.path.join(fin, src)
        if not os.path.exists(p):
            continue
        # The caller already has the main-span master in memory; re-reading it
        # decodes a second ~110 MB copy alongside the one that is still live.
        img = ortho if (plane == "main-span" and ortho is not None) else cv2.imread(p)
        if plane == "main-span":
            th, sr = theta, source
        else:
            th = meas.get(fid, (0.0, None))[0]
            sr = "measured (work/segments/segments.json -> %s; the stored model records " \
                 "this facet as a nominal 0 deg segment)" % fid
        c = float(np.cos(np.radians(th)))
        out, hn = squash(img, c)
        png, jpg = os.path.join(fin, stem + ".png"), os.path.join(fin, stem + ".jpg")
        cv2.imwrite(png, out, [cv2.IMWRITE_PNG_COMPRESSION, png_compression])
        cv2.imwrite(jpg, out, [cv2.IMWRITE_JPEG_QUALITY, jpeg_quality])
        info["planes"][plane] = dict(
            angle_deg=th, angle_source=sr, scale_factor=c,
            ortho=dict(width=int(img.shape[1]), height=int(img.shape[0]), file=src),
            angled=dict(width=int(out.shape[1]), height=hn,
                        png=os.path.basename(png), jpg=os.path.basename(jpg),
                        png_bytes=os.path.getsize(png), jpg_bytes=os.path.getsize(jpg)),
            resampling="cv2.INTER_AREA (downscale)" if hn != img.shape[0] else "identity")
        log("angled %-10s %dx%d -> %dx%d  (theta %.2f deg, x%.4f)"
            % (plane, img.shape[1], img.shape[0], out.shape[1], hn, th, c))
        if plane == "main-span":
            main = (img, out, c)
    info["planes"]["left-return"] = dict(skipped=True, reason=LEFT_RETURN_SKIP,
                                         measured_angle_deg=meas.get("F_left", (None,))[0],
                                         measured_yaw_deg=89.7)
    if main:
        img, out, c = main
        holds, whole_wall = _holds(work)
        # The transferred holds now live in the natural master's whole-wall coordinate
        # space, not the fronto-parallel ortho's. Drawing them on the ortho / squashed-ortho
        # panels would place them on the wrong features, so the ortho-vs-squash figures are
        # rendered WITHOUT the hold overlay in that case; the authoritative hold overlay on
        # the natural master is holds-match/overlay-new.jpg from the recognition step.
        fig_holds = [] if whole_wall else holds
        info["hold_invariance"] = invariance(img, out, fig_holds, c)
        info["figures"] = dict(angle_check=os.path.basename(angle_check(work, img, out, fig_holds, c, theta)),
                               angled_vs_ortho=os.path.basename(side_by_side(work, img, out, c, theta)))
        if whole_wall:
            info["holds_overlay"] = ("holds are whole-wall natural-master coords; see"
                                     " holds-match/overlay-new.jpg for the overlay on the"
                                     " natural master")
        hi = info["hold_invariance"]
        if hi:
            log("hold invariance: %d holds, patch NCC median %.4f min %.4f, %.1f%% > 0.90"
                % (hi["n_holds_tested"], hi["patch_ncc"]["median"], hi["patch_ncc"]["min"],
                   100 * hi["frac_ncc_above_0p90"]))
    report["angled_view"] = info
    return info
