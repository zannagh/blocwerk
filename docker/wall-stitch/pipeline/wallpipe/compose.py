"""Warp frames through given homographies onto one plane and blend them into one photo.

Vendored from new-run/exp7/compose2.py, which superseded exp3/compose.py. exp3 picked
its seams per pixel from a sampling-density argmax with no exposure compensation, so
the composite stepped visibly at every frame boundary; it also resized each source
frame to a *swapped* (landscape) size, stretching every frame 1.333x wide and 0.75x
short before the warp. Both are fixed here. The path is the standard OpenCV `detail::`
one, run at two scales:

    seam scale   warp every frame small -> resolution-aware candidate masks ->
                 GAIN_BLOCKS exposure compensation -> DP seam finding
    full scale   warp each frame, apply its gain, feed the up-sampled seam mask to
                 a 5-band MultiBandBlender

Changes from the R&D copy: the frame list and the frame size are arguments rather than
a module-level path template and a hardcoded (4284, 5712), and the module-level logger
is passed in, so this file has no path dependencies.
"""
import cv2
import numpy as np


def to_output_scale(hs, wscale, out_scale):
    """Homographies are estimated in work pixels; re-express them for source
    frames resampled at out_scale."""
    k = out_scale / wscale
    s = np.diag([k, k, 1.0])
    si = np.diag([1 / k, 1 / k, 1.0])
    return {n: s @ hs[n] @ si for n in hs}


def frame_quads(hs, paths, src_w, src_h):
    c = np.array([[0, 0], [src_w, 0], [src_w, src_h], [0, src_h]],
                 np.float32).reshape(-1, 1, 2)
    return [cv2.perspectiveTransform(c, hs[n]).reshape(-1, 2) for n in paths]


def canvas_of(hs, paths, src_w, src_h):
    q = np.concatenate(frame_quads(hs, paths, src_w, src_h))
    x0, y0 = q.min(0)
    x1, y1 = q.max(0)
    return x0, y0, int(np.ceil(x1 - x0)), int(np.ceil(y1 - y0))


def source_size(path):
    """(width, height) of a frame, exactly as the rest of the pipeline will read it.

    Decoded in full on purpose. The cheap routes are both wrong here:

      * a 1/8 decode and multiply back rounds UP per axis, so a 4284x5712 frame
        reconstructs as 4288x5712 -- a 0.09% stretch in ONE axis, i.e. a small
        anisotropic bias baked into every compose map;
      * PIL's `Image.open(p).size` reads the header without decoding, but reports the
        STORED orientation and ignores the EXIF rotation that `cv2.imread` applies. On
        these frames it answers 5712x4284, the landscape transpose -- which is exactly
        the swapped-size defect this function exists to avoid.

    One extra decode per run is nothing against the 46 the composite already does, and
    it is the only answer guaranteed to match the arrays every other stage sees.
    """
    probe = cv2.imread(path)
    if probe is None:
        raise SystemExit('could not read frame: %s' % path)
    return probe.shape[1], probe.shape[0]


def _boxes(m, paths, sw, sh, cw, ch, min_side=8):
    """Per frame: its canvas bounding box, clipped, and the homography into that box."""
    c = np.array([[0, 0], [sw, 0], [sw, sh], [0, sh]], np.float32).reshape(-1, 1, 2)
    for i, n in enumerate(paths):
        q = cv2.perspectiveTransform(c, m[n]).reshape(-1, 2)
        bx0, by0 = np.floor(q.min(0)).astype(int)
        bx1, by1 = np.ceil(q.max(0)).astype(int)
        bx0, by0 = max(int(bx0), 0), max(int(by0), 0)
        bx1, by1 = min(int(bx1), cw), min(int(by1), ch)
        if bx1 - bx0 < min_side or by1 - by0 < min_side:
            continue
        mo = np.array([[1, 0, -bx0], [0, 1, -by0], [0, 0, 1]], np.float64) @ m[n]
        yield i, n, (bx0, by0, bx1, by1), mo


def _density(mo, sw, sh):
    """Source pixels per canvas pixel: w^3/|det| for a homography. Frames seen
    edge-on resolve the wall badly and must not win a seam."""
    ys, xs = np.mgrid[0:sh, 0:sw].astype(np.float32)
    wq = mo[2, 0] * xs + mo[2, 1] * ys + mo[2, 2]
    return np.abs(wq) ** 3 / (abs(np.linalg.det(mo)) + 1e-30)


