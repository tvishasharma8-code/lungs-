"""
In-memory job + progress store.

The project spec explicitly forbids a database: data is processed for
the current session only and deleted afterward. A process-local dict
is enough for a single-user research demo. If LungVision ever needs to
survive a server restart or serve multiple concurrent worker
processes, swap this for Redis and keep the same interface.
"""

from __future__ import annotations

import threading
import time
import uuid
from typing import Optional

from models.schemas import (
    AnalysisType,
    JobState,
    JobStatusResponse,
    PipelineProgress,
    ResultsResponse,
    StepProgress,
    StepStatus,
)

_lock = threading.RLock()


class Job:
    def __init__(self, job_id: str, session_id: str, analysis_type: AnalysisType):
        self.job_id = job_id
        self.session_id = session_id
        self.analysis_type = analysis_type
        self.state = JobState.PENDING
        self.started_at = time.time()
        self.error: Optional[str] = None
        self.pipelines: dict[str, PipelineProgress] = {}
        self.results = ResultsResponse(job_id=job_id)
        self.workspace_dir: Optional[str] = None  # scratch dir to clean up

        if analysis_type.wants_reconstruction:
            self.pipelines["reconstruction"] = PipelineProgress(
                key="reconstruction",
                label="3D Reconstruction Pipeline",
                steps=[
                    StepProgress(id="dicom_read", label="Reading DICOM series"),
                    StepProgress(id="dicom_to_nifti", label="Converting to NIfTI"),
                    StepProgress(id="segmentation", label="Segmenting lungs & airways"),
                    StepProgress(id="merge", label="Merging structures"),
                    StepProgress(id="smooth", label="Smoothing volume"),
                    StepProgress(id="mesh", label="Generating 3D mesh (STL)"),
                ],
            )
        if analysis_type.wants_abnormality:
            self.pipelines["abnormality"] = PipelineProgress(
                key="abnormality",
                label="AI Abnormality Detection Pipeline",
                steps=[
                    StepProgress(id="ab_prepare", label="Preparing CT data"),
                    StepProgress(id="ab_infer", label="Running AI model"),
                    StepProgress(id="ab_overlay", label="Generating detection overlay"),
                ],
            )

    def set_step(
        self,
        pipeline_key: str,
        step_id: str,
        status: StepStatus,
        detail: str = "",
        percent: Optional[int] = None,
    ) -> None:
        with _lock:
            pipeline = self.pipelines.get(pipeline_key)
            if not pipeline:
                return
            for step in pipeline.steps:
                if step.id == step_id:
                    step.status = status
                    if detail:
                        step.detail = detail
                    if percent is not None:
                        step.percent = percent
                    return

    def to_status_response(self) -> JobStatusResponse:
        with _lock:
            return JobStatusResponse(
                job_id=self.job_id,
                state=self.state,
                analysis_type=self.analysis_type,
                pipelines=list(self.pipelines.values()),
                elapsed_seconds=round(time.time() - self.started_at, 1),
                error=self.error,
                stl_ready=self.results.reconstruction is not None,
                abnormality_ready=self.results.abnormality is not None,
            )


class JobStore:
    def __init__(self):
        self._jobs: dict[str, Job] = {}

    def create(self, session_id: str, analysis_type: AnalysisType) -> Job:
        job_id = str(uuid.uuid4())
        job = Job(job_id, session_id, analysis_type)
        with _lock:
            self._jobs[job_id] = job
        return job

    def get(self, job_id: str) -> Optional[Job]:
        with _lock:
            return self._jobs.get(job_id)

    def delete(self, job_id: str) -> None:
        with _lock:
            self._jobs.pop(job_id, None)


job_store = JobStore()
