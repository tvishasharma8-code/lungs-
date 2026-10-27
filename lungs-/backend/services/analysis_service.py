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
import traceback

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


def vr_output_dir(job_id: str) -> str:
    """The job's VR outputs: mesh.bin + meta.json (reconstruction), findings.json +
    slices/ (detection). "Send to VR" copies them into a Patient Package."""
    return os.path.join(OUTPUT_ROOT, f"{job_id}_vr")


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
    """
    Runs each selected pipeline on its own: one failing doesn't throw away the other.
    Detection needs nothing from the reconstruction except the lobe masks (without
    them tumors just get no lobe), and a finished 3D model is still worth showing and
    sending to VR when detection fails. The job completes if any result came out; the
    parts that failed are listed in results.warnings and marked on their progress step.
    Still one after the other, not in parallel: detection reads the reconstruction's
    lobe masks, and TotalSegmentator alone already needs most of the memory.
    """
    job.state = JobState.RUNNING
    out_stl_path = os.path.join(OUTPUT_ROOT, f"{job.job_id}.stl")
    errors: list[tuple[str, str]] = []

    try:
        if job.analysis_type.wants_reconstruction:
            _run_part(job, "reconstruction", "3D reconstruction", errors, lambda: stl_service.run_reconstruction(
                job,
                dicom_dir=dicom_dir,
                workspace_dir=job.workspace_dir,
                out_stl_path=out_stl_path,
            ))

        if job.analysis_type.wants_abnormality:
            _run_part(job, "abnormality", "Tumor detection", errors, lambda: tumor_service.run_abnormality_detection(
                job, dicom_dir, vr_output_dir(job.job_id)))

        if job.results.reconstruction is not None or job.results.abnormality is not None:
            job.results.warnings = [f"{label} failed: {msg}" for label, msg in errors]
            job.state = JobState.COMPLETED
        else:
            job.error = errors[0][1] if len(errors) == 1 else "; ".join(f"{l}: {m}" for l, m in errors)
            job.state = JobState.FAILED
    finally:
        # Always clean the scratch workspace (NIfTI + raw seg masks).
        # The final STL in OUTPUT_ROOT is kept until the job/session is
        # explicitly reset via cleanup_session(), so the user can still
        # download it.
        if job.workspace_dir and os.path.isdir(job.workspace_dir):
            shutil.rmtree(job.workspace_dir, ignore_errors=True)


def _run_part(job: Job, key: str, label: str, errors: list, run) -> None:
    try:
        run()
    except PipelineError as exc:
        msg = str(exc)
    except Exception as exc:  # noqa: BLE001 - surface anything unexpected
        traceback.print_exc()
        msg = f"Unexpected error: {exc}"
    else:
        return
    print(f"[job {job.job_id}] {label} failed: {msg}")
    job.fail_pipeline(key, msg)
    errors.append((label, msg))


def cleanup_session(session_id: str, job_id: str | None = None) -> None:
    """Deletes all uploaded DICOMs, workspace data, and generated
    outputs for a session. Called on 'New Analysis' / reset."""
    upload_dir = session_upload_dir(session_id)
    if os.path.isdir(upload_dir):
        shutil.rmtree(upload_dir, ignore_errors=True)

    # Every job of the session, not only the last: after a failed run and a retry the
    # frontend only knows the newest job id, and the earlier job's outputs were left behind.
    job_ids = {j.job_id for j in job_store.for_session(session_id)}
    if job_id:
        job_ids.add(job_id)
    for jid in job_ids:
        job = job_store.get(jid)
        if job and job.workspace_dir and os.path.isdir(job.workspace_dir):
            shutil.rmtree(job.workspace_dir, ignore_errors=True)
        out_stl_path = os.path.join(OUTPUT_ROOT, f"{jid}.stl")
        if os.path.exists(out_stl_path):
            os.remove(out_stl_path)
        shutil.rmtree(vr_output_dir(jid), ignore_errors=True)
        job_store.delete(jid)
