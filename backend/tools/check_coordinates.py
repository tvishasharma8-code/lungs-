"""
Coordinate check (integration plan I1.7): do the lung mesh and tumor positions share
one frame on a real patient?

Tumor positions come from DICOM pixels through each slice's own geometry tags
(dicom_series.SliceInfo.pixel_to_lps). The mesh comes from TotalSegmentator's NIfTI
masks through the NIfTI affine (lung_mesh.index_to_lps). Those are two independent
routes to "a point in the patient"; if they disagree, every tumor marker lands in the
wrong place. This checks, on real data:

  1. Voxel identity - random DICOM pixels, taken DICOM -> LPS -> NIfTI index, must land
     on (near-)integer voxels holding the same CT value.
  2. Mesh placement - the reconstructed mesh (NIfTI route) must coincide with an
     independent lung mask built purely from DICOM geometry (the ML team's air-threshold
     approach): bounding boxes and centroids within a few millimetres.
  3. Package round-trip - mesh.bin / meta.json read back intact.

Usage (from backend/):
    .venv\\Scripts\\python tools\\check_coordinates.py <dicom_folder> [--full] [--out DIR]

--full runs TotalSegmentator at full resolution (slow on CPU); the default uses --fast,
which is plenty to check coordinates.
"""

from __future__ import annotations

import argparse
import json
import os
import struct
import sys
import tempfile
import time

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

import nibabel as nib  # noqa: E402
from pydicom import dcmread  # noqa: E402
from scipy import ndimage  # noqa: E402

from pipeline import lung_mesh  # noqa: E402
from pipeline.dicom_series import load_series  # noqa: E402
from pipeline.run_totalseg import run_pipeline  # noqa: E402

failures = 0


def check(ok: bool, what: str) -> None:
    global failures
    print(("  PASS " if ok else "  FAIL ") + what, flush=True)
    if not ok:
        failures += 1


def hu(ds) -> np.ndarray:
    return ds.pixel_array.astype(np.float64) * float(getattr(ds, "RescaleSlope", 1)) \
        + float(getattr(ds, "RescaleIntercept", 0))


