"""Natural (display) views of the flat base, as interchangeable projections.

THE PROJECTION IS NOT SETTLED. The flat base is a settled, measured artifact; how the
wall should be presented for display is an open product question, and the cylindrical
remap below has already been rejected once as "extremely morphed". So the choice is a
plug rather than a hardcoded stage: a projection is anything that can

  * render one or more named views of the flat base (`emit`), and
  * carry a point from the flat base onto the default view (`map_boxes`),

and `--natural NAME` picks one. Adding a candidate means adding a class here and a line
in PROJECTIONS; nothing else in the pipeline knows which one ran.

The second requirement is the one that bites. A view whose hold coordinates cannot be
derived from the flat base's is not a display view, it is a second source of truth.
"""
import numpy as np

from . import cylinder, finish

CURVE_PRESETS = {'gentle': 30.0, 'medium': 42.5, 'strong': 55.0}


class Flat:
    """The flat base itself, presented unchanged.

    This is the honest default while the projection is undecided: it is one of the two
    candidates under consideration (a flat developed view), and it cannot misrepresent
    the wall because it applies no reprojection at all. Hold coordinates are the
    identity, so nothing can drift.
    """

    name = 'flat'
    describe = 'the flat base itself, no reprojection'

    def emit(self, args, log, base, mask):
        log('flat: no reprojection')
        return {'flat': (base, {'projection': 'flat'})}, 'flat'

    def map_boxes(self, boxes, w, h, args, meta, log):
        return boxes, 0, w, h


class Cylindrical:
    """Analytic vertical-axis cylindrical remap.

    PROVISIONAL. This is the exp7 geometry, normalised on the CENTRE derivative so the
    middle of the wall is untouched and isotropic and the ends foreshorten horizontally
    (the earlier normalisation held the ends at the frame edges, which magnified the
    centre by up to 1.4x -- the anamorphic widening). The output is therefore always
    NARROWER than the flat base, never wider.

    That fixes the arithmetic, not the product question: a reviewer rejected this view
    as over-morphed, and the presets below are somebody's picks rather than measured
    values. Per the design note, theta_max should ultimately come from the layout pass
    (the sum of the extreme facet yaws) so the curvature is metric rather than guessed.
    Do not treat these numbers as settled.
    """

    name = 'cylindrical'
    describe = 'analytic vertical-axis cylindrical remap (provisional)'

    def emit(self, args, log, base, mask):
        curves = self._curve_set(args.curve, args.emit_all_curves)
        chosen = self._default_name(args.curve, curves)
        out = {}
        for name in sorted(curves, key=curves.get):
            theta = curves[name]
            tm, k, radius = cylinder.geometry(theta, args.wall_width_m, args.view_dist_m)
            log('%s: theta_max %.1f deg, k %.3f, R %.2f m' % (name, theta, k, radius))
            img, m = cylinder.warp(base, theta, k, 1.0, eye_frac=args.eye_frac, mask=mask)
            # The curve re-introduces a staircase at the ends, where columns are
            # minified hardest; trim it the same way the base was trimmed. The crop is
            # an axis-aligned offset, so map_boxes can undo it exactly.
            img, _, box, verts, gap = finish.apply(img, m)
            out[name] = (img, {'projection': 'cylindrical',
                               'theta_max_deg': round(theta, 3),
                               'k': round(float(k), 5),
                               'radius_m': round(float(radius), 3),
                               'crop_box': [int(v) for v in box],
                               'polygon_vertices': int(verts),
                               'inpainted_px': int(gap)})
            del m
        return out, chosen

    def map_boxes(self, boxes, w, h, args, meta, log):
        theta = meta['theta_max_deg']
        _, k, _ = cylinder.geometry(theta, args.wall_width_m, args.view_dist_m)
        moved, off, out_w = _to_natural(boxes, w, h, theta, k, args.eye_frac, log)
        x0, y0, x1, y1 = meta['crop_box']
        moved[:, 0] -= x0
        moved[:, 1] -= y0
        return moved, off, x1 - x0, y1 - y0

    @staticmethod
    def _curve_set(theta, emit_all):
        """Named curvatures to render. The requested one always keeps its own label."""
        if not emit_all:
            name = next((k for k, v in CURVE_PRESETS.items() if v == theta), 'custom')
            return {name: theta}
        if theta in CURVE_PRESETS.values():
            return dict(CURVE_PRESETS)
        return {'gentle': theta * 0.7, 'medium': theta, 'strong': theta * 1.3}

    @staticmethod
    def _default_name(theta, curves):
        for name, value in curves.items():
            if value == theta:
                return name
        return next(iter(curves))


def _to_natural(boxes, w, h, theta_max_deg, k, eye_frac, log):
    """Map flat-base boxes through the same analytic cylinder that made the view."""
    src_x, s_of_x = cylinder.forward_lookup(w, theta_max_deg, k, 1.0, eye_frac)
    w_out = len(src_x)
    out_cols = np.arange(w_out, dtype=np.float64)
    yc = h * eye_frac

    cxf = boxes[:, 0] + boxes[:, 2] / 2
    cyf = boxes[:, 1] + boxes[:, 3] / 2
    xo = np.interp(cxf, src_x, out_cols)
    sc = np.interp(xo, out_cols, s_of_x)
    yo = yc + (cyf - yc) * sc
    # box size: horizontal scale is d(out)/d(src) = 1/gradient(src_x), vertical is sc
    hscale = np.interp(xo, out_cols, 1.0 / np.maximum(np.gradient(src_x), 1e-9))
    wo = boxes[:, 2] * hscale
    ho = boxes[:, 3] * sc
    off = int(((yo < 0) | (yo >= h)).sum())
    log('natural: %d holds, %d fall outside the frame vertically' % (len(boxes), off))
    return np.stack([xo - wo / 2, yo - ho / 2, wo, ho], 1), off, w_out


PROJECTIONS = {p.name: p for p in (Flat(), Cylindrical())}
