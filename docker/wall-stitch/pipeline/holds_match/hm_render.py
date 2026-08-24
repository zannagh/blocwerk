"""Review overlays. These are the deliverable the result is actually judged on."""
import os

import cv2
import numpy as np

COLORS = {           # BGR
    "matched": (80, 220, 80),
    "moved": (0, 200, 255),
    "uncertain": (255, 120, 0),
    "missing": (60, 60, 255),
    "new": (255, 0, 255),
}
WIDE = 2500


def old_coverage_mask(old, new, seed, step=64):
    """Boolean mask over the NEW image marking where the OLD photo actually saw wall."""
    from hm_global import main_span_mask
    nh, nw = new.shape[:2]
    oh, ow = old.shape[:2]
    span = main_span_mask(old.shape)
    ys, xs = np.mgrid[0:oh:step, 0:ow:step]
    pts = np.stack([xs.ravel(), ys.ravel()], 1).astype(np.float64)
    ok = span[ys.ravel(), xs.ravel()] > 0
    mapped = seed(pts[ok])
    mask = np.zeros((nh, nw), np.uint8)
    for x, y in mapped.astype(int):
        if 0 <= x < nw and 0 <= y < nh:
            cv2.circle(mask, (x, y), step * 3, 255, -1)
    return mask > 0


def _shape_poly(rec, key, w, h):
    node = rec[key]
    if not node or not node.get("ShapePoints"):
        return None
    return np.array([[(node["X"] + p["Dx"]) * w, (node["Y"] + p["Dy"]) * h]
                     for p in node["ShapePoints"]], np.int32)


def _draw(img, rec, key, w, h, scale, thickness=2):
    node = rec[key]
    if not node:
        return
    if key == "new" and not rec.get("new_in_frame", True):
        return
    col = COLORS[rec["classification"]]
    poly = _shape_poly(rec, key, w, h)
    if poly is not None:
        cv2.polylines(img, [(poly * scale).astype(np.int32)], True, col, thickness, cv2.LINE_AA)
    r = float(node.get("Radius") or 0.0) * max(w, h) * scale
    c = (int(node["X"] * w * scale), int(node["Y"] * h * scale))
    cv2.circle(img, c, max(2, int(r)), col, thickness, cv2.LINE_AA)


def _legend(img, counts):
    y = 30
    for k in ("matched", "moved", "uncertain", "missing", "new"):
        if k not in counts:
            continue
        cv2.rectangle(img, (14, y - 14), (40, y + 6), COLORS[k], -1)
        cv2.putText(img, f"{k}: {counts[k]}", (50, y + 2), 0, 0.7, (0, 0, 0), 4, cv2.LINE_AA)
        cv2.putText(img, f"{k}: {counts[k]}", (50, y + 2), 0, 0.7, (255, 255, 255), 2, cv2.LINE_AA)
        y += 32


