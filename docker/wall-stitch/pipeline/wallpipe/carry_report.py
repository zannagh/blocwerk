"""Turn an alignment into the carryover record set, its diagnostics and its overlay.

Split out of new-run/exp5/carryover.py; the classification logic and every threshold
and caveat string are carried over unchanged.
"""
import json

import cv2
import numpy as np

from .carryover import COLOUR_OK, CONSISTENCY_PX, GATE_MAX, GATE_MIN, GATE_REL

BLOCKER = (
    'DO NOT APPLY UNATTENDED. The limit is the recognition surface, not the matcher.'
    ' After the best available alignment, large-patch correlation still leaves a ~99 px'
    ' median residual, and patches only 150 px apart disagree about the local'
    ' displacement by a median 51 px (p90 196 px). The stored holds sit a median 174 px'
    ' apart, so the target\'s local geometry varies faster than the holds are spaced and'
    ' no smooth map can put them in correspondence. The flat base is a composite whose'
    ' internal geometry is not a consistent projection of the wall.')

DISTANCE_WARNING = (
    'match_distance_px is NOT a correctness measure. On a hold field this dense and'
    ' uniform a wrong alignment yields just as tight a residual as the right one; an'
    ' earlier run reported a 68 px median while pairing holds with the wrong holds.'
    ' Use estimated_precision.')

COLOUR_POLICY = (
    'Colour is a SOFT tie-break inside the ICP correspondence cost only. It never'
    ' rejects a geometric match. `colour_distance` and `colour_agrees` on each hold'
    ' are informational.')


def load_prior_holds(path, generation=None):
    """Read the app's holds export. Returns (document, live holds sorted by id)."""
    with open(path) as fh:
        doc = json.load(fh)
    holds = doc['holds']
    if generation is None:
        gens = {h.get('Generation', 0) for h in holds}
        generation = max(gens)
    live = [h for h in holds if h.get('Generation', 0) == generation]
    live.sort(key=lambda h: h['Id'])
    return doc, live, generation


def hold_points(live, ow, oh):
    po = np.array([[h['X'] * ow, h['Y'] * oh] for h in live], np.float64)
    r_old = np.array([float(h.get('Radius') or 0.0) * max(ow, oh) for h in live])
    return po, r_old


def load_detections(path, nw, nh):
    rows = json.load(open(path))['holds']
    cen = np.array([[r['x'] * nw, r['y'] * nh] for r in rows], np.float64)
    rad = np.array([max(r['w'] * nw, r['h'] * nh) / 2.0 for r in rows], np.float64)
    conf = np.array([r['confidence'] for r in rows], np.float64)
    return [r['id'] for r in rows], cen, rad, conf


def build_records(live, al, cen, r_det, det_ids, ow, oh, nw, nh):
    who, dist, cd = al['who'], al['dist'], al['colour_distance']
    transferred, r_pred, resid = al['transferred'], al['r_pred'], al['resid']
    records = []
    for k, h in enumerate(live):
        j = int(who[k])
        carried = j >= 0
        pos = cen[j] if carried else transferred[k]
        rad = max(r_det[j], 0.6 * r_pred[k]) if carried else r_pred[k]
        if carried:
            reason = ''
        elif not al['in_frame'][k]:
            reason = 'transferred position falls outside the new image'
        elif al['still_there'][k]:
            reason = ('no detection claimed it but a hold-like blob is still there:'
                      ' likely a detector miss rather than a removal')
        else:
            reason = 'no detection nearby and the wall looks bare there: likely removed'
        rec = {
            'Id': h['Id'], 'Category': h.get('Category'), 'Color': h.get('Color'),
            'BoulderLinkCount': h.get('BoulderLinkCount', 0),
            'old': {'X': h['X'], 'Y': h['Y'], 'Radius': h.get('Radius')},
            'transferred': {'X': round(float(transferred[k][0] / nw), 6),
                            'Y': round(float(transferred[k][1] / nh), 6)},
            'new': {'X': round(float(pos[0] / nw), 6), 'Y': round(float(pos[1] / nh), 6),
                    'Radius': round(float(rad) / max(nw, nh), 6)},
            'classification': 'CARRIED_OVER' if carried else 'MISSING',
            'matched_detection_id': (det_ids[j] if carried else None),
            'match_distance_px': (round(float(dist[k]), 1) if carried else None),
            'match_distance_radii': (round(float(dist[k] / max(r_pred[k], 1.0)), 2)
                                     if carried else None),
            # informational only: colour never rejects a match in this configuration
            'colour_distance': (round(float(cd[k, j]), 1) if carried else None),
            'colour_agrees': (bool(cd[k, j] <= COLOUR_OK) if carried else None),
            'consistency_residual_px': (round(float(resid[k]), 1)
                                        if np.isfinite(resid[k]) else None),
            'blobness': (None if carried else round(float(al['blob'][k]), 1)),
            'in_frame': bool(al['in_frame'][k]),
            'reason': reason,
            '_px_transferred': [float(transferred[k][0]), float(transferred[k][1])],
            '_px_final': [float(pos[0]), float(pos[1])],
            '_px_radius': float(rad),
        }
        if h.get('ShapePoints'):
            jac = al['mls'].jacobian(np.array([h['X'] * ow, h['Y'] * oh]))
            rec['new']['ShapePoints'] = [
                {'Dx': round(float((jac @ np.array([p['Dx'] * ow, p['Dy'] * oh]))[0] / nw), 6),
                 'Dy': round(float((jac @ np.array([p['Dx'] * ow, p['Dy'] * oh]))[1] / nh), 6)}
                for p in h['ShapePoints']]
        records.append(rec)
    return records


