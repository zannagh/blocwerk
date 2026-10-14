"""The app's hold detector, replayed on a runner: YOLO on ONNX Runtime over overlapping windows plus one whole-image
pass, merged exactly like the Blocwerk server merges them (src/Blocwerk.HoldDetection/Tiling: TileGrid, TileBoxMerger,
YoloTiledDetector; YoloDotNet 3.1's letterbox, decode and NMS). The server turns the merged boxes into holds itself
(normalising, the mat filter, volumes left out), so this module returns boxes in the photo's pixels:
(left, top, right, bottom, confidence, label), integer edges, right and bottom exclusive.

Checked against the app on a real capture photo (3955 x 2444, the Attic model): the same 636 boxes, edge for edge.
One difference is deliberate: YoloDotNet pads a letterboxed image with whatever its reused buffer held before; here
the padding is black. Only the whole-image pass is letterboxed for a non-square photo, and it only contributes objects
larger than half the overlap (volumes, big holds)."""
import numpy as np

EDGE_MARGIN_PX = 4
NMS_IOU = 0.45
CONTAINED_IOU = 0.25
TANGLE_IOU = 0.2
TANGLE_NEIGHBOURS = 2
NESTED_SIDE_RATIO = 0.25
NESTED_CENTRE_FRACTION = 0.5
F32 = np.float32
SKIA_NEAREST_BIAS = -1e-4  # measured: with it a 3955 x 2444 capture photo gives the app's 636 boxes, edge for edge


def tile_starts(size, tile, overlap):
    """Window start offsets along one axis: steps of tile - overlap, the last one flush with the far edge."""
    starts = [0]
    if size <= tile:
        return starts
    step, last = tile - overlap, size - tile
    s = step
    while s < last:
        starts.append(s)
        s += step
    starts.append(last)
    return starts


def tile_grid(width, height, tile, overlap):
    """The windows (x, y, w, h) covering the image, row-major."""
    if tile < 1 or overlap < 0 or overlap >= tile:
        raise ValueError("bad tiling")
    xs, ys = tile_starts(width, tile, overlap), tile_starts(height, tile, overlap)
    return [(x, y, min(tile, width - x), min(tile, height - y)) for y in ys for x in xs]


def _nearest(n, src):
    """Skia's nearest sampling of `n` output pixels from `src`: the pixel under each output centre, a centre that falls
    exactly on a pixel border taking the pixel before it (the bias; without it every 2:1 window is off by one pixel)."""
    return np.clip(np.floor((np.arange(n) + 0.5) * (src / n) + SKIA_NEAREST_BIAS).astype(np.int64), 0, src - 1)


def letterbox(rgb, size):
    """YoloDotNet's proportional resize with nearest sampling into a size x size canvas (black padding); returns the
    CHW float tensor (1, 3, size, size) scaled to 0..1."""
    h, w = rgb.shape[:2]
    scale = min(F32(size) / F32(w), F32(size) / F32(h))
    nw, nh = int(F32(w) * scale + F32(0.5)), int(F32(h) * scale + F32(0.5))
    ox, oy = (size - nw) // 2, (size - nh) // 2
    xs, ys = _nearest(nw, w), _nearest(nh, h)
    canvas = np.zeros((size, size, 3), dtype=np.uint8)
    canvas[oy:oy + nh, ox:ox + nw] = rgb[ys][:, xs]
    return (canvas.astype(np.float32) / F32(255)).transpose(2, 0, 1)[None]


def decode(output, width, height, size, labels, confidence):
    """YoloDotNet's v8/v11 decode of one (4 + classes, anchors) output for an image of width x height: the best class
    per anchor at or above the confidence, boxes mapped back with the proportional gain, truncated to integers."""
    out = np.asarray(output, dtype=np.float32).reshape(4 + len(labels), -1)
    scores = out[4:]
    best = scores.argmax(axis=0)
    conf = scores[best, np.arange(scores.shape[1])]
    keep = np.nonzero((conf > 0) & ~(conf.astype(np.float64) < confidence))[0]
    gain = max(F32(width) / F32(size), F32(height) / F32(size))
    ratio = min(F32(size) / F32(width), F32(size) / F32(height))
    pad_x = (F32(size) - F32(width) * ratio) / F32(2)
    pad_y = (F32(size) - F32(height) * ratio) / F32(2)
    boxes = []
    for i in keep:
        cx, cy, bw, bh = (F32(v) for v in out[:4, i])
        left = int((cx - bw / F32(2) - pad_x) * gain)
        top = int((cy - bh / F32(2) - pad_y) * gain)
        right = int((cx + bw / F32(2) - pad_x) * gain)
        bottom = int((cy + bh / F32(2) - pad_y) * gain)
        boxes.append((left, top, right, bottom, float(conf[i]), labels[int(best[i])]))
    return boxes


def _rect_iou(a, b):
    """SkiaSharp's SKRectI IoU as YoloDotNet computes it (no overlap unless the rectangles strictly intersect)."""
    if not (a[0] < b[2] and b[0] < a[2] and a[1] < b[3] and b[1] < a[3]):
        return 0.0
    inter = (min(a[2], b[2]) - max(a[0], b[0])) * (min(a[3], b[3]) - max(a[1], b[1]))
    if inter == 0:
        return 0.0
    return inter / ((a[2] - a[0]) * (a[3] - a[1]) + (b[2] - b[0]) * (b[3] - b[1]) - inter)


