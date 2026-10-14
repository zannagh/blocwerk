"""The capture limits: one pair of variables (CAPTURE_MAX_PHOTOS, CAPTURE_MAX_VIDEO_FRAMES) sizes both workers' caps
and their timeouts (computejobs/capture_limits.py, every number in capture_limits.json, which the app reads too)."""
from computejobs import capture_limits as cl

ATTIC = {"CAPTURE_MAX_PHOTOS": "450", "CAPTURE_MAX_VIDEO_FRAMES": "400"}


def test_the_defaults_come_from_the_shared_file():
    assert cl.capture({}) == (cl.DEFAULTS["maxPhotos"], cl.DEFAULTS["maxVideoFrames"]) == (200, 120)
    assert cl.geometry_images({}) == 200  # wall-geometry never gets the video frames
    assert cl.splat_images({}) == 600  # 200 + 120 + 15 is below the floor it always took


def test_the_attic_fits_both_workers_once_the_capture_limits_are_raised():
    assert cl.geometry_images(ATTIC) == 450 >= 429
    assert cl.splat_images(ATTIC) == 450 + 400 + 15 >= 429 + 400 + 15


def test_values_outside_the_apps_ranges_keep_the_defaults_as_the_app_does():
    assert cl.capture({"CAPTURE_MAX_PHOTOS": "", "CAPTURE_MAX_VIDEO_FRAMES": "x"}) == (200, 120)
    assert cl.capture({"CAPTURE_MAX_PHOTOS": "1", "CAPTURE_MAX_VIDEO_FRAMES": "401"}) == (200, 120)
    assert cl.capture({"CAPTURE_MAX_PHOTOS": "1000", "CAPTURE_MAX_VIDEO_FRAMES": "3"}) == (1000, 3)


def test_timeouts_grow_with_the_images_and_never_shrink():
    assert cl.scaled_s(900, 100) == 900 and cl.scaled_s(900, 200) == 900
    assert cl.scaled_s(900, 850) == 3825
    assert cl.scaled_s(14400, 600, 600) == 14400 and cl.scaled_s(14400, 865, 600) == 20760
