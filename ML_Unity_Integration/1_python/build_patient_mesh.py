r"""
build_patient_mesh.py - make a 3D lung mesh for Unity from ONE patient's CT (DICOM folder),
in the SAME coordinates the tumor server uses, so the tumor marker lands in the right spot.

Usage (PowerShell):
    pip install pydicom numpy scipy scikit-image
    python build_patient_mesh.py "I:\My Drive\Patients\A0265\dicom" --out A0265_lungs

Writes:
    A0265_lungs.obj        -> drag into Unity Assets (lungs + 4 tiny calibration cubes)
    A0265_lungs_meta.json  -> drag into Unity Assets too (TumorMarker3D reads it)

How it works (no TotalSegmentator / dicom2nifti needed):
    1. Load every slice, convert to Hounsfield units, sort by real z-position,
       resample to even slice spacing (fixes "uneven slice spacing" scans like A0265)
    2. Lungs = air inside the body: HU < -400, minus the air outside the body,
       keep the biggest air regions (both lungs + trachea), fill holes (vessels, tumors)
    3. Marching cubes -> surface mesh in millimetres, DICOM patient coordinates
    4. Export OBJ (metres, Y-up) + 4 calibration cubes at known millimetre offsets.
       Unity measures those cubes after import, so the marker math is exact no matter
       how Unity converts axes or how you move/scale/rotate the lung object.
"""
import argparse
import glob
import json
import os
import sys
from collections import defaultdict

import numpy as np
import pydicom
from scipy import ndimage
from skimage import measure

CALIB_MM = 100.0    # calibration cube offsets (mm) along DICOM x, y, z
SCALE = 0.001       # mm -> Unity metres


# ------------------------------------------------------------------ load CT
def load_series(folder):
    files = glob.glob(os.path.join(folder, "**", "*.dcm"), recursive=True)
    if not files:
        files = [f for f in glob.glob(os.path.join(folder, "**", "*"), recursive=True) if os.path.isfile(f)]
    by_series = defaultdict(list)
    for f in files:
        try:
            ds = pydicom.dcmread(f, stop_before_pixels=True)
            if hasattr(ds, "ImagePositionPatient") and hasattr(ds, "Rows"):
                by_series[ds.SeriesInstanceUID].append(f)
        except Exception:
            pass
    if not by_series:
        sys.exit("No CT slices with position info found in that folder.")
    files = max(by_series.values(), key=len)

    slices = []
    for f in files:
        ds = pydicom.dcmread(f)
        hu = ds.pixel_array.astype(np.float32) * float(getattr(ds, "RescaleSlope", 1)) \
            + float(getattr(ds, "RescaleIntercept", 0))
        slices.append((float(ds.ImagePositionPatient[2]), hu, ds))
    slices.sort(key=lambda s: s[0])

    ds0 = slices[0][2]
    iop = [float(v) for v in ds0.ImageOrientationPatient]
    if not np.allclose(iop, [1, 0, 0, 0, 1, 0], atol=0.05):
        print(f"WARNING: slices are not plain axial (orientation {iop}); positions may be off.")
    row_sp, col_sp = (float(v) for v in ds0.PixelSpacing)
    x0, y0 = float(ds0.ImagePositionPatient[0]), float(ds0.ImagePositionPatient[1])
    zs = np.array([s[0] for s in slices])
    vol = np.stack([s[1] for s in slices])   # (z, y, x)
    return vol, zs, x0, y0, row_sp, col_sp


def resample_z(vol, zs):
    """Linear resample along z onto an even grid (handles uneven / duplicate spacing)."""
    zs, keep = np.unique(zs, return_index=True)
    vol = vol[keep]
    if len(zs) < 2:
        sys.exit("Need at least 2 slices.")
    dz = float(np.median(np.diff(zs)))
    grid = np.arange(zs[0], zs[-1] + dz * 0.5, dz)
    idx = np.interp(grid, zs, np.arange(len(zs)))
    lo = np.floor(idx).astype(int)
    hi = np.minimum(lo + 1, len(zs) - 1)
    w = (idx - lo)[:, None, None].astype(np.float32)
    return vol[lo] * (1 - w) + vol[hi] * w, grid[0], dz


# ------------------------------------------------------------------ segment lungs
def segment_lungs(vol):
    air = vol < -400
    labels, n = ndimage.label(air)
    # air touching the image border (outside the body, table) is not lung
    border = np.unique(np.concatenate([labels[:, 0, :].ravel(), labels[:, -1, :].ravel(),
                                       labels[:, :, 0].ravel(), labels[:, :, -1].ravel()]))
    sizes = ndimage.sum(air, labels, index=np.arange(1, n + 1))
    sizes[border[border > 0] - 1] = 0
    if sizes.max() == 0:
        sys.exit("Could not find lungs (no air region inside the body).")
    order = np.argsort(sizes)[::-1]
    keep = [i + 1 for i in order[:3] if sizes[i] > 0.15 * sizes[order[0]]]   # lungs (+ trachea)
    mask = np.isin(labels, keep)
    mask = ndimage.binary_closing(mask, structure=np.ones((3, 3, 3)), iterations=2)
    for z in range(mask.shape[0]):           # fill vessels / nodules / tumor inside the lung
        mask[z] = ndimage.binary_fill_holes(mask[z])
    return mask


# ------------------------------------------------------------------ mesh + export
def make_mesh(mask, z0, dz, x0, y0, row_sp, col_sp, step):
    smooth = ndimage.gaussian_filter(mask.astype(np.float32), sigma=1.0)
    verts, faces, _, _ = measure.marching_cubes(smooth, level=0.5, spacing=(dz, row_sp, col_sp),
                                                step_size=step)
    # (z, y, x) mm offsets -> DICOM patient coordinates (LPS, mm)
    lps = np.column_stack([x0 + verts[:, 2], y0 + verts[:, 1], z0 + verts[:, 0]])
    return lps, faces