def new_detections(records, det_ids, cen, r_det, conf, claimed, nw, nh):
    carried_pos = np.array([r['_px_final'] for r in records
                            if r['classification'] == 'CARRIED_OVER'])
    carried_rad = np.array([r['_px_radius'] for r in records
                            if r['classification'] == 'CARRIED_OVER'])
    out = []
    for j in range(len(det_ids)):
        if j in claimed:
            continue
        if len(carried_pos):
            d = np.linalg.norm(carried_pos - cen[j], axis=1)
            is_dup = bool((d - np.maximum(carried_rad, r_det[j])).min() <= 0.0)
            nearest = round(float(d.min()), 1)
        else:
            is_dup, nearest = False, None
        out.append({
            'detection_id': det_ids[j],
            'new': {'X': round(float(cen[j][0] / nw), 6),
                    'Y': round(float(cen[j][1] / nh), 6),
                    'Radius': round(float(r_det[j]) / max(nw, nh), 6)},
            'classification': 'NEW', 'confidence': round(float(conf[j]), 4),
            'likely_duplicate_of_carried_hold': is_dup,
            'nearest_carried_hold_px': nearest})
    return out


def document(al, live, records, new_dets, convention, old_meta, new_meta):
    claimed = set(int(x) for x in al['who'] if x >= 0)
    n_dup = sum(1 for d in new_dets if d['likely_duplicate_of_carried_hold'])
    counts = {'CARRIED_OVER': len(claimed), 'MISSING': len(live) - len(claimed),
              'NEW': len(new_dets), 'NEW_excluding_duplicate_boxes': len(new_dets) - n_dup}
    linked = {c: sum(1 for r in records
                     if r['classification'] == c and r['BoulderLinkCount'] > 0)
              for c in ('CARRIED_OVER', 'MISSING')}
    nw, nh = new_meta['width'], new_meta['height']
    m = al['matched']
    return {
        '_old_image': old_meta, '_new_image': new_meta,
        '_convention': convention,
        '_note': ('`new` and `transferred` are normalised against the NEW image per axis'
                  ' and independently (X = px/%d, Y = px/%d, origin top-left); Radius'
                  ' against the LONGER side (%d). ShapePoints remain Dx/Dy offsets'
                  ' relative to (X, Y) in the same space.' % (nw, nh, max(nw, nh))),
        '_colour_policy': COLOUR_POLICY,
        '_quality': {
            'estimated_precision': round(al['precision'], 3),
            'colour_agreement': round(al['hit'], 3),
            'colour_agreement_chance_level': round(al['chance'], 3),
            'estimated_correct_carried': int(round(al['precision'] * m.sum())),
            'warning': DISTANCE_WARNING,
        },
        '_blocker': BLOCKER,
        '_match_gate': {'rel_to_predicted_radius': GATE_REL, 'min_px': GATE_MIN,
                        'max_px': GATE_MAX, 'box_aware': True,
                        'consistency_px': CONSISTENCY_PX},
        '_counts': counts, '_counts_boulder_linked': linked,
        'holds': [{k: v for k, v in r.items() if not k.startswith('_px_')}
                  for r in records],
        'new_detections': new_dets,
    }, counts, linked


