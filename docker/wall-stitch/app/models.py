"""Wire models. Field names are camelCase because the .NET client is written against them."""
from __future__ import annotations

from typing import List, Literal, Optional

from pydantic import BaseModel, ConfigDict, Field
from pydantic.alias_generators import to_camel


class Wire(BaseModel):
    """camelCase on the wire, snake_case in Python, and both accepted on input.

    The .NET client serialises with JsonNamingPolicy.CamelCase, so every field name
    here is generated rather than spelled twice; `populate_by_name` keeps the Python
    spelling usable when this service builds its own models.
    """

    model_config = ConfigDict(alias_generator=to_camel, populate_by_name=True,
                              extra="ignore", validate_by_name=True, validate_by_alias=True)


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


class JobOptions(Wire):
    wall_angle_degrees: float = Field(default=45.0)
    default_projection: Literal["angled", "ortho"] = Field(default="angled")
    transfer_holds: bool = Field(default=False)
    old_photo_width: Optional[int] = Field(default=None)
    old_photo_height: Optional[int] = Field(default=None)
    holds: List[HoldInput] = Field(default_factory=list)


class ArtifactRef(Wire):
    artifact: str
    width: int
    height: int


class RejectedImage(Wire):
    name: str
    reason: str


class Diagnostics(Wire):
    images_used: List[str] = Field(default_factory=list)
    images_rejected: List[RejectedImage] = Field(default_factory=list)
    seam_angle_rms_deg: float = Field(default=0.0)
    bow_median_px: float = Field(default=0.0)
    coverage_warnings: List[str] = Field(default_factory=list)


class ResultHold(Wire):
    id: str
    x: float
    y: float
    radius: float
    shape_points: Optional[List[ShapePoint]] = Field(default=None)
    classification: str
    confidence: float


class JobResult(Wire):
    ortho: ArtifactRef
    angled: ArtifactRef
    display_ortho: str
    display_angled: str
    wall_angle_degrees: float
    vertical_scale: float
    diagnostics: Optional[Diagnostics] = None
    holds: Optional[List[ResultHold]] = None


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
