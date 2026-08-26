"""Pipeline stages: base composite, natural view, hold detection, carryover.

Split out of the CLI so each file stays readable; every stage takes the parsed
arguments, a logger and the output directory, and writes only under that directory.

The natural stage delegates to `projections`, because which display view the wall
should get is an open question while the flat base is not.
"""
import json
import os

import cv2
import numpy as np

from . import carry_report, carryover, compose, detect
from . import detect_run, finish, projections, rectify, register, util

#: A frame warping to more than this many times the median frame area sees the wall
#: nearly edge-on. It is still worth compositing - it may be the only cover of some
#: strip - but its corner quads land tens of thousands of work-pixels away, and letting
#: those into the ROI statistic is what blows the canvas up.
ROI_AREA_RATIO = 8.0
#: Frames entering the composite smaller than this on the long side never produce a
#: usable wall, whatever the blend does.
MIN_COMPOSE_LONG_EDGE = 400


def resolve_frames(args):
    """(frames, reference). The reference is None unless --ref asked for a specific one:
    picking it is registration's job, since only registration knows how the sweep
    actually chains together."""
    paths = args.images or util.list_images(args.input_dir)
    if len(paths) < 2:
        raise SystemExit('need at least two frames to stitch')
    paths = [os.path.abspath(p) for p in paths]
    if not args.ref:
        return paths, None
    ref = os.path.abspath(args.ref)
    if ref not in paths:
        raise SystemExit('--ref %s is not one of the input frames' % ref)
    return paths, ref


def load_matrix(path):
    if path.endswith('.npy'):
        return np.load(path)
    return np.array(json.load(open(path)), np.float64)


def roi_from_quads(quads, paths, log):
    """The crop the composite is built on, from the frames that actually face the wall.

    `percentile_roi` alone cannot survive this input. On a real sweep a sixth of the
    frames catch the wall almost edge-on, and their warped quads land so far out that
    even a 2/98 percentile over the corner points is dragged with them - the canvas then
    exceeds the megapixel cap by three orders of magnitude and the compose scale
    collapses to fit. Discard those frames from the STATISTIC only: they still go into
    the blend, where they may be the sole cover of some strip.
    """
    areas = np.array([abs(float(cv2.contourArea(np.asarray(q, np.float32))))
                      for q in quads])
    median = float(np.median(areas))
    keep = areas <= ROI_AREA_RATIO * median if median > 0 else np.ones(len(areas), bool)
    if not keep.any():
        keep = np.ones(len(areas), bool)
    dropped = [os.path.basename(p) for p, k in zip(paths, keep) if not k]
    if dropped:
        log('ROI ignores %d near-edge-on frame(s) (>%.0fx median footprint): %s'
            % (len(dropped), ROI_AREA_RATIO, ', '.join(dropped)))
    return util.percentile_roi([q for q, k in zip(quads, keep) if k])


def check_plane(out_scale, src_w, src_h, log):
    """Refuse a composite that is already known to be worthless.

    The pipeline used to exit 0 on a radial smear: when the reference plane or the ROI
    is wrong the canvas blows up, the megapixel cap drags the compose scale down to
    compensate, and every frame enters the warp a couple of hundred pixels wide. The
    blend then does a beautiful job on mush. Fail loudly instead - the caller can pick
    a different --ref or pass an explicit --roi, but only if it is told.
    """
    long_edge = max(src_w, src_h) * out_scale
    if long_edge < MIN_COMPOSE_LONG_EDGE:
        raise SystemExit(
            'no dominant plane: the composite plane is degenerate. Frames enter the '
            'canvas %d px on the long side (floor %d), which means the canvas hit the '
            '--max-canvas-mpx cap by orders of magnitude. The reference frame or the '
            'ROI is wrong; try an explicit --ref or --roi.'
            % (int(long_edge), MIN_COMPOSE_LONG_EDGE))
    log('compose sanity: frames enter at %d px on the long side' % int(long_edge))