def to_obj_coords(d_mm):
    """DICOM offsets (mm) -> OBJ file coords (metres, right-handed, Y-up).
    Up = patient superior, forward = patient anterior. Unity flips X on import."""
    u = np.column_stack([d_mm[:, 0], d_mm[:, 2], d_mm[:, 1]]) * SCALE   # Unity-style (x, up, depth)
    return np.column_stack([-u[:, 0], u[:, 1], u[:, 2]])


def signed_volume(v, f):
    a, b, c = v[f[:, 0]], v[f[:, 1]], v[f[:, 2]]
    return np.einsum("ij,ij->i", a, np.cross(b, c)).sum() / 6.0


def cube(center, half):
    o = np.array([[-1, -1, -1], [1, -1, -1], [1, 1, -1], [-1, 1, -1],
                  [-1, -1, 1], [1, -1, 1], [1, 1, 1], [-1, 1, 1]], float) * half + center
    f = np.array([[0, 2, 1], [0, 3, 2], [4, 5, 6], [4, 6, 7], [0, 1, 5], [0, 5, 4],
                  [2, 3, 7], [2, 7, 6], [1, 2, 6], [1, 6, 5], [3, 0, 4], [3, 4, 7]])
    return o, f


def write_obj(path, objects):
    with open(path, "w") as fh:
        fh.write("# lung mesh from build_patient_mesh.py\n")
        base = 1
        for name, v, f in objects:
            fh.write(f"o {name}\ng {name}\n")   # Unity splits OBJ parts by 'g' groups
            fh.writelines(f"v {a:.5f} {b:.5f} {c:.5f}\n" for a, b, c in v)
            fh.writelines(f"f {i + base} {j + base} {k + base}\n" for i, j, k in f)
            base += len(v)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("dicom_folder")
    ap.add_argument("--out", default=None, help="output name (default: <folder>_lungs)")
    ap.add_argument("--quality", choices=["normal", "high"], default="normal")
    args = ap.parse_args()
    out = args.out or os.path.basename(os.path.normpath(args.dicom_folder)) + "_lungs"

    print("Loading CT...")
    vol, zs, x0, y0, row_sp, col_sp = load_series(args.dicom_folder)
    print(f"  {vol.shape[0]} slices, {vol.shape[1]}x{vol.shape[2]} px, "
          f"pixel {row_sp:.2f}x{col_sp:.2f} mm, z {zs.min():.1f} to {zs.max():.1f} mm")
    vol, z0, dz = resample_z(vol, zs)
    print(f"  resampled to {vol.shape[0]} even slices of {dz:.2f} mm")

    if args.quality == "normal":             # halve in-plane resolution: faster, lighter mesh
        h, w = vol.shape[1] // 2 * 2, vol.shape[2] // 2 * 2
        vol = vol[:, :h, :w].reshape(vol.shape[0], h // 2, 2, w // 2, 2).mean(axis=(2, 4))
        x0, y0 = x0 + col_sp * 0.5, y0 + row_sp * 0.5   # new pixel centres
        row_sp, col_sp = row_sp * 2, col_sp * 2

    print("Segmenting lungs...")
    mask = segment_lungs(vol)
    print(f"  lung volume {mask.sum() * dz * row_sp * col_sp / 1e6:.2f} litres")
    if mask.shape[0] * dz < 120:
        print("  NOTE: scan covers < 12 cm in z - lungs will look cut off (pick a patient with more slices).")

    print("Building surface...")
    lps, faces = make_mesh(mask, z0, dz, x0, y0, row_sp, col_sp, step=1)
    lo, hi = lps.min(axis=0), lps.max(axis=0)
    center = (lo + hi) / 2

    verts = to_obj_coords(lps - center)
    if signed_volume(verts, faces) < 0:      # make sure normals face outwards
        faces = faces[:, ::-1]
    objects = [("lungs", verts, faces)]
    for name, off in [("calib_origin", (0, 0, 0)), ("calib_x", (CALIB_MM, 0, 0)),
                      ("calib_y", (0, CALIB_MM, 0)), ("calib_z", (0, 0, CALIB_MM))]:
        cv, cf = cube(to_obj_coords(np.array([off], float))[0], 0.002)
        if signed_volume(cv, cf) < 0:
            cf = cf[:, ::-1]
        objects.append((name, cv, cf))

    write_obj(out + ".obj", objects)
    meta = {
        "source_dicom_folder": os.path.abspath(args.dicom_folder),
        "center_mm": [round(float(v), 3) for v in center],
        "calib_mm": CALIB_MM,
        "bbox_min_mm": [round(float(v), 2) for v in lo],
        "bbox_max_mm": [round(float(v), 2) for v in hi],
        "triangles": int(len(faces)),
        "note": "center_mm/bbox in DICOM patient coords (LPS). Same coords as tumor_server center_mm.",
    }
    with open(out + "_meta.json", "w") as fh:
        json.dump(meta, fh, indent=2)

    size = (hi - lo) / 10
    print(f"\nDone: {out}.obj  ({len(faces):,} triangles, {os.path.getsize(out + '.obj') / 1e6:.1f} MB)")
    print(f"      {out}_meta.json")
    print(f"Lung size: {size[0]:.1f} x {size[1]:.1f} x {size[2]:.1f} cm (left-right x front-back x top-bottom)")


if __name__ == "__main__":
    main()
