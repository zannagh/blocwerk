"""The capture limits, in one place for the app and both workers.

A capture has at most CAPTURE_MAX_PHOTOS photos and CAPTURE_MAX_VIDEO_FRAMES video frames (the app reads the same two
variables as CAPTURE__MAXPHOTOS / CAPTURE__MAXVIDEOFRAMES; the compose files pass one value to all three). Every per-job
cap of a worker follows from them, and so do the timeouts, which were sized for `baselineImages` (200) images:

  wall-geometry   MAX_PHOTOS     = photos + frames            (a feature solve and the textures see every image)
  splat worker    MAX_PHOTOS     = photos + frames + anchors  (plus the marker anchor crops)
  timeouts        base x images / baselineImages, never below the base

An explicit MAX_PHOTOS or *_TIMEOUT_S still wins (a machine that must stay below the capture limits). The defaults live in
capture_limits.json next to this file; the app's WallCapturePipelineOptions / CaptureLimits use the same numbers (a test
on the app side reads this file)."""
import json
import math
import os

with open(os.path.join(os.path.dirname(__file__), "capture_limits.json")) as _fh:
    DEFAULTS = json.load(_fh)


def _int(env, name, default):
    try:
        value = int(env.get(name, ""))
    except ValueError:
        return default
    return value if value > 0 else default


def capture(env=None):
    """(photos, video frames) a capture may have."""
    env = os.environ if env is None else env
    return (_int(env, "CAPTURE_MAX_PHOTOS", DEFAULTS["maxPhotos"]),
            _int(env, "CAPTURE_MAX_VIDEO_FRAMES", DEFAULTS["maxVideoFrames"]))


def geometry_images(env=None):
    """The wall-geometry service's images per job for the largest capture."""
    photos, frames = capture(env)
    return photos + frames


def splat_images(env=None):
    """The splat worker's images per job for the largest capture (photos, frames and the anchor crops)."""
    return geometry_images(env) + DEFAULTS["anchorImages"]


def scaled_s(base_s, images):
    """A timeout sized for baselineImages images, grown in proportion for more (never below the base)."""
    baseline = DEFAULTS["baselineImages"]
    return base_s if images <= baseline else int(math.ceil(base_s * images / baseline))
