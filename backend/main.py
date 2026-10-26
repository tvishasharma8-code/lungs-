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

  VR app (Patient Packages - see services/vr_service.py):
    POST   /api/vr/send/{job_id}              keep this job's 3D model + findings for VR, make it active
    GET    /api/vr/patients                   list kept patients
    GET    /api/vr/active                     {"id": ...} the patient VR should show
    POST   /api/vr/active/{patient_id}        choose the patient VR should show
    GET    /api/vr/patients/{id}/{file}       meta.json | mesh.bin | findings.json
    GET    /api/vr/patients/{id}/slices/NNN.png  flagged CT slice NNN, boxes drawn
    DELETE /api/vr/patients/{id}              remove a kept patient

  Desktop Unity app (see services/unity_service.py; only from this PC):
    GET    /api/unity/status                  {"built": ..., "running": ..., "editor": ...}
    POST   /api/unity/view/{job_id}           send the job's model to VR (if not yet), make it
                                              active, open the desktop app if it isn't open
                                              (no build: the open Unity Editor shows it)

No login, no database. Uploaded files and generated outputs live only
in the OS temp folder and are removed by /api/reset (also called
automatically by the frontend's "New Analysis" button). The one
exception is "Send to VR", which the user chooses explicitly.

For a standalone Quest to reach this server, run it on the network:
    uvicorn main:app --host 0.0.0.0 --port 8000
"""

from __future__ import annotations

import inspect
import io
import os
import re
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
from pipeline.dicom_series import safe_relative_path
from pipeline.tumor_infer import list_series_files, normalize_to_grayscale_uint8
from services import analysis_service, unity_service, vr_service
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
        # Keep the path inside the chosen folder (the frontend sends
        # webkitRelativePath). Saving by base name alone let same-named files
        # from different series sub-folders overwrite each other.
        rel = safe_relative_path(getattr(upload, "filename", "") or "")
        if not rel:
            continue
        dest_path = os.path.join(dest_dir, rel)
        os.makedirs(os.path.dirname(dest_path), exist_ok=True)
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


# ---------------------------------------------------------------- VR app

# Job ids are uuid4 strings; checked before one is used in a folder name.
_JOB_ID_RE = re.compile(r"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$")


@app.post("/api/vr/send/{job_id}")
def vr_send(job_id: str, label: str | None = None) -> dict:
    job = job_store.get(job_id)
    vr_dir = analysis_service.vr_output_dir(job_id) if _JOB_ID_RE.match(job_id) else None
    if not job:
        # Jobs live in memory, so a backend restart forgets them - but a finished job's
        # model is still on disk until "New Analysis", and that is all a package needs.
        if not vr_dir or not os.path.isfile(os.path.join(vr_dir, "mesh.bin")):
            raise HTTPException(status_code=404, detail="Job not found.")
    elif job.state != JobState.COMPLETED or not job.results.reconstruction:
        raise HTTPException(status_code=409, detail="Send to VR needs a completed 3D reconstruction.")
    try:
        return vr_service.create_package(job_id, vr_dir, findings=None, label=label)
    except FileNotFoundError as exc:
        raise HTTPException(status_code=404, detail=str(exc)) from exc


def _require_local(request: Request) -> None:
    # Starting a program is for the person at this PC, not for others on the network
    # when the backend is bound to 0.0.0.0 for a Quest.
    if not request.client or request.client.host not in ("127.0.0.1", "::1", "localhost"):
        raise HTTPException(status_code=403, detail="View in Unity only works from the PC running LungVision.")


@app.get("/api/unity/status")
def unity_status() -> dict:
    return {"built": unity_service.is_built(), "running": unity_service.is_running(),
            "editor": unity_service.editor_running()}


@app.post("/api/unity/view/{job_id}")
def unity_view(job_id: str, request: Request, label: str | None = None) -> dict:
    _require_local(request)
    patient_id = vr_service.package_id_for_job(job_id)
    known = any(p.get("id") == patient_id for p in vr_service.list_packages())
    patient = vr_send(job_id, label) if not known else {"id": patient_id}
    if known:
        vr_service.set_active(patient_id)
    # The patient is active now, so anything following it (headset, Editor in Play mode,
    # desktop app) switches to it. Then open the desktop app if there is one.
    if not unity_service.is_built():
        if unity_service.editor_running():
            return {"id": patient["id"], "started": False, "mode": "editor"}
        raise HTTPException(status_code=404, detail=(
            "The Unity desktop app isn't built and the Unity Editor isn't open. Open the "
            "project in Unity and press Play, or build the app once: BioGears > Build "
            "Desktop App (Windows)."))
    port = request.scope.get("server", (None, 8000))[1] or 8000
    try:
        started = unity_service.open_app(f"http://127.0.0.1:{port}")
    except (RuntimeError, OSError) as exc:
        raise HTTPException(status_code=500, detail=str(exc)) from exc
    return {"id": patient["id"], "started": started, "mode": "app"}


@app.get("/api/vr/patients")
def vr_patients() -> list[dict]:
    return vr_service.list_packages()


@app.get("/api/vr/active")
def vr_active() -> dict:
    return {"id": vr_service.get_active()}


@app.post("/api/vr/active/{patient_id}")
def vr_set_active(patient_id: str) -> dict:
    try:
        vr_service.set_active(patient_id)
    except vr_service.PackageError as exc:
        raise HTTPException(status_code=400, detail=str(exc)) from exc
    except FileNotFoundError as exc:
        raise HTTPException(status_code=404, detail="Patient not found.") from exc
    return {"id": patient_id}


@app.get("/api/vr/patients/{patient_id}/{file_name}")
def vr_package_file(patient_id: str, file_name: str):
    try:
        path = vr_service.package_file(patient_id, file_name)
    except vr_service.PackageError as exc:
        raise HTTPException(status_code=400, detail=str(exc)) from exc
    except FileNotFoundError as exc:
        raise HTTPException(status_code=404, detail="Not found.") from exc
    media = "application/octet-stream" if file_name.endswith(".bin") else "application/json"
    return FileResponse(path, media_type=media)


@app.get("/api/vr/patients/{patient_id}/slices/{image}")
def vr_package_slice(patient_id: str, image: str):
    try:
        path = vr_service.package_file(patient_id, f"slices/{image}")
    except vr_service.PackageError as exc:
        raise HTTPException(status_code=400, detail=str(exc)) from exc
    except FileNotFoundError as exc:
        raise HTTPException(status_code=404, detail="Not found.") from exc
    return FileResponse(path, media_type="image/png")


@app.delete("/api/vr/patients/{patient_id}")
def vr_delete(patient_id: str) -> dict:
    try:
        vr_service.delete_package(patient_id)
    except vr_service.PackageError as exc:
        raise HTTPException(status_code=400, detail=str(exc)) from exc
    return {"status": "deleted"}