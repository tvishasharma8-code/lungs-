"""
Pydantic models shared across the LungVision backend.

Nothing here touches a database on purpose: per the project requirements
there is no login, no history, and no persistence beyond a single
in-memory job that is deleted once the session ends.
"""

from __future__ import annotations

from enum import Enum
from typing import Optional

from pydantic import BaseModel, Field


class AnalysisType(str, Enum):
    RECONSTRUCTION = "reconstruction"
    ABNORMALITY = "abnormality"
    BOTH = "both"

    @property
    def wants_reconstruction(self) -> bool:
        return self in (AnalysisType.RECONSTRUCTION, AnalysisType.BOTH)

    @property
    def wants_abnormality(self) -> bool:
        return self in (AnalysisType.ABNORMALITY, AnalysisType.BOTH)


class JobState(str, Enum):
    PENDING = "pending"
    RUNNING = "running"
    COMPLETED = "completed"
    FAILED = "failed"


class StepStatus(str, Enum):
    WAITING = "waiting"
    RUNNING = "running"
    COMPLETED = "completed"
    FAILED = "failed"


class StepProgress(BaseModel):
    id: str
    label: str
    detail: str = ""
    status: StepStatus = StepStatus.WAITING
    percent: Optional[int] = None  # 0-100, only set for long-running steps


class PipelineProgress(BaseModel):
    """One pipeline (reconstruction or abnormality) worth of steps."""
    key: str  # "reconstruction" | "abnormality"
    label: str
    steps: list[StepProgress]


class UploadResponse(BaseModel):
    session_id: str
    file_count: int
    message: str


class StartAnalysisRequest(BaseModel):
    session_id: str
    analysis_type: AnalysisType


class StartAnalysisResponse(BaseModel):
    job_id: str


class JobStatusResponse(BaseModel):
    job_id: str
    state: JobState
    analysis_type: AnalysisType
    pipelines: list[PipelineProgress]
    elapsed_seconds: float
    error: Optional[str] = None
    stl_ready: bool = False
    abnormality_ready: bool = False


class AbnormalityFinding(BaseModel):
    id: str
    label: str
    slice_index: int
    confidence: float
    bbox: list[float] = Field(
        description="Normalized [x_min, y_min, x_max, y_max] in 0..1 image space"
    )
    volume_mm3: Optional[float] = None
    note: str = ""


class TumorFinding(BaseModel):
    """One tumor: the per-slice detections that belong together (pipeline/tumor_cluster.py)."""
    id: str
    max_confidence: float
    mean_confidence: float
    slice_count: int
    slice_range: list[int] = Field(description="First and last slice index, body order")
    key_slice: int = Field(description="Slice where it is detected most confidently")
    key_bbox: list[float] = Field(description="Normalized [x_min, y_min, x_max, y_max] on key_slice")
    center_mm: Optional[list[float]] = Field(None, description="DICOM patient coords (LPS)")
    size_mm: Optional[list[float]] = None
    volume_mm3: Optional[float] = Field(None, description="Rough estimate from the boxes")
    location: Optional[str] = Field(None, description="Lobe (TotalSegmentator name) or 'outside the lungs'; "
                                                      "only when 3D reconstruction ran too")


class AbnormalityResult(BaseModel):
    mock: bool = True
    total_slices: int
    findings: list[AbnormalityFinding]
    summary: str
    tumors: list[TumorFinding] = []
    scanned_slices: Optional[int] = None


class ReconstructionResult(BaseModel):
    stl_url: str
    vertex_count: int
    face_count: int
    file_size_bytes: int
    included_structures: list[str]


class ResultsResponse(BaseModel):
    job_id: str
    reconstruction: Optional[ReconstructionResult] = None
    abnormality: Optional[AbnormalityResult] = None
