"""Photo hygiene: nothing of the uploaded metadata survives ingest."""
import io

import pytest
from PIL import Image

from splatworker.ingest import PhotoError, sanitize

MARKERS = [b"Exif\x00\x00", b"TestPhone", b"GPS", b"xmpmeta", b"http://ns.adobe.com/xap", b"ICC_PROFILE",
           b"acspFAKE", b"secret comment"]


def assert_clean(jpg):
    for m in MARKERS:
        assert m not in jpg, m
    im = Image.open(io.BytesIO(jpg))
    assert len(im.getexif()) == 0
    assert not {"exif", "xmp", "icc_profile", "comment"} & set(im.info)


def test_input_really_has_metadata(gps_jpeg):
    im = Image.open(io.BytesIO(gps_jpeg))
    assert im.getexif().get_ifd(0x8825)  # GPS present
    assert b"TestPhone" in gps_jpeg and b"xmpmeta" in gps_jpeg and b"acspFAKE" in gps_jpeg


def test_sanitize_strips_everything_but_keeps_what_sfm_needs(gps_jpeg):
    jpg, facts = sanitize(gps_jpeg, 4096)
    assert_clean(jpg)
    assert (facts["width"], facts["height"]) == (32, 64)  # EXIF orientation 6 applied to the pixels
    assert facts["focal35"] == 14.0
    assert facts["lensKey"] and "TestPhone" not in facts["lensKey"]  # opaque hash only
    px = Image.open(io.BytesIO(jpg)).convert("RGB").getpixel((16, 32))
    assert px[0] > 150 and px[1] < 80


def test_sanitize_downscales_png_with_text_chunks():
    from PIL.PngImagePlugin import PngInfo
    info = PngInfo()
    info.add_text("GPS", "48.0N 8.0E")
    buf = io.BytesIO()
    Image.new("RGB", (3000, 1000), (1, 2, 3)).save(buf, "PNG", pnginfo=info)
    jpg, facts = sanitize(buf.getvalue(), 1500)
    assert_clean(jpg)
    assert (facts["width"], facts["height"]) == (1500, 500) and facts["focal35"] is None


@pytest.mark.parametrize("raw", [b"not an image", b"GIF89a" + b"\x00" * 40])
def test_sanitize_rejects_non_photos(raw):
    with pytest.raises(PhotoError):
        sanitize(raw, 1000)


def _png_header_only(w, h):
    """A PNG whose header claims w x h (the pixel data is never needed: the size check comes first)."""
    import struct
    import zlib

    def chunk(t, d):
        return struct.pack(">I", len(d)) + t + d + struct.pack(">I", zlib.crc32(t + d) & 0xFFFFFFFF)
    return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 2, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(b"\x00" * 16)) + chunk(b"IEND", b""))


@pytest.mark.parametrize("w, h", [(11000, 11000), (15000, 15000), (60000, 60000)])
def test_decompression_bombs_are_errors_not_warnings(w, h):
    """121 MP and 225 MP are in Pillow's 'warn only' window (limit..2x limit); 3.6 GP beyond it.
    All are refused before decoding."""
    import warnings
    with warnings.catch_warnings():
        warnings.simplefilter("error")
        with pytest.raises(PhotoError, match="too large"):
            sanitize(_png_header_only(w, h), 4096)


def test_jpeg_qualities_default_to_the_old_values():
    from splatworker.settings import settings
    assert settings.ingest_jpeg_quality == 95 and settings.frame_jpeg_quality == 92


def test_sanitize_uses_the_quality_it_is_given(gps_jpeg):
    low, _ = sanitize(gps_jpeg, 4096, 10)
    high, _ = sanitize(gps_jpeg, 4096, 100)
    assert len(low) < len(high)
