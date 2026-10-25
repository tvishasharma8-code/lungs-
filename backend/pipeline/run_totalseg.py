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
from dataclasses import dataclass
from typing import Callable, Optional

import nibabel as nib
import numpy as np
import trimesh

import dicom2nifti

from pipeline import ct_refine, lung_mesh
from pipeline.dicom_series import load_series

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
    # VR package (mesh.bin + meta.json), when requested
    vr_dir: Optional[str] = None
    vr_triangles: int = 0

# Triangle budget for the VR mesh. Default: the detail of the VR app's own lung model
# (LungModel.fbx, made in 3D Slicer + Blender: ~730k vertices / ~1.46M triangles),
# which already runs on the Quest - so patient lungs look as good as that model, not
# coarser. LUNGVISION_VR_TRIANGLES overrides it (e.g. 400000 for a lighter mesh).
VR_TARGET_TRIANGLES = int(os.environ.get("LUNGVISION_VR_TRIANGLES", "1460000"))


class PipelineError(RuntimeError):
    pass


def _noop_progress(step_id: str, detail: str, percent: Optional[int] = None) -> None:
    pass


def _totalsegmentator_exe() -> str:
    """The TotalSegmentator command. Looks next to the running Python first, so the
    backend works from a virtualenv that was never 'activated' (Scripts/ or bin/ not
    on PATH), then falls back to PATH."""
    import sys

    here = os.path.dirname(sys.executable)
    for name in ("TotalSegmentator.exe", "TotalSegmentator"):
        candidate = os.path.join(here, name)
        if os.path.isfile(candidate):
            return candidate
    return shutil.which("TotalSegmentator") or "TotalSegmentator"