def stage_base(args, log, out):
    """Register the frames and composite the flat, plane-rectified base."""
    paths, ref = resolve_frames(args)
    cache = args.cache_dir or os.path.join(args.output_dir, '.cache')
    os.makedirs(cache, exist_ok=True)
    log('%d frames, reference %s'
        % (len(paths), os.path.basename(ref) if ref else 'to be chosen'))

    hs, wscale, ww, wh, ref, reg_stats = register.run(
        paths, cache, ref=ref, work_mpx=args.work_mp, nfeat=args.nfeat, log=log)
    kept = [p for p in paths if p in hs]

    rect_source = 'none'
    rect = np.eye(3)
    if args.rect_matrix:
        rect, rect_source = load_matrix(args.rect_matrix), args.rect_matrix
    elif args.rectify == 'auto':
        rect, _, probe = rectify.solve(hs, wscale, kept, ww, wh, log=log)
        rect_source = 'auto' if not np.allclose(rect, np.eye(3)) else 'auto (fell back)'
        util.write_preview(os.path.join(out, 'plane-probe.jpg'), probe)
        del probe

    rect_hs = {n: rect @ hs[n] for n in kept}
    quads = compose.frame_quads(rect_hs, kept, ww, wh)
    roi = tuple(args.roi) if args.roi else roi_from_quads(quads, kept, log)

    src_w, src_h = util.image_size(kept[0])
    out_scale = float(min(1.0, np.sqrt(args.compose_mp * 1e6 / (src_w * src_h))))
    log('compose at %.4f of source (%d x %d)' % (out_scale, src_w, src_h))
    base, mask, cams, out_scale = compose.build(hs, wscale, kept, out_scale, rect=rect,
                                                roi=roi, max_mpx=args.max_canvas_mpx,
                                                log=log)
    check_plane(out_scale, src_w, src_h, log)
    # Straighten the jagged coverage staircase and crop tight. Doing it here, before
    # the curve and before detection, is what keeps every hold coordinate in this run
    # normalised against one image: everything downstream sees only the finished base.
    base, mask, box, verts, gap = finish.apply(base, mask)
    log('silhouette %d verts, %d px inpainted, crop %s' % (verts, gap, box))
    return dict(base=base, mask=mask, paths=kept, ref=ref, rect=rect,
                rect_source=rect_source, roi=[float(v) for v in roi],
                work_scale=wscale, work_size=[ww, wh], out_scale=out_scale,
                registration=reg_stats, source_size=[src_w, src_h],
                cameras=cams, crop_box=[int(v) for v in box],
                finish={'crop_box': [int(v) for v in box],
                        'polygon_vertices': int(verts), 'inpainted_px': int(gap)})


def write_cameras(out, st, manifest):
    """Per-frame homography taking SOURCE pixels straight to finished flat-base pixels.

    The crop that `finish` applied is folded in, so a consumer needs nothing but this
    matrix to project a point from an original photo onto the flat base it can see.
    """
    x0, y0 = st['crop_box'][0], st['crop_box'][1]
    shift = np.array([[1, 0, -x0], [0, 1, -y0], [0, 0, 1]], np.float64)
    scale = np.diag([st['out_scale'], st['out_scale'], 1.0])
    doc = {'schema': 'blocwerk.wall-cameras/1',
           'target': 'flat-base.jpg',
           'target_width': int(st['base'].shape[1]),
           'target_height': int(st['base'].shape[0]),
           'source_width': int(st['source_size'][0]),
           'source_height': int(st['source_size'][1]),
           'note': ('matrix maps a point in the ORIGINAL frame, in that frame\'s own '
                    'pixels, onto flat-base.jpg pixels.'),
           'frames': []}
    for path in st['paths']:
        h = shift @ st['cameras'][path] @ scale
        doc['frames'].append({'frame': os.path.basename(path),
                              'matrix': [[float(v) for v in row] for row in h]})
    with open(os.path.join(out, 'cameras.json'), 'w') as fh:
        json.dump(doc, fh, indent=1)
    manifest['artifacts']['cameras'] = 'cameras.json'


def stage_naturals(args, log, out, base, mask, manifest):
    """Render the display views through whichever projection --natural selected."""
    projection = projections.PROJECTIONS[args.natural]
    log('natural projection: %s (%s)' % (projection.name, projection.describe))
    views, chosen = projection.emit(args, log, base, mask)

    manifest['curvature'] = {'projection': projection.name,
                             'requested_theta_max_deg': args.curve,
                             'default': chosen, 'emitted': {}}
    for name, (img, meta) in views.items():
        path = util.write_jpeg(os.path.join(out, 'natural-%s.jpg' % name), img)
        util.write_preview(os.path.join(out, 'natural-%s-preview.jpg' % name), img)
        entry = dict(meta)
        entry.update({'image': os.path.basename(path),
                      'preview': 'natural-%s-preview.jpg' % name,
                      'width': img.shape[1], 'height': img.shape[0]})
        manifest['curvature']['emitted'][name] = entry
        if name == chosen:
            manifest['artifacts']['natural_default'] = os.path.basename(path)
        manifest['artifacts'].setdefault('naturals', []).append(os.path.basename(path))
    return views, chosen


