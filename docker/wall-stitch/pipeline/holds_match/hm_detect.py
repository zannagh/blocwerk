"""Independent hold detection in the new orthophoto (tiled YOLOv8 via cv2.dnn).

This is what turns "approximately placed" into "mechanically placed": a transferred
hold is snapped onto a blob the detector found in the NEW image, so the outline sits
on a real hold rather than wherever the deformation field happened to land.
"""
import cv2
import numpy as np

from hm_common import ONNX

MIN_SIDE = 45.0      # px: reject slivers
MAX_ASPECT = 3.0     # reject the tall thin phantom boxes the detector emits on bare wood
MIN_GRAD_P90 = 90.0  # px-value: a real hold has strong internal shading
MIN_L_STD = 16.0

TILE = 1280
STRIDE = 960
NET_IN = 640


def _nms(boxes, scores, thr=0.5):
    """Greedy NMS on intersection-over-MIN-area.

    Plain IoU leaves nested duplicates alive (a small box fully inside a big one has
    low IoU), and tiled inference produces exactly that.
    """
    if len(boxes) == 0:
        return np.zeros(0, int)
    x1, y1 = boxes[:, 0], boxes[:, 1]
    x2, y2 = boxes[:, 0] + boxes[:, 2], boxes[:, 1] + boxes[:, 3]
    area = boxes[:, 2] * boxes[:, 3]
    sup = np.zeros(len(boxes), bool)
    keep = []
    for i in np.argsort(-scores):
        if sup[i]:
            continue
        keep.append(i)
        inter = (np.clip(np.minimum(x2[i], x2) - np.maximum(x1[i], x1), 0, None)
                 * np.clip(np.minimum(y2[i], y2) - np.maximum(y1[i], y1), 0, None))
        sup |= inter / np.minimum(area[i], area) > thr
        sup[i] = True
    return np.array(keep, int)


def detect(img, conf_thr=0.45, log=print, tile=TILE, stride=STRIDE,
           min_side=MIN_SIDE, filter_scale=0.25):
    """Tiled detection. `tile` is the crop size fed to the net; a plane whose
    holds are physically smaller in pixels wants a smaller tile (more zoom).

    Images narrower or shorter than one tile - the 113 px kickboard strip - are
    edge-padded so the tile keeps its aspect ratio; feeding a 3190x113 strip
    straight into a 640x640 net would stretch every hold by 5.7x vertically.
    """
    net = cv2.dnn.readNetFromONNX(ONNX)
    h, w = img.shape[:2]
    pad_y = max(0, tile - h)
    pad_x = max(0, tile - w)
    if pad_y or pad_x:
        img = cv2.copyMakeBorder(img, 0, pad_y, 0, pad_x, cv2.BORDER_REPLICATE)
        h, w = img.shape[:2]
    xs = list(range(0, max(1, w - tile + 1), stride))
    ys = list(range(0, max(1, h - tile + 1), stride))
    if xs[-1] + tile < w:
        xs.append(w - tile)
    if ys[-1] + tile < h:
        ys.append(h - tile)
    boxes, scores, classes = [], [], []
    for n, y0 in enumerate(ys):
        for x0 in xs:
            crop = img[y0:y0 + tile, x0:x0 + tile]
            blob = cv2.dnn.blobFromImage(crop, 1 / 255.0, (NET_IN, NET_IN), swapRB=True, crop=False)
            net.setInput(blob)
            out = net.forward()[0]
            if out.shape[0] < out.shape[1]:
                out = out.T
            cls_scores = out[:, 4:]
            best = cls_scores.max(1)
            keep = best >= conf_thr
            if not keep.any():
                continue
            sel = out[keep]
            sc = best[keep]
            cl = cls_scores[keep].argmax(1)
            k = tile / NET_IN
            cx, cy, bw, bh = sel[:, 0] * k, sel[:, 1] * k, sel[:, 2] * k, sel[:, 3] * k
            boxes.append(np.stack([cx - bw / 2 + x0, cy - bh / 2 + y0, bw, bh], 1))
            scores.append(sc)
            classes.append(cl)
        if log:
            log(f"    detector row {n + 1}/{len(ys)}")
    if not boxes:
        return np.zeros((0, 4)), np.zeros(0), np.zeros(0, int)
    boxes = np.concatenate(boxes)
    scores = np.concatenate(scores)
    classes = np.concatenate(classes)
    keep = _nms(boxes.astype(np.float32), scores.astype(np.float32))
    boxes, scores, classes = boxes[keep], scores[keep], classes[keep]
    good = appearance_filter(img, boxes, scale=filter_scale, min_side=min_side)
    if log:
        log(f"    appearance filter kept {int(good.sum())}/{len(boxes)}")
    return boxes[good], scores[good], classes[good]


