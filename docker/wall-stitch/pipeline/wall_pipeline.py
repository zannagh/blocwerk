#!/usr/bin/env python
"""Wall stitching pipeline: photos in, natural wall master + holds + carryover out.

One CLI over the R&D stages that were validated separately, with no developer paths
baked in: every input and every output is an argument, and every artifact lands under
--output-dir.

    register            homography graph over the frames, globally refined
    flat base           plane-rectified fronto-parallel composite (the metric surface)
    natural             a display view of that base, via a PLUGGABLE projection
    detect              tiled YOLO hold detection on the flat base
    carryover           match the wall's existing holds onto the new detections

The flat base is settled; the natural view is not. `--natural` selects the projection,
and the same projection carries the hold coordinates onto whatever it renders, so a new
candidate can be dropped in without touching any other stage.

Coordinates: every hold JSON this writes is normalised against ITS OWN image, per axis
and independently (X = px/width, Y = px/height, origin top-left), with Radius against
the LONGER side. That is the app's convention.
"""
import argparse
import json
import os
import sys

import cv2

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from wallpipe import projections, rectify, stages, util  # noqa: E402
from wallpipe.projections import CURVE_PRESETS, PROJECTIONS  # noqa: E402

DEFAULT_ONNX = os.environ.get('WALL_PIPELINE_ONNX', '')


def curve_value(text):
    """A preset name or an explicit theta_max in degrees."""
    if text in CURVE_PRESETS:
        return CURVE_PRESETS[text]
    try:
        v = float(text)
    except ValueError:
        raise argparse.ArgumentTypeError(
            '--curve takes %s or a theta_max in degrees'
            % '|'.join(CURVE_PRESETS)) from None
    if not 1.0 <= v <= 80.0:
        raise argparse.ArgumentTypeError('--curve degrees must be within 1..80')
    return v


def build_parser():
    p = argparse.ArgumentParser(
        prog='wall_pipeline.py',
        description=__doc__.split('\n\n')[0],
        formatter_class=argparse.ArgumentDefaultsHelpFormatter)

    src = p.add_argument_group('input')
    g = src.add_mutually_exclusive_group(required=True)
    g.add_argument('--input-dir', metavar='DIR',
                   help='directory of ordered frames; filename order is sweep order')
    g.add_argument('--images', nargs='+', metavar='IMG',
                   help='explicit ordered list of frames')
    src.add_argument('--ref', metavar='IMG',
                     help='reference frame defining the mosaic plane (default: the '
                          'frame the rest of the sweep projects onto most gently)')

    out = p.add_argument_group('output')
    out.add_argument('--output-dir', required=True, metavar='DIR',
                     help='every artifact is written here and nowhere else')
    out.add_argument('--cache-dir', metavar='DIR',
                     help='reusable intermediates (features, pair matches, seed search)')
    out.add_argument('--quiet', action='store_true', help='suppress progress logging')

    view = p.add_argument_group('natural (display) view')
    view.add_argument('--natural', choices=sorted(PROJECTIONS), default='flat',
                      help='how the wall is presented for display. THIS IS NOT '
                           'SETTLED: %s' % '; '.join(
                               '%s = %s' % (n, PROJECTIONS[n].describe)
                               for n in sorted(PROJECTIONS)))

    curve = p.add_argument_group('curvature (--natural cylindrical only)')
    curve.add_argument('--curve', type=curve_value, default='medium', metavar='PRESET|DEG',
                       help='gentle|medium|strong, or an explicit theta_max in degrees')
    curve.add_argument('--emit-all-curves', dest='emit_all_curves',
                       action='store_true', default=True,
                       help='also emit the two curvatures either side of --curve')
    curve.add_argument('--no-emit-all-curves', dest='emit_all_curves',
                       action='store_false', help='emit only the --curve natural')
    curve.add_argument('--view-dist-m', type=float, default=3.0, metavar='M',
                       help='viewer distance from the wall used to derive the curve')
    curve.add_argument('--eye-frac', type=float, default=0.34, metavar='F',
                       help='eye height as a fraction of the base height')

    geom = p.add_argument_group('wall geometry')
    geom.add_argument('--wall-width-m', type=float, default=5.5, metavar='M')
    geom.add_argument('--wall-height-m', type=float, default=2.5, metavar='M')

    mem = p.add_argument_group('resolution and memory')
    mem.add_argument('--work-mp', type=float, default=0.7, metavar='MP',
                     help='megapixels per frame during feature matching')
    mem.add_argument('--compose-mp', type=float, default=2.5, metavar='MP',
                     help='megapixels per frame when the base is composited')
    mem.add_argument('--max-canvas-mpx', type=float, default=40.0, metavar='MP',
                     help='hard cap on the flat base; the compose scale drops to fit')
    mem.add_argument('--nfeat', type=int, default=12000, metavar='N',
                     help='SIFT features per frame')

    plane = p.add_argument_group('plane')
    plane.add_argument('--rectify', choices=('none', 'auto'), default='none',
                       help="'none' lets the reference frame define the plane; 'auto' "
                            'tries vanishing-point rectification and falls back to none')
    plane.add_argument('--rect-matrix', metavar='FILE',
                       help='explicit 3x3 rectification (.npy or .json) in work pixels')
    plane.add_argument('--roi', nargs=4, type=float, metavar=('X0', 'Y0', 'X1', 'Y1'),
                       help='crop in rectified work pixels; default is a robust '
                            'percentile box over all frame footprints')

    det = p.add_argument_group('hold detection')
    det.add_argument('--detect', dest='detect', action='store_true', default=True,
                     help='detect holds on the flat base')
    det.add_argument('--no-detect', dest='detect', action='store_false')
    det.add_argument('--onnx', default=DEFAULT_ONNX, metavar='FILE',
                     help='hold-detection ONNX model (env WALL_PIPELINE_ONNX)')
    det.add_argument('--wall-poly', metavar='FILE',
                     help='JSON list of normalised [x, y] vertices bounding the '
                          'climbable surface; default is the composite coverage outline')

    carry = p.add_argument_group('carryover (skipped unless both are given)')
    carry.add_argument('--prior-holds', metavar='FILE',
                       help="the app's holds export for the wall's current holds")
    carry.add_argument('--prior-image', metavar='FILE',
                       help='the wall photo those holds are normalised against')
    carry.add_argument('--prior-generation', type=int, metavar='N',
                       help='hold generation to carry over (default: the newest)')
    carry.add_argument('--prior-mask', metavar='FILE',
                       help='optional 8-bit mask limiting the prior image to the wall')
    carry.add_argument('--seed-matrix', metavar='FILE',
                       help='skip the coarse search with a known prior->new homography')
    return p


