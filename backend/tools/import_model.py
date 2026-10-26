"""
Turn a lung model made by hand in 3D Slicer (exported STL / OBJ / PLY) into a VR
Patient Package - so a model built with the team's original Slicer workflow reaches the
VR app at exactly its own quality, and still lines up with that patient's tumor
findings (which are in DICOM patient coordinates, LPS mm).

Slicer exports models in patient coordinates, normally LPS (Slicer 4.11+ default) but
RAS if "Coordinate system: RAS" was chosen. This tool works out which by fitting the
model to the same patient's CT: it builds a quick lung mask from the DICOM (air inside
the body, the same check tools/check_coordinates.py uses) and keeps the interpretation
whose bounding box matches it. A model that fits neither - wrong patient, or moved /
scaled in Blender after export - is rejected rather than silently misplaced.

Usage (from backend/):
    .venv\\Scripts\\python tools\\import_model.py lungs.stl --dicom <patient's DICOM folder>
        [--label "Patient A0265"] [--seg <TotalSegmentator output folder>] [--send]

  --seg   label lobes / trachea from TotalSegmentator masks of the same CT (optional);
          without it the model is one part, "lungs".
  --send  store it in the VR patient store and make it the active patient (what the
          web app's "Send to VR" does). Otherwise writes mesh.bin + meta.json to --out.
"""

from __future__ import annotations

import argparse
import os
import sys
import tempfile

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

import trimesh  # noqa: E402
from pydicom import dcmread  # noqa: E402
from scipy import ndimage  # noqa: E402

from pipeline import lung_mesh  # noqa: E402
from pipeline.dicom_series import load_series  # noqa: E402

RAS_TO_LPS = np.array([-1.0, -1.0, 1.0])


