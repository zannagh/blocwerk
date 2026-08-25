#!/usr/bin/env python3
"""Natural photographic stitch of a climbing wall from the undistorted source frames.

This produces the *natural* master: a single body-height photo (mats + ceiling +
both end wall pieces + every hold), rendered as a **vertical-axis cylindrical
panorama**: the main span (centre) reads roughly straight/unbent while the
perpendicular left 90-deg return and the steep right corner curve toward the
viewer so both stay visible.  Replaces the earlier "flat panels on black" collage
for the angled slot.

Method (option c: cylindrical re-projection of a stable flat mosaic):
  1. Flat mosaic via the cv2.detail affine (SCANS) pipeline -- stable on these
     translational-pan frames where rotation-model warpers blew up on parallax:
     SIFT -> AffineBestOf2NearestMatcher -> AffineBasedEstimator ->
     BundleAdjusterAffinePartial -> PyRotationWarper('affine') ->
     DpSeamFinder('COLOR') + _protect_seams -> MultiBandBlender.
     DP-COLOR keeps the head-on central frame dominant across the hold field (what the
     recognition NCC templates correlate against; a GraphCut finder here collapses
     recognition ~300 -> ~60), and _protect_seams rescues protruding volumes DP would
     erase (the box on the right of the main span, in the 3-4-5 overlap).
  2. Re-project that mosaic onto a cylinder about a vertical axis centred on the
     reference (main-span) frame -- reuses the clean multiband composite (no ghosting,
     no black wedges through the wall).  `_CYL_FOCAL_PX` is a direct curvature knob:
     centre stays straight, columns bend toward the viewer with increasing angle.

Coordinate convention recorded in report["natural"]:
  source = 01-undistorted/<photo>.jpg frame; flat = intermediate affine mosaic;
  master = cylindrical natural master PNG (all top-left origin, x right / y down).
  frames[].H maps source->flat pixels ([xf,yf,1]^T = H @ [xs,ys,1]^T); flat->master is
  the cylindrical remap in report["natural"]["cylindrical"].  Recognition re-registers
  directly to the master image, so the exact source->master chain is not needed
  downstream; H + cylindrical params are recorded for reference.
"""
import os
import cv2
import numpy as np

# Deterministic; matches the validation experiment that picked SCANS/affine.
_MATCH_CONF = 0.3
_CONF_THRESH = 0.3

# Cylindrical curvature knob, in flat-mosaic pixels (the mosaic is ~3000 px wide for
# this 5.5 m wall).  Smaller focal = stronger curve.  1800 keeps the main span visibly
# straight while wrapping the left return (~36 deg off-axis) and the right corner
# (~22 deg) enough to read them head-on; picked by rendering 1200/1600/2000/2600.
_CYL_FOCAL_PX = 1800.0

# Protruding climbing volumes to protect from seam erasure, keyed by the source frame
# (report['input']['usable'] name) that sees the volume head-on.  Each rect is in that
# undistorted frame's pixels; _protect_seams pins the frame as the sole contributor over
# the rect's footprint so multiband cannot average the volume away.  Configured for The
# Attic: the box volume at the right end of the main span, seen head-on by frame '4'.
_PROTECT_VOLUMES = {
    "4": [(330, 740, 720, 1120)],
}


def _load_frames(undist_dir, names):
    """Read the undistorted 01-undistorted/<name>.jpg frames used by m2."""
    imgs, used = [], []
    for n in names:
        p = os.path.join(undist_dir, f"{n}.jpg")
        im = cv2.imread(p)
        if im is not None:
            imgs.append(im)
            used.append(n)
    return imgs, used


