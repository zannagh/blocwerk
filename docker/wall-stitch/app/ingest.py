"""Validating and landing the uploaded photos.

Uploads are streamed to disk in chunks and counted as they go, so an oversized body
is rejected before it is ever held in memory. HEIC is transcoded to JPEG on arrival
because the pipeline reads images through OpenCV, which does not decode HEIC.
"""
from __future__ import annotations

import os
from typing import Iterable, List, Optional, Tuple

from PIL import Image, UnidentifiedImageError

try:  # optional: only needed for iPhone-native HEIC uploads
    import pillow_heif

    pillow_heif.register_heif_opener()
    HEIF_SUPPORTED = True
except Exception:  # noqa: BLE001 - absence is a capability, not an error
    HEIF_SUPPORTED = False

Image.MAX_IMAGE_PIXELS = None

ALLOWED_TYPES = {
    "image/jpeg", "image/jpg", "image/pjpeg", "image/png",
    "image/heic", "image/heif", "image/heic-sequence",
}
ALLOWED_EXTENSIONS = {".jpg", ".jpeg", ".png", ".heic", ".heif"}
CHUNK = 1024 * 1024


class UploadRejected(Exception):
    """A validation failure that becomes a 400 with a machine-readable code."""

    def __init__(self, code: str, message: str):
        self.code = code
        self.message = message
        super().__init__(message)


def acceptable(filename: str, content_type: Optional[str]) -> bool:
    extension = os.path.splitext(filename or "")[1].lower()
    if extension in ALLOWED_EXTENSIONS:
        return True
    return (content_type or "").split(";")[0].strip().lower() in ALLOWED_TYPES


async def save_upload(upload, destination: str, max_bytes: int, budget: List[int],
                      total_max: int) -> int:
    """Streams one upload to disk. `budget` is a single-element running byte total."""
    written = 0
    with open(destination, "wb") as handle:
        while True:
            chunk = await upload.read(CHUNK)
            if not chunk:
                break
            written += len(chunk)
            budget[0] += len(chunk)
            if written > max_bytes:
                raise UploadRejected(
                    "photo_too_large",
                    f"'{os.path.basename(destination)}' is larger than the "
                    f"{max_bytes // (1024 * 1024)} MB per-photo limit.")
            if budget[0] > total_max:
                raise UploadRejected(
                    "request_too_large",
                    f"The upload exceeds the {total_max // (1024 * 1024)} MB total limit.")
            handle.write(chunk)
    if written == 0:
        raise UploadRejected("empty_photo", "One of the uploaded files was empty.")
    return written


def normalise(source: str, destination: str) -> Tuple[int, int]:
    """Rewrites `source` as a JPEG at `destination`; returns its pixel size."""
    try:
        with Image.open(source) as img:
            img.load()
            rgb = img.convert("RGB")
            rgb.save(destination, "JPEG", quality=97, subsampling=0)
            return int(rgb.width), int(rgb.height)
    except UnidentifiedImageError as exc:
        extra = "" if HEIF_SUPPORTED else " HEIC support is not installed in this image."
        raise UploadRejected(
            "unreadable_photo",
            "One of the uploaded files could not be read as an image."
            f" Re-export the photos as JPEG or PNG.{extra}") from exc
    except OSError as exc:
        raise UploadRejected(
            "unreadable_photo",
            "One of the uploaded files could not be read as an image.") from exc


def numbered_names(count: int) -> Iterable[str]:
    """`1.jpeg`, `2.jpeg`, ... - the layout the current stitcher expects in --src."""
    return (f"{index}.jpeg" for index in range(1, count + 1))