def lung_surface_from_ct(series) -> np.ndarray:
    """
    Points (LPS mm) on the lungs' surface, from air inside the body - fast and
    model-free. Slices are sampled ~4 mm apart, which is plenty to judge where a model sits.
    """
    def hu(ds):
        return ds.pixel_array * float(getattr(ds, "RescaleSlope", 1)) + float(getattr(ds, "RescaleIntercept", 0))

    if len(series) > 1:
        dz = abs(float(np.dot(series.slices[0].position - series.slices[1].position,
                              np.cross(series.slices[0].row_dir, series.slices[0].col_dir))))
    else:
        dz = 1.0
    step = max(1, int(round(4.0 / max(dz, 0.1))))
    idx = list(range(0, len(series), step))
    air = np.stack([hu(dcmread(series.slices[i].path)) < -400 for i in idx])
    lab, n = ndimage.label(air)
    border = np.unique(np.concatenate([lab[:, 0, :].ravel(), lab[:, -1, :].ravel(),
                                       lab[:, :, 0].ravel(), lab[:, :, -1].ravel()]))
    sizes = ndimage.sum(air, lab, index=np.arange(1, n + 1))
    sizes[border[border > 0] - 1] = 0
    if sizes.max() <= 0:
        raise SystemExit("Could not find the lungs in this CT (no air region inside the body).")
    order = np.argsort(sizes)[::-1]
    keep = [i + 1 for i in order[:3] if sizes[i] > 0.15 * sizes[order[0]]]
    mask = np.isin(lab, keep)
    # In-plane boundary of each sampled slice: the lung wall where the model should sit.
    edge = mask & ~ndimage.binary_erosion(mask, structure=np.ones((1, 3, 3), bool))
    zz, rr, cc = np.nonzero(edge)
    pick = np.random.default_rng(0).choice(len(zz), size=min(120_000, len(zz)), replace=False)
    return np.array([series.slices[idx[z]].pixel_to_lps(c, r) for z, r, c in zip(zz[pick], rr[pick], cc[pick])])


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    ap.add_argument("model", help="STL / OBJ / PLY exported from 3D Slicer")
    ap.add_argument("--dicom", required=True, help="the same patient's DICOM folder")
    ap.add_argument("--label", default=None)
    ap.add_argument("--seg", default=None, help="TotalSegmentator output folder for lobe labels (optional)")
    ap.add_argument("--send", action="store_true", help="store in the VR patient store and make it active")
    ap.add_argument("--out", default=None, help="output folder when not using --send")
    args = ap.parse_args()

    model = trimesh.load(args.model, force="mesh", process=True)
    if len(model.faces) == 0:
        raise SystemExit("The model file contains no triangles.")
    model.merge_vertices()
    print(f"Model: {len(model.vertices):,} vertices, {len(model.faces):,} triangles "
          f"(kept as is - your Slicer quality)")

    series = load_series(args.dicom)
    if not series.has_geometry:
        raise SystemExit("This CT has no position/orientation tags; it can't be matched to the model.")
    from scipy.spatial import cKDTree

    ct_surface = cKDTree(lung_surface_from_ct(series))

    # How far the model's surface is from the real lung wall, for each way of reading
    # its coordinates. Bounding boxes alone can't tell them apart when the lungs are
    # near the scanner's centre line; the anatomy can (the right lung is bigger, the
    # left has the heart's notch), so a mirrored reading lands millimetres off.
    v = np.asarray(model.vertices, dtype=np.float64)
    sample = v[np.random.default_rng(1).choice(len(v), size=min(20_000, len(v)), replace=False)]
    candidates = {"LPS": v, "RAS": v * RAS_TO_LPS}
    dist = {}
    for name in candidates:
        pts = sample if name == "LPS" else sample * RAS_TO_LPS
        dist[name] = float(np.median(ct_surface.query(pts)[0]))
        print(f"  read as {name}: model surface is a median {dist[name]:.1f} mm from the CT's lung wall")
    best, other = sorted(dist, key=dist.get)
    if dist[best] > 5.0:
        raise SystemExit(
            f"The model doesn't line up with this CT (best: {dist[best]:.1f} mm off as {best}). "
            "Is it the same patient? Was it moved or scaled after export (e.g. in Blender)? "
            "Export straight from 3D Slicer for VR packages.")
    if dist[other] < 1.5 * dist[best]:
        raise SystemExit(
            f"Can't tell whether the model is LPS or RAS ({dist['LPS']:.1f} vs {dist['RAS']:.1f} mm). "
            "Re-export from 3D Slicer with 'Coordinate system: LPS' and try again.")
    print(f"Model coordinates: {best}" + (" (converted to LPS)" if best == "RAS" else ""))
    verts = candidates[best]

    faces = np.asarray(model.faces, dtype=np.int64)
    if lung_mesh.signed_volume(verts, faces) < 0:  # RAS -> LPS mirrors the winding
        faces = faces[:, ::-1].copy()
    surface = lung_mesh.Surface(verts, faces)

    names = ["lungs"]
    face_labels = np.ones(len(faces), dtype=np.int16)
    if args.seg:
        import nibabel as nib
        from pipeline.run_totalseg import MASK_FILES

        labels, affine, names = None, None, []
        for f in MASK_FILES:
            p = os.path.join(args.seg, f)
            if os.path.exists(p):
                img = nib.load(p)
                mask = np.asanyarray(img.dataobj) > 0
                if labels is None:
                    labels, affine = np.zeros(mask.shape, np.int16), img.affine
                names.append(f[:-7])
                labels[mask & (labels == 0)] = len(names)
        if labels is None:
            raise SystemExit(f"No TotalSegmentator masks found in {args.seg}")
        face_labels = lung_mesh.label_faces(surface, labels, affine)
        print("Lobes labelled from TotalSegmentator masks.")

    out = args.out or tempfile.mkdtemp(prefix="lv_import_")
    os.makedirs(out, exist_ok=True)
    structures = lung_mesh.write_mesh_bin(os.path.join(out, "mesh.bin"), surface, face_labels, names)
    lung_mesh.write_meta(os.path.join(out, "meta.json"), surface, structures,
                         series_uid=series.series_uid, slice_count=len(series),
                         voxel_spacing_mm=[series.slices[0].col_spacing, series.slices[0].row_spacing, 0.0],
                         mesh_file="mesh.bin")
    print("Parts: " + ", ".join(f"{s['name']}={s['triangles']:,}" for s in structures))

    if args.send:
        from services import vr_service

        base = os.path.splitext(os.path.basename(args.model))[0]
        pid_source = "".join(ch for ch in base if ch.isalnum())[:8] or "model"
        info = vr_service.create_package(f"slicer{pid_source}", out, findings=None,
                                         label=args.label or f"{base} (3D Slicer)")
        print(f"Sent to VR as '{info['label']}' (id {info['id']}) - now the active patient.")
    else:
        print(f"Package files written to {out}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