def _protect_seams(seam_masks, warped_masks, names, cameras, warper, corners, log):
    """Force a named frame to be the sole contributor over a protruding volume.

    A high-parallax volume (e.g. the box at the right end of the main span) is seen
    head-on by only one frame; the DP seam otherwise leaves its footprint owned by the
    neighbouring, more head-on-to-the-*wall* frame (which sees the volume edge-on or not
    at all), so multiband erases it.  We reproject each protected source rectangle into
    the shared canvas and pin that frame's mask there (zeroing the others), so the volume
    survives -- WITHOUT the global frame reassignment a GraphCut finder does, which
    reprojects the whole hold field through oblique frames and collapses hold recognition.
    Mutates seam_masks in place.
    """
    name_to_idx = {n: k for k, n in enumerate(names)}
    for fname, rects in _PROTECT_VOLUMES.items():
        i = name_to_idx.get(fname)
        if i is None:
            continue
        K = cameras[i].K().astype(np.float32)
        R = cameras[i].R.astype(np.float32)
        for (x0, y0, x1, y1) in rects:
            pts = [warper.warpPoint((float(x), float(y)), K, R)
                   for x in (x0, x1) for y in (y0, y1)]
            sx0 = min(p[0] for p in pts); sx1 = max(p[0] for p in pts)
            sy0 = min(p[1] for p in pts); sy1 = max(p[1] for p in pts)
            for j in range(len(seam_masks)):
                cx, cy = corners[j]
                h, w = seam_masks[j].shape[:2]
                lx0 = int(max(0, np.floor(sx0 - cx))); ly0 = int(max(0, np.floor(sy0 - cy)))
                lx1 = int(min(w, np.ceil(sx1 - cx))); ly1 = int(min(h, np.ceil(sy1 - cy)))
                if lx1 <= lx0 or ly1 <= ly0:
                    continue
                seam_masks[j][ly0:ly1, lx0:lx1] = (
                    warped_masks[j][ly0:ly1, lx0:lx1] if j == i else 0)
            log("natural: protected volume from frame %s over master rect ~(%.0f,%.0f)-(%.0f,%.0f)"
                % (fname, sx0, sy0, sx1, sy1))


def _affine_pano(imgs, names, log):
    """Run the detail affine pipeline; return (result_bgr, cameras, warper, offset)."""
    finder = cv2.SIFT_create()
    features = [cv2.detail.computeImageFeatures2(finder, im) for im in imgs]

    matcher = cv2.detail_AffineBestOf2NearestMatcher(False, False, _MATCH_CONF)
    matches = matcher.apply2(features)
    matcher.collectGarbage()

    estimator = cv2.detail_AffineBasedEstimator()
    ok, cameras = estimator.apply(features, matches, None)
    if not ok:
        raise RuntimeError("affine camera estimation failed")
    for c in cameras:
        c.R = c.R.astype(np.float32)

    adjuster = cv2.detail_BundleAdjusterAffinePartial()
    adjuster.setConfThresh(_CONF_THRESH)
    ok, cameras = adjuster.apply(features, matches, cameras)
    if not ok:
        raise RuntimeError("affine bundle adjustment failed")

    scale = float(np.median([c.focal for c in cameras]))
    warper = cv2.PyRotationWarper('affine', scale)

    corners, sizes, warped, warped_masks = [], [], [], []
    for i, im in enumerate(imgs):
        K = cameras[i].K().astype(np.float32)
        R = cameras[i].R.astype(np.float32)
        corner, wimg = warper.warp(im, K, R, cv2.INTER_LINEAR, cv2.BORDER_REFLECT)
        full = 255 * np.ones(im.shape[:2], np.uint8)
        _, wmask = warper.warp(full, K, R, cv2.INTER_NEAREST, cv2.BORDER_CONSTANT)
        corners.append(corner)
        sizes.append((wimg.shape[1], wimg.shape[0]))
        warped.append(wimg.astype(np.float32))
        warped_masks.append(wmask)

    # DP graph-cut over COLOR keeps the head-on central frame dominant across the hold
    # field, which is what the old-photo NCC templates correlate against -- swapping in a
    # GraphCutSeamFinder here reassigns large regions to oblique frames and collapses hold
    # recognition (~300 -> ~60 matched).  Protruding volumes that DP would erase are
    # rescued locally instead, by _protect_seams.
    seam_finder = cv2.detail_DpSeamFinder('COLOR')
    seam_masks = seam_finder.find(warped, corners, warped_masks)
    seam_masks = [m.get() if isinstance(m, cv2.UMat) else m for m in seam_masks]
    _protect_seams(seam_masks, warped_masks, names, cameras, warper, corners, log)

    roi = cv2.detail.resultRoi(corners, sizes)          # (x, y, w, h) in the shared frame
    offset = (float(roi[0]), float(roi[1]))
    blender = cv2.detail_MultiBandBlender()
    blender.prepare(roi)
    for i in range(len(imgs)):
        m = cv2.resize(seam_masks[i], (warped_masks[i].shape[1], warped_masks[i].shape[0]),
                       interpolation=cv2.INTER_LINEAR)
        m = cv2.bitwise_and(cv2.dilate(m, None), warped_masks[i])
        blender.feed(warped[i].astype(np.int16), m, corners[i])
    result, _ = blender.blend(None, None)
    result = cv2.convertScaleAbs(result)
    log("natural: affine pano %dx%d from %d frames (scale=%.1f)"
        % (result.shape[1], result.shape[0], len(imgs), scale))
    return result, cameras, warper, offset


