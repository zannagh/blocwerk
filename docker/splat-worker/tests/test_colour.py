"""Colour consistency: video frames take on the photos' look; nothing changes without both."""
import json
import os

import numpy as np
from PIL import Image

from splatworker import colour, pipeline
from splatworker.frames import is_frame
from splatworker.options import SplatOptions


def textured(seed, tint=(1.0, 1.0, 1.0), lift=0.0, contrast=1.0):
    """A 'wall' of random warm holds on a grey ground, optionally re-shot brighter / cooler."""
    rng = np.random.default_rng(seed)
    base = np.full((96, 128, 3), (150, 135, 115), np.float32)
    for _ in range(40):
        y, x = rng.integers(0, 88), rng.integers(0, 120)
        base[y:y + 8, x:x + 8] = rng.integers(40, 220, 3)
    base += rng.normal(0, 6, base.shape)
    shot = (base - 128) * contrast + 128 + lift
    return Image.fromarray(np.clip(shot * np.array(tint), 0, 255).astype(np.uint8), "RGB")


def shoot(n_photos=4, n_frames=4):
    photos = {f"IMG_{i}": textured(i) for i in range(n_photos)}
    # HDR-ish video: brighter, cooler (less red, more blue), flatter
    frames = {f"vf_{i:04d}": textured(100 + i, (0.85, 1.0, 1.15), 25, 0.8) for i in range(n_frames)}
    return photos | frames


def corrected(images):
    fixes, ref = colour.plan({k: colour.image_stats(im) for k, im in images.items()}, is_frame)
    return {k: colour.apply(im, fixes.get(k)) for k, im in images.items()}, fixes, ref


def test_lab_round_trip_is_lossless_to_one_level():
    rgb = np.random.default_rng(1).integers(0, 256, (1000, 3)).astype(np.uint8)
    assert np.abs(colour.lab_to_srgb(colour.srgb_to_lab(rgb)).astype(int) - rgb).max() <= 1


def test_frames_are_matched_to_the_photo_statistics():
    images = shoot()
    out, fixes, ref = corrected(images)
    before = colour.group_stats([colour.image_stats(images[k]) for k in images if is_frame(k)])
    frames = colour.group_stats([colour.image_stats(out[k]) for k in out if is_frame(k)])
    photos = colour.group_stats([colour.image_stats(out[k]) for k in out if not is_frame(k)])
    assert abs(before.lab_mean[0] - ref.lab_mean[0]) > 5  # the synthetic shift is real
    assert np.abs(frames.lab_mean - ref.lab_mean).max() < 1.5
    assert np.abs(frames.lab_std - ref.lab_std).max() < 2.0
    rb = lambda s: s.rgb_mean[0] / s.rgb_mean[2]  # noqa: E731
    assert abs(rb(frames) - rb(photos)) < 0.05 < abs(rb(before) - rb(photos))
    assert all(fixes[k].kind == "gain" and np.all(np.abs(fixes[k].scale - 1) < 0.05)
               for k in fixes if not is_frame(k))  # photos: nearly untouched


def test_nothing_changes_without_photos_or_without_frames():
    for images in (shoot(n_photos=0), shoot(n_frames=0)):
        out, fixes, ref = corrected(images)
        assert fixes == {} and ref is None
        assert all(np.array_equal(np.asarray(out[k]), np.asarray(images[k])) for k in images)


def test_std_ratio_is_clamped():
    flat = colour.Stats(np.array([50., 0, 0]), np.array([1., 1, 1]), np.ones(3))
    ref = colour.Stats(np.array([60., 0, 0]), np.array([20., 20, 20]), np.ones(3))
    assert np.allclose(colour.frame_correction(flat, ref).scale, colour.STD_RATIO[1])


def make_job(tmp_path, images, colour_match=True):
    (tmp_path / "arrived").mkdir()
    for k, im in images.items():
        im.save(tmp_path / "arrived" / f"{k}.jpg", quality=95)
    opts = SplatOptions(colourMatch=colour_match).to_dict()
    (tmp_path / "inputs.json").write_text(json.dumps({
        "photos": {k: {"width": 128, "height": 96} for k in images}, "options": opts}))
    return pipeline.Run(str(tmp_path), lambda *a: None)


def ingested_frame_l(tmp_path, colour_match):
    images = shoot(n_photos=3, n_frames=3)
    r = make_job(tmp_path, images, colour_match)
    img_dir = r.ingest()
    names = [n for g in r.groups.values() for n in g["names"]]
    got = {n.split("/")[1][:-4]: colour.image_stats(Image.open(f"{img_dir}/{n}")) for n in names}
    photos = colour.group_stats([s for k, s in got.items() if not is_frame(k)])
    frames = colour.group_stats([s for k, s in got.items() if is_frame(k)])
    log = open(r.log).read() if os.path.exists(r.log) else ""
    return abs(frames.lab_mean[0] - photos.lab_mean[0]), log


def test_ingest_applies_the_colour_match_and_logs_it(tmp_path):
    gap, log = ingested_frame_l(tmp_path, True)
    assert gap < 2.0
    assert "colour: frames before: L*" in log and "colour: frames after: L*" in log
    assert "colour: reference (median photo)" in log


def test_ingest_leaves_colours_alone_when_switched_off(tmp_path):
    gap, _ = ingested_frame_l(tmp_path, False)
    assert gap > 5