def appearance_filter(img, boxes, scale=0.25, min_side=MIN_SIDE):
    """Drop detections that are geometrically or photometrically implausible.

    On the softer, low-contrast bottom of the orthophoto the detector emits dense
    stacks of tall thin boxes over bare plywood. They are trivially separable from
    real holds by aspect ratio and by internal contrast (bare wood has almost none).
    """
    if len(boxes) == 0:
        return np.zeros(0, bool)
    small = cv2.resize(img, None, fx=scale, fy=scale, interpolation=cv2.INTER_AREA)
    lab = cv2.cvtColor(small, cv2.COLOR_BGR2LAB)
    lum = lab[:, :, 0]
    grad = np.hypot(cv2.Sobel(lum, cv2.CV_32F, 1, 0, 3), cv2.Sobel(lum, cv2.CV_32F, 0, 1, 3))
    side = np.minimum(boxes[:, 2], boxes[:, 3])
    aspect = np.maximum(boxes[:, 2], boxes[:, 3]) / np.maximum(side, 1.0)
    keep = (side >= min_side) & (aspect <= MAX_ASPECT)
    for i in np.where(keep)[0]:
        x, y, w, h = boxes[i] * scale
        x0, y0 = max(0, int(x)), max(0, int(y))
        pl = lum[y0:int(y + h), x0:int(x + w)]
        pg = grad[y0:int(y + h), x0:int(x + w)]
        if pl.size < 9 or np.percentile(pg, 90) < MIN_GRAD_P90 or pl.std() < MIN_L_STD:
            keep[i] = False
    return keep


def blob_centres(boxes):
    return np.stack([boxes[:, 0] + boxes[:, 2] / 2, boxes[:, 1] + boxes[:, 3] / 2], 1)


MAX_SIZE_RATIO = 1.7   # a snap that changes the hold size by more than this keeps
                       # the predicted radius (it is probably a neighbouring blob)
SNAP_SIZE_GATE = 2.2   # never snap onto a blob this many times bigger/smaller: that
                       # is a volume or a neighbour, not this hold


def snap(points, radii, boxes, max_rel=1.0, max_abs=70.0, min_tol=25.0):
    """Snap each point to the nearest detection whose size is compatible.

    Returns (snapped_points, snapped_radii, distance, index) with index -1 = no snap.
    """
    if len(boxes) == 0:
        return points.copy(), radii.copy(), np.full(len(points), np.inf), np.full(len(points), -1)
    cen = blob_centres(boxes)
    bsize = np.maximum(boxes[:, 2], boxes[:, 3]) / 2.0
    out_p = points.copy()
    out_r = radii.copy()
    dist = np.full(len(points), np.inf)
    who = np.full(len(points), -1)
    for i, (p, r) in enumerate(zip(points, radii)):
        tol = min(max_abs, max(max_rel * max(r, 12.0), min_tol))
        d = np.linalg.norm(cen - p, axis=1)
        cand = np.where(d <= tol)[0]
        if len(cand) == 0:
            continue
        ratio = np.maximum(bsize[cand] / max(r, 1.0), max(r, 1.0) / np.maximum(bsize[cand], 1.0))
        cand, ratio = cand[ratio <= SNAP_SIZE_GATE], ratio[ratio <= SNAP_SIZE_GATE]
        if len(cand) == 0:
            continue
        cost = d[cand] / tol + 0.6 * np.maximum(ratio - 1.6, 0)
        j = cand[int(np.argmin(cost))]
        out_p[i] = cen[j]
        ratio = max(bsize[j] / max(r, 1.0), max(r, 1.0) / max(bsize[j], 1.0))
        out_r[i] = bsize[j] if ratio <= MAX_SIZE_RATIO else r
        dist[i] = float(d[j])
        who[i] = int(j)
    return out_p, out_r, dist, who