def run_pipeline(
    dicom_dir: str,
    workspace_dir: str,
    out_stl_path: str,
    log: Callable[[str], None] = print,
    progress: ProgressCallback = _noop_progress,
    fast: bool = False,
    vr_dir: Optional[str] = None,
    vr_target_triangles: int = VR_TARGET_TRIANGLES,
    refine_with_ct: bool = True,
) -> PipelineResult:
    """
    Run the full DICOM -> NIfTI -> TotalSegmentator -> STL pipeline.

    dicom_dir:      folder containing the uploaded DICOM files for one
                    patient/session (may contain multiple series; the
                    largest series is auto-selected, same as before).
    workspace_dir:  a per-job scratch directory. NIfTI and raw
                    TotalSegmentator outputs are written here. The
                    caller is responsible for deleting this directory
                    once the job is done (see services/analysis_service.py).
    out_stl_path:   final .stl file destination.
    log:            callable for human-readable log lines.
    progress:       callable invoked as the pipeline advances, used to
                    drive the frontend's live progress list.
    fast:           if True, passes --fast to TotalSegmentator. Kept
                    False by default to match the original script,
                    which explicitly avoids --fast for accuracy.
    refine_with_ct: take the exact surface from the CT at native resolution
                    (TotalSegmentator only locates the lungs and labels lobes)
                    - see ct_refine.py. False = AI masks alone, smoothed.
    vr_dir:         if set, also writes the VR package mesh here:
                    mesh.bin (decimated, one sub-mesh per lobe + trachea)
                    and meta.json - see pipeline/lung_mesh.py.

    Both the STL and the VR mesh are in DICOM patient coordinates (LPS,
    mm) - the same frame as tumor detections - so findings can be placed
    on the model.
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

        # Shared loader: the same series and slice order tumor detection uses.
        try:
            dicom_series = load_series(dicom_dir)
        except FileNotFoundError as exc:
            raise PipelineError(
                "No valid DICOM series found in the uploaded folder."
            ) from exc

        files = dicom_series.paths
        log(f"Selected series: {dicom_series.series_uid}")
        log(f"Slice count: {len(files)}")
        progress("dicom_read", f"Selected series with {len(files)} slices", 100)

        # ------------------------------------------------------------
        # STEP 2: Copy series to a clean temp folder
        # ------------------------------------------------------------
        # Numbered names: files from different sub-folders can share a
        # name, and copying by base name would overwrite slices.
        tmp_dicom_dir = tempfile.mkdtemp(prefix="dicom_series_")
        for i, f in enumerate(files):
            shutil.copy(f, os.path.join(tmp_dicom_dir, f"{i:05d}.dcm"))
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
            _totalsegmentator_exe(),
            "-i", ct_nifti,
            "-o", seg_dir,
            "--roi_subset",
            *ROI_SUBSET,
            # Saving masks uses parallel worker processes by default, each
            # holding a full copy of the volume in memory. On CPU-only /
            # memory-constrained machines that throws a MemoryError even
            # after prediction succeeds, so force single-threaded
            # resampling/saving here - slower, but far less memory-hungry.
            "--nr_thr_resamp", "1",
            "--nr_thr_saving", "1",
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
            # Creep the reported percent forward on each output line so
            # the UI doesn't look frozen during the long prediction step.
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

        # One label volume (1..K = structure, 0 = outside) rather than a
        # plain OR of the masks, so the VR mesh can tell lobes apart.
        labels = None
        affine = None
        found_structures = []
        for f in MASK_FILES:
            p = os.path.join(seg_dir, f)
            if os.path.exists(p):
                img = nib.load(p)
                mask = np.asanyarray(img.dataobj) > 0
                if labels is None:
                    labels = np.zeros(mask.shape, dtype=np.int16)
                    affine = img.affine
                found_structures.append(f.replace(".nii.gz", ""))
                labels[mask & (labels == 0)] = len(found_structures)

        if labels is None:
            raise PipelineError(
                "No lung / trachea masks were produced by TotalSegmentator."
            )

        merged = labels > 0
        log("Lungs and trachea merged")
        progress("merge", f"Merged {len(found_structures)} structures", 100)

        # ------------------------------------------------------------
        # STEP 5.5 + 6: Smooth, mesh in patient coordinates -> STL
        # ------------------------------------------------------------
        # Same smoothing (sigma 1.2) and iso-level as before, but the surface is
        # now taken through the full NIfTI affine into DICOM patient coordinates
        # (LPS, mm) instead of an origin-less spacing-only box - see lung_mesh.py.
        log("STEP 5.5: Smoothing volume")
        progress("smooth", "Smoothing volume", 0)
        log("STEP 6: Creating STL mesh (patient coordinates, LPS mm)")
        progress("smooth", "Volume smoothed", 100)
        progress("mesh", "Running marching cubes", 10)

        if refine_with_ct:
            # The surface follows the CT at native resolution, like a 3D Slicer
            # threshold model; TotalSegmentator only says where the lungs are and
            # which lobe is which (see ct_refine.py). Light smoothing only - heavy
            # smoothing erased fissures and rib impressions.
            progress("smooth", "Refining the lung surface from the CT", 30)
            ct_hu = np.asanyarray(nib.load(ct_nifti).dataobj).astype(np.float32)
            surface_mask = ct_refine.refine_lung_mask(ct_hu, labels, lung_mesh.voxel_spacing_mm(affine))
            del ct_hu
            sigma_mm, taubin_iterations = 0.6, 10
        else:
            # AI masks alone: --fast works at 3 mm, so it needs a stronger blur.
            surface_mask = merged
            sigma_mm, taubin_iterations = (3.0 if fast else 2.0), 30

        surface = lung_mesh.extract_surface(surface_mask, affine, sigma_mm=sigma_mm)

        mesh = trimesh.Trimesh(surface.verts_lps, surface.faces, process=False)
        mesh.remove_unreferenced_vertices()
        mesh.merge_vertices()
        mesh.update_faces(mesh.nondegenerate_faces())
        mesh = lung_mesh.keep_main_bodies(mesh)
        progress("mesh", "Smoothing surface", 40)
        lung_mesh.smooth_mesh(mesh, iterations=taubin_iterations)
        mesh.fix_normals()

        progress("mesh", "Exporting STL", 70)
        mesh.export(out_stl_path)
        log(f"STL saved: {out_stl_path}")

        vr_triangles = 0
        if vr_dir:
            progress("mesh", "Building VR mesh (decimate + label lobes)", 80)
            os.makedirs(vr_dir, exist_ok=True)
            clean = lung_mesh.Surface(np.asarray(mesh.vertices, dtype=np.float64),
                                      np.asarray(mesh.faces, dtype=np.int64))
            vr_surface = lung_mesh.decimate(clean, vr_target_triangles)
            face_labels = lung_mesh.label_faces(vr_surface, labels, affine)
            structures = lung_mesh.write_mesh_bin(
                os.path.join(vr_dir, "mesh.bin"), vr_surface, face_labels, found_structures)
            voxel_spacing = lung_mesh.voxel_spacing_mm(affine)
            lung_mesh.write_meta(
                os.path.join(vr_dir, "meta.json"), vr_surface, structures,
                series_uid=dicom_series.series_uid, slice_count=len(files),
                voxel_spacing_mm=list(voxel_spacing), mesh_file="mesh.bin")
            vr_triangles = int(len(vr_surface.faces))
            per_structure = ", ".join("%s=%d" % (s["name"], s["triangles"]) for s in structures)
            log(f"VR mesh saved: {vr_dir} ({vr_triangles} triangles: {per_structure})")

        progress("mesh", "STL generated", 100)

        elapsed = time.time() - start
        return PipelineResult(
            stl_path=out_stl_path,
            vertex_count=int(len(mesh.vertices)),
            face_count=int(len(mesh.faces)),
            included_structures=found_structures,
            elapsed_seconds=elapsed,
            vr_dir=vr_dir,
            vr_triangles=vr_triangles,
        )
    finally:
        if tmp_dicom_dir and os.path.isdir(tmp_dicom_dir):
            shutil.rmtree(tmp_dicom_dir, ignore_errors=True)