def main(argv=None):
    args = build_parser().parse_args(argv)
    log = util.Log(args.quiet)
    out = os.path.abspath(args.output_dir)
    os.makedirs(out, exist_ok=True)

    st = stages.stage_base(args, log, out)
    base, mask = st['base'], st['mask']
    util.write_jpeg(os.path.join(out, 'flat-base.jpg'), base)
    cv2.imwrite(os.path.join(out, 'flat-base-mask.png'), mask)
    util.write_preview(os.path.join(out, 'flat-base-preview.jpg'), base)
    log('flat-base %dx%d' % (base.shape[1], base.shape[0]))

    gray = cv2.cvtColor(cv2.resize(base, None, fx=0.25, fy=0.25,
                                   interpolation=cv2.INTER_AREA), cv2.COLOR_BGR2GRAY)
    manifest = {
        'schema': 'blocwerk.wall-pipeline/1',
        'coordinate_convention': (
            'Hold geometry is normalised against its own image, per axis and '
            'independently: X = px/imageWidth, Y = px/imageHeight, both 0..1, origin '
            'top-left. Radius is normalised against the LONGER side. ShapePoints are '
            'Dx/Dy offsets from (X, Y) in the same space. The aspect ratio is NOT '
            'preserved by this normalisation.'),
        'command': ' '.join(sys.argv),
        'inputs': {'frames': [os.path.basename(p) for p in st['paths']],
                   'frame_count': len(st['paths']),
                   'reference_frame': os.path.basename(st['ref']),
                   'source_width': st['source_size'][0],
                   'source_height': st['source_size'][1]},
        'registration': st['registration'],
        'plane': {'rectification': st['rect_source'],
                  'rect_matrix': [[float(v) for v in row] for row in st['rect']],
                  'roi_rectified_work_px': st['roi'],
                  'work_scale': st['work_scale'], 'work_size': st['work_size'],
                  'compose_scale': st['out_scale'],
                  'straightness': rectify.straightness(gray)},
        'wall': {'width_m': args.wall_width_m, 'height_m': args.wall_height_m,
                 'view_dist_m': args.view_dist_m, 'eye_frac': args.eye_frac},
        'artifacts': {'flat_base': 'flat-base.jpg',
                      'flat_base_mask': 'flat-base-mask.png',
                      'flat_base_preview': 'flat-base-preview.jpg'},
        'flat_base': {'width': base.shape[1], 'height': base.shape[0]},
    }

    manifest['plane']['finish'] = st['finish']
    stages.write_cameras(out, st, manifest)

    views, chosen = stages.stage_naturals(args, log, out, base, mask, manifest)

    flat_json = None
    if args.detect:
        flat_json = stages.stage_detect(args, log, out, base, mask, manifest,
                                        views, chosen)
    else:
        log('detection skipped (--no-detect)')

    if args.prior_holds and args.prior_image:
        if flat_json is None:
            raise SystemExit('carryover needs detections; drop --no-detect')
        stages.stage_carryover(args, log, out, base, manifest, flat_json)
    else:
        log('carryover skipped (fresh wall: no --prior-holds/--prior-image)')

    manifest['elapsed_seconds'] = round(log.elapsed(), 1)
    path = os.path.join(out, 'manifest.json')
    with open(path, 'w') as fh:
        json.dump(manifest, fh, indent=1)
    log('manifest %s' % path)
    return 0


if __name__ == '__main__':
    sys.exit(main())