def render_all(work, old, new, live, records, boxes, new_blobs, seed, log=print):
    nh, nw = new.shape[:2]
    oh, ow = old.shape[:2]
    counts = {}
    for r in records:
        counts[r["classification"]] = counts.get(r["classification"], 0) + 1

    # --- overlay on the new orthophoto -------------------------------------
    s = WIDE / nw
    vis = cv2.resize(new, None, fx=s, fy=s, interpolation=cv2.INTER_AREA)
    for j in new_blobs:
        x, y, bw, bh = boxes[j] * s
        cv2.rectangle(vis, (int(x), int(y)), (int(x + bw), int(y + bh)), COLORS["new"], 1)
    for rec in records:
        _draw(vis, rec, "new", nw, nh, s, 2)
    _legend(vis, dict(counts, new=len(new_blobs)))
    cv2.imwrite(os.path.join(work, "overlay-new.jpg"), vis, [cv2.IMWRITE_JPEG_QUALITY, 88])

    # --- same holds on the old photo ---------------------------------------
    so = WIDE / ow
    old_vis = cv2.resize(old, None, fx=so, fy=so, interpolation=cv2.INTER_AREA)
    for rec in records:
        _draw(old_vis, rec, "old", ow, oh, so, 2)
    _legend(old_vis, counts)
    cv2.imwrite(os.path.join(work, "overlay-old.jpg"), old_vis, [cv2.IMWRITE_JPEG_QUALITY, 88])

    # --- uncertain / missing only ------------------------------------------
    vis2 = cv2.resize(new, None, fx=s, fy=s, interpolation=cv2.INTER_AREA)
    vis2 = (vis2 * 0.45).astype(np.uint8)
    sub = {}
    for rec in records:
        if rec["classification"] in ("uncertain", "missing"):
            _draw(vis2, rec, "new", nw, nh, s, 3)
            sub[rec["classification"]] = sub.get(rec["classification"], 0) + 1
    off = 0
    for rec in records:
        if rec["new"] and not rec.get("new_in_frame", True):
            # outside the orthophoto: pin it to the nearest border so it is visible
            x = int(np.clip(rec["new"]["X"] * nw * s, 8, vis2.shape[1] - 9))
            y = int(np.clip(rec["new"]["Y"] * nh * s, 8, vis2.shape[0] - 9))
            cv2.drawMarker(vis2, (x, y), COLORS["missing"], cv2.MARKER_TILTED_CROSS, 16, 2)
            off += 1
    if off:
        cv2.putText(vis2, f"x = {off} outside the orthophoto (pinned to the border)",
                    (14, vis2.shape[0] - 18), 0, 0.8, (0, 0, 0), 5, cv2.LINE_AA)
        cv2.putText(vis2, f"x = {off} outside the orthophoto (pinned to the border)",
                    (14, vis2.shape[0] - 18), 0, 0.8, COLORS["missing"], 2, cv2.LINE_AA)
    _legend(vis2, sub)

    # --- 100% detail crops --------------------------------------------------
    crops = [("top", 0.30, 0.10), ("middle", 0.50, 0.45), ("bottom-edge", 0.42, 0.86),
             ("left", 0.10, 0.55), ("right", 0.86, 0.35)]
    cw, ch = 1400, 1000
    for name, fx, fy in crops:
        x0 = int(np.clip(fx * nw - cw / 2, 0, nw - cw))
        y0 = int(np.clip(fy * nh - ch / 2, 0, nh - ch))
        crop = new[y0:y0 + ch, x0:x0 + cw].copy()
        for rec in records:
            if not rec["new"]:
                continue
            cx, cy = rec["new"]["X"] * nw - x0, rec["new"]["Y"] * nh - y0
            if not (-80 < cx < cw + 80 and -80 < cy < ch + 80):
                continue
            col = COLORS[rec["classification"]]
            poly = _shape_poly(rec, "new", nw, nh)
            if poly is not None:
                cv2.polylines(crop, [poly - [x0, y0]], True, col, 2, cv2.LINE_AA)
            r = float(rec["new"]["Radius"] or 0) * max(nw, nh)
            cv2.circle(crop, (int(cx), int(cy)), max(2, int(r)), col, 2, cv2.LINE_AA)
            cv2.drawMarker(crop, (int(cx), int(cy)), col, cv2.MARKER_CROSS, 9, 1)
        cv2.imwrite(os.path.join(work, f"overlay-detail-{name}.jpg"), crop,
                    [cv2.IMWRITE_JPEG_QUALITY, 92])
    if log:
        log(f"  wrote overlays to {work}")
    return vis2   # the main-span review panel; stack_review composites all three


def _draw_px(img, rec, w, h, ox=0, oy=0, scale=1.0, thickness=2, dim=False):
    """Draw one record's new-space outline into an image cropped at (ox, oy)."""
    node = rec.get("new")
    if not node or not rec.get("new_in_frame", True):
        return
    col = COLORS[rec["classification"]]
    cx = (node["X"] * w - ox) * scale
    cy = (node["Y"] * h - oy) * scale
    poly = _shape_poly(rec, "new", w, h)
    if poly is not None:
        p = ((poly - [ox, oy]) * scale).astype(np.int32)
        cv2.polylines(img, [p], True, col, thickness, cv2.LINE_AA)
    r = float(node.get("Radius") or 0.0) * max(w, h) * scale
    cv2.circle(img, (int(cx), int(cy)), max(2, int(r)), col, thickness, cv2.LINE_AA)
    cv2.drawMarker(img, (int(cx), int(cy)), col, cv2.MARKER_CROSS, 9, 1)


def ribbon(img, rows=3, gap=10, scale=1.0):
    """Slice a very wide, very short image into stacked rows so it is readable."""
    h, w = img.shape[:2]
    step = int(np.ceil(w / rows))
    out = []
    for i in range(rows):
        seg = img[:, i * step:(i + 1) * step]
        if seg.shape[1] < step:
            seg = cv2.copyMakeBorder(seg, 0, 0, 0, step - seg.shape[1],
                                     cv2.BORDER_CONSTANT, value=(30, 30, 30))
        out.append(seg)
        out.append(np.full((gap, step, 3), 255, np.uint8))
    stack = np.vstack(out[:-1])
    if scale != 1.0:
        stack = cv2.resize(stack, None, fx=scale, fy=scale, interpolation=cv2.INTER_CUBIC)
    return stack


