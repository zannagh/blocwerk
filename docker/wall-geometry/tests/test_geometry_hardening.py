"""Security hardening: metadata stripped on arrival without touching pixels, bounded options and
geometry, decompression bombs, strict solve options."""
import json
import os
import struct
import zlib

import cv2
import numpy as np
import pytest
import synthetic
from test_api import _wait, client  # noqa: F401 - fixture

from service.photos import BadPhoto, strip
from service.settings import settings

SECRETS = [b"TestPhone", b"GPSLatitude", b"48,1.4N", b"secret comment", b"ICC_PROFILE", b"TRAILER-GPS"]


def _seg(marker, payload):
    return bytes([0xFF, marker]) + struct.pack(">H", len(payload) + 2) + payload


def with_metadata(jpg):
    """Insert EXIF(GPS)/XMP/ICC/COM segments after SOI and append a trailer + a second image carrying
    GPS after EOI (as MPO files and some phone apps do)."""
    exif = _seg(0xE1, b"Exif\x00\x00MM\x00*\x00\x00\x00\x08 TestPhone GPSLatitude 48,1.4N")
    xmp = _seg(0xE1, b"http://ns.adobe.com/xap/1.0/\x00<x:xmpmeta>48,1.4N</x:xmpmeta>")
    icc = _seg(0xE2, b"ICC_PROFILE\x00\x01\x01" + b"\x00" * 64)
    com = _seg(0xFE, b"secret comment")
    second = b"\xff\xd8" + _seg(0xE1, b"Exif\x00\x00 TRAILER-GPS TestPhone") + b"\xff\xd9"
    return jpg[:2] + exif + xmp + icc + com + jpg[2:] + b"TRAILER-GPS" + second


def _png_chunk(t, d):
    return struct.pack(">I", len(d)) + t + d + struct.pack(">I", zlib.crc32(t + d) & 0xFFFFFFFF)


def _photo():
    return synthetic.scene()


@pytest.mark.parametrize("params", [
    [cv2.IMWRITE_JPEG_QUALITY, 95],
    [cv2.IMWRITE_JPEG_QUALITY, 90, cv2.IMWRITE_JPEG_PROGRESSIVE, 1],  # several scans
    [cv2.IMWRITE_JPEG_QUALITY, 90, cv2.IMWRITE_JPEG_RST_INTERVAL, 4],  # restart markers in the scan
])
def test_jpeg_strip_is_byte_exact_on_the_image_data(params):
    _, photo = _photo()
    jpg = cv2.imencode(".jpg", photo, params)[1].tobytes()
    kind, clean, size = strip(with_metadata(jpg))
    assert kind == "jpeg" and size == (photo.shape[1], photo.shape[0])
    assert clean == jpg  # cv2 writes only JFIF + tables + scans: exactly what survives
    for s in SECRETS:
        assert s not in clean
    a = cv2.imdecode(np.frombuffer(clean, np.uint8), cv2.IMREAD_COLOR | cv2.IMREAD_IGNORE_ORIENTATION)
    b = cv2.imdecode(np.frombuffer(jpg, np.uint8), cv2.IMREAD_COLOR | cv2.IMREAD_IGNORE_ORIENTATION)
    assert np.array_equal(a, b)


def test_png_strip_keeps_pixels_drops_text_and_exif():
    _, photo = _photo()
    png = cv2.imencode(".png", photo)[1].tobytes()
    iend = png.rindex(b"IEND") - 4
    dirty = (png[:33] + _png_chunk(b"tEXt", b"GPS\x0048,1.4N TestPhone") + _png_chunk(b"eXIf", b"MM\x00*TestPhone")
             + png[33:iend] + _png_chunk(b"iTXt", b"secret comment") + png[iend:] + b"TRAILER-GPS")
    kind, clean, size = strip(dirty)
    assert kind == "png" and size == (photo.shape[1], photo.shape[0])
    for s in SECRETS:
        assert s not in clean
    assert np.array_equal(cv2.imdecode(np.frombuffer(clean, np.uint8), cv2.IMREAD_COLOR), photo)


@pytest.mark.parametrize("raw", [b"GIF89a....", b"\xff\xd8\xff\xe1\x00", b"\x89PNG\r\n\x1a\n\x00\x00", b""])
def test_strip_refuses_other_or_broken_files(raw):
    with pytest.raises(BadPhoto):
        strip(raw)


def test_textures_upload_never_writes_gps_and_still_renders(client):
    doc, photo = _photo()
    jpg = cv2.imencode(".jpg", photo, [cv2.IMWRITE_JPEG_QUALITY, 95])[1].tobytes()
    r = client.post("/v1/jobs/textures", files=[
        ("photos", ("SYN_1.jpg", with_metadata(jpg), "image/jpeg")),
        ("geometry", ("geometry.json", json.dumps(doc), "application/json"))])
    assert r.status_code == 202, r.text
    job = r.json()["jobId"]
    d = os.path.join(settings.work_dir, job)
    stored = open(os.path.join(d, "photo_SYN_1.jpg"), "rb").read()
    assert stored == jpg
    st = _wait(client, job)
    assert st["status"] == "succeeded", st["error"]
    assert abs(st["result"]["facets"][0]["markerCheck"]["markers"][0]["sideErrMm"]) < 1.5
    for root, _, files in os.walk(d):
        for f in files:
            data = open(os.path.join(root, f), "rb").read()
            assert not any(s in data for s in SECRETS), f


