"""The gsplat trainer's views: an undistorted COLMAP dataset (images/ + sparse/0) at a capped long edge.

Every image keeps its own camera (photos of different lenses / sizes and the walk-along video frames
each have their COLMAP camera), downscaled to at most `max_edge` with its intrinsics scaled alike.
Decoded images stay in host memory up to `cache_mb` (uint8); the rest is decoded again when drawn,
so a small host budget costs time instead of failing. numpy + PIL only (no torch): testable anywhere.
eval_split picks the held-out views of GSPLAT_EVAL_EVERY (gsplat_train.py).
"""
import os
from concurrent.futures import ThreadPoolExecutor

import numpy as np

from .colmap_model import camera_centre, intrinsics, read_model, viewmat


def scaled_size(w, h, max_edge):
    s = min(1.0, max_edge / max(w, h)) if max_edge else 1.0
    return max(1, round(w * s)), max(1, round(h * s))


def scene_scale(centres):
    """How far the cameras spread (max distance from their mean): the scale of the position learning
    rate. It only scales step sizes; the splats are never moved into a normalised frame."""
    c = np.asarray(centres, float)
    return float(np.linalg.norm(c - c.mean(0), axis=1).max()) if len(c) > 1 else 1.0


def eval_split(names, every):
    """(train indices, held-out indices) of views sorted by name: every `every`-th PHOTO (0, every,
    2 x every, ... among the non-`vf_` views: the mip-NeRF 360 / 3DGS convention) is held out; the
    walk-along video frames always train (a held-out frame's neighbours show nearly the same view, so it
    would flatter the score). No photos: every `every`-th view. every <= 0 or fewer than 2 views: none."""
    n = len(names)
    if every <= 0 or n < 2:
        return list(range(n)), []
    photos = [i for i, nm in enumerate(names) if not nm.rsplit("/", 1)[-1].startswith("vf_")] or list(range(n))
    held = [i for k, i in enumerate(photos) if k % every == 0]
    if len(held) >= n:  # every == 1 would leave nothing to train on: hold out all but one
        held = held[:-1]
    keep = set(held)
    return [i for i in range(n) if i not in keep], held


class Views:
    def __init__(self, dataset_dir, max_edge, cache_mb=0, progress=None):
        cams, images, self.points, self.colors = read_model(dataset_dir)
        self.image_dir = os.path.join(dataset_dir, "images")
        self.max_edge, self.views = max_edge, []
        for im in sorted(images, key=lambda x: x["name"]):
            cam = cams[im["camera_id"]]
            fx, fy, cx, cy = intrinsics(cam)  # raises for a distorted model
            w, h = scaled_size(cam["width"], cam["height"], max_edge)
            sx, sy = w / cam["width"], h / cam["height"]
            K = np.array([[fx * sx, 0, cx * sx], [0, fy * sy, cy * sy], [0, 0, 1]], np.float32)
            self.views.append({"name": im["name"], "size": (w, h), "K": K,
                               "viewmat": viewmat(im).astype(np.float32), "centre": camera_centre(im)})
        if not self.views:
            raise ValueError("the COLMAP model has no registered images")
        self.scene_scale = scene_scale([v["centre"] for v in self.views])
        self.cache = {}
        self._fill_cache(cache_mb, progress)

    def __len__(self):
        return len(self.views)

    def load(self, i):
        """uint8 (h, w, 3) of view i at its training size."""
        from PIL import Image
        v = self.views[i]
        with Image.open(os.path.join(self.image_dir, v["name"])) as im:
            im = im.convert("RGB")
            if im.size != v["size"]:
                im = im.resize(v["size"], Image.LANCZOS)
            return np.array(im)  # writable: torch.from_numpy warns on read-only arrays

    def _fill_cache(self, cache_mb, progress):
        budget = cache_mb * 1024 * 1024
        chosen = []
        for i, v in enumerate(self.views):
            need = v["size"][0] * v["size"][1] * 3
            if need > budget:
                break
            budget -= need
            chosen.append(i)
        with ThreadPoolExecutor(max_workers=min(8, os.cpu_count() or 4)) as pool:
            for n, (i, arr) in enumerate(zip(chosen, pool.map(self.load, chosen))):
                self.cache[i] = arr
                if progress:
                    progress(n + 1, len(chosen))

    def get(self, i):
        """(uint8 image, K 3x3, world-to-camera 4x4) of view i."""
        img = self.cache.get(i)
        if img is None:
            img = self.load(i)
        v = self.views[i]
        return img, v["K"], v["viewmat"]