def read_mesh_bin(path: str):
    with open(path, "rb") as fh:
        magic = fh.read(8)
        version, nverts, nsub = struct.unpack("<III", fh.read(12))
        subs = []
        for _ in range(nsub):
            (n,) = struct.unpack("<B", fh.read(1))
            name = fh.read(n).decode("utf-8")
            (count,) = struct.unpack("<I", fh.read(4))
            subs.append((name, count))
        verts = np.frombuffer(fh.read(nverts * 12), dtype="<f4").reshape(-1, 3)
        indices = {name: np.frombuffer(fh.read(count * 4), dtype="<u4") for name, count in subs}
        rest = fh.read()
    return magic, version, verts, indices, rest


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("dicom_folder")
    ap.add_argument("--full", action="store_true", help="full-resolution TotalSegmentator (slow on CPU)")
    ap.add_argument("--out", default=None, help="keep outputs here (default: temp, deleted)")
    args = ap.parse_args()

    out = args.out or tempfile.mkdtemp(prefix="lv_coordcheck_")
    os.makedirs(out, exist_ok=True)
    work = os.path.join(out, "workspace")
    rng = np.random.default_rng(0)

    # ------------------------------------------------------------------ series
    print("Loading series...", flush=True)
    series = load_series(args.dicom_folder)
    print(f"  {len(series)} slices in the chosen series ({series.series_count} series in folder), "
          f"geometry={'yes' if series.has_geometry else 'NO'}", flush=True)
    check(series.has_geometry, "series has position/orientation tags (needed for mm positions)")
    if not series.has_geometry:
        return 1
    normal = np.cross(series.slices[0].row_dir, series.slices[0].col_dir)
    if normal[2] < 0:
        normal = -normal
    along = [float(np.dot(s.position, normal)) for s in series.slices]
    check(all(a > b for a, b in zip(along, along[1:])),
          f"slices ordered superior -> inferior along the normal ({along[0]:.1f} .. {along[-1]:.1f} mm)")

    # ------------------------------------------------------------------ pipeline
    print(f"Running reconstruction ({'full' if args.full else 'fast'} TotalSegmentator)...", flush=True)
    t0 = time.time()
    stl = os.path.join(out, "lungs.stl")
    vr_dir = os.path.join(out, "vr")

    # run_pipeline leaves the workspace (NIfTI + masks) in place - the service deletes
    # it - so the CT NIfTI is still there to compare against below.
    result = run_pipeline(args.dicom_folder, work, stl, log=lambda m: None,
                          fast=not args.full, vr_dir=vr_dir)
    print(f"  done in {time.time() - t0:.0f}s: STL {result.face_count} faces, "
          f"VR {result.vr_triangles} triangles, structures {result.included_structures}", flush=True)

    ct = nib.load(os.path.join(work, "nifti", "ct.nii.gz"))
    affine = ct.affine
    ct_data = np.asanyarray(ct.dataobj)

    # ------------------------------------------------------------------ 1. voxel identity
    print("1. Voxel identity (DICOM pixel -> LPS -> NIfTI voxel):", flush=True)
    worst_frac, mismatched, tested = 0.0, 0, 0
    for _ in range(60):
        si = int(rng.integers(0, len(series)))
        s = series.slices[si]
        col, row = int(rng.integers(0, s.cols)), int(rng.integers(0, s.rows))
        lps = s.pixel_to_lps(col, row)
        ijk = lung_mesh.lps_to_index(lps[None, :], affine)[0]
        worst_frac = max(worst_frac, float(np.abs(ijk - np.rint(ijk)).max()))
        idx = np.rint(ijk).astype(int)
        if np.any(idx < 0) or np.any(idx >= np.array(ct_data.shape)):
            mismatched += 1
            continue
        dicom_value = hu(dcmread(s.path))[row, col]
        nifti_value = float(ct_data[tuple(idx)])
        tested += 1
        if abs(dicom_value - nifti_value) > 1.0:
            mismatched += 1
    check(worst_frac < 0.05, f"pixels land on whole voxels (worst offset {worst_frac:.4f} voxel)")
    check(mismatched == 0, f"same CT value at both ends ({tested - mismatched}/{tested} match"
                           f"{'' if mismatched == 0 else f', {mismatched} mismatched'})")

    # ------------------------------------------------------------------ 2. mesh placement
    print("2. Mesh vs independent DICOM-geometry lung mask:", flush=True)
    # ML team's prototype approach (build_patient_mesh.py, in git history): air < -400 HU inside the body.
    # Slice by slice: a full float CT stack can run to a gigabyte.
    air = np.stack([hu(dcmread(s.path)) < -400 for s in series.slices])  # (slice, row, col)
    lab, n = ndimage.label(air)
    border = np.unique(np.concatenate([lab[:, 0, :].ravel(), lab[:, -1, :].ravel(),
                                       lab[:, :, 0].ravel(), lab[:, :, -1].ravel()]))
    sizes = ndimage.sum(air, lab, index=np.arange(1, n + 1))
    sizes[border[border > 0] - 1] = 0
    order = np.argsort(sizes)[::-1]
    keep = [i + 1 for i in order[:3] if sizes[i] > 0.15 * sizes[order[0]]]
    mask = np.isin(lab, keep)
    zz, rr, cc = np.nonzero(mask)
    pick = rng.choice(len(zz), size=min(200_000, len(zz)), replace=False)
    pts = np.array([series.slices[z].pixel_to_lps(c, r) for z, r, c in zip(zz[pick], rr[pick], cc[pick])])
    thr_lo, thr_hi = pts.min(axis=0), pts.max(axis=0)
    thr_centroid = pts.mean(axis=0)

    meta = json.load(open(os.path.join(vr_dir, "meta.json")))
    mesh_lo, mesh_hi = np.array(meta["bbox_min_mm"]), np.array(meta["bbox_max_mm"])
    print(f"  threshold mask bbox  {np.round(thr_lo, 1)} .. {np.round(thr_hi, 1)} mm")
    print(f"  TotalSeg mesh  bbox  {np.round(mesh_lo, 1)} .. {np.round(mesh_hi, 1)} mm")
    # Lateral (x) and front-back (y) extents: both methods find the same lung walls.
    dx = np.abs(np.concatenate([thr_lo - mesh_lo, thr_hi - mesh_hi]))
    check(max(dx[0], dx[3], dx[1], dx[4]) < 15.0,
          f"left-right and front-back walls agree within 15 mm (worst {max(dx[0], dx[3], dx[1], dx[4]):.1f} mm)")
    # Bottom: both stop at the diaphragm.
    check(dx[2] < 15.0, f"bottoms (diaphragm) agree within 15 mm ({dx[2]:.1f} mm)")
    # Top: both follow the airway up to where the threshold's airway ends (the edge of
    # the scan on patient 11) - the pipeline extends TotalSegmentator's short trachea
    # through the CT air (ct_refine._extend_airway), as a 3D Slicer model does.
    check(abs(dx[5]) < 15.0, f"tops (airway) agree within 15 mm ({dx[5]:.1f} mm)")
    # Anything the threshold still finds above the mesh must be airway-sized (a tube of
    # a few cm^2), never lung-sized.
    above = [i for i, s in enumerate(series.slices) if float(s.position[2]) > mesh_hi[2] + 2.0]
    px_area = series.slices[0].row_spacing * series.slices[0].col_spacing / 100.0  # cm^2
    biggest = max((float(mask[i].sum()) * px_area for i in above), default=0.0)
    check(biggest < 10.0, f"air above the mesh top is airway only ({len(above)} slices, "
                          f"largest {biggest:.1f} cm^2 per slice; lung slices are 50+ cm^2)")

    # Most mesh vertices should sit on the threshold lung's surface (within ~1 cm).
    surf = mask ^ ndimage.binary_erosion(mask)
    sz, sr, sc = np.nonzero(surf)
    spick = rng.choice(len(sz), size=min(150_000, len(sz)), replace=False)
    surf_pts = np.array([series.slices[z].pixel_to_lps(c, r) for z, r, c in zip(sz[spick], sr[spick], sc[spick])])
    from scipy.spatial import cKDTree
    magic, version, verts, indices, rest = read_mesh_bin(os.path.join(vr_dir, "mesh.bin"))
    dist, _ = cKDTree(surf_pts).query(verts[rng.choice(len(verts), size=min(20_000, len(verts)), replace=False)])
    print(f"  mesh-to-threshold-surface distance: median {np.median(dist):.1f} mm, 90th pct {np.percentile(dist, 90):.1f} mm")
    check(np.median(dist) < 6.0, f"mesh surface lies on the DICOM-geometry lung surface (median {np.median(dist):.1f} mm)")

    # A mirrored or shifted mesh would put the centroid far off even with similar boxes.
    # Volume against volume: the mask's voxels vs the volume the mesh encloses (sum of
    # signed tetrahedra). The plain vertex mean is a surface average weighted by where
    # decimation kept detail - on A0265 it sat 24 mm off with walls agreeing to 0.7 mm.
    tri = verts[np.concatenate(list(indices.values())).reshape(-1, 3).astype(np.int64)].astype(np.float64)
    vol = np.einsum("ij,ij->i", tri[:, 0], np.cross(tri[:, 1], tri[:, 2])) / 6.0
    mesh_centroid = (vol[:, None] * tri.sum(axis=1) / 4.0).sum(axis=0) / vol.sum()
    off = np.linalg.norm(mesh_centroid[:2] - thr_centroid[:2])
    check(off < 15.0, f"centroids agree left-right / front-back within 15 mm ({off:.1f} mm)")

    # ------------------------------------------------------------------ 3. package
    print("3. VR package round-trip:", flush=True)
    check(magic == lung_mesh.MESH_MAGIC and version == lung_mesh.MESH_VERSION, "mesh.bin header")
    check(len(rest) == 0, "mesh.bin has no trailing bytes")
    total_idx = sum(len(v) for v in indices.values())
    check(total_idx == meta["triangles"] * 3, f"triangle count matches meta ({meta['triangles']})")
    check(all(int(v.max()) < len(verts) for v in indices.values()), "all indices in range")
    names = list(indices)
    print(f"  sub-meshes: {', '.join(f'{k}={len(v) // 3}' for k, v in indices.items())}")
    check(any("lobe" in k for k in names), "lobes present as separate sub-meshes")
    lab_frac = 1.0 - (len(indices.get("unlabeled", [])) / max(1, total_idx))
    check(lab_frac > 0.98, f"{lab_frac * 100:.1f}% of triangles carry a lobe/trachea label")
    for key in ("frame", "center_mm", "source"):
        check(key in meta, f"meta.json has '{key}'")
    folder_name = os.path.basename(os.path.normpath(args.dicom_folder))
    check(folder_name not in json.dumps(meta) and ":\\\\" not in json.dumps(meta),
          "meta.json carries no folder paths or patient folder name")

    print(f"\nOutputs: {out}")
    print("ALL PASSED" if failures == 0 else f"{failures} CHECK(S) FAILED")
    if not args.out:
        print("(temporary - pass --out DIR to keep them)")
    return 0 if failures == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
