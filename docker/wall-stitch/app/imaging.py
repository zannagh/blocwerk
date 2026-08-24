"""Image helpers: dimension probing and the display-resolution copies.

The full-resolution masters are ~40 MB PNGs; the app stores only the JPEG copies
produced here in the database and serves those to browsers.
"""
from __future__ import annotations

import os
from typing import Tuple

from PIL import Image

# The masters are far larger than Pillow's decompression-bomb guard allows by default,
# and they are our own output rather than untrusted input.
Image.MAX_IMAGE_PIXELS = None


def dimensions(path: str) -> Tuple[int, int]:
    with Image.open(path) as img:
        return int(img.width), int(img.height)


def write_display_copy(source: str, destination: str, max_edge: int, quality: int) -> Tuple[int, int]:
    """Writes a JPEG copy roughly `max_edge` px on its long side. Never upscales."""
    with Image.open(source) as img:
        img = img.convert("RGB")
        longest = max(img.width, img.height)
        if longest > max_edge:
            scale = max_edge / float(longest)
            size = (max(1, round(img.width * scale)), max(1, round(img.height * scale)))
            img = img.resize(size, Image.LANCZOS)
        os.makedirs(os.path.dirname(destination), exist_ok=True)
        img.save(destination, "JPEG", quality=quality, optimize=True, progressive=True)
        return int(img.width), int(img.height)


def content_type_for(name: str) -> str:
    ext = os.path.splitext(name)[1].lower()
    return {
        ".png": "image/png",
        ".jpg": "image/jpeg",
        ".jpeg": "image/jpeg",
        ".webp": "image/webp",
        ".json": "application/json",
    }.get(ext, "application/octet-stream")
