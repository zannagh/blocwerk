"""Metadata stripping for textures photos WITHOUT re-encoding (the pixel grid must stay byte-identical:
the marker corners in the geometry were measured on exactly these pixels).

Mirrors the app's ImageMetadataStripper (src/Blocwerk.Core/Capture/ImageMetadataStripper.cs), and also
drops everything after the primary image's end (JPEG EOI / PNG IEND), where e.g. an MPO's second
image with its own EXIF/GPS would sit:

- JPEG: every APP1..APP15 (EXIF incl. GPS and orientation, XMP, ICC, IPTC, MPF, ...) and COM segment
  goes; APP0 stays only as the plain JFIF header. Everything else (tables, frame, scans) is copied
  byte for byte. Dropping the orientation tag is deliberate, as in the app: the photo is used on its
  raw pixel grid.
- PNG: only IHDR, PLTE, tRNS, IDAT, IEND are kept (no eXIf/tEXt/zTXt/iTXt/tIME/iCCP/...).

The image size is read from the header (JPEG SOF / PNG IHDR) so an oversized image is refused before
anything decodes it.
"""
import struct

PNG_SIGNATURE = b"\x89PNG\r\n\x1a\n"
PNG_KEEP = {b"IHDR", b"PLTE", b"tRNS", b"IDAT", b"IEND"}
SOF_MARKERS = {0xC0, 0xC1, 0xC2, 0xC3, 0xC5, 0xC6, 0xC7, 0xC9, 0xCA, 0xCB, 0xCD, 0xCE, 0xCF}


class BadPhoto(ValueError):
    pass


def sniff(data):
    if data[:3] == b"\xff\xd8\xff":
        return "jpeg"
    if data[:8] == PNG_SIGNATURE:
        return "png"
    return None


def _keep_jpeg_segment(marker, payload):
    if marker == 0xE0:
        return payload.startswith(b"JFIF\x00")
    return not (0xE1 <= marker <= 0xEF or marker == 0xFE)


def _scan_end(src, pos):
    """End of entropy-coded data starting at pos: the next marker that is not a stuffed 0xFF00 or RSTn."""
    n = len(src)
    while True:
        i = src.find(b"\xff", pos)
        if i < 0 or i + 1 >= n:
            raise BadPhoto("the JPEG is truncated (no end of image)")
        nb = src[i + 1]
        if nb == 0x00 or 0xD0 <= nb <= 0xD7 or nb == 0xFF:
            pos = i + (1 if nb == 0xFF else 2)
            continue
        return i


def strip_jpeg(src):
    """-> (stripped bytes, (width, height))."""
    out, pos, n, size = bytearray(src[:2]), 2, len(src), None
    while True:
        if pos + 2 > n:
            raise BadPhoto("the JPEG is truncated (no end of image)")
        if src[pos] != 0xFF:
            raise BadPhoto("the JPEG is corrupt (segment marker expected)")
        while pos < n and src[pos] == 0xFF:  # fill bytes
            pos += 1
        if pos >= n:
            raise BadPhoto("the JPEG is truncated")
        marker, start = src[pos], pos - 1
        pos += 1
        if marker == 0xD9:  # EOI: done, whatever follows (trailers, a second image) is dropped
            if size is None:
                raise BadPhoto("the JPEG has no frame header")
            out += b"\xff\xd9"
            return bytes(out), size
        if marker == 0x01 or 0xD0 <= marker <= 0xD7:
            out += src[start:pos]
            continue
        if marker == 0xD8 or pos + 2 > n:
            raise BadPhoto("the JPEG is corrupt")
        length = struct.unpack(">H", src[pos:pos + 2])[0]
        end = pos + length
        if length < 2 or end > n:
            raise BadPhoto("the JPEG is corrupt (segment length out of range)")
        if marker in SOF_MARKERS and size is None:
            if length < 7:
                raise BadPhoto("the JPEG frame header is corrupt")
            h, w = struct.unpack(">HH", src[pos + 3:pos + 7])
            if not w or not h:
                raise BadPhoto("the JPEG frame header has no size")
            size = (w, h)
        if _keep_jpeg_segment(marker, src[pos + 2:end]):
            out += src[start:end]
        pos = end
        if marker == 0xDA:  # start of scan: copy the entropy-coded data verbatim
            stop = _scan_end(src, pos)
            out += src[pos:stop]
            pos = stop


def strip_png(src):
    """-> (stripped bytes, (width, height))."""
    out, pos, n, size = bytearray(PNG_SIGNATURE), 8, len(src), None
    while pos + 12 <= n:
        length, ctype = struct.unpack(">I4s", src[pos:pos + 8])
        end = pos + 12 + length
        if end > n:
            raise BadPhoto("the PNG is corrupt (chunk length out of range)")
        if ctype == b"IHDR":
            if length < 8:
                raise BadPhoto("the PNG header is corrupt")
            size = struct.unpack(">II", src[pos + 8:pos + 16])
        if ctype in PNG_KEEP:
            out += src[pos:end]
        pos = end
        if ctype == b"IEND":
            if size is None or not all(size):
                raise BadPhoto("the PNG has no valid header")
            return bytes(out), size
    raise BadPhoto("the PNG is truncated (no IEND chunk)")


def strip(src):
    """Raw upload bytes -> (kind 'jpeg'|'png', stripped bytes, (width, height)). BadPhoto otherwise."""
    kind = sniff(src)
    if kind == "jpeg":
        return (kind, *strip_jpeg(src))
    if kind == "png":
        return (kind, *strip_png(src))
    raise BadPhoto("only JPEG and PNG photos are accepted")
