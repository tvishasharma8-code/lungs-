"""
LungVision segmentation pipeline.

This is a refactor of the original standalone `run_totalseg.py` script.
The logic is unchanged (same DICOM series selection, same
dicom2nifti -> TotalSegmentator -> merge -> smooth -> marching_cubes ->
STL flow) but it is now a callable function that accepts dynamic,
per-upload paths instead of hard-coded Windows paths, and reports
progress through a callback instead of only writing to a static log
file. This lets the FastAPI backend run it once per uploaded session
and stream progress to the frontend.

Nothing about the actual segmentation/meshing algorithm was changed.
"""

from __future__ import annotations

import os
import shutil
import subprocess
import tempfile
import time
from collections import defaultdict
from dataclasses import dataclass
from typing import Callable, Optional

import nibabel as nib
import numpy as np
import trimesh
from pydicom import dcmread
from skimage import measure
from skimage.filters import gaussian

import dicom2nifti

# Structures we segment and fuse into the final lung + airway mesh.
ROI_SUBSET = [
    "lung_upper_lobe_left",
    "lung_lower_lobe_left",
    "lung_upper_lobe_right",
    "lung_middle_lobe_right",
    "lung_lower_lobe_right",
    "trachea",
]

MASK_FILES = [f"{roi}.nii.gz" for roi in ROI_SUBSET]

ProgressCallback = Callable[[str, str, Optional[int]], None]
# callback(step_id: str, detail: str, percent: Optional[int]) -> None


@dataclass
class PipelineResult:
    stl_path: str
    vertex_count: int
    face_count: int
    included_structures: list[str]
    elapsed_seconds: float


class PipelineError(RuntimeError):
    pass


def _noop_progress(step_id: str, detail: str, percent: Optional[int] = None) -> None:
    pass