def _recover_H(warper, camera, wh, offset):
    """Closed-form 3x3 mapping source pixels -> master pixels for one frame.

    The warp is affine, so a 5x5 grid of warpPoint samples pins the exact affine;
    subtracting the canvas origin (offset) puts it in master-pixel space.
    Returns (H 3x3, max_residual_px).
    """
    K = camera.K().astype(np.float32)
    R = camera.R.astype(np.float32)
    w, h = wh
    xs = np.linspace(0, w - 1, 5)
    ys = np.linspace(0, h - 1, 5)
    src, dst = [], []
    for x in xs:
        for y in ys:
            gx, gy = warper.warpPoint((float(x), float(y)), K, R)
            src.append([x, y])
            dst.append([gx - offset[0], gy - offset[1]])
    src = np.asarray(src, float)
    dst = np.asarray(dst, float)
    A = np.hstack([src, np.ones((len(src), 1))])
    aff, *_ = np.linalg.lstsq(A, dst, rcond=None)       # (3x2): [x,y,1] @ aff = [xm,ym]
    resid = float(np.abs(A @ aff - dst).max())
    H = np.vstack([aff.T, [0.0, 0.0, 1.0]])
    return H, resid


def _cylindrical_remap(flat, cx, cy, focal):
    """Re-project a flat mosaic onto a vertical-axis cylinder centred at (cx, cy).

    The column x=cx stays straight; columns bend toward the viewer with angle
    theta = atan((x-cx)/focal).  A pixel on the cylinder at (angle theta, height h)
    projects back to the flat mosaic through a pinhole of the same focal:
        xf = cx + focal*tan(theta)          # theta = X_out / focal
        yf = cy + Y_out / cos(theta)
    We size the output to the forward-projected bounding box of the flat border, then
    fill it by inverse mapping (cv2.remap).  Returns (warped_bgr, params_dict).
    """
    H, W = flat.shape[:2]
    # forward-project the four borders to find the output bounding box
    bx = np.r_[np.arange(W), np.arange(W), np.full(H, 0), np.full(H, W - 1)].astype(np.float64)
    by = np.r_[np.full(W, 0), np.full(W, H - 1), np.arange(H), np.arange(H)].astype(np.float64)
    dx = bx - cx
    Xf = focal * np.arctan2(dx, focal)
    Yf = (by - cy) * focal / np.sqrt(dx * dx + focal * focal)
    minX, maxX = float(Xf.min()), float(Xf.max())
    minY, maxY = float(Yf.min()), float(Yf.max())
    outW = int(np.ceil(maxX - minX))
    outH = int(np.ceil(maxY - minY))
    OX, OY = np.meshgrid(np.arange(outW), np.arange(outH))
    theta = (OX + minX) / focal
    map_x = (cx + focal * np.tan(theta)).astype(np.float32)
    map_y = (cy + (OY + minY) / np.cos(theta)).astype(np.float32)
    warped = cv2.remap(flat, map_x, map_y, cv2.INTER_LINEAR,
                       borderMode=cv2.BORDER_CONSTANT, borderValue=(0, 0, 0))
    params = dict(model="vertical-axis cylinder", focal_px=float(focal),
                  flat_center_x=float(cx), flat_center_y=float(cy),
                  flat_width=int(W), flat_height=int(H),
                  out_origin_x=minX, out_origin_y=minY,
                  master_width=int(outW), master_height=int(outH),
                  formula="theta=atan((xf-cx)/f); Xout=f*theta; Yout=(yf-cy)*f/hypot(xf-cx,f)")
    return warped, params


