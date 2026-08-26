"""Tiled climbing-hold detection on the flat base (YOLOv8 via cv2.dnn).

Vendored from new-run/exp4/hd_core.py. Same model, same preprocessing (BGR->RGB,
1/255, letterbox-free square resize of a square crop), same intersection-over-min
NMS, same appearance filter. The only edits are that the ONNX path is now supplied
by the caller and the wall polygon is optional rather than a constant traced from
one particular composite.
"""
import cv2
import numpy as np

NET_IN = 640


def nms_iomin(boxes, scores, thr=0.45, iomin_thr=0.85):
    """Greedy NMS, suppressing on IoU *or* on intersection-over-MIN-area.

    Plain IoU leaves nested duplicates alive (tiled inference produces exactly
    those), but suppressing on intersection-over-min alone is far too greedy on a
    densely bolted wall, where a small foothold legitimately sits inside the
    bounding box of a long elongated jug. Two thresholds: a normal IoU one for
    genuine duplicates, and a high iomin one for boxes that are almost entirely
    swallowed by a stronger detection.
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
        iou = inter / (area[i] + area - inter)
        iomin = inter / np.minimum(area[i], area)
        sup |= (iou > thr) | (iomin > iomin_thr)
        sup[i] = True
    return np.array(keep, int)


def load_net(onnx_path):
    return cv2.dnn.readNetFromONNX(onnx_path)


def raw_detect(img, tile, stride, conf_thr, log=None, net=None):
    """Sliding-window inference. Returns (boxes xywh, scores) in img pixels."""
    if net is None:
        raise ValueError('pass a net from load_net(onnx_path)')
    h, w = img.shape[:2]
    pad_y, pad_x = max(0, tile - h), max(0, tile - w)
    if pad_y or pad_x:
        img = cv2.copyMakeBorder(img, 0, pad_y, 0, pad_x, cv2.BORDER_REPLICATE)
        h, w = img.shape[:2]
    xs = list(range(0, max(1, w - tile + 1), stride))
    ys = list(range(0, max(1, h - tile + 1), stride))
    if xs[-1] + tile < w:
        xs.append(w - tile)
    if ys[-1] + tile < h:
        ys.append(h - tile)
    boxes, scores = [], []
    for n, y0 in enumerate(ys):
        for x0 in xs:
            crop = img[y0:y0 + tile, x0:x0 + tile]
            blob = cv2.dnn.blobFromImage(crop, 1 / 255.0, (NET_IN, NET_IN), swapRB=True, crop=False)
            net.setInput(blob)
            out = net.forward()[0]
            if out.shape[0] < out.shape[1]:
                out = out.T
            best = out[:, 4:].max(1)
            keep = best >= conf_thr
            if not keep.any():
                continue
            sel, sc = out[keep], best[keep]
            k = tile / NET_IN
            cx, cy = sel[:, 0] * k, sel[:, 1] * k
            bw, bh = sel[:, 2] * k, sel[:, 3] * k
            boxes.append(np.stack([cx - bw / 2 + x0, cy - bh / 2 + y0, bw, bh], 1))
            scores.append(sc)
        if log:
            log("  row %d/%d  running=%d" % (n + 1, len(ys), sum(len(b) for b in boxes)))
    if not boxes:
        return np.zeros((0, 4), np.float32), np.zeros(0, np.float32)
    return (np.concatenate(boxes).astype(np.float32),
            np.concatenate(scores).astype(np.float32))


def appearance_filter(img, boxes, scale=0.25, min_side=45.0, max_aspect=3.0,
                      min_grad_p90=90.0, min_l_std=16.0):
    """Drop geometrically or photometrically implausible boxes (bare-plywood phantoms)."""
    if len(boxes) == 0:
        return np.zeros(0, bool)
    small = cv2.resize(img, None, fx=scale, fy=scale, interpolation=cv2.INTER_AREA)
    lab = cv2.cvtColor(small, cv2.COLOR_BGR2LAB)
    lum = lab[:, :, 0]
    grad = np.hypot(cv2.Sobel(lum, cv2.CV_32F, 1, 0, 3), cv2.Sobel(lum, cv2.CV_32F, 0, 1, 3))
    side = np.minimum(boxes[:, 2], boxes[:, 3])
    aspect = np.maximum(boxes[:, 2], boxes[:, 3]) / np.maximum(side, 1.0)
    keep = (side >= min_side) & (aspect <= max_aspect)
    for i in np.where(keep)[0]:
        x, y, w, h = boxes[i] * scale
        x0, y0 = max(0, int(x)), max(0, int(y))
        pl = lum[y0:int(y + h), x0:int(x + w)]
        pg = grad[y0:int(y + h), x0:int(x + w)]
        if pl.size < 9 or np.percentile(pg, 90) < min_grad_p90 or pl.std() < min_l_std:
            keep[i] = False
    return keep


def in_wall(cx, cy, w, h, poly):
    """Point-in-polygon test for box centroids, in pixels.

    `poly` is a list of normalised (x, y) vertices bounding the climbable surface, so
    detections on the floor, the crash pad and the black surround can be dropped. It
    is wall-specific and therefore an input: with no polygon everything is kept.
    """
    if not poly:
        return np.ones(len(cx), bool)
    pts = np.array([[px * w, py * h] for px, py in poly], np.float32)
    return np.array([cv2.pointPolygonTest(pts, (float(x), float(y)), False) >= 0
                     for x, y in zip(cx, cy)])


def coverage_poly(mask, epsilon_frac=0.004):
    """Fallback wall polygon: the outline of the composite's own coverage mask.

    It cannot tell wall from crash pad, but it does remove the black surround, which
    is where the detector's worst phantoms live.
    """
    m = (np.asarray(mask) > 0).astype(np.uint8)
    if m.sum() == 0:
        return []
    cnts = cv2.findContours(m, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)[0]
    c = max(cnts, key=cv2.contourArea)
    approx = cv2.approxPolyDP(c, epsilon_frac * cv2.arcLength(c, True), True)
    h, w = m.shape[:2]
    return [(float(p[0][0]) / w, float(p[0][1]) / h) for p in approx]


def centre_ring_delta(img, boxes, scale=0.35, inner=0.6, outer=2.0):
    """Lab distance between a box's core and the ring of wall around it.

    This is what separates a hold from a phantom: the detector's plywood
    hallucinations are boxes whose interior looks exactly like their surroundings,
    while a real hold - however pale - differs from the wood it is bolted to.
    """
    if len(boxes) == 0:
        return np.zeros(0, np.float32)
    small = cv2.resize(img, None, fx=scale, fy=scale, interpolation=cv2.INTER_AREA)
    lab = cv2.cvtColor(small, cv2.COLOR_BGR2LAB).astype(np.float32)
    hh, ww = lab.shape[:2]
    out = np.zeros(len(boxes), np.float32)
    for i, box in enumerate(boxes):
        x, y, w, h = box * scale
        cx, cy = x + w / 2, y + h / 2

        def win(f):
            return (int(max(0, cx - w * f / 2)), int(max(0, cy - h * f / 2)),
                    int(min(ww, cx + w * f / 2)), int(min(hh, cy + h * f / 2)))

        ix0, iy0, ix1, iy1 = win(inner)
        ox0, oy0, ox1, oy1 = win(outer)
        if ix1 <= ix0 or iy1 <= iy0 or ox1 <= ox0 or oy1 <= oy0:
            continue
        core = lab[iy0:iy1, ix0:ix1].reshape(-1, 3).mean(0)
        patch = lab[oy0:oy1, ox0:ox1]
        mask = np.ones(patch.shape[:2], bool)
        bx0, by0, bx1, by1 = win(1.0)
        mask[max(0, by0 - oy0):max(0, by1 - oy0), max(0, bx0 - ox0):max(0, bx1 - ox0)] = False
        ring = patch[mask]
        if len(ring) < 9:
            continue
        out[i] = float(np.linalg.norm(core - ring.mean(0)))
    return out


def blob_fraction(img, boxes, scale=0.35, core=0.7, outer=2.0, z=2.5):
    """Fraction of the box core that stands out from the surrounding ring in Lab z-units.

    A shadow cast beside a real hold fools the plain mean-difference test but not
    this one: it fades, so only a small part of the box core is genuinely off-wall.
    It is deliberately used only as an OR-branch with confidence, because in a
    densely bolted area the "ring" is other holds and its variance explodes.
    """
    if len(boxes) == 0:
        return np.zeros(0, np.float32)
    small = cv2.resize(img, None, fx=scale, fy=scale, interpolation=cv2.INTER_AREA)
    lab = cv2.cvtColor(small, cv2.COLOR_BGR2LAB).astype(np.float32)
    hh, ww = lab.shape[:2]
    out = np.zeros(len(boxes), np.float32)
    for i, box in enumerate(boxes):
        x, y, w, h = box * scale
        cx, cy = x + w / 2, y + h / 2

        def win(f):
            return (int(max(0, cx - w * f / 2)), int(max(0, cy - h * f / 2)),
                    int(min(ww, cx + w * f / 2)), int(min(hh, cy + h * f / 2)))

        ix0, iy0, ix1, iy1 = win(core)
        ox0, oy0, ox1, oy1 = win(outer)
        if ix1 <= ix0 or iy1 <= iy0:
            continue
        patch = lab[oy0:oy1, ox0:ox1]
        mask = np.ones(patch.shape[:2], bool)
        bx0, by0, bx1, by1 = win(1.0)
        mask[max(0, by0 - oy0):max(0, by1 - oy0), max(0, bx0 - ox0):max(0, bx1 - ox0)] = False
        ring = patch[mask]
        if len(ring) < 9:
            continue
        mu, sd = ring.mean(0), np.maximum(ring.std(0), 3.0)
        d = np.linalg.norm((lab[iy0:iy1, ix0:ix1].reshape(-1, 3) - mu) / sd, axis=1)
        out[i] = float((d > z).mean())
    return out


def centre_merge(boxes, scores, k=0.5):
    """Collapse detections that sit on the same hold.

    Greedy by confidence: the winner absorbs any box whose centre falls within
    k * the diagonal of the SMALLER of the two boxes, so a small box nested on a
    big hold is merged while two same-sized neighbours are not. k is the knob
    that decides "same hold" vs "adjacent hold" - past ~0.8 it starts eating
    genuinely distinct holds on a densely bolted wall.
    """
    if len(boxes) == 0:
        return np.zeros(0, int)
    cx = boxes[:, 0] + boxes[:, 2] / 2
    cy = boxes[:, 1] + boxes[:, 3] / 2
    diag = np.hypot(boxes[:, 2], boxes[:, 3])
    dead = np.zeros(len(boxes), bool)
    keep = []
    for i in np.argsort(-scores):
        if dead[i]:
            continue
        keep.append(i)
        dead |= np.hypot(cx - cx[i], cy - cy[i]) <= k * np.minimum(diag[i], diag)
        dead[i] = True
    return np.array(keep, int)