def nms(boxes, iou):
    """YoloDotNet's class-agnostic NMS: by confidence, a box overlapping a kept one above `iou` goes."""
    kept = []
    for box in sorted(boxes, key=lambda b: -b[4]):
        if all(_rect_iou(box, k) <= iou for k in kept):
            kept.append(box)
    return kept


def iou(a, b):
    """TileBoxMerger's PixelBox.IoU."""
    ix = max(0, min(a[2], b[2]) - max(a[0], b[0]))
    iy = max(0, min(a[3], b[3]) - max(a[1], b[1]))
    inter = ix * iy
    union = (a[2] - a[0]) * (a[3] - a[1]) + (b[2] - b[0]) * (b[3] - b[1]) - inter
    return inter / union if union > 0 else 0.0


def _centre(b):
    return b[0] + (b[2] - b[0]) / 2.0, b[1] + (b[3] - b[1]) / 2.0


def square(b):
    """The square of side max(w, h) around the same centre (C#'s Math.Round rounds half to even, like round())."""
    side = max(b[2] - b[0], b[3] - b[1])
    cx, cy = _centre(b)
    left, top = int(round(cx - side / 2.0)), int(round(cy - side / 2.0))
    return (left, top, left + side, top + side, b[4], b[5])


def touches_inner_edge(b, window, width, height):
    x, y, w, h = window
    return ((x > 0 and b[0] <= EDGE_MARGIN_PX) or (y > 0 and b[1] <= EDGE_MARGIN_PX)
            or (x + w < width and b[2] >= w - EDGE_MARGIN_PX) or (y + h < height and b[3] >= h - EDGE_MARGIN_PX))


def to_image(local, window, width, height):
    x, y = window[0], window[1]
    return [(b[0] + x, b[1] + y, b[2] + x, b[3] + y, b[4], b[5]) for b in local
            if not touches_inner_edge(b, window, width, height)]


def _duplicate(c, k):
    v = iou(c, k)
    cx, cy = _centre(c)
    return v > NMS_IOU or (v > CONTAINED_IOU and k[0] <= cx <= k[2] and k[1] <= cy <= k[3])


def merge(boxes):
    """Greedy class-aware NMS by confidence (a stable sort, like LINQ's OrderByDescending) plus the containment dedupe."""
    kept = []
    for box in sorted(boxes, key=lambda b: -b[4]):
        if not any(k[5] == box[5] and _duplicate(box, k) for k in kept):
            kept.append(box)
    return kept


def drop_tangles(boxes):
    squares = [square(b) for b in boxes]
    kept = []
    for i, b in enumerate(boxes):
        neighbours = 0
        for j in range(len(boxes)):
            if neighbours >= TANGLE_NEIGHBOURS:
                break
            if j != i and iou(squares[i], squares[j]) > TANGLE_IOU:
                neighbours += 1
        if neighbours < TANGLE_NEIGHBOURS:
            kept.append(b)
    return kept


def _nested_in(part, hold):
    side = max(hold[2] - hold[0], hold[3] - hold[1])
    if max(part[2] - part[0], part[3] - part[1]) >= NESTED_SIDE_RATIO * side:
        return False
    reach = NESTED_CENTRE_FRACTION * side / 2.0
    (px, py), (hx, hy) = _centre(part), _centre(hold)
    return abs(px - hx) <= reach and abs(py - hy) <= reach


def drop_nested(boxes):
    return [b for b in boxes if not any(h[5] == "hold" and _nested_in(b, h) for h in boxes)]


class Detector:
    """One ONNX session and the app's settings (holds-job.json): `detect(rgb)` gives a photo's merged boxes."""

    def __init__(self, session, labels, size, job):
        self.session, self.labels, self.size = session, labels, size
        self.input = session.get_inputs()[0].name
        tiling = job.get("tiling") or {}
        self.tiled = bool(tiling.get("enabled", True))
        self.tile, self.overlap = int(tiling.get("tileSize", 1280)), int(tiling.get("overlap", 256))
        self.confidence = float(tiling.get("confidence", 0.35))
        self.whole_confidence = float(job.get("wholeImageConfidence", 0.25))
        self.window_iou = float(job.get("windowIoU", NMS_IOU))

    def run(self, rgb, confidence):
        h, w = rgb.shape[:2]
        output = self.session.run(None, {self.input: letterbox(rgb, self.size)})[0]
        return nms(decode(output, w, h, self.size, self.labels, confidence), self.window_iou)

    def detect(self, rgb):
        h, w = rgb.shape[:2]
        if not self.tiled:
            return self.run(rgb, self.whole_confidence)
        windows = tile_grid(w, h, self.tile, self.overlap)
        boxes = []
        for win in windows:
            x, y, ww, wh = win
            boxes.extend(to_image(self.run(rgb[y:y + wh, x:x + ww], self.confidence), win, w, h))
        if len(windows) > 1:
            min_side = self.overlap // 2
            boxes.extend(b for b in self.run(rgb, self.confidence) if max(b[2] - b[0], b[3] - b[1]) >= min_side)
        return drop_nested(drop_tangles(merge(boxes)))
