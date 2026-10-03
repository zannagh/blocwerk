import io
import os
import sys

import pytest

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))  # splatworker
# the shared protocol package (copied into /app in the image; from the source tree when run in place)
sys.path.insert(0, os.path.join(os.path.dirname(os.path.dirname(HERE)), "compute-jobs-py"))
sys.path.insert(0, HERE)  # helpers importable by spawned job processes

@pytest.fixture
def gps_jpeg():
    """A JPEG carrying GPS, device EXIF, XMP, an ICC profile and orientation 6 (rotate 90)."""
    from PIL import Image
    im = Image.new("RGB", (64, 32), (200, 30, 30))
    exif = Image.Exif()
    exif[0x010F], exif[0x0110], exif[0x0112] = "TestPhone", "Model X", 6  # make, model, orientation
    gps = exif.get_ifd(0x8825)
    gps[1], gps[2], gps[3], gps[4] = "N", (48.0, 1.0, 26.0), "E", (8.0, 2.0, 57.0)
    sub = exif.get_ifd(0x8769)
    sub[0xA405] = 14  # FocalLengthIn35mmFilm
    xmp = b'<x:xmpmeta xmlns:x="adobe:ns:meta/"><rdf:RDF><exif:GPSLatitude>48,1.4N</exif:GPSLatitude></rdf:RDF></x:xmpmeta>'
    icc = b"\x00" * 128 + b"acspFAKEPROFILE"
    buf = io.BytesIO()
    im.save(buf, "JPEG", exif=exif, xmp=xmp, icc_profile=icc, comment=b"secret comment")
    return buf.getvalue()
