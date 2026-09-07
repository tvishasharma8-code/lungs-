"""
LungVision backend.

Endpoints:
    POST   /api/upload                       upload a DICOM folder (multi-file, webkitdirectory)
    POST   /api/analyze                      start a job for a session (reconstruction | abnormality | both)
    GET    /api/status/{job_id}               poll live progress
    GET    /api/results/{job_id}              fetch final results once completed
    GET    /api/download/{job_id}/stl         download the real generated STL
    GET    /api/slice-image/{job_id}/{n}      render CT slice n as a PNG (for overlaying findings)
    POST   /api/reset                         delete all data for a session (no history is kept)

No login, no database. Uploaded files and generated outputs live only
under backend/temp/ and are removed by /api/reset (also called
automatically by the frontend's "New Analysis" button).
"""

from __future__ import annotations

import inspect
import io
import os
import shutil
import uuid

from fastapi import FastAPI, HTTPException, Request
from fastapi.middleware.cors import CORSMiddleware
from fastapi.responses import FileResponse, Response
from PIL import Image
from pydicom import dcmread

from models.schemas import (
    JobState,
    JobStatusResponse,
    ResultsResponse,
    StartAnalysisRequest,
    StartAnalysisResponse,
    UploadResponse,
)
from pipeline.tumor_infer import list_series_files, normalize_to_grayscale_uint8
from services import analysis_service
from services.progress_service import job_store

app = FastAPI(title="LungVision API", version="0.1.0")

app.add_middleware(
    CORSMiddleware,
    allow_origins=["http://localhost:5173"],
    allow_credentials=True,
    allow_methods=["*"],
    allow_headers=["*"],
)

VALID_DICOM_EXTENSIONS = {".dcm", ""}  # DICOM files often have no extension


@app.get("/api/health")
def health() -> dict:
    return {"status": "ok"}


async def _parse_upload_form(request: Request):
    """
    Wraps request.form() with raised multipart limits, but stays
    compatible across Starlette versions: max_part_size was only added
    in newer Starlette releases, so we detect support for it instead
    of hard-coding the kwarg and breaking on older installs.
    """
    kwargs = {"max_files": 20_000, "max_fields": 20_000}
    if "max_part_size" in inspect.signature(request.form).parameters:
        kwargs["max_part_size"] = 50 * 1024 * 1024  # 50MB per file
    return await request.form(**kwargs)


@app.post("/api/upload", response_model=UploadResponse)
async def upload_dicom(request: Request) -> UploadResponse:
    # NOTE: we parse the multipart form manually (rather than using
    # FastAPI's `files: list[UploadFile] = File(...)` dependency) so we
    # can raise Starlette's default multipart limits. A full CT series
    # is commonly 500-2000+ slice files, and any individual slice can
    # exceed 1MB at higher resolutions - both blow past Starlette's
    # defaults (max_files=1000, max_fields=1000, and on newer versions
    # max_part_size=1MB), which otherwise silently reject the upload
    # with a 400.
    form = await _parse_upload_form(request)
    uploads = form.getlist("files")

    if not uploads:
        raise HTTPException(status_code=400, detail="No files were uploaded.")

    session_id = str(uuid.uuid4())
    dest_dir = analysis_service.session_upload_dir(session_id)
    os.makedirs(dest_dir, exist_ok=True)

    saved = 0
    for upload in uploads:
        filename = os.path.basename(getattr(upload, "filename", "") or "")
        if not filename:
            continue
        dest_path = os.path.join(dest_dir, filename)
        with open(dest_path, "wb") as out:
            shutil.copyfileobj(upload.file, out)
        saved += 1

    if saved == 0:
        shutil.rmtree(dest_dir, ignore_errors=True)
        raise HTTPException(status_code=400, detail="No valid files were found in the upload.")

    return UploadResponse(
        session_id=session_id,
        file_count=saved,
        message=f"{saved} files uploaded successfully.",
    )


@app.post("/api/analyze", response_model=StartAnalysisResponse)
def start_analysis(request: StartAnalysisRequest) -> StartAnalysisResponse:
    try:
        job = analysis_service.start_job(request.session_id, request.analysis_type)
    except FileNotFoundError as exc:
        raise HTTPException(status_code=404, detail=str(exc)) from exc
    return StartAnalysisResponse(job_id=job.job_id)


@app.get("/api/status/{job_id}", response_model=JobStatusResponse)
def get_status(job_id: str) -> JobStatusResponse:
    job = job_store.get(job_id)
    if not job:
        raise HTTPException(status_code=404, detail="Job not found.")
    return job.to_status_response()


@app.get("/api/results/{job_id}", response_model=ResultsResponse)
def get_results(job_id: str) -> ResultsResponse:
    job = job_store.get(job_id)
    if not job:
        raise HTTPException(status_code=404, detail="Job not found.")
    if job.state != JobState.COMPLETED:
        raise HTTPException(status_code=409, detail=f"Job is not completed yet (state={job.state.value}).")
    return job.results


@app.get("/api/download/{job_id}/stl")
def download_stl(job_id: str):
    job = job_store.get(job_id)
    if not job or not job.results.reconstruction:
        raise HTTPException(status_code=404, detail="STL not available for this job.")
    stl_path = os.path.join(analysis_service.OUTPUT_ROOT, f"{job_id}.stl")
    if not os.path.exists(stl_path):
        raise HTTPException(status_code=404, detail="STL file is missing on disk.")
    return FileResponse(
        stl_path,
        media_type="model/stl",
        filename="lungvision_lungs_with_trachea.stl",
    )


@app.get("/api/slice-image/{job_id}/{slice_index}")
def get_slice_image(job_id: str, slice_index: int):
    """
    Renders one CT slice as a PNG, normalized exactly the way the tumor
    detection model sees it (see tumor_infer.normalize_to_grayscale_uint8),
    so the frontend can draw a finding's bounding box directly on top of
    the actual image the model was looking at - not just report numbers.

    Uses pipeline.tumor_infer.list_series_files() for ordering, the same
    function run_inference_on_series() used to assign slice_index values
    in the first place - if these ever used different orderings,
    slice_index N here could silently point at the wrong physical slice.
    """
    job = job_store.get(job_id)
    if not job:
        raise HTTPException(status_code=404, detail="Job not found.")

    dicom_dir = analysis_service.session_upload_dir(job.session_id)
    if not os.path.isdir(dicom_dir):
        raise HTTPException(
            status_code=404,
            detail="Source DICOM files are no longer available for this session.",
        )

    files = list_series_files(dicom_dir)
    if slice_index < 0 or slice_index >= len(files):
        raise HTTPException(status_code=404, detail="Slice index out of range.")

    dcm = dcmread(files[slice_index])
    gray = normalize_to_grayscale_uint8(dcm.pixel_array)
    img = Image.fromarray(gray).convert("L")

    buf = io.BytesIO()
    img.save(buf, format="PNG")
    return Response(content=buf.getvalue(), media_type="image/png")


@app.post("/api/reset")
def reset(session_id: str, job_id: str | None = None) -> dict:
    analysis_service.cleanup_session(session_id, job_id)
    return {"status": "cleared"}