def _post(client, doc, photos=None, options=None):
    if photos is None:
        _, photo = _photo()
        photos = [("SYN_1.jpg", cv2.imencode(".jpg", photo)[1].tobytes())]
    files = [("photos", (n, b, "image/jpeg")) for n, b in photos]
    files.append(("geometry", ("g.json", json.dumps(doc) if not isinstance(doc, str) else doc, "application/json")))
    if options is not None:
        files.append(("options", (None, options if isinstance(options, str) else json.dumps(options))))
    return client.post("/v1/jobs/textures", files=files)


@pytest.mark.parametrize("options, text", [
    ({"maxSidePx": 10 ** 9}, "maxSidePx"), ({"maxSidePx": -5}, "maxSidePx"), ({"mmPerPx": 0}, "mmPerPx"),
    ({"mmPerPx": 0.001}, "mmPerPx"), ({"extraMarginMm": 1e12}, "extraMarginMm"), ({"jpegQuality": 101}, "jpegQuality"),
    ({"jpegQuality": 90.5}, "jpegQuality"), ({"labelCellPx": 0}, "unknown option"),
    ({"modeFilterCells": 10 ** 6}, "unknown option"), ({"maxSidePx": True}, "maxSidePx"), ([1, 2], "object")])
def test_textures_options_are_bounded(client, options, text):
    doc, _ = _photo()
    r = _post(client, doc, options=options)
    assert r.status_code == 422 and text in r.json()["detail"], r.text


@pytest.mark.parametrize("raw", ['{"mmPerPx": NaN}', '{"maxSidePx": Infinity}'])
def test_textures_options_non_finite(client, raw):
    doc, _ = _photo()
    assert _post(client, doc, options=raw).status_code in (400, 422)


def test_textures_total_pixel_budget(client, monkeypatch):
    doc, _ = _photo()
    monkeypatch.setattr(settings, "textures_max_pixels", 10_000)
    r = _post(client, doc)
    assert r.status_code == 422 and "MP in total" in r.json()["detail"]


def _bad_geometry(mutate):
    doc, _ = _photo()
    mutate(doc)
    return doc


@pytest.mark.parametrize("mutate", [
    lambda d: d.update(dictionary="__class__"),
    lambda d: d["segments"][0]["facets"][0]["extentMm"].update(aMax=1e300),
    lambda d: d["segments"][0]["facets"][0].update(u=[1, 0]),
    lambda d: d["cameras"][0].update(width=10 ** 7),
    lambda d: d["cameras"][0].update(K=[1] * 8),
    lambda d: d["segments"].extend([{"facets": [d["segments"][0]["facets"][0]] * 40}] * 2),
    lambda d: d["markers"][0].update(cornersPlaneMm="x"),
])
def test_textures_geometry_is_validated(client, mutate):
    r = _post(client, _bad_geometry(mutate))
    assert r.status_code == 422, r.text


def test_textures_photo_checks(client, monkeypatch):
    doc, photo = _photo()
    small = cv2.imencode(".jpg", photo[:600, :800])[1].tobytes()
    r = _post(client, doc, photos=[("SYN_1.jpg", small)])
    assert r.status_code == 422 and "solved as" in r.json()["detail"]
    r = _post(client, doc, photos=[("SYN_1.jpg", b"GIF89a not a photo")])
    assert r.status_code == 422 and "JPEG and PNG" in r.json()["detail"]
    r = _post(client, doc, photos=[("SYN_1.webp", small)])
    assert r.status_code == 400
    r = _post(client, doc, photos=[("../SYN_1.jpg", small)])
    assert r.status_code in (400, 422)


def test_decompression_bomb_header_is_refused_before_decoding(client):
    """A 12000 x 12000 PNG header (144 MP, inside Pillow's warn-only window; OpenCV would try up to
    1 GP) is refused from the header alone."""
    bomb = (b"\x89PNG\r\n\x1a\n" + _png_chunk(b"IHDR", struct.pack(">IIBBBBB", 12000, 12000, 8, 2, 0, 0, 0))
            + _png_chunk(b"IDAT", zlib.compress(b"\x00" * 64)) + _png_chunk(b"IEND", b""))
    doc, _ = _photo()
    r = _post(client, doc, photos=[("SYN_1.png", bomb)])
    assert r.status_code == 413 and "MP" in r.json()["detail"]


@pytest.mark.parametrize("patch, text", [
    ({"options": {"validate": False, "bogus": 1}}, "unknown option"),
    ({"options": {"validate": "yes"}}, "validate"),
    ({"options": {"facets": {"foldDeg": -1}}}, "foldDeg"),
    ({"options": {"facets": {"minMarkersPerFacet": 0}}}, "minMarkersPerFacet"),
    ({"options": {"facets": {"mergeMm": 1e9}}}, "mergeMm"),
    ({"markerSizeOverridesMm": {"abc": 80}}, "markerSizeOverridesMm"),
    ({"levelPairs": [[1, 2]] * 1000}, "levelPairs"),
    ({"levelPairs": [[1, True]]}, "levelPairs"),
])
def test_solve_options_are_validated(client, capture1_request, patch, text):
    r = client.post("/v1/jobs/solve", json={**capture1_request, **patch})
    assert r.status_code == 422 and text in r.json()["detail"], r.text


def test_solve_rejects_non_finite_json(client):
    r = client.post("/v1/jobs/solve", content=b'{"markerSizeMm": NaN}', headers={"content-type": "application/json"})
    assert r.status_code == 400