def build(work, names, report, log, jpeg_quality=95, png_compression=3,
          undist_dir=None, angled_basename="wall-orthophoto-angled",
          source_photo=None):
    """Build the natural master, overwrite the angled slot, record report['natural'].

    `names` are the usable undistorted frames (report['input']['usable']).
    `source_photo` optionally maps frame name -> original photo path for the report.
    Returns the report['natural'] dict.
    """
    if undist_dir is None:
        undist_dir = os.path.join(work, "01-undistorted")
    imgs, used = _load_frames(undist_dir, names)
    if len(imgs) < 2:
        raise RuntimeError("natural stitch needs >=2 undistorted frames, got %d" % len(imgs))

    result, cameras, warper, offset = _affine_pano(imgs, used, log)
    flat_h, flat_w = result.shape[:2]

    frames_rep, max_resid = [], 0.0
    anchor = None            # (name, rotation_deg, camera_index)
    for i, n in enumerate(used):
        h, w = imgs[i].shape[:2]
        H, resid = _recover_H(warper, cameras[i], (w, h), offset)
        max_resid = max(max_resid, resid)
        # rotation magnitude of the affine part, reported so a reader can see how far
        # each frame was rotated into the master (the anchor is the least-rotated frame)
        rot_deg = float(abs(np.degrees(np.arctan2(H[1, 0], H[0, 0]))))
        rec = dict(index=i, source_photo=(source_photo or {}).get(n, n),
                   source_file=os.path.join("01-undistorted", f"{n}.jpg"),
                   src_width=int(w), src_height=int(h),
                   H=[[float(v) for v in row] for row in H],
                   affine_residual_px=round(resid, 5),
                   rotation_deg=round(rot_deg, 3))
        frames_rep.append(rec)
        if anchor is None or rot_deg < anchor[1]:
            anchor = (n, rot_deg, i)

    # Cylinder axis = the reference (least-rotated) frame's centre in the flat mosaic,
    # so the main span it sees head-on stays straight and the ends wrap symmetrically
    # around it.  Fall back to the mosaic centre if no anchor was found.
    if anchor is not None:
        ci = anchor[2]
        K = cameras[ci].K().astype(np.float32)
        R = cameras[ci].R.astype(np.float32)
        gx, gy = warper.warpPoint((imgs[ci].shape[1] / 2.0, imgs[ci].shape[0] / 2.0), K, R)
        cx, cy = gx - offset[0], gy - offset[1]
    else:
        cx, cy = flat_w / 2.0, flat_h / 2.0
    master, cyl = _cylindrical_remap(result, cx, cy, _CYL_FOCAL_PX)
    Hm, Wm = master.shape[:2]
    log("natural: cylindrical remap f=%.0f centre=(%.0f,%.0f) flat %dx%d -> master %dx%d"
        % (_CYL_FOCAL_PX, cx, cy, flat_w, flat_h, Wm, Hm))

    # overwrite the angled master slot (app downloads this as `angled`)
    final = os.path.join(work, "06-final")
    png = os.path.join(final, angled_basename + ".png")
    jpg = os.path.join(final, angled_basename + ".jpg")
    cv2.imwrite(png, master, [cv2.IMWRITE_PNG_COMPRESSION, png_compression])
    cv2.imwrite(jpg, master, [cv2.IMWRITE_JPEG_QUALITY, jpeg_quality])

    nat = dict(
        method=("cylindrical natural master: cv2.detail affine mosaic (SIFT -> "
                "AffineBestOf2NearestMatcher -> AffineBasedEstimator -> "
                "BundleAdjusterAffinePartial -> affine warp -> DpSeamFinder(COLOR) + "
                "protruding-volume seam protection -> MultiBandBlender) re-projected onto "
                "a vertical-axis cylinder centred on the reference frame"),
        master_file=os.path.basename(png),
        master_width=int(Wm), master_height=int(Hm),
        flat_width=int(flat_w), flat_height=int(flat_h),
        source_space="01-undistorted/<photo>.jpg (undistorted frame, top-left origin, x right / y down)",
        flat_space="intermediate flat affine mosaic (top-left origin, x right / y down)",
        master_space="cylindrical natural master PNG (top-left origin, x right / y down)",
        coordinate_convention=("frames[].H maps source->FLAT mosaic ([xf,yf,1]^T = H @ "
                               "[xs,ys,1]^T, affine); flat->master is the cylindrical remap"),
        cylindrical=cyl,
        protected_volumes={k: [list(r) for r in v] for k, v in _PROTECT_VOLUMES.items()},
        reference_frame=(anchor[0] if anchor else None),
        frames=frames_rep,
        validation=dict(
            # H_i is recovered in closed form from the same warper that placed each
            # frame into the FLAT mosaic (sub-pixel residual, verified with a
            # master-vs-H-warped checkerboard on the flat mosaic).
            max_affine_residual_px=round(max_resid, 5),
            method="closed-form warpPoint recovery; residual = |grid_warpPoint - H@grid| (flat mosaic)",
        ),
    )
    report["natural"] = nat
    log("natural: master -> %s (%dx%d); flat mosaic %dx%d; affine residual<=%.5fpx over %d frames"
        % (os.path.basename(png), Wm, Hm, flat_w, flat_h, max_resid, len(frames_rep)))
    return nat
