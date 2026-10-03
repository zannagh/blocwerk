"""Stage progress from captured tool output (COLMAP 4.2 on macOS, Brush 0.3.0 TTY) + 3.9-style lines."""
from helpers import fixture_lines

from splatworker.parsers import BrushParser, ExtractParser, MapParser, MatchParser, UndistortParser


def feed(parser, lines):
    return [r for r in map(parser, lines) if r]


def monotone(values):
    return all(b >= a for a, b in zip(values, values[1:]))


def test_feature_extraction_with_offset_for_camera_groups():
    out = feed(ExtractParser(28, offset=14), fixture_lines("colmap-4.2-feature_extractor.log"))
    assert out and monotone([f for f, _ in out])
    assert out[0] == (15 / 28, "15/28 photos")


def test_matching_blocks():
    out = feed(MatchParser(), fixture_lines("colmap-4.2-exhaustive_matcher.log"))
    fr = [f for f, _ in out]
    assert len(out) >= 4 and monotone(fr) and fr[0] == 0.0 and fr[-1] < 1.0
    # COLMAP 3.9 wording
    assert MatchParser()("I0923 feature_matching.cc:111] Matching block [2/3, 1/3]") == (3 / 9, "block 4/9")
    assert MatchParser()("Matching image [5/40]")[0] == 4 / 40


def test_mapping_counts_registered_images():
    out = feed(MapParser(14), fixture_lines("colmap-4.2-mapper.log"))
    assert out[0] == (2 / 14, "2/14 images registered")
    assert out[-1] == (13 / 14, "13/14 images registered") and monotone([f for f, _ in out])
    assert MapParser(10)("I0923 incremental_mapper.cc:12] Registering image #7 (4)") == (0.4, "4/10 images registered")


def test_undistort():
    out = feed(UndistortParser(), fixture_lines("colmap-4.2-image_undistorter.log"))
    assert out[-1] == (1.0, "14/14 images")


def test_brush_steps_splats_and_time():
    p = BrushParser()
    out = feed(p, fixture_lines("brush-0.3.0-tty.log"))
    assert out[0] == (10 / 300, "step 10/300") and out[-1] == (1.0, "step 300/300")
    assert monotone([f for f, _ in out])
    assert p.splats == 17430 and p.took == "32s" and p.views == 14


def test_brush_ignores_noise_and_regressions():
    p = BrushParser()
    assert p("✅ evaluating every 1000 steps") is None
    assert p("[3s] ◍◍○○ 50/100 Steps (9/s)") == (0.5, "step 50/100")
    assert p("[3s] ◍○○○ 10/100 Steps (9/s)") is None  # stale redraw
