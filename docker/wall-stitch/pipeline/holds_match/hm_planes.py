"""The three target planes, and which stored hold belongs to which.

The wall is not one surface. `wall.json` already records that: the main span
(Segment 1, 45 deg overhang), the kickboard strip beneath it (Segment 3, near
plumb - measured 46.37 deg off the main span), and the left return panel
(Segment 2, ~87 deg dihedral). The stitcher rectifies each of those as its own
plane, so each is its own image with its own coordinate space.

Plane assignment is therefore *read off the stored data*, not guessed: a hold is
assigned to the plane whose segment polygon contains its stored (X, Y) in the
old photo. That partitions the 463 live holds 411 / 30 / 22 with no hold in two
polygons and none outside all of them. Segment 4 is the crash mat (Kind 1) and
holds none.

Each plane then gets its own old->new seed map, built by exactly the stage-1 +
stage-2 procedure `hm_global` uses for the main span, with the old photo masked
to that plane's segment.
"""
import json
import os

import cv2
import numpy as np

from hm_common import PolyMap, WALL_JSON, WALL_ROOT, seed_everything
from hm_global import SeedMap, _ratio_match, _rootsift

MAIN = "main-span"
KICK = "kickboard"
LEFT = "left-return"


class Plane:
    """One rectified target surface: an image, the old-photo region that shows it,
    and the stage parameters that region's geometry needs."""

    def __init__(self, key, image, segments, coarse_scale=1.0, mask_grow=9,
                 guided_gate=120.0, guided_scale=1.0, degree=3, search_px=60,
                 det_tile=1280, det_stride=960, det_min_side=45.0,
                 det_filter_scale=0.25, snap_abs=70.0, snap_min_tol=25.0,
                 field="tps", note=""):
        self.key = key
        self.image = image
        self.segments = tuple(segments)
        self.coarse_scale = coarse_scale
        self.mask_grow = mask_grow
        self.guided_gate = guided_gate
        self.guided_scale = guided_scale
        self.degree = degree
        self.search_px = search_px
        self.det_tile = det_tile
        self.det_stride = det_stride
        self.det_min_side = det_min_side
        self.det_filter_scale = det_filter_scale
        self.snap_abs = snap_abs
        self.snap_min_tol = snap_min_tol
        self.field = field
        self.note = note

    def read(self):
        img = cv2.imread(self.image, cv2.IMREAD_COLOR)
        if img is None:
            raise SystemExit(f"could not read {self.image}")
        return img


PLANES = [
    Plane(MAIN,
          os.path.join(WALL_ROOT, "06-final", "wall-orthophoto.png"),
          ("Segment 1",), coarse_scale=1 / 3.0, guided_scale=0.5,
          note="45 deg overhanging main span, 7648x4864"),
    Plane(KICK,
          os.path.join(WALL_ROOT, "06-final", "wall-orthophoto-kickboard.png"),
          ("Segment 3",), guided_gate=40.0, degree=3, search_px=20,
          det_tile=320, det_stride=240, det_min_side=18.0,
          det_filter_scale=1.0, snap_abs=20.0, snap_min_tol=12.0,
          field="affine",
          note="near-plumb kickboard strip, 3190x113, ~0.42x the main span's px/m"),
    Plane(LEFT,
          os.path.join(WALL_ROOT, "06-final", "wall-orthophoto-left-return.png"),
          ("Segment 2",), guided_gate=120.0, degree=3, search_px=35,
          det_tile=1280, det_stride=960, det_min_side=40.0,
          det_filter_scale=0.25, snap_abs=60.0, snap_min_tol=22.0,
          field="affine",
          note="left return panel, 1433x3176, single-view rectification"),
]
BY_KEY = {p.key: p for p in PLANES}


def segment_masks(shape):
    """One filled mask per named wall segment, in old-photo pixels."""
    h, w = shape[:2]
    with open(WALL_JSON) as fh:
        wall = json.load(fh)
    out = {}
    for seg in wall["segments"]:
        m = np.zeros((h, w), np.uint8)
        pts = np.array([[p["Dx"] * w, p["Dy"] * h] for p in seg["Points"]], np.int32)
        cv2.fillPoly(m, [pts], 255)
        out[seg["Name"]] = m
    return out


def plane_mask(shape, plane, grow=None):
    masks = segment_masks(shape)
    h, w = shape[:2]
    m = np.zeros((h, w), np.uint8)
    for name in plane.segments:
        m = np.maximum(m, masks[name])
    g = plane.mask_grow if grow is None else grow
    if g:
        m = cv2.dilate(m, np.ones((g, g), np.uint8))
    return m


def assign(holds, shape):
    """Plane key per hold, from the segment polygon its stored (X, Y) falls in."""
    h, w = shape[:2]
    masks = segment_masks(shape)
    seg2plane = {}
    for p in PLANES:
        for s in p.segments:
            seg2plane[s] = p.key
    out = []
    for hold in holds:
        x = int(min(w - 1, max(0, hold["X"] * w)))
        y = int(min(h - 1, max(0, hold["Y"] * h)))
        hit = [n for n, m in masks.items() if m[y, x] > 0]
        keys = [seg2plane[n] for n in hit if n in seg2plane]
        out.append(keys[0] if len(keys) == 1 else (keys[0] if keys else MAIN))
    return out