def stage_detect(args, log, out, base, mask, manifest, views, chosen):
    if not args.onnx or not os.path.exists(args.onnx):
        raise SystemExit('--onnx model not found: %r (or set WALL_PIPELINE_ONNX)'
                         % args.onnx)
    poly = (json.load(open(args.wall_poly)) if args.wall_poly
            else detect.coverage_poly(mask))
    log('wall polygon: %d vertices (%s)'
        % (len(poly), 'supplied' if args.wall_poly else 'from the coverage mask'))
    boxes, scores, stats = detect_run.detect_holds(base, args.onnx, poly, log=log)
    h, w = base.shape[:2]
    flat_json = os.path.join(out, 'holds-flat.json')
    detect_run.dump(boxes, scores, w, h, flat_json, 'flat-base.jpg')
    detect_run.draw(base, boxes, os.path.join(out, 'holds-flat-overlay.jpg'))
    log('flat done: %d holds' % len(boxes))

    # The same projection that rendered the default view carries the holds onto it, so
    # the two can never disagree about where a hold is.
    projection = projections.PROJECTIONS[args.natural]
    nboxes, off, nw, nh = projection.map_boxes(
        boxes, w, h, args, views[chosen][1], log)
    nat_json = os.path.join(out, 'holds-natural.json')
    detect_run.dump(nboxes, scores, nw, nh, nat_json, 'natural-%s.jpg' % chosen)
    nat = cv2.imread(os.path.join(out, 'natural-%s.jpg' % chosen))
    detect_run.draw(nat, nboxes, os.path.join(out, 'holds-natural-overlay.jpg'))
    del nat

    stats['final'] = int(len(boxes))
    stats['outside_natural_frame'] = off
    stats['wall_polygon_vertices'] = len(poly)
    stats['wall_polygon_source'] = 'supplied' if args.wall_poly else 'coverage-mask'
    manifest['detection'] = stats
    manifest['artifacts'].update({
        'holds_flat': 'holds-flat.json', 'holds_natural': 'holds-natural.json',
        'holds_flat_overlay': 'holds-flat-overlay.jpg',
        'holds_natural_overlay': 'holds-natural-overlay.jpg'})
    with open(os.path.join(out, 'wall-polygon.json'), 'w') as fh:
        json.dump(poly, fh)
    manifest['artifacts']['wall_polygon'] = 'wall-polygon.json'
    return flat_json


def stage_carryover(args, log, out, base, manifest, flat_json):
    cache = args.cache_dir or os.path.join(args.output_dir, '.cache')
    old = util.read_image(args.prior_image)
    oh, ow = old.shape[:2]
    nh, nw = base.shape[:2]
    doc, live, generation = carry_report.load_prior_holds(args.prior_holds,
                                                         args.prior_generation)
    po, r_old = carry_report.hold_points(live, ow, oh)
    det_ids, cen, r_det, conf = carry_report.load_detections(flat_json, nw, nh)
    log('prior %dx%d, new %dx%d, generation %s: %d live holds vs %d detections'
        % (ow, oh, nw, nh, generation, len(live), len(det_ids)))

    seed_h = load_matrix(args.seed_matrix) if args.seed_matrix else None
    mask = util.read_image(args.prior_mask, cv2.IMREAD_GRAYSCALE) if args.prior_mask else None
    if mask is not None:
        log('prior mask: %d%% of the old photo' % (100 * (mask > 0).mean()))
    al = carryover.align(old, base, po, r_old, cen, r_det, cache_dir=cache,
                         seed_h=seed_h, log=log)
    np.save(os.path.join(out, 'carryover-seed.npy'), al['seed_h'])

    records = carry_report.build_records(live, al, cen, r_det, det_ids, ow, oh, nw, nh)
    claimed = set(int(x) for x in al['who'] if x >= 0)
    new_dets = carry_report.new_detections(records, det_ids, cen, r_det, conf,
                                           claimed, nw, nh)
    doc_out, counts, linked = carry_report.document(
        al, live, records, new_dets, doc.get('_coordinateConvention'),
        {'path': os.path.abspath(args.prior_image), 'width': ow, 'height': oh},
        {'path': 'flat-base.jpg', 'width': nw, 'height': nh})
    with open(os.path.join(out, 'carryover.json'), 'w') as fh:
        json.dump(doc_out, fh, indent=1)
    diag = carry_report.diagnostics(al, live, records, counts, linked, new_dets)
    diag['radii_px']['detection_median'] = round(float(np.median(r_det)), 1)
    with open(os.path.join(out, 'carryover-diagnostics.json'), 'w') as fh:
        json.dump(diag, fh, indent=1)
    carry_report.draw(base, records, cen, r_det, claimed,
                      os.path.join(out, 'carryover-overlay.jpg'))
    log('counts %s  boulder-linked %s' % (counts, linked))

    manifest['carryover'] = {
        'prior_image': os.path.abspath(args.prior_image),
        'prior_holds': os.path.abspath(args.prior_holds),
        'generation': generation, 'counts': counts,
        'counts_boulder_linked': linked,
        'estimated_precision': diag['estimated_precision'],
        'seed': al['seed_stats'],
        'blocker': carry_report.BLOCKER}
    manifest['artifacts'].update({
        'carryover': 'carryover.json',
        'carryover_diagnostics': 'carryover-diagnostics.json',
        'carryover_overlay': 'carryover-overlay.jpg',
        'carryover_seed': 'carryover-seed.npy'})
