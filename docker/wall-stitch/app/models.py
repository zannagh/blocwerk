"""Wire models. Field names are camelCase because the .NET client is written against them."""
from __future__ import annotations

from typing import Dict, List, Literal, Optional

from pydantic import BaseModel, ConfigDict, Field
from pydantic.alias_generators import to_camel

# Every normalised coordinate below follows the pipeline's own convention, which is also
# the app's: X = px / imageWidth, Y = px / imageHeight, both 0..1, origin top-left, the
# two axes normalised INDEPENDENTLY (so the aspect ratio is not preserved), and Radius
# normalised against the LONGER side. Holds are normalised against the FLAT master
# unless the field name says natural.
COORDINATE_CONVENTION = (
    "X = px/imageWidth, Y = px/imageHeight, origin top-left, axes normalised "
    "independently; Radius against the longer side; shapePoints are dx/dy offsets "
    "from (x, y) in the same space."
)


class Wire(BaseModel):
    """camelCase on the wire, snake_case in Python, and both accepted on input.

    The .NET client serialises with JsonNamingPolicy.CamelCase, so every field name
    here is generated rather than spelled twice; `populate_by_name` keeps the Python
    spelling usable when this service builds its own models.
    """

    model_config = ConfigDict(alias_generator=to_camel, populate_by_name=True,
                              extra="ignore", validate_by_name=True, validate_by_alias=True)


# ---- request -------------------------------------------------------------------

class ShapePoint(Wire):
    dx: float
    dy: float


class HoldInput(Wire):
    id: str
    x: float
    y: float
    radius: float = 0.0
    shape_points: Optional[List[ShapePoint]] = Field(default=None)
    color: Optional[str] = None
    category: int = 0
    boulder_link_count: int = Field(default=0)
    generation: int = Field(default=0)


class JobOptions(Wire):
    """What the app asks for.

    `natural` picks the display projection and is deliberately an open string rather
    than an enum: which projection the wall should get is an unsettled product
    question, and a new candidate must not require a redeploy of both sides to try.
    The sidecar falls back to its configured default when this is empty.
    """

    natural: str = Field(default="")
    curve: Literal["gentle", "medium", "strong"] = Field(default="gentle")
    wall_width_m: float = Field(default=5.5)
    wall_height_m: float = Field(default=2.5)
    transfer_holds: bool = Field(default=False)
    old_photo_width: Optional[int] = Field(default=None)
    old_photo_height: Optional[int] = Field(default=None)
    holds: List[HoldInput] = Field(default_factory=list)


# ---- result --------------------------------------------------------------------

class ArtifactRef(Wire):
    artifact: str
    width: int
    height: int


class CurveRef(Wire):
    """One emitted variant of the natural master.

    The curvature fields are optional because they only mean anything under a curved
    projection; a flat or panoramic view emits a variant with a name and a size and
    nothing else.
    """

    name: str
    artifact: str
    display: str
    width: int
    height: int
    theta_max_deg: Optional[float] = None
    k: Optional[float] = None
    radius_m: Optional[float] = None


class Curvature(Wire):
    """The display views the pipeline rendered, and which projection made them."""

    projection: str = ""
    default: str
    requested_theta_max_deg: float = 0.0
    view_dist_m: float = 0.0
    eye_frac: float = 0.0
    curves: List[CurveRef] = Field(default_factory=list)


class DetectedHold(Wire):
    """A hold the detector found, normalised against the flat master."""

    id: str
    x: float
    y: float
    radius: float
    confidence: float


class CarriedHold(Wire):
    """One of the wall's existing holds, placed on the new flat master.

    `classification` is CARRIED_OVER when a detection claimed it and MISSING when none
    did; a MISSING hold still carries its transferred position, so the app can show
    where it used to be.
    """

    id: str
    x: float
    y: float
    radius: float
    shape_points: Optional[List[ShapePoint]] = Field(default=None)
    classification: Literal["CARRIED_OVER", "MISSING"]
    matched_detection_id: Optional[str] = None
    match_distance_px: Optional[float] = None
    colour_agrees: Optional[bool] = None
    boulder_link_count: int = 0
    in_frame: bool = True
    reason: str = ""


class NewHold(Wire):
    """A detection on the new master that no existing hold claimed."""

    detection_id: str
    x: float
    y: float
    radius: float
    confidence: float
    likely_duplicate: bool = False


class Carryover(Wire):
    """The match of the wall's current holds onto the new master.

    `blocker` is the pipeline's own standing caveat about the accuracy of this match.
    It is non-empty whenever carryover ran, and the app must not apply the result
    unattended while it is: show the operator the overlay and let them confirm.
    """

    generation: int
    counts: Dict[str, int] = Field(default_factory=dict)
    counts_boulder_linked: Dict[str, int] = Field(default_factory=dict)
    estimated_precision: float = 0.0
    blocker: str = ""
    carried: List[CarriedHold] = Field(default_factory=list)
    missing: List[CarriedHold] = Field(default_factory=list)
    new: List[NewHold] = Field(default_factory=list)


class RejectedImage(Wire):
    name: str
    reason: str


class Diagnostics(Wire):
    images_used: List[str] = Field(default_factory=list)
    images_rejected: List[RejectedImage] = Field(default_factory=list)
    reference_frame: str = ""
    straightness: float = Field(default=0.0)
    inpainted_px: int = Field(default=0)
    elapsed_seconds: float = Field(default=0.0)
    coverage_warnings: List[str] = Field(default_factory=list)


class JobResult(Wire):
    """What the sidecar hands back for a finished job.

    `flatMaster` is the metric, fronto-parallel composite: the surface every hold
    coordinate in this result is normalised against, and the one to re-run detection or
    carryover on. `naturalMaster` is the curved photographic view for display, at the
    curvature named by `curvature.default`.
    """

    flat_master: ArtifactRef
    natural_master: ArtifactRef
    display_flat: str
    display_natural: str
    cameras_json: str
    coordinate_convention: str = COORDINATE_CONVENTION
    wall_width_m: float = 0.0
    wall_height_m: float = 0.0
    curvature: Optional[Curvature] = None
    holds: Optional[List[DetectedHold]] = None
    holds_natural: Optional[List[DetectedHold]] = None
    carryover: Optional[Carryover] = None
    diagnostics: Optional[Diagnostics] = None


class JobError(Wire):
    code: str
    message: str


class JobCreated(Wire):
    job_id: str
    status: str


class JobState(Wire):
    job_id: str
    status: Literal["queued", "running", "succeeded", "failed"]
    progress: float = 0.0
    stage: Optional[str] = None
    error: Optional[JobError] = None
    result: Optional[JobResult] = None
