"""Colour consistency between the marker photos and the walk-along video frames.

An iPhone's HLG HDR video comes out brighter and cooler than its still photos of the same wall; a
splat trained on both averages the two looks into a washed-out, yellow-tinted wall. Ingest therefore
matches every image to ONE look, the photos':

- statistics per image: mean / std of L*, a*, b* (CIE Lab, D65) on a small copy, ignoring
  near-black and near-saturated pixels (they carry no colour and clip differently per exposure);
- reference = the per-channel median of the photos' statistics;
- video frames: Reinhard colour transfer in Lab onto the reference (std ratio clamped);
- photos: a gentle, damped per-channel RGB gain towards the reference mean (clamped), so a single
  odd exposure is pulled in without changing the set's look.

No photos, or no frames: nothing is changed (there is no second look to reconcile).
"""
from dataclasses import dataclass

import numpy as np
from PIL import Image

STATS_EDGE = 256  # statistics are computed on a copy at most this long edge
L_MIN, L_MAX = 4.0, 97.0  # L* outside this: near-black / near-saturated, ignored in the statistics
STD_RATIO = (0.6, 1.6)  # frames: clamp of reference std / frame std per Lab channel
PHOTO_GAIN = (0.7, 1.4)  # photos: clamp of the per-channel RGB gain
PHOTO_STRENGTH = 0.5  # photos: fraction of the (log) gain applied: keep them nearly untouched
MIN_PIXELS = 64  # fewer usable pixels than this: statistics of the whole copy instead

_M_RGB2XYZ = np.array([[0.4124564, 0.3575761, 0.1804375],
                       [0.2126729, 0.7151522, 0.0721750],
                       [0.0193339, 0.1191920, 0.9503041]], np.float32)
_M_XYZ2RGB = np.linalg.inv(_M_RGB2XYZ).astype(np.float32)
_WHITE = np.array([0.95047, 1.0, 1.08883], np.float32)
_EPS, _KAPPA = 216 / 24389, 24389 / 27


@dataclass
class Stats:
    lab_mean: np.ndarray  # (3,) L*, a*, b*
    lab_std: np.ndarray  # (3,)
    rgb_mean: np.ndarray  # (3,) sRGB 0..255

    def summary(self):
        """Log line: 'L* 59.1 a* 1.2 b* 9.8 R/B 1.57'."""
        L, a, b = self.lab_mean
        return f"L* {L:.1f} a* {a:.1f} b* {b:.1f} R/B {self.rgb_mean[0] / max(self.rgb_mean[2], 1e-6):.2f}"


def srgb_to_lab(rgb):
    """uint8 (..., 3) sRGB -> float32 (..., 3) Lab."""
    c = rgb.astype(np.float32) / 255.0
    c = np.where(c <= 0.04045, c / 12.92, ((c + 0.055) / 1.055) ** 2.4)
    xyz = (c @ _M_RGB2XYZ.T) / _WHITE
    f = np.where(xyz > _EPS, np.cbrt(xyz), (_KAPPA * xyz + 16) / 116)
    return np.stack([116 * f[..., 1] - 16, 500 * (f[..., 0] - f[..., 1]), 200 * (f[..., 1] - f[..., 2])], -1)


def lab_to_srgb(lab):
    """float (..., 3) Lab -> uint8 (..., 3) sRGB (out-of-gamut values clipped)."""
    fy = (lab[..., 0] + 16) / 116
    f = np.stack([fy + lab[..., 1] / 500, fy, fy - lab[..., 2] / 200], -1)
    xyz = np.where(f ** 3 > _EPS, f ** 3, (116 * f - 16) / _KAPPA) * _WHITE
    c = np.clip(xyz @ _M_XYZ2RGB.T, 0.0, 1.0)
    c = np.where(c <= 0.0031308, 12.92 * c, 1.055 * c ** (1 / 2.4) - 0.055)
    return np.clip(np.rint(c * 255.0), 0, 255).astype(np.uint8)


def image_stats(im):
    """Stats of a PIL image, on a copy at most STATS_EDGE long."""
    small = im.convert("RGB")
    if max(small.size) > STATS_EDGE:
        s = STATS_EDGE / max(small.size)
        small = small.resize((max(1, round(small.width * s)), max(1, round(small.height * s))), Image.BILINEAR)
    rgb = np.asarray(small, np.uint8).reshape(-1, 3)
    lab = srgb_to_lab(rgb)
    keep = (lab[:, 0] > L_MIN) & (lab[:, 0] < L_MAX) & (rgb.max(1) < 250)
    if keep.sum() < MIN_PIXELS:
        keep = np.ones(len(rgb), bool)
    lab, rgb = lab[keep], rgb[keep].astype(np.float32)
    return Stats(lab.mean(0), lab.std(0), rgb.mean(0))


def group_stats(stats):
    """Per-channel median of a list of Stats (the robust 'typical image' of a group), or None."""
    if not stats:
        return None
    return Stats(*(np.median(np.stack([getattr(s, k) for s in stats]), 0)
                   for k in ("lab_mean", "lab_std", "rgb_mean")))


@dataclass
class Correction:
    kind: str  # "transfer" (Lab mean/std onto the reference) or "gain" (per-channel RGB gain)
    scale: np.ndarray  # (3,) transfer: std ratio per Lab channel; gain: RGB gains
    src_mean: np.ndarray = None  # transfer only: the image's Lab mean
    ref_mean: np.ndarray = None  # transfer only: the reference Lab mean


def frame_correction(s, ref):
    ratio = np.clip(ref.lab_std / np.maximum(s.lab_std, 1e-3), *STD_RATIO)
    return Correction("transfer", ratio.astype(np.float32), s.lab_mean.astype(np.float32),
                      ref.lab_mean.astype(np.float32))


def photo_correction(s, ref):
    gain = np.clip(ref.rgb_mean / np.maximum(s.rgb_mean, 1.0), *PHOTO_GAIN) ** PHOTO_STRENGTH
    return Correction("gain", gain.astype(np.float32))


def plan(stats, is_frame):
    """{stem: Stats} -> ({stem: Correction}, reference Stats or None). Empty unless there are photos
    AND frames."""
    photos = [s for k, s in stats.items() if not is_frame(k)]
    frames = [k for k in stats if is_frame(k)]
    if not photos or not frames:
        return {}, None
    ref = group_stats(photos)
    return {k: (frame_correction(s, ref) if is_frame(k) else photo_correction(s, ref))
            for k, s in stats.items()}, ref


def apply(im, corr):
    """Corrected copy of a PIL RGB image (the input is untouched); None -> the image itself."""
    if corr is None:
        return im
    rgb = np.asarray(im.convert("RGB"), np.uint8)
    if corr.kind == "gain":
        out = np.clip(np.rint(rgb.astype(np.float32) * corr.scale), 0, 255).astype(np.uint8)
    else:
        lab = srgb_to_lab(rgb)
        lab -= corr.src_mean
        lab *= corr.scale
        lab += corr.ref_mean
        out = lab_to_srgb(lab)
    return Image.fromarray(out, "RGB")
