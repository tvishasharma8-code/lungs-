"""
Orchestrates a job: runs only the pipeline(s) the user selected,
in a background thread, and cleans up all temporary files afterward
regardless of success or failure (no history is kept per the spec).
"""

from __future__ import annotations

import os
import shutil
import tempfile
import threading

from models.schemas import AnalysisType, JobState
from pipeline.run_totalseg import PipelineError
from services import stl_service, tumor_service
from services.progress_service import Job, job_store

# Runtime scratch space lives OUTSIDE the backend/ source tree on purpose:
# uvicorn --reload watches backend/ for changes, and writing uploaded
# DICOM files or generated STL/NIfTI output under backend/temp/ would
# trigger a reload (and kill in-flight requests) every time a file
# landed on disk. Using the OS temp dir avoids that entirely.
_RUNTIME_ROOT = os.path.join(tempfile.gettempdir(), "lungvision")
UPLOAD_ROOT = os.path.join(_RUNTIME_ROOT, "uploads")
WORKSPACE_ROOT = os.path.join(_RUNTIME_ROOT, "workspace")
OUTPUT_ROOT = os.path.join(_RUNTIME_ROOT, "output")

for d in (UPLOAD_ROOT, WORKSPACE_ROOT, OUTPUT_ROOT):
    os.makedirs(d, exist_ok=True)


def session_upload_dir(session_id: str) -> str:
    return os.path.join(UPLOAD_ROOT, session_id)


def start_job(session_id: str, analysis_type: AnalysisType) -> Job:
    dicom_dir = session_upload_dir(session_id)
    if not os.path.isdir(dicom_dir) or not os.listdir(dicom_dir):
        raise FileNotFoundError("No uploaded DICOM files found for this session.")

    job = job_store.create(session_id, analysis_type)
    job.workspace_dir = os.path.join(WORKSPACE_ROOT, job.job_id)

    thread = threading.Thread(target=_run_job, args=(job, dicom_dir), daemon=True)
    thread.start()
    return job


def _run_job(job: Job, dicom_dir: str) -> None:
    job.state = JobState.RUNNING
    out_stl_path = os.path.join(OUTPUT_ROOT, f"{job.job_id}.stl")

    try:
        if job.analysis_type.wants_reconstruction:
            stl_service.run_reconstruction(
                job,
                dicom_dir=dicom_dir,
                workspace_dir=job.workspace_dir,
                out_stl_path=out_stl_path,
            )

        if job.analysis_type.wants_abnormality:
            tumor_service.run_abnormality_detection(job, dicom_dir)

        job.state = JobState.COMPLETED
    except PipelineError as exc:
        job.state = JobState.FAILED
        job.error = str(exc)
    except Exception as exc:  # noqa: BLE001 - surface anything unexpected
        job.state = JobState.FAILED
        job.error = f"Unexpected error: {exc}"
    finally:
        # Always clean the scratch workspace (NIfTI + raw seg masks).
        # The final STL in OUTPUT_ROOT is kept until the job/session is
        # explicitly reset via cleanup_session(), so the user can still
        # download it.
        if job.workspace_dir and os.path.isdir(job.workspace_dir):
            shutil.rmtree(job.workspace_dir, ignore_errors=True)


def cleanup_session(session_id: str, job_id: str | None = None) -> None:
    """Deletes all uploaded DICOMs, workspace data, and generated
    outputs for a session. Called on 'New Analysis' / reset."""
    upload_dir = session_upload_dir(session_id)
    if os.path.isdir(upload_dir):
        shutil.rmtree(upload_dir, ignore_errors=True)

    if job_id:
        job = job_store.get(job_id)
        if job and job.workspace_dir and os.path.isdir(job.workspace_dir):
            shutil.rmtree(job.workspace_dir, ignore_errors=True)
        out_stl_path = os.path.join(OUTPUT_ROOT, f"{job_id}.stl")
        if os.path.exists(out_stl_path):
            os.remove(out_stl_path)
        job_store.delete(job_id)