def run_pipeline(
    dicom_dir: str,
    workspace_dir: str,
    out_stl_path: str,
    log: Callable[[str], None] = print,
    progress: ProgressCallback = _noop_progress,
    fast: bool = False,
) -> PipelineResult:
    """
    Run the full DICOM -> NIfTI -> TotalSegmentator -> STL pipeline.

    dicom_dir:      folder containing the uploaded DICOM files for one
                     patient/session (may contain multiple series; the
                     largest series is auto-selected, same as before).
    workspace_dir:   a per-job scratch directory. NIfTI and raw
                     TotalSegmentator outputs are written here. The
                     caller is responsible for deleting this directory
                     once the job is done (see services/stl_service.py).
    out_stl_path:    final .stl file destination.
    log:             callable for human-readable log lines.
    progress:        callable invoked as the pipeline advances, used to
                     drive the frontend's live progress list.
    fast:            if True, passes --fast to TotalSegmentator. Kept
                     False by default to match the original script,
                     which explicitly avoids --fast for accuracy.
    """
    start = time.time()
    os.makedirs(workspace_dir, exist_ok=True)
    os.makedirs(os.path.dirname(out_stl_path), exist_ok=True)

    nifti_dir = os.path.join(workspace_dir, "nifti")
    seg_dir = os.path.join(workspace_dir, "seg_output")
    os.makedirs(nifti_dir, exist_ok=True)
    os.makedirs(seg_dir, exist_ok=True)

    tmp_dicom_dir = None
    try:
        # ------------------------------------------------------------
        # STEP 1: Find best CT series
        # ------------------------------------------------------------
        log("STEP 1: Scanning DICOM series")
        progress("dicom_read", "Reading DICOM series", 0)

        series: dict[str, list[str]] = defaultdict(list)
        for f in os.listdir(dicom_dir):
            p = os.path.join(dicom_dir, f)
            if not os.path.isfile(p):
                continue
            try:
                dcm = dcmread(p, stop_before_pixels=True)
                series[dcm.SeriesInstanceUID].append(p)
            except Exception:
                pass

        if not series:
            raise PipelineError(
                "No valid DICOM series found in the uploaded folder."
            )

        series_uid, files = max(series.items(), key=lambda x: len(x[1]))
        log(f"Selected series: {series_uid}")
        log(f"Slice count: {len(files)}")
        progress("dicom_read", f"Selected series with {len(files)} slices", 100)

        # ------------------------------------------------------------
        # STEP 2: Copy series to a clean temp folder
        # ------------------------------------------------------------
        tmp_dicom_dir = tempfile.mkdtemp(prefix="dicom_series_")
        for f in files:
            shutil.copy(f, tmp_dicom_dir)
        log(f"Temporary DICOM dir: {tmp_dicom_dir}")

        # ------------------------------------------------------------
        # STEP 3: DICOM -> NIfTI
        # ------------------------------------------------------------
        log("STEP 3: Converting DICOM to NIfTI")
        progress("dicom_to_nifti", "Converting DICOM series to NIfTI", 0)

        ct_nifti = os.path.join(nifti_dir, "ct.nii.gz")
        dicom2nifti.convert_directory(
            tmp_dicom_dir,
            nifti_dir,
            compression=True,
            reorient=False,
        )

        converted = None
        for f in os.listdir(nifti_dir):
            if f.endswith(".nii.gz"):
                converted = os.path.join(nifti_dir, f)
                break
        if converted is None:
            raise PipelineError("dicom2nifti did not produce a NIfTI file.")
        if converted != ct_nifti:
            os.rename(converted, ct_nifti)

        log("CT saved as NIfTI")
        progress("dicom_to_nifti", "CT volume converted", 100)

        # ------------------------------------------------------------
        # STEP 4: Run TotalSegmentator (lungs + trachea)
        # ------------------------------------------------------------
        log("STEP 4: Running TotalSegmentator (lungs + trachea only)")
        progress("segmentation", "Starting TotalSegmentator", 5)

        cmd = [
            "TotalSegmentator",
            "-i", ct_nifti,
            "-o", seg_dir,
            "--roi_subset",
            *ROI_SUBSET,
        ]
        if fast:
            cmd.append("--fast")

        # TotalSegmentator prints its own progress to stdout; we stream
        # it through so the frontend log / detail text can update live.
        process = subprocess.Popen(
            cmd,
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
            text=True,
            bufsize=1,
        )
        last_pct = 5
        for line in process.stdout:  # type: ignore[union-attr]
            line = line.rstrip()
            if line:
                log(f"[TotalSegmentator] {line}")
            # TotalSegmentator emits tqdm-style "NN%" progress lines;
            # nudge our reported percent up when we see them, otherwise
            # just creep forward so the UI doesn't look frozen.
            last_pct = min(last_pct + 1, 95)
            progress("segmentation", line or "Running segmentation model", last_pct)

        return_code = process.wait()
        if return_code != 0:
            raise PipelineError(
                f"TotalSegmentator exited with code {return_code}. See logs for details."
            )

        log("TotalSegmentator finished")
        progress("segmentation", "Segmentation complete", 100)

        # ------------------------------------------------------------
        # STEP 5: Merge lungs + trachea
        # ------------------------------------------------------------
        log("STEP 5: Merging lungs and trachea")
        progress("merge", "Merging lung lobes and trachea masks", 0)

        masks = []
        affine = None
        found_structures = []
        for f in MASK_FILES:
            p = os.path.join(seg_dir, f)
            if os.path.exists(p):
                img = nib.load(p)
                masks.append(img.get_fdata() > 0)
                affine = img.affine
                found_structures.append(f.replace(".nii.gz", ""))

        if not masks:
            raise PipelineError(
                "No lung / trachea masks were produced by TotalSegmentator."
            )

        merged = np.logical_or.reduce(masks)
        log("Lungs and trachea merged")
        progress("merge", f"Merged {len(found_structures)} structures", 100)

        # ------------------------------------------------------------
        # STEP 5.5: Smooth volume
        # ------------------------------------------------------------
        log("STEP 5.5: Smoothing volume")
        progress("smooth", "Smoothing volume", 0)

        merged_smooth = gaussian(
            merged.astype(np.float32),
            sigma=1.2,
            preserve_range=True,
        )
        log("Volume smoothed")
        progress("smooth", "Volume smoothed", 100)

        # ------------------------------------------------------------
        # STEP 6: Mesh -> STL
        # ------------------------------------------------------------
        log("STEP 6: Creating STL mesh")
        progress("mesh", "Running marching cubes", 10)

        verts, faces, _, _ = measure.marching_cubes(
            merged_smooth,
            level=0.5,
            spacing=np.abs(np.diag(affine))[:3],
        )

        mesh = trimesh.Trimesh(verts, faces, process=False)
        mesh.remove_unreferenced_vertices()
        mesh.merge_vertices()
        mesh.update_faces(mesh.nondegenerate_faces())
        mesh.fix_normals()

        progress("mesh", "Exporting STL", 90)
        mesh.export(out_stl_path)

        log(f"STL saved: {out_stl_path}")
        progress("mesh", "STL generated", 100)

        elapsed = time.time() - start
        return PipelineResult(
            stl_path=out_stl_path,
            vertex_count=int(len(mesh.vertices)),
            face_count=int(len(mesh.faces)),
            included_structures=found_structures,
            elapsed_seconds=elapsed,
        )
    finally:
        if tmp_dicom_dir and os.path.isdir(tmp_dicom_dir):
            shutil.rmtree(tmp_dicom_dir, ignore_errors=True)
