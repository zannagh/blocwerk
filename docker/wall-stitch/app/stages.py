"""Real progress, derived from the pipeline's own stdout.

The pipeline narrates what it is doing on stdout. Each marker below is a line it
prints when it *finishes* a step, so progress only moves on real work. Markers are
matched in order and progress is monotonic; an unrecognised line leaves the reported
progress alone rather than inventing motion.

Carryover, when it runs, numbers its own steps `[n]` and is folded into the tail of the
budget by the same tracker.
"""
from __future__ import annotations

import re
from dataclasses import dataclass
from typing import List


#: The pipeline stamps every line with its own elapsed time (`[  12.3s] ...`). Strip it
#: before matching, so the markers can anchor on what the line actually says.
TIMESTAMP = re.compile(r"^\s*\[\s*[\d.]+s\]\s?")


@dataclass(frozen=True)
class Marker:
    pattern: re.Pattern
    fraction: float
    stage: str


# Fractions are within the whole run (0..1); the runner rescales them into its budget.
# Registration dominates the wall clock on a real sweep - features and pairwise
# matching are most of it - so it gets most of the budget rather than an even split.
PIPELINE_MARKERS: List[Marker] = [
    Marker(re.compile(r"^\s*\d+ frames, reference "), 0.01, "reading"),
    Marker(re.compile(r"features for \d+ frames"), 0.03, "reading"),
    Marker(re.compile(r"^\s*feat \S+: \d"), 0.10, "matching"),
    Marker(re.compile(r"^\s*pair \S+-\S+: \d"), 0.25, "matching"),
    Marker(re.compile(r"\bconnected pairs\b"), 0.45, "matching"),
    Marker(re.compile(r"^refining \d+ params"), 0.48, "registering"),
    Marker(re.compile(r"^transfer rms "), 0.60, "registering"),
    Marker(re.compile(r"\bcanvas \d+x\d+ \("), 0.63, "blending"),
    Marker(re.compile(r"^seam-scale warps:"), 0.68, "blending"),
    Marker(re.compile(r"^exposure gains fed"), 0.71, "blending"),
    Marker(re.compile(r"^seams found,"), 0.74, "blending"),
    Marker(re.compile(r"^composited \d+ frames"), 0.80, "blending"),
    Marker(re.compile(r"^silhouette \d+ verts"), 0.82, "blending"),
    Marker(re.compile(r"^flat-base \d+x\d+"), 0.84, "projecting"),
    Marker(re.compile(r"^wall polygon: \d+ vertices"), 0.86, "detecting"),
    Marker(re.compile(r"^\s*tile \d+ -> \d+ raw"), 0.88, "detecting"),
    Marker(re.compile(r"^flat done: \d+ holds"), 0.92, "detecting"),
    Marker(re.compile(r"^\[[1-4]\] "), 0.94, "matching holds"),
    Marker(re.compile(r"^\[[5-7]\] "), 0.96, "matching holds"),
    Marker(re.compile(r"^manifest "), 0.99, "packaging"),
]


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
        line = TIMESTAMP.sub("", line)
        for marker in self.markers:
            if marker.pattern.search(line):
                moved = marker.fraction > self.fraction or marker.stage != self.stage
                self.fraction = max(self.fraction, marker.fraction)
                self.stage = marker.stage
                return moved
        return False
