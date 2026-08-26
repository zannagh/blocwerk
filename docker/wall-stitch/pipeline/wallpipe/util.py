"""Small shared helpers: timing log, previews, image listing."""
import os
import time

import cv2
import numpy as np

JPEG_Q = 94
PREVIEW_W = 1600
IMAGE_EXT = ('.jpg', '.jpeg', '.png', '.tif', '.tiff', '.bmp', '.webp')


class Log:
    """Elapsed-time logger. One instance per run; passed explicitly, never global."""

    def __init__(self, quiet=False):
        self.t0 = time.time()
        self.quiet = quiet

    def __call__(self, *a):
        if not self.quiet:
            print('[%7.1fs]' % (time.time() - self.t0), *a, flush=True)

    def elapsed(self):
        return time.time() - self.t0


def list_images(input_dir):
    """Ordered image files in a directory. Order is the frame order of the sweep."""
    names = [n for n in sorted(os.listdir(input_dir))
             if n.lower().endswith(IMAGE_EXT) and not n.startswith('.')]
    return [os.path.join(input_dir, n) for n in names]


def write_jpeg(path, img, quality=JPEG_Q):
    cv2.imwrite(path, img, [cv2.IMWRITE_JPEG_QUALITY, quality])
    return path


def write_preview(path, img, width=PREVIEW_W):
    h, w = img.shape[:2]
    small = cv2.resize(img, (width, max(1, int(h * width / w))), interpolation=cv2.INTER_AREA)
    cv2.imwrite(path, small, [cv2.IMWRITE_JPEG_QUALITY, 90])
    return path


def read_image(path, flags=cv2.IMREAD_COLOR):
    img = cv2.imread(path, flags)
    if img is None:
        raise SystemExit('could not read image: %s' % path)
    return img


def image_size(path):
    """(width, height) as cv2 will actually read the file.

    A 1/8 decode multiplied back rounds up per axis (4284 -> 4288) and PIL's header
    read ignores EXIF rotation, so both cheap routes introduce a silent geometry bias.
    See compose.source_size for the full reasoning.
    """
    img = read_image(path)
    return img.shape[1], img.shape[0]


def percentile_roi(quads, lo=2.0, hi=98.0, pad=0.02):
    """Robust bounding box of a set of warped frame corners.

    A hand-picked crop is what the R&D scripts used; this replaces it. Frames that
    catch the floor plane at a grazing angle warp into enormous quads, so a plain
    union blows the canvas up by an order of magnitude. Percentiles over all corner
    points drop those tails while keeping every frame that actually sees the wall.
    """
    p = np.concatenate([np.asarray(q, np.float64).reshape(-1, 2) for q in quads])
    x0, x1 = np.percentile(p[:, 0], [lo, hi])
    y0, y1 = np.percentile(p[:, 1], [lo, hi])
    dx, dy = (x1 - x0) * pad, (y1 - y0) * pad
    return float(x0 - dx), float(y0 - dy), float(x1 + dx), float(y1 + dy)
