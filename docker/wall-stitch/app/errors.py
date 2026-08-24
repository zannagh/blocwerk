"""Failure classification.

The app shows `message` to a climber, so it must be an instruction rather than a
stack trace. Nothing derived from a traceback or a filesystem path is ever put in
`message`: only the fixed sentences below.
"""
from __future__ import annotations

import re
from typing import Tuple

MESSAGES = {
    "too_few_usable_images": (
        "Not enough of the photos could be used. Upload at least two sharp, well-lit shots "
        "that each show a large part of the wall."),
    "insufficient_overlap": (
        "The photos do not overlap enough to be joined. Reshoot so each photo shares at "
        "least a third of the wall with the previous one."),
    "no_dominant_plane": (
        "No single flat wall surface could be found in the photos. Shoot the main climbing "
        "span head-on and keep floor, ceiling and side panels out of frame where possible."),
    "unreadable_image": (
        "One of the uploaded files could not be read as an image. Re-export the photos as "
        "JPEG or PNG and try again."),
    "image_too_small": (
        "The photos are too low-resolution to stitch. Upload the originals from the camera "
        "roll rather than shared or messaged copies."),
    "hold_transfer_failed": (
        "The wall was stitched but the existing holds could not be carried across. Retry "
        "without hold transfer, or place the holds again on the new photo."),
    "timeout": (
        "The stitch took too long and was stopped. Try again with fewer or smaller photos."),
    "out_of_memory": (
        "The stitch ran out of memory. Try again with fewer photos."),
    "cancelled": "The stitch was cancelled.",
    "pipeline_failed": (
        "The stitch failed for an unexpected reason. Please try again; if it keeps failing, "
        "reshoot the wall."),
    "interrupted": (
        "The stitch was interrupted by a service restart and did not finish. Please start it again."),
}

# Ordered: first pattern that matches the pipeline's own output wins.
PATTERNS = [
    (re.compile(r"could not read (?:input )?image", re.I), "unreadable_image"),
    (re.compile(r"\bcannot identify image file\b", re.I), "unreadable_image"),
    # OpenCV's own way of saying "imread returned nothing for that path".
    (re.compile(r"!\w*image\.empty\(\)|!_src\.empty\(\)", re.I), "unreadable_image"),
    (re.compile(r"\btoo few usable images\b|\bneed at least (?:two|2)\b", re.I), "too_few_usable_images"),
    (re.compile(r"\binsufficient overlap\b|\bno usable overlap\b|pair \S+: FAILED", re.I), "insufficient_overlap"),
    (re.compile(r"\bno dominant plane\b|\bplane normal\b.*\bfail", re.I), "no_dominant_plane"),
    (re.compile(r"\bdecomposeHomographyMat\b", re.I), "no_dominant_plane"),
    (re.compile(r"\bMemoryError\b|\bstd::bad_alloc\b|Insufficient memory", re.I), "out_of_memory"),
    (re.compile(r"\bimage too small\b", re.I), "image_too_small"),
]


class JobFailure(Exception):
    """A classified, user-presentable failure."""

    def __init__(self, code: str, detail: str = ""):
        self.code = code if code in MESSAGES else "pipeline_failed"
        self.detail = detail
        super().__init__(self.code)

    @property
    def message(self) -> str:
        return MESSAGES[self.code]

    def as_tuple(self) -> Tuple[str, str]:
        return self.code, self.message


def classify(output: str, fallback: str = "pipeline_failed") -> str:
    """Maps captured pipeline output onto one of the codes above."""
    tail = (output or "")[-20000:]
    for pattern, code in PATTERNS:
        if pattern.search(tail):
            return code
    return fallback
