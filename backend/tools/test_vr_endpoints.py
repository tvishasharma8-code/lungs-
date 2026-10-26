"""
Tests the /api/vr Patient Package endpoints (integration plan I1.5) against a real VR
mesh, without running the pipeline: a completed reconstruction job is faked in the job
store and pointed at a mesh.bin/meta.json produced earlier (e.g. by check_coordinates.py
--out DIR, which writes DIR/vr).

Usage (from backend/):
    .venv\\Scripts\\python tools\\test_vr_endpoints.py <dir containing mesh.bin + meta.json>
Packages are stored in a temporary folder (LUNGVISION_VR_DIR), not in your home folder.
"""

from __future__ import annotations

import json
import os
import shutil
import sys
import tempfile

store = tempfile.mkdtemp(prefix="lv_vrstore_")
os.environ["LUNGVISION_VR_DIR"] = store  # before the backend modules are imported
sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from fastapi.testclient import TestClient  # noqa: E402

import main  # noqa: E402
from models.schemas import AnalysisType, JobState, ReconstructionResult  # noqa: E402
from services import analysis_service  # noqa: E402
from services.progress_service import job_store  # noqa: E402

failures = 0


def check(ok: bool, what: str) -> None:
    global failures
    print(("  PASS " if ok else "  FAIL ") + what)
    if not ok:
        failures += 1


def main_test(src: str) -> int:
    client = TestClient(main.app)

    job = job_store.create("test-session", AnalysisType.RECONSTRUCTION)
    vr_dir = os.path.join(analysis_service.OUTPUT_ROOT, f"{job.job_id}_vr")

    print("Before the job is complete:")
    r = client.post(f"/api/vr/send/{job.job_id}")
    check(r.status_code == 409, f"send refused for an unfinished job ({r.status_code})")

    job.state = JobState.COMPLETED
    job.results.reconstruction = ReconstructionResult(
        stl_url="", vertex_count=0, face_count=0, file_size_bytes=0, included_structures=[])
    shutil.copytree(src, vr_dir, dirs_exist_ok=True)

    print("Send to VR:")
    r = client.post(f"/api/vr/send/{job.job_id}", params={"label": "Test patient"})
    check(r.status_code == 200, f"send accepted ({r.status_code} {r.text[:80]})")
    pid = r.json().get("id")
    check(bool(pid) and r.json().get("label") == "Test patient", f"package created with label (id={pid})")
    check(len(r.json().get("structures", [])) >= 5, f"structures listed: {r.json().get('structures')}")

    print("List / active:")
    lst = client.get("/api/vr/patients").json()
    check(len(lst) == 1 and lst[0]["id"] == pid and lst[0]["active"], "listed and marked active")
    check(client.get("/api/vr/active").json()["id"] == pid, "active endpoint returns it")

    print("Package files:")
    mesh = client.get(f"/api/vr/patients/{pid}/mesh.bin")
    original = open(os.path.join(src, "mesh.bin"), "rb").read()
    check(mesh.status_code == 200 and mesh.content == original, f"mesh.bin byte-identical ({len(mesh.content)} bytes)")
    meta = client.get(f"/api/vr/patients/{pid}/meta.json")
    check(meta.status_code == 200 and meta.json().get("frame") == "DICOM_LPS_mm", "meta.json served, LPS frame")
    fnd = client.get(f"/api/vr/patients/{pid}/findings.json")
    # The source folder's own findings (detection ran) travel with the package; without
    # them the package still has an empty findings.json.
    src_findings = os.path.join(src, "findings.json")
    expected = json.load(open(src_findings, encoding="utf-8"))["tumors"] if os.path.isfile(src_findings) else []
    check(fnd.status_code == 200 and fnd.json().get("tumors") == expected,
          f"findings.json carries the job's tumors ({len(expected)})")
    if expected:
        first = fnd.json()["slices"][0]["image"]
        img = client.get(f"/api/vr/patients/{pid}/{first}")
        check(img.status_code == 200 and img.headers["content-type"] == "image/png", f"flagged slice image served ({first})")
        check(client.get(f"/api/vr/patients/{pid}/slices/..%2Fmeta.json").status_code in (400, 404),
              "slice path tricks rejected")
        check(client.get(f"/api/vr/patients/{pid}/slices/notapng.txt").status_code == 400, "only slice PNGs are served")

    print("Rejects bad requests:")
    check(client.get(f"/api/vr/patients/{pid}/package.json").status_code == 400, "only package files are served")
    check(client.get("/api/vr/patients/..%2F..%2Fetc/meta.json").status_code in (400, 404), "path tricks rejected")
    check(client.post("/api/vr/active/nope123").status_code == 404, "unknown patient can't be made active")

    print("Survives the web session reset:")
    analysis_service.cleanup_session("test-session", job.job_id)
    check(client.get(f"/api/vr/patients/{pid}/mesh.bin").status_code == 200, "package still there after New Analysis")

    print("Delete:")
    check(client.delete(f"/api/vr/patients/{pid}").status_code == 200, "deleted")
    check(client.get("/api/vr/patients").json() == [], "list is empty")
    check(client.get("/api/vr/active").json()["id"] is None, "no active patient")

    print("After a backend restart (job forgotten, its model still on disk):")
    import uuid
    lost = str(uuid.uuid4())
    shutil.copytree(src, analysis_service.vr_output_dir(lost), dirs_exist_ok=True)
    r = client.post(f"/api/vr/send/{lost}", params={"label": "After restart"})
    check(r.status_code == 200 and r.json().get("label") == "After restart", f"send still works ({r.status_code})")
    check(client.post("/api/vr/send/..%2F..%2Fetc").status_code == 404, "non-id job names rejected")
    check(client.post(f"/api/vr/send/{uuid.uuid4()}").status_code == 404, "unknown job without a model: 404")
    shutil.rmtree(analysis_service.vr_output_dir(lost), ignore_errors=True)

    print("ALL PASSED" if failures == 0 else f"{failures} FAILED")
    return 0 if failures == 0 else 1


if __name__ == "__main__":
    try:
        sys.exit(main_test(sys.argv[1]))
    finally:
        shutil.rmtree(store, ignore_errors=True)