def _coverage(mo, sw, sh, size):
    """Which canvas pixels this frame actually covers, eroded off its own border."""
    cov = cv2.warpPerspective(np.full((sh, sw), 255, np.uint8), mo, size,
                              flags=cv2.INTER_NEAREST)
    return cv2.erode(cov, np.ones((3, 3), np.uint8))


def plan(hs, wscale, paths, out_scale, rect=None, roi=None, src_size=None, lam=1.0):
    """Fold the rect correction, the metric x-correction and the scale into one
    homography per frame, and derive the canvas from the ROI.

    roi is (x0, y0, x1, y1) in rectified work-pixel coordinates; without it the canvas
    is the bounding box of every frame, which the floor plane blows up.
    """
    if rect is not None:
        hs = {n: rect @ hs[n] for n in paths}
    if lam != 1.0:
        a = np.diag([1.0 / lam, 1.0, 1.0])
        hs = {n: a @ hs[n] for n in paths}
    ho = to_output_scale(hs, wscale, out_scale)
    if src_size is None:
        src_size = source_size(paths[0])
    sw, sh = int(round(src_size[0] * out_scale)), int(round(src_size[1] * out_scale))
    k = out_scale / wscale
    if roi is not None:
        x0, y0 = roi[0] * k, roi[1] * k
        cw = int(round((roi[2] - roi[0]) * k))
        ch = int(round((roi[3] - roi[1]) * k))
    else:
        x0, y0, cw, ch = canvas_of(ho, paths, sw, sh)
    t = np.array([[1, 0, -x0], [0, 1, -y0], [0, 0, 1]], np.float64)
    return {n: t @ ho[n] for n in paths}, cw, ch, sw, sh, t, src_size