def diagnostics(al, live, records, counts, linked, new_dets):
    m = al['matched']
    dist = al['dist']
    n_dup = sum(1 for d in new_dets if d['likely_duplicate_of_carried_hold'])
    diag = {'counts': counts, 'counts_boulder_linked': linked,
            'estimated_precision': round(al['precision'], 3),
            'estimated_correct_carried': int(round(al['precision'] * m.sum())),
            'colour_agreement': {'matches': round(al['hit'], 3),
                                 'chance': round(al['chance'], 3)}}
    bands = {'left(x<0.2)': lambda h: h['X'] < 0.2,
             'centre(0.2-0.8)': lambda h: 0.2 <= h['X'] <= 0.8,
             'right(x>0.8)': lambda h: h['X'] > 0.8,
             'top(y<0.33)': lambda h: h['Y'] < 0.33,
             'mid(y0.33-0.66)': lambda h: 0.33 <= h['Y'] <= 0.66,
             'bottom(y>0.66)': lambda h: h['Y'] > 0.66}
    for name, fn in bands.items():
        ix = [k for k, h in enumerate(live) if fn(h)]
        c = sum(1 for k in ix if records[k]['classification'] == 'CARRIED_OVER')
        dd = [dist[k] for k in ix if np.isfinite(dist[k])]
        diag[name] = {'n': len(ix), 'carried': c, 'rate': round(c / max(len(ix), 1), 3),
                      'median_match_px': round(float(np.median(dd)), 1) if dd else None}
    diag['match_px'] = ({'median': round(float(np.median(dist[m])), 1),
                         'p90': round(float(np.percentile(dist[m], 90)), 1)}
                        if m.any() else {'median': None, 'p90': None})
    diag['missing_breakdown'] = {
        'off_frame': int(((~m) & ~al['in_frame']).sum()),
        'blob_still_there_detector_miss': int(al['still_there'].sum()),
        'looks_bare_likely_removed': int(((~m) & al['in_frame']
                                          & ~al['still_there']).sum())}
    diag['new_detections'] = {'total': len(new_dets), 'likely_duplicate_boxes': n_dup,
                              'genuinely_new': len(new_dets) - n_dup}
    diag['radii_px'] = {'predicted_median': round(float(np.median(al['r_pred'])), 1),
                        'detection_median': None}
    return diag


def draw(new, records, cen, r_det, claimed, path, width=2200, crop=None):
    img = new if crop is None else new[crop[1]:crop[3], crop[0]:crop[2]]
    ox, oy = (0, 0) if crop is None else (crop[0], crop[1])
    s = width / img.shape[1]
    img = cv2.resize(img, (width, int(round(img.shape[0] * s))), interpolation=cv2.INTER_AREA)
    green, red, blue = (60, 210, 60), (40, 40, 235), (250, 165, 40)

    def pt(p):
        return int((p[0] - ox) * s), int((p[1] - oy) * s)

    for j in range(len(cen)):
        if j in claimed:
            continue
        cv2.circle(img, pt(cen[j]), max(3, int(r_det[j] * s)), blue, 2)
    for rec in records:
        b = pt(rec['_px_final'])
        r = max(4, int(rec['_px_radius'] * s))
        if rec['classification'] == 'CARRIED_OVER':
            a = pt(rec['_px_transferred'])
            cv2.line(img, a, b, (0, 0, 0), 3)
            cv2.line(img, a, b, green, 1)
            cv2.circle(img, b, r, green, 2)
        else:
            cv2.circle(img, b, r, red, 2)
            cv2.line(img, (b[0] - r, b[1] - r), (b[0] + r, b[1] + r), red, 2)
            cv2.line(img, (b[0] - r, b[1] + r), (b[0] + r, b[1] - r), red, 2)
    n_c = sum(1 for r in records if r['classification'] == 'CARRIED_OVER')
    legend = [(green, 'CARRIED OVER  %d' % n_c), (red, 'MISSING  %d' % (len(records) - n_c)),
              (blue, 'NEW to validate  %d' % (len(cen) - len(claimed)))]
    x0, y0 = 24, 24
    cv2.rectangle(img, (x0, y0), (x0 + 450, y0 + 34 * len(legend) + 24), (255, 255, 255), -1)
    cv2.rectangle(img, (x0, y0), (x0 + 450, y0 + 34 * len(legend) + 24), (30, 30, 30), 2)
    for k, (col, text) in enumerate(legend):
        y = y0 + 34 + 34 * k
        cv2.circle(img, (x0 + 27, y - 7), 11, col, 3)
        cv2.putText(img, text, (x0 + 52, y), cv2.FONT_HERSHEY_SIMPLEX, 0.8, (20, 20, 20), 2)
    cv2.imwrite(path, img, [cv2.IMWRITE_JPEG_QUALITY, 92])
    return path
