"""
Patient Packages for the VR app (integration plan I1.5).

A finished job's VR mesh (written by the reconstruction next to its STL, see
stl_service.py) and its findings are copied here when the user presses "Send to VR".
That copy is the ONLY thing LungVision keeps after a session ends - deliberately opt-in,
since the web app otherwise keeps no history.

Layout (one folder per patient):
    <VR_ROOT>/<patient_id>/meta.json      frame, centre, bbox, structures (lung_mesh.write_meta)
                          /mesh.bin       decimated lobe-labelled mesh (lung_mesh.write_mesh_bin)
                          /findings.json  tumors + flagged slices (tumor_cluster.findings_document),
                                          empty lists if detection didn't run
                          /slices/NNN.png flagged CT slices, boxes drawn (NNN = slice index)
                          /package.json   label, created time, what's inside
    <VR_ROOT>/active.json                 {"id": ...} - the patient the VR app should show

VR_ROOT defaults to ~/LungVision/vr_patients and can be moved with the
LUNGVISION_VR_DIR environment variable. It is outside backend/ on purpose: uvicorn
--reload watches that folder and would restart on every write.
"""

from __future__ import annotations

import json
import os
import re
import shutil
import time
from typing import Optional

VR_ROOT = os.environ.get(
    "LUNGVISION_VR_DIR",
    os.path.join(os.path.expanduser("~"), "LungVision", "vr_patients"),
)
os.makedirs(VR_ROOT, exist_ok=True)

PACKAGE_FILES = ("meta.json", "mesh.bin", "findings.json")
_ID_RE = re.compile(r"^[A-Za-z0-9_-]{1,64}$")
_SLICE_RE = re.compile(r"^slices/\d{3,5}\.png$")


class PackageError(ValueError):
    pass


def _patient_dir(patient_id: str) -> str:
    # Ids come from URLs: only allow plain ids, never a path.
    if not _ID_RE.match(patient_id or ""):
        raise PackageError("Invalid patient id.")
    return os.path.join(VR_ROOT, patient_id)


def package_file(patient_id: str, name: str) -> str:
    if name not in PACKAGE_FILES and not _SLICE_RE.match(name):
        raise PackageError(f"Unknown package file: {name}")
    path = os.path.join(_patient_dir(patient_id), name)
    if not os.path.isfile(path):
        raise FileNotFoundError(path)
    return path


def package_id_for_job(job_id: str) -> str:
    return job_id.replace("-", "")[:12]


def create_package(job_id: str, vr_mesh_dir: str, findings: Optional[dict], label: Optional[str]) -> dict:
    """Copies a job's VR mesh + findings into a new patient package and makes it active."""
    for name in ("meta.json", "mesh.bin"):
        if not os.path.isfile(os.path.join(vr_mesh_dir, name)):
            raise FileNotFoundError(f"This job has no VR mesh ({name} missing) - run 3D reconstruction first.")

    patient_id = package_id_for_job(job_id)
    dest = _patient_dir(patient_id)
    tmp = dest + ".partial"
    shutil.rmtree(tmp, ignore_errors=True)
    os.makedirs(tmp)
    for name in ("meta.json", "mesh.bin"):
        shutil.copy2(os.path.join(vr_mesh_dir, name), os.path.join(tmp, name))

    with open(os.path.join(tmp, "meta.json"), encoding="utf-8") as fh:
        meta = json.load(fh)

    # Detection's findings.json + slices/ sit in the same job folder (tumor_service.py).
    src_findings = os.path.join(vr_mesh_dir, "findings.json")
    if findings is None and os.path.isfile(src_findings):
        with open(src_findings, encoding="utf-8") as fh:
            findings = json.load(fh)
        if os.path.isdir(os.path.join(vr_mesh_dir, "slices")):
            shutil.copytree(os.path.join(vr_mesh_dir, "slices"), os.path.join(tmp, "slices"))
    with open(os.path.join(tmp, "findings.json"), "w", encoding="utf-8") as fh:
        json.dump(findings or {"frame": "DICOM_LPS_mm", "tumors": [], "slices": []}, fh, indent=2)

    info = {
        "id": patient_id,
        "label": (label or "").strip() or f"Patient {meta.get('source', {}).get('series_id', patient_id)[:6]}",
        "created_utc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
        "triangles": meta.get("triangles"),
        "structures": [s["name"] for s in meta.get("structures", [])],
        "has_findings": bool(findings and findings.get("tumors")),
        "tumors": len(findings.get("tumors", [])) if findings else 0,
    }
    with open(os.path.join(tmp, "package.json"), "w", encoding="utf-8") as fh:
        json.dump(info, fh, indent=2)

    # Swap in atomically-ish, so the VR app never downloads a half-written package.
    shutil.rmtree(dest, ignore_errors=True)
    os.replace(tmp, dest)
    set_active(patient_id)
    return info


def list_packages() -> list[dict]:
    active = get_active()
    out = []
    for name in sorted(os.listdir(VR_ROOT)):
        info_path = os.path.join(VR_ROOT, name, "package.json")
        if not os.path.isfile(info_path):
            continue
        try:
            with open(info_path, encoding="utf-8") as fh:
                info = json.load(fh)
        except (OSError, ValueError):
            continue
        info["active"] = info.get("id") == active
        out.append(info)
    out.sort(key=lambda i: i.get("created_utc", ""), reverse=True)
    return out


def get_active() -> Optional[str]:
    try:
        with open(os.path.join(VR_ROOT, "active.json"), encoding="utf-8") as fh:
            pid = json.load(fh).get("id")
    except (OSError, ValueError):
        return None
    return pid if pid and os.path.isdir(os.path.join(VR_ROOT, pid)) else None


def set_active(patient_id: str) -> None:
    if not os.path.isdir(_patient_dir(patient_id)):
        raise FileNotFoundError(patient_id)
    with open(os.path.join(VR_ROOT, "active.json"), "w", encoding="utf-8") as fh:
        json.dump({"id": patient_id, "since_utc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())}, fh)


def delete_package(patient_id: str) -> None:
    shutil.rmtree(_patient_dir(patient_id), ignore_errors=True)
    if get_active() is None:
        try:
            os.remove(os.path.join(VR_ROOT, "active.json"))
        except OSError:
            pass