def build(hs, wscale, paths, out_scale, rect=None, max_mpx=110.0, roi=None,
          src_size=None, lam=1.0, seam_mpx=2.5, rel_density=0.45, bands=5,
          seam='dp', log=print):
    """Composite `paths` onto the rectified plane.

    Returns (image, mask, per-frame source-pixel -> canvas-pixel homographies,
    effective out_scale). The scale is returned because the Mpx cap may have lowered it.
    """
    m, cw, ch, sw, sh, t, src_size = plan(
        hs, wscale, paths, out_scale, rect, roi, src_size, lam)
    if cw * ch / 1e6 > max_mpx:
        k = np.sqrt(max_mpx * 1e6 / (cw * ch))
        log('canvas %dx%d over %.0f Mpx cap; out_scale %.3f -> %.3f'
            % (cw, ch, max_mpx, out_scale, out_scale * k))
        return build(hs, wscale, paths, out_scale * k, rect, max_mpx, roi, src_size,
                     lam, seam_mpx, rel_density, bands, seam, log)
    log('canvas %dx%d (%.1f Mpx) at out_scale %.3f, frames %dx%d'
        % (cw, ch, cw * ch / 1e6, out_scale, sw, sh))

    # ---- seam scale --------------------------------------------------------
    # Everything at seam scale is the full-scale geometry times sc, so a seam mask
    # up-samples back onto its own full-scale tile exactly. Deriving the two scales
    # independently drifts by several output pixels and tears the seams.
    sc = min(1.0, np.sqrt(seam_mpx * 1e6 / (cw * ch)))
    scw, sch = int(round(cw * sc)), int(round(ch * sc))
    ssw, ssh = max(2, int(round(sw * sc))), max(2, int(round(sh * sc)))
    ds = np.diag([sc, sc, 1.0])
    dsrc = np.diag([sw / ssw, sh / ssh, 1.0])
    ms = {n: ds @ m[n] @ dsrc for n in m}
    log('seam scale %.4f -> %dx%d' % (sc, scw, sch))

    dens = np.zeros((sch, scw), np.float32)
    tiles, corners, masks, order, qs = [], [], [], [], []
    for i, n, (bx0, by0, bx1, by1), mo in _boxes(ms, paths, ssw, ssh, scw, sch):
        img = cv2.resize(cv2.imread(n), (ssw, ssh), interpolation=cv2.INTER_AREA)
        tile = cv2.warpPerspective(img, mo, (bx1 - bx0, by1 - by0))
        cov = _coverage(mo, ssw, ssh, (bx1 - bx0, by1 - by0))
        q = cv2.warpPerspective(np.clip(_density(mo, ssw, ssh), 0, 4.0), mo,
                                (bx1 - bx0, by1 - by0))
        q[cov == 0] = 0
        np.maximum(dens[by0:by1, bx0:bx1], q, out=dens[by0:by1, bx0:bx1])
        tiles.append(tile)
        corners.append((bx0, by0))
        masks.append(cov)
        qs.append(q)
        order.append((i, n, (bx0, by0, bx1, by1)))
        del img
    if not tiles:
        raise SystemExit('no frame lands on the canvas; check --roi')
    log('seam-scale warps: %d frames' % len(tiles))

    # Drop, per frame, the pixels it resolves far worse than the best frame does.
    for j, (bx0, by0) in enumerate(corners):
        h, w = masks[j].shape
        keep = qs[j] >= rel_density * dens[by0:by0 + h, bx0:bx0 + w]
        masks[j] = (masks[j] > 0).astype(np.uint8) * 255 * keep
    del dens, qs

    comp = cv2.detail.ExposureCompensator_createDefault(
        cv2.detail.ExposureCompensator_GAIN_BLOCKS)
    comp.feed(corners, tiles, masks)
    log('exposure gains fed')

    finder = (cv2.detail_DpSeamFinder('COLOR') if seam == 'dp'
              else cv2.detail_GraphCutSeamFinder('COST_COLOR_GRAD'))
    seams = finder.find([tile.astype(np.float32) / 255.0 for tile in tiles],
                        corners, [mask.copy() for mask in masks])
    seams = [np.asarray(s.get() if hasattr(s, 'get') else s) for s in seams]
    claimed_px = sum(int((s > 0).sum()) for s in seams)
    log('seams found, %.1f Mpx claimed of %.1f' % (claimed_px / 1e6, scw * sch / 1e6))
    del tiles

    # A DP seam can leave slivers unclaimed; give every still-unclaimed pixel back to
    # the frame that covered it, so the blend has no holes.
    claimed = np.zeros((sch, scw), np.uint8)
    for (bx0, by0), s in zip(corners, seams):
        h, w = s.shape
        claimed[by0:by0 + h, bx0:bx0 + w] |= (s > 0)
    for j, ((bx0, by0), s) in enumerate(zip(corners, seams)):
        h, w = s.shape
        sub = claimed[by0:by0 + h, bx0:bx0 + w]
        take = (masks[j] > 0) & (sub == 0)
        s[take] = 255
        sub[take] = 1
    log('unclaimed slivers reassigned')

    # ---- full scale --------------------------------------------------------
    blender = cv2.detail_MultiBandBlender()
    blender.setNumBands(bands)
    blender.prepare((0, 0, cw, ch))
    for j, (i, n, (sx0, sy0, sx1, sy1)) in enumerate(order):
        # The seam tile covers [sx0/sc, sx1/sc) at full scale; render exactly that
        # window so the up-sampled seam mask lands where it was computed.
        bx0, by0 = int(np.floor(sx0 / sc)), int(np.floor(sy0 / sc))
        bw, bh = int(round((sx1 - sx0) / sc)), int(round((sy1 - sy0) / sc))
        bx1, by1 = min(bx0 + bw, cw), min(by0 + bh, ch)
        if bx1 <= bx0 or by1 <= by0:
            continue
        mo = np.array([[1, 0, -bx0], [0, 1, -by0], [0, 0, 1]], np.float64) @ m[n]
        img = cv2.resize(cv2.imread(n), (sw, sh), interpolation=cv2.INTER_AREA)
        tile = cv2.warpPerspective(img, mo, (bx1 - bx0, by1 - by0))
        del img
        cov = _coverage(mo, sw, sh, (bx1 - bx0, by1 - by0))
        comp.apply(j, (bx0, by0), tile, cov)
        # Dilate at seam scale before up-sampling: neighbouring seam masks must
        # overlap by a few output pixels or the blend leaves black hairlines.
        d = cv2.dilate(seams[j], np.ones((3, 3), np.uint8))
        mask = cv2.resize(d, (bw, bh), interpolation=cv2.INTER_LINEAR)
        mask = mask[:by1 - by0, :bx1 - bx0]
        blender.feed(tile.astype(np.int16), ((mask > 8).astype(np.uint8) * 255) & cov,
                     (bx0, by0))
        del tile, cov, mask
    log('composited %d frames' % len(order))
    res, res_mask = blender.blend(None, None)
    return cv2.convertScaleAbs(res), np.asarray(res_mask), m, out_scale