def build_plane_map(old, plane, log=print):
    """Old photo -> this plane's image. Same two stages as the main span.

    Stage 1: RootSIFT + MAGSAC homography from the old photo masked to this
    plane's segment. Stage 2: warp the old photo through it, re-match under a
    proximity gate, fit a robust polynomial residual on top. The polynomial is
    what absorbs the old photo's lens distortion.
    """
    seed_everything()
    nh_img = plane.read()
    nh, nw = nh_img.shape[:2]
    mask = plane_mask(old.shape, plane)
    cs = plane.coarse_scale
    small = (nh_img if cs == 1.0 else
             cv2.resize(nh_img, None, fx=cs, fy=cs, interpolation=cv2.INTER_AREA))
    k1, d1 = _rootsift(cv2.cvtColor(old, cv2.COLOR_BGR2GRAY), mask, 60000)
    k2, d2 = _rootsift(cv2.cvtColor(small, cv2.COLOR_BGR2GRAY), None, 60000)
    good = _ratio_match(d1, d2, 0.8)
    if len(good) < 12:
        raise SystemExit(f"{plane.key}: only {len(good)} coarse matches")
    src = np.float32([k1[g.queryIdx].pt for g in good])
    dst = np.float32([k2[g.trainIdx].pt for g in good])
    h_c, inl = cv2.findHomography(src, dst, cv2.USAC_MAGSAC, 4.0,
                                  maxIters=50000, confidence=0.9999)
    h_full = np.diag([1 / cs, 1 / cs, 1.0]) @ h_c
    log(f"  stage 1 homography: {int(inl.sum())}/{len(good)} RANSAC inliers")

    ws = plane.guided_scale
    h_w = np.diag([ws, ws, 1.0]) @ h_full
    gw, gh = int(round(nw * ws)), int(round(nh * ws))
    warped = cv2.warpPerspective(old, h_w, (gw, gh), flags=cv2.INTER_LANCZOS4)
    cover = cv2.warpPerspective(mask, h_w, (gw, gh), flags=cv2.INTER_NEAREST)
    news = (nh_img if ws == 1.0 else cv2.resize(nh_img, (gw, gh), interpolation=cv2.INTER_AREA))
    ka, da = _rootsift(cv2.cvtColor(warped, cv2.COLOR_BGR2GRAY), cover, 60000)
    kb, db = _rootsift(cv2.cvtColor(news, cv2.COLOR_BGR2GRAY), cover, 60000)
    gg = _ratio_match(da, db, 0.85)
    p1 = np.float32([ka[g.queryIdx].pt for g in gg])
    p2 = np.float32([kb[g.trainIdx].pt for g in gg])
    keep = np.linalg.norm(p1 - p2, axis=1) <= plane.guided_gate * ws
    p1, p2 = p1[keep], p2[keep]
    src_old = cv2.perspectiveTransform(
        p1.reshape(-1, 1, 2), np.linalg.inv(h_w)).reshape(-1, 2)
    dst_new = p2 / ws
    log(f"  stage 2 guided correspondences: {len(src_old)}")

    base = cv2.perspectiveTransform(src_old.reshape(-1, 1, 2), h_full).reshape(-1, 2)
    hres = np.linalg.norm(base - dst_new, axis=1)
    delta = dst_new - base
    keep = np.ones(len(src_old), bool)
    poly = None
    for _ in range(4):
        poly = PolyMap.fit(src_old[keep], delta[keep], degree=plane.degree, huber=6.0)
        r = np.linalg.norm(base + poly(src_old) - dst_new, axis=1)
        keep = r < max(12.0, 3.0 * np.median(r[keep]))
    seed = SeedMap(h_full, poly)
    res = np.linalg.norm(seed(src_old[keep]) - dst_new[keep], axis=1)
    stats = {
        "homography_inliers": int(inl.sum()),
        "guided_correspondences": int(len(src_old)),
        "homography_residual_px": {"median": float(np.median(hres)),
                                   "p90": float(np.percentile(hres, 90))},
        "seedmap_residual_px": {"median": float(np.median(res)),
                                "p90": float(np.percentile(res, 90))},
        "correspondences_kept": int(keep.sum()),
        "degree": plane.degree,
    }
    log("  homography residual  med %.1f px  p90 %.1f px"
        % (stats["homography_residual_px"]["median"], stats["homography_residual_px"]["p90"]))
    log("  seed map  residual  med %.1f px  p90 %.1f px"
        % (stats["seedmap_residual_px"]["median"], stats["seedmap_residual_px"]["p90"]))
    return seed, h_full, (src_old[keep], dst_new[keep]), stats
