"""
Wraps pipeline.run_totalseg.run_pipeline for use inside a FastAPI
background job. This is the ONLY place that touches the real,
non-mocked segmentation pipeline.
"""

from __future__ import annotations

import os

from models.schemas import ReconstructionResult, StepStatus
from pipeline.run_totalseg import PipelineError, run_pipeline
from services.progress_service import Job

PIPELINE_KEY = "reconstruction"

# Maps a pipeline step id to the previous step id, so we can mark the
# previous step "completed" the moment a new one starts.
STEP_ORDER = [
    "dicom_read",
    "dicom_to_nifti",
    "segmentation",
    "merge",
    "smooth",
    "mesh",
]


def run_reconstruction(job: Job, dicom_dir: str, workspace_dir: str, out_stl_path: str) -> None:
    """
    Runs the real DICOM -> STL pipeline for `job` and stores the result
    on job.results.reconstruction. Raises on failure; the caller
    (analysis_service) is responsible for catching and marking the job
    failed.
    """
    active_step_index = -1

    def log(msg: str) -> None:
        print(f"[job {job.job_id}] {msg}")

    def on_progress(step_id: str, detail: str, percent) -> None:
        nonlocal active_step_index
        if step_id in STEP_ORDER:
            idx = STEP_ORDER.index(step_id)
            # mark any earlier steps completed in case we skipped an update
            for earlier in STEP_ORDER[:idx]:
                job.set_step(PIPELINE_KEY, earlier, StepStatus.COMPLETED, percent=100)
            active_step_index = idx

        status = StepStatus.COMPLETED if percent == 100 else StepStatus.RUNNING
        job.set_step(PIPELINE_KEY, step_id, status, detail=detail, percent=percent)

    try:
        result = run_pipeline(
            dicom_dir=dicom_dir,
            workspace_dir=workspace_dir,
            out_stl_path=out_stl_path,
            log=log,
            progress=on_progress,
        )
    except PipelineError:
        raise
    except Exception as exc:  # surface unexpected errors with context
        raise PipelineError(f"Reconstruction pipeline failed: {exc}") from exc

    for step_id in STEP_ORDER:
        job.set_step(PIPELINE_KEY, step_id, StepStatus.COMPLETED, percent=100)

    job.results.reconstruction = ReconstructionResult(
        stl_url=f"/api/download/{job.job_id}/stl",
        vertex_count=result.vertex_count,
        face_count=result.face_count,
        file_size_bytes=os.path.getsize(out_stl_path),
        included_structures=result.included_structures,
    )
