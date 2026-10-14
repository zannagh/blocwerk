"""The capture limits, in one place for the app and both workers.

A capture has at most CAPTURE_MAX_PHOTOS photos and CAPTURE_MAX_VIDEO_FRAMES video frames (the app reads the same two
variables as CAPTURE__MAXPHOTOS / CAPTURE__MAXVIDEOFRAMES, with the same ranges: a value outside them keeps the default on
both sides; the compose files pass one value to all three). Every per-job cap of a worker follows from them, and so do the
timeouts, which were sized for `baselineImages` (200) images:

  wall-geometry   MAX_PHOTOS = photos                                 (it never gets the video frames)
  splat worker    MAX_PHOTOS = photos + frames + anchors, >= 600      (the anchor photos of a markerless capture)
  timeouts        base x images / baselineImages, never below the base

An explicit MAX_PHOTOS or *_TIMEOUT_S still wins (a machine that must stay below the capture limits). Every number lives in
capture_limits.json next to this file; the app's CaptureLimits / WallCapturePipelineOptions use the same ones (a test on the
app side reads this file)."""
import json
import math
import os

with open(os.path.join(os.path.dirname(__file__), "capture_limits.json")) as _fh:
    DEFAULTS = json.load(_fh)


def _int(env, name, default, bounds):
    """The app's ReadInt: an integer within the bounds, else the default."""
    try:
        value = int(env.get(name, ""))
    except ValueError:
        return default
    return value if bounds[0] <= value <= bounds[1] else default


def capture(env=None):
    """(photos, video frames) a capture may have."""
    env = os.environ if env is None else env
    return (_int(env, "CAPTURE_MAX_PHOTOS", DEFAULTS["maxPhotos"], DEFAULTS["photosRange"]),
            _int(env, "CAPTURE_MAX_VIDEO_FRAMES", DEFAULTS["maxVideoFrames"], DEFAULTS["videoFramesRange"]))


def geometry_images(env=None):
    """The wall-geometry service's images per job for the largest capture: its photos."""
    return capture(env)[0]


def splat_images(env=None):
    """The splat worker's images per job for the largest capture (photos, frames and anchor photos), at least the floor."""
    photos, frames = capture(env)
    return max(DEFAULTS["splatFloorImages"], photos + frames + DEFAULTS["anchorImages"])


def scaled_s(base_s, images, baseline=None):
    """A timeout sized for `baseline` (default baselineImages) images, grown in proportion for more (never below the base)."""
    baseline = baseline or DEFAULTS["baselineImages"]
    return base_s if images <= baseline else int(math.ceil(base_s * images / baseline))
