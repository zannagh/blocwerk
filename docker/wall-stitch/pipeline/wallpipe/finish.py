"""Turn a composite plus its coverage mask into a tidy picture.

Vendored from new-run/exp7/finish.py, unchanged apart from this note.

The staircase of black steps around a mosaic is what makes the individual frames
readable as frames, so the silhouette is straightened rather than left raw:
morphological opening shaves the protruding steps, a coarse polygon fit replaces what
is left with a few long straight edges, the handful of pixels the polygon claims but no
frame covered are inpainted, and the result is cropped tight.
"""
import cv2
import numpy as np


def fill_holes(m):
    f = (m > 0).astype(np.uint8) * 255
    pad = cv2.copyMakeBorder(f, 1, 1, 1, 1, cv2.BORDER_CONSTANT, value=0)
    ff = pad.copy()
    cv2.floodFill(ff, np.zeros((pad.shape[0] + 2, pad.shape[1] + 2), np.uint8),
                  (0, 0), 255)
    return f | cv2.bitwise_not(ff[1:-1, 1:-1])


def silhouette(mask, open_frac=0.010, eps_frac=0.0022, close_frac=0.020):
    filled = fill_holes(mask)
    h, w = filled.shape
    k = max(5, int(open_frac * max(h, w)) | 1)
    op = cv2.morphologyEx(filled, cv2.MORPH_OPEN,
                          cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (k, k)))
    # Fill the narrow notches the coverage leaves in an otherwise straight edge; the
    # kernel is far smaller than the genuine concavities (return, corner).
    kc = max(5, int(close_frac * max(h, w)) | 1)
    op = cv2.morphologyEx(op, cv2.MORPH_CLOSE,
                          cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (kc, kc)))
    n, lab, st, _ = cv2.connectedComponentsWithStats((op > 0).astype(np.uint8))
    if n > 1:
        op = (lab == 1 + np.argmax(st[1:, 4])).astype(np.uint8) * 255
    contours = cv2.findContours(op, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)[0]
    if not contours:
        return filled, None
    c = max(contours, key=cv2.contourArea)
    poly = cv2.approxPolyDP(c, eps_frac * cv2.arcLength(c, True), True)
    out = np.zeros_like(filled)
    cv2.fillPoly(out, [poly], 255)
    return out, poly


def apply(img, mask, pad=0, open_frac=0.014, eps_frac=0.0022):
    """Returns (image, mask, box, polygon_vertices, inpainted_pixels)."""
    sil, poly = silhouette(mask, open_frac, eps_frac)
    if poly is None:
        return img, mask, (0, 0, img.shape[1], img.shape[0]), 0, 0
    gap = cv2.subtract(sil, (mask > 0).astype(np.uint8) * 255)
    if gap.any():
        img = cv2.inpaint(img, cv2.dilate(gap, np.ones((3, 3), np.uint8)), 5,
                          cv2.INPAINT_TELEA)
    img = img.copy()
    img[sil == 0] = 0
    x, y, w, h = cv2.boundingRect(poly)
    x0, y0 = max(0, x - pad), max(0, y - pad)
    x1, y1 = min(img.shape[1], x + w + pad), min(img.shape[0], y + h + pad)
    return (img[y0:y1, x0:x1], sil[y0:y1, x0:x1], (x0, y0, x1, y1), len(poly),
            int(gap.sum() // 255))
