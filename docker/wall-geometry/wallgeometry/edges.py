"""Round the staircase of a texture's coverage edge.

Which pixels any photo painted is decided per label cell (16 mm), so where the covered area ends (behind
another facet, outside every photo) the edge is a staircase of cell-sized steps. `smooth_coverage` fills
the notches of that staircase: pixels that a Gaussian of the coverage (sigma = `edgeSmoothPx`) says are
mostly covered get the colour of their covered neighbours (normalised convolution) and count as covered.
Only grows the coverage, by under one sigma, so thin covered strips never vanish.
"""
import cv2
import numpy as np

GROW_AT = 0.4  # blurred coverage at which a notch pixel counts as covered
TILE_ROWS = 256


def smooth_coverage(img, filled, sigma):
    """(image uint8 (h, w, 3), filled bool (h, w)) -> the same with the coverage notches filled in place."""
    if sigma <= 0.5 or not filled.any() or filled.all():
        return img, filled
    reach = int(3 * sigma) | 1
    soft = cv2.GaussianBlur(filled.astype(np.uint8) * 255, (0, 0), sigma, borderType=cv2.BORDER_REPLICATE)
    grow = (soft >= GROW_AT * 255) & ~filled
    if not grow.any():
        return img, filled
    rows = np.flatnonzero(grow.any(1))
    h = img.shape[0]
    for r0 in range(rows[0], rows[-1] + 1, TILE_ROWS):
        r1 = min(r0 + TILE_ROWS, rows[-1] + 1)
        a0, a1 = max(r0 - reach, 0), min(r1 + reach, h)
        f = filled[a0:a1].astype(np.float32)
        num = cv2.GaussianBlur(img[a0:a1].astype(np.float32) * f[..., None], (0, 0), sigma)
        den = np.maximum(cv2.GaussianBlur(f, (0, 0), sigma), 1e-4)
        g = grow[r0:r1]
        fill = np.clip(num[r0 - a0:r1 - a0] / den[r0 - a0:r1 - a0, :, None] + 0.5, 0, 255).astype(np.uint8)
        img[r0:r1][g] = fill[g]
    return img, filled | grow
