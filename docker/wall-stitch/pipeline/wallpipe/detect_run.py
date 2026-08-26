"""Hold detection on the flat base, and transfer of those holds onto a natural view.

Vendored from new-run/exp4/run_detect.py. The thresholds are unchanged; the fixed
input/output paths, the fixed ONNX path and the fixed wall polygon are now arguments.
"""
import json

import cv2
import numpy as np

from . import detect

SCALES = [(1280, 960), (1920, 1440), (2560, 2560)]
CONF = 0.22
NMS_IOU, NMS_IOMIN = 0.30, 0.85
FILT = dict(scale=0.35, min_side=26.0, max_aspect=3.0, min_grad_p90=40.0, min_l_std=7.0)
MIN_RING_DELTA = 8.0
MIN_BLOB_FRAC = 0.20
HIGH_CONF = 0.90
MERGE_K = 0.5          # centre-distance dedup, in diagonals of the smaller box


def draw(img, boxes, path, width=2000):
    vis = img.copy()
    for x, y, w, h in boxes:
        cv2.rectangle(vis, (int(x), int(y)), (int(x + w), int(y + h)), (0, 255, 0), 6)
        cv2.circle(vis, (int(x + w / 2), int(y + h / 2)), 5, (0, 0, 255), -1)
    hh, ww = vis.shape[:2]
    small = cv2.resize(vis, (width, max(1, int(hh * width / ww))), interpolation=cv2.INTER_AREA)
    cv2.imwrite(path, small, [cv2.IMWRITE_JPEG_QUALITY, 92])


def dump(boxes, scores, w, h, path, image):
    """Ids are the array index, so holds-flat.json entry N is holds-natural.json entry
    N: the carryover step can reference either file by the same id."""
    rows = []
    for i, ((x, y, bw, bh), s) in enumerate(zip(boxes, scores)):
        rows.append({
            'id': i,
            'x': round(float((x + bw / 2) / w), 6),
            'y': round(float((y + bh / 2) / h), 6),
            'w': round(float(bw / w), 6),
            'h': round(float(bh / h), 6),
            'confidence': round(float(s), 4),
        })
    with open(path, 'w') as fh:
        json.dump({'image': image, 'count': len(rows), 'holds': rows}, fh, indent=1)
    return rows


def detect_holds(base, onnx_path, wall_poly=None, log=print):
    """Full detection cascade on the flat base. Returns (boxes xywh, scores, stats)."""
    net = detect.load_net(onnx_path)
    box_list, score_list = [], []
    for tile, stride in SCALES:
        b, s = detect.raw_detect(base, tile, stride, CONF, net=net)
        log('  tile %d -> %d raw' % (tile, len(b)))
        box_list.append(b)
        score_list.append(s)
    boxes = np.concatenate(box_list)
    scores = np.concatenate(score_list)
    stats = {'raw': int(len(boxes))}

    keep = detect.nms_iomin(boxes, scores, NMS_IOU, NMS_IOMIN)
    boxes, scores = boxes[keep], scores[keep]
    stats['after_nms'] = int(len(boxes))
    log('raw %d -> nms %d' % (stats['raw'], stats['after_nms']))

    good = detect.appearance_filter(base, boxes, **FILT)
    boxes, scores = boxes[good], scores[good]
    stats['after_appearance_filter'] = int(len(boxes))
    log('appearance filter -> %d' % len(boxes))

    h, w = base.shape[:2]
    cx = boxes[:, 0] + boxes[:, 2] / 2
    cy = boxes[:, 1] + boxes[:, 3] / 2
    inside = detect.in_wall(cx, cy, w, h, wall_poly)
    boxes, scores = boxes[inside], scores[inside]
    stats['after_wall_polygon'] = int(len(boxes))
    log('wall polygon -> %d (dropped %d)' % (len(boxes), int((~inside).sum())))

    delta = detect.centre_ring_delta(base, boxes)
    solid = delta >= MIN_RING_DELTA
    boxes, scores = boxes[solid], scores[solid]
    stats['after_ring_contrast'] = int(len(boxes))
    log('ring contrast >= %.0f -> %d (dropped %d bare-plywood phantoms)'
        % (MIN_RING_DELTA, len(boxes), int((~solid).sum())))

    frac = detect.blob_fraction(base, boxes)
    real = (frac >= MIN_BLOB_FRAC) | (scores >= HIGH_CONF)
    boxes, scores = boxes[real], scores[real]
    stats['after_blob_fraction'] = int(len(boxes))
    log('blob>=%.2f or conf>=%.2f -> %d (dropped %d shadow/grain boxes)'
        % (MIN_BLOB_FRAC, HIGH_CONF, len(boxes), int((~real).sum())))

    n_before = len(boxes)
    merged = detect.centre_merge(boxes, scores, MERGE_K)
    boxes, scores = boxes[merged], scores[merged]
    stats['after_centre_merge'] = int(len(boxes))
    log('centre merge (k=%.2f) -> %d (merged %d duplicate boxes)'
        % (MERGE_K, len(boxes), n_before - len(boxes)))

    order = np.lexsort((boxes[:, 0] + boxes[:, 2] / 2, boxes[:, 1] + boxes[:, 3] / 2))
    return boxes[order], scores[order], stats
