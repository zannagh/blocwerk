"""Real progress, derived from the pipeline's own stdout.

The stitcher and the hold matcher both narrate what they are doing on stdout. Each
marker below is a line the pipeline prints when it *finishes* a step, so progress
only moves on real work. Markers are matched in order and progress is monotonic;
an unrecognised line leaves the reported progress alone rather than inventing motion.
"""
from __future__ import annotations

import re
from dataclasses import dataclass
from typing import List, Optional, Tuple


@dataclass(frozen=True)
class Marker:
    pattern: re.Pattern
    fraction: float
    stage: str


# Fractions are within the stitch phase (0..1); the runner rescales them.
STITCH_MARKERS: List[Marker] = [
    Marker(re.compile(r"plumb-line distortion calibration"), 0.02, "calibrating"),
    Marker(re.compile(r"\bk1=[-\d.]"), 0.08, "calibrating"),
    Marker(re.compile(r"undistorted \+ masked"), 0.18, "undistorting"),
    Marker(re.compile(r"\bpair \S+-\S+:"), 0.30, "registering"),
    Marker(re.compile(r"global refinement:"), 0.42, "registering"),
    Marker(re.compile(r"plane normal"), 0.50, "rectifying"),
    Marker(re.compile(r"seam chains on the wall"), 0.56, "rectifying"),
    Marker(re.compile(r"best-source sampling density"), 0.60, "rectifying"),
    Marker(re.compile(r"\bcanvas \d+x\d+"), 0.64, "blending"),
    Marker(re.compile(r"exposure gains"), 0.70, "blending"),
    Marker(re.compile(r"composited \d+x\d+"), 0.82, "blending"),
    Marker(re.compile(r"\bcrop: \d"), 0.88, "blending"),
    Marker(re.compile(r"\bangled (?:main-span|kickboard)\b"), 0.94, "projecting"),
    Marker(re.compile(r"report ->"), 0.99, "projecting"),
]

# The hold matcher prints "[n/7] label".
HOLDS_STEP = re.compile(r"^\s*\[(\d+)(?:b)?/(\d+)\]\s*(.*)$")


class ProgressTracker:
    """Folds pipeline stdout lines into a monotonic (progress, stage) pair."""

    def __init__(self, markers: List[Marker], lo: float, hi: float, default_stage: str):
        self.markers = markers
        self.lo = lo
        self.hi = hi
        self.stage = default_stage
        self.fraction = 0.0

    @property
    def progress(self) -> float:
        return round(self.lo + (self.hi - self.lo) * self.fraction, 4)

    def feed(self, line: str) -> bool:
        """Returns True when the line moved progress or changed the stage."""
        for marker in self.markers:
            if marker.pattern.search(line):
                moved = marker.fraction > self.fraction or marker.stage != self.stage
                self.fraction = max(self.fraction, marker.fraction)
                self.stage = marker.stage
                return moved
        return False


class HoldsProgressTracker(ProgressTracker):
    """The matcher numbers its own steps, so read the numbers instead of guessing."""

    def __init__(self, lo: float, hi: float):
        super().__init__([], lo, hi, "matching")

    def feed(self, line: str) -> bool:
        match = HOLDS_STEP.match(line)
        if not match:
            return False
        step, total = int(match.group(1)), max(1, int(match.group(2)))
        fraction = min(1.0, step / total)
        moved = fraction > self.fraction
        self.fraction = max(self.fraction, fraction)
        return moved


def split_phases(transfer_holds: bool) -> Tuple[Tuple[float, float], Optional[Tuple[float, float]]]:
    """Progress budget for the stitch phase and, when asked for, the hold-transfer phase."""
    if transfer_holds:
        return (0.02, 0.72), (0.72, 0.97)
    return (0.02, 0.97), None