def render_plane(work, plane, img, records, boxes, crops, wide=None, rows=1,
                 log=print):
    """Full-plane overlay + 100 % detail crops for one non-main-span plane."""
    h, w = img.shape[:2]
    counts = {}
    for r in records:
        counts[r["classification"]] = counts.get(r["classification"], 0) + 1

    full = img.copy()
    for b in boxes:
        x, y, bw, bh = b
        cv2.rectangle(full, (int(x), int(y)), (int(x + bw), int(y + bh)), (200, 200, 200), 1)
    for rec in records:
        _draw_px(full, rec, w, h, thickness=2)
    if rows > 1:
        vis = ribbon(full, rows=rows, scale=(wide / (w / rows)) if wide else 1.0)
    else:
        s = (wide / w) if wide else 1.0
        vis = cv2.resize(full, None, fx=s, fy=s, interpolation=cv2.INTER_AREA) if s != 1 else full
        for rec in records:  # redraw at output scale so thin outlines survive
            pass
    _legend(vis, counts)
    cv2.imwrite(os.path.join(work, f"overlay-{plane.key}.jpg"), vis,
                [cv2.IMWRITE_JPEG_QUALITY, 92])

    for name, (x0, y0, cw, ch) in crops.items():
        x0 = int(np.clip(x0, 0, max(0, w - cw)))
        y0 = int(np.clip(y0, 0, max(0, h - ch)))
        crop = img[y0:y0 + ch, x0:x0 + cw].copy()
        for rec in records:
            _draw_px(crop, rec, w, h, ox=x0, oy=y0, thickness=2)
        cv2.imwrite(os.path.join(work, f"overlay-detail-{plane.key}-{name}.jpg"),
                    crop, [cv2.IMWRITE_JPEG_QUALITY, 95])
    if log:
        log(f"  wrote {plane.key} overlays")


def review_panel(img, records, wide, rows=1, title="", rotate=False):
    """Dimmed plane image with only the uncertain/missing holds drawn."""
    h, w = img.shape[:2]
    vis = (img * 0.45).astype(np.uint8)
    sub = {}
    for rec in records:
        if rec["classification"] in ("uncertain", "missing"):
            _draw_px(vis, rec, w, h, thickness=3)
            sub[rec["classification"]] = sub.get(rec["classification"], 0) + 1
    off = sum(1 for r in records if r.get("new") and not r.get("new_in_frame", True))
    if rotate:      # a very tall plane reads better laid on its side here
        vis = cv2.rotate(vis, cv2.ROTATE_90_COUNTERCLOCKWISE)
        w = vis.shape[1]
    if rows > 1:
        vis = ribbon(vis, rows=rows, scale=wide / (w / rows))
    else:
        s = wide / w
        vis = cv2.resize(vis, None, fx=s, fy=s, interpolation=cv2.INTER_AREA)
    _legend(vis, sub)
    if title:
        cv2.putText(vis, title, (14, vis.shape[0] - 16), 0, 0.8, (0, 0, 0), 5, cv2.LINE_AA)
        cv2.putText(vis, title, (14, vis.shape[0] - 16), 0, 0.8, (255, 255, 255), 2, cv2.LINE_AA)
    if off:
        cv2.putText(vis, f"{off} outside this plane's image", (14, vis.shape[0] - 46),
                    0, 0.8, (0, 0, 0), 5, cv2.LINE_AA)
        cv2.putText(vis, f"{off} outside this plane's image", (14, vis.shape[0] - 46),
                    0, 0.8, COLORS["missing"], 2, cv2.LINE_AA)
    return vis


def stack_review(work, panels, log=print):
    """One review-uncertain.jpg spanning all three planes, padded to equal width."""
    wmax = max(p.shape[1] for p in panels)
    rows = []
    for p in panels:
        if p.shape[1] < wmax:
            p = cv2.copyMakeBorder(p, 0, 0, 0, wmax - p.shape[1],
                                   cv2.BORDER_CONSTANT, value=(20, 20, 20))
        rows.append(p)
        rows.append(np.full((12, wmax, 3), 255, np.uint8))
    cv2.imwrite(os.path.join(work, "review-uncertain.jpg"), np.vstack(rows[:-1]),
                [cv2.IMWRITE_JPEG_QUALITY, 88])
    if log:
        log("  wrote review-uncertain.jpg (all three planes)")


def render_extra_planes(work, images, records, boxes, main_panel, log=print):
    """Overlays + 100 % detail crops for the two extra planes, and the composite
    review sheet that covers all three."""
    import hm_planes

    kick, left = hm_planes.KICK, hm_planes.LEFT
    render_plane(work, hm_planes.BY_KEY[kick], images[kick], records[kick],
                 boxes[kick], crops={"left": (150, 0, 1000, 113),
                                     "middle": (1450, 0, 1000, 113),
                                     "right": (2150, 0, 1000, 113)},
                 wide=2500, rows=3, log=log)
    render_plane(work, hm_planes.BY_KEY[left], images[left], records[left],
                 boxes[left], crops={"upper": (60, 300, 1300, 1000),
                                     "lower": (0, 1500, 1300, 1000)},
                 wide=900, log=log)
    stack_review(work, [
        main_panel,
        review_panel(images[kick], records[kick], 2500, rows=3,
                     title="kickboard plane (3190x113)"),
        review_panel(images[left], records[left], 2500, rotate=True,
                     title="left return panel (1433x3176), rotated 90 deg CCW"),
    ], log=log)
