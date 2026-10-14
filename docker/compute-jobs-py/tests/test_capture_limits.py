"""The capture limits: one pair of variables (CAPTURE_MAX_PHOTOS, CAPTURE_MAX_VIDEO_FRAMES) sizes both workers' caps
and their timeouts (computejobs/capture_limits.py, defaults in capture_limits.json)."""
from computejobs import capture_limits as cl


def test_the_defaults_come_from_the_shared_file():
    assert cl.capture({}) == (cl.DEFAULTS["maxPhotos"], cl.DEFAULTS["maxVideoFrames"]) == (200, 120)
    assert cl.geometry_images({}) == 320 and cl.splat_images({}) == 320 + cl.DEFAULTS["anchorImages"]


def test_the_attic_fits_both_workers_once_the_capture_limits_are_raised():
    env = {"CAPTURE_MAX_PHOTOS": "450", "CAPTURE_MAX_VIDEO_FRAMES": "400"}
    assert cl.geometry_images(env) >= 429 + 400 and cl.splat_images(env) >= 429 + 400 + 15


def test_empty_or_bad_values_keep_the_defaults():
    assert cl.capture({"CAPTURE_MAX_PHOTOS": "", "CAPTURE_MAX_VIDEO_FRAMES": "x"}) == (200, 120)
    assert cl.capture({"CAPTURE_MAX_PHOTOS": "0"}) == (200, 120)


def test_timeouts_grow_with_the_images_and_never_shrink():
    assert cl.scaled_s(900, 100) == 900 and cl.scaled_s(900, 200) == 900
    assert cl.scaled_s(900, 850) == 3825
