"""
Shared DICOM series loading for every LungVision pipeline.

Reconstruction, tumor detection and the slice-image endpoint all used to pick and
order slices on their own (detection sorted by FILE NAME, the ML team's Unity server by
body position). A slice index only means something if every part agrees on it, so this
module is now the single place that decides:

  * which series is "the CT": the largest series in the folder (as before), using only
    slices that share its orientation, so a scout/localizer image mixed into the series
    can't break the ordering;
  * slice order: by real position along the slice normal, MOST SUPERIOR FIRST (index 0
    is the top of the chest), the same order the ML team's prototype tumor_server.py used (in git history);
  * geometry: each slice keeps its ImagePositionPatient / ImageOrientationPatient /
    PixelSpacing, so a pixel can be turned into DICOM patient coordinates (LPS, mm) -
    the frame the reconstructed mesh is written in too (see run_totalseg.py).

Folders are searched recursively, because uploads now keep their sub-folder structure
(see main.py) instead of flattening every file into one directory.
"""

from __future__ import annotations

import os
import threading
from collections import defaultdict
from dataclasses import dataclass
from typing import Optional

import numpy as np
from pydicom import dcmread


@dataclass
class SliceInfo:
    path: str
    position: np.ndarray        # ImagePositionPatient: LPS mm of the first pixel's centre
    row_dir: np.ndarray         # direction of increasing column index (along a row)
    col_dir: np.ndarray         # direction of increasing row index (down a column)
    row_spacing: float          # mm between rows    (PixelSpacing[0])
    col_spacing: float          # mm between columns (PixelSpacing[1])
    rows: int
    cols: int
    instance_number: int

    def pixel_to_lps(self, col: float, row: float) -> np.ndarray:
        """Pixel (col = x, row = y; fractional allowed) -> DICOM patient coordinates, mm."""
        return self.position + self.row_dir * (col * self.col_spacing) + self.col_dir * (row * self.row_spacing)


@dataclass
class DicomSeries:
    series_uid: str
    slices: list[SliceInfo]     # index 0 = most superior slice
    series_count: int           # how many series were in the folder
    has_geometry: bool          # False if position/orientation tags were missing

    @property
    def paths(self) -> list[str]:
        return [s.path for s in self.slices]

    def __len__(self) -> int:
        return len(self.slices)

    def pixel_to_lps(self, slice_index: int, col: float, row: float) -> Optional[list[float]]:
        """LPS mm for a pixel on slice `slice_index`, or None without geometry."""
        if not self.has_geometry:
            return None
        return [round(float(v), 2) for v in self.slices[slice_index].pixel_to_lps(col, row)]


def iter_files(folder: str):
    for root, _dirs, files in os.walk(folder):
        for name in files:
            yield os.path.join(root, name)


def _floats(value, n: int) -> Optional[np.ndarray]:
    try:
        arr = np.array([float(v) for v in value], dtype=np.float64)
        return arr if arr.shape == (n,) else None
    except Exception:
        return None


def load_series(folder: str) -> DicomSeries:
    """
    Picks the CT series in `folder` and returns its slices in body order with geometry.
    Raises FileNotFoundError if the folder holds no image DICOM files.
    """
    by_series: dict[str, list] = defaultdict(list)
    for path in iter_files(folder):
        try:
            ds = dcmread(path, stop_before_pixels=True)
        except Exception:
            continue
        if not hasattr(ds, "Rows") or not hasattr(ds, "Columns"):
            continue  # not an image (report, dose sheet, ...)
        by_series[str(getattr(ds, "SeriesInstanceUID", "unknown"))].append((path, ds))

    if not by_series:
        raise FileNotFoundError(f"No image DICOM files found in {folder}")

    series_uid, members = max(by_series.items(), key=lambda kv: len(kv[1]))

    parsed = []
    for path, ds in members:
        pos = _floats(getattr(ds, "ImagePositionPatient", None) or [], 3)
        iop = _floats(getattr(ds, "ImageOrientationPatient", None) or [], 6)
        spacing = _floats(getattr(ds, "PixelSpacing", None) or [], 2)
        parsed.append((path, ds, pos, iop, spacing))

    has_geometry = all(p[2] is not None and p[3] is not None and p[4] is not None for p in parsed)

    if has_geometry:
        # Keep only slices with the series' dominant orientation (drops a stray scout).
        iops = [tuple(np.round(p[3], 3)) for p in parsed]
        dominant = max(set(iops), key=iops.count)
        parsed = [p for p, o in zip(parsed, iops) if o == dominant]
        row_dir = np.array(dominant[0:3])
        col_dir = np.array(dominant[3:6])
        normal = np.cross(row_dir, col_dir)
        # Body order: distance along the slice normal. For ordinary axial CT the normal
        # is +z (superior), so sorting descending puts the top of the chest first.
        # Normals of other acquisitions are made to point superior-ish for consistency.
        if normal[2] < 0:
            normal = -normal
        parsed.sort(key=lambda p: float(np.dot(p[2], normal)), reverse=True)
    else:
        parsed.sort(key=lambda p: int(getattr(p[1], "InstanceNumber", 0) or 0))

    slices = []
    for path, ds, pos, iop, spacing in parsed:
        slices.append(SliceInfo(
            path=path,
            position=pos if pos is not None else np.zeros(3),
            row_dir=iop[0:3] if iop is not None else np.array([1.0, 0.0, 0.0]),
            col_dir=iop[3:6] if iop is not None else np.array([0.0, 1.0, 0.0]),
            row_spacing=float(spacing[0]) if spacing is not None else 1.0,
            col_spacing=float(spacing[1]) if spacing is not None else 1.0,
            rows=int(ds.Rows),
            cols=int(ds.Columns),
            instance_number=int(getattr(ds, "InstanceNumber", 0) or 0),
        ))

    return DicomSeries(
        series_uid=series_uid,
        slices=slices,
        series_count=len(by_series),
        has_geometry=has_geometry,
    )


_series_cache: dict[tuple, DicomSeries] = {}
_series_cache_lock = threading.Lock()


def cached_series(folder: str) -> DicomSeries:
    """
    load_series(), cached per folder. Reconstruction, detection, the VR slice images and
    the slice-image endpoint all need the same series; reading every header takes ~3 s
    on a 1,400-file upload, and it used to happen three times per job. An upload folder
    never changes after the upload; its modification time is part of the key anyway.
    Raises FileNotFoundError like load_series (also for a deleted folder).
    """
    try:
        key = (os.path.abspath(folder), os.stat(folder).st_mtime_ns)
    except OSError as exc:
        raise FileNotFoundError(folder) from exc
    with _series_cache_lock:
        cached = _series_cache.get(key)
    if cached is None:
        cached = load_series(folder)
        with _series_cache_lock:
            if len(_series_cache) >= 8:
                _series_cache.pop(next(iter(_series_cache)))
            _series_cache[key] = cached
    return cached


def safe_relative_path(name: str) -> Optional[str]:
    """
    Turns an uploaded file's name (the browser sends its path inside the chosen folder,
    e.g. "CT_Scan/series2/IM-0001") into a safe relative path, or None if nothing usable
    is left. Keeping the sub-folders matters: DICOM exports often reuse the same file
    names in every series folder, and flattening to the base name silently overwrote
    slices of one series with another's.
    """
    parts = []
    for part in name.replace("\\", "/").split("/"):
        part = part.strip()
        if part in ("", ".", ".."):
            continue
        # Drop drive letters / characters Windows can't store.
        part = "".join(ch for ch in part if ch not in '<>:"|?*').strip()
        if part:
            parts.append(part)
    return os.path.join(*parts) if parts else None


def has_even_spacing(series: DicomSeries, tolerance: float = 0.01) -> bool:
    """Whether consecutive slices are the same distance apart (within `tolerance`, as a
    fraction of the typical gap) - what dicom2nifti requires."""
    if not series.has_geometry or len(series.slices) < 3:
        return True
    s0 = series.slices[0]
    normal = np.cross(s0.row_dir, s0.col_dir)
    gaps = np.abs(np.diff([float(np.dot(s.position, normal)) for s in series.slices]))
    typical = float(np.median(gaps))
    return typical > 0 and float(np.max(np.abs(gaps - typical))) <= tolerance * typical


def write_uniform_nifti(series: DicomSeries, out_path: str) -> float:
    """
    Writes the series as a NIfTI volume (Hounsfield units, int16) on an EVEN slice grid,
    interpolating linearly between neighbouring slices from their real positions.
    Returns the slice step used (mm; the series' most common spacing).

    For series dicom2nifti refuses ("slice increment not consistent" - a missing slice,
    or sparser slices at one end; A0265 has 0.7 mm spacing with 5-21 mm gaps at the
    bottom). Turning its check off instead would assume even spacing and misplace
    every slice after the first gap. The affine maps voxels to the true patient
    positions, so the mesh and tumor positions stay in the same mm frame.
    """
    import nibabel as nib

    if not series.has_geometry or len(series.slices) < 2:
        raise ValueError("Needs slice positions/orientation to resample.")
    s0 = series.slices[0]
    normal = np.cross(s0.row_dir, s0.col_dir)
    # Distance of each slice from the first, towards the last (inferior).
    t = np.array([float(np.dot(s.position - s0.position, normal)) for s in series.slices])
    direction = -normal if t[-1] < 0 else normal
    t = np.abs(t)
    gaps = np.diff(t)
    step = float(np.median(gaps[gaps > 1e-3]))
    nz = int(round(t[-1] / step)) + 1

    def hu(k: int) -> np.ndarray:
        ds = dcmread(series.slices[k].path)
        img = ds.pixel_array.astype(np.float32) * float(getattr(ds, "RescaleSlope", 1) or 1) \
            + float(getattr(ds, "RescaleIntercept", 0) or 0)
        return img.T  # NIfTI axes: i = column, j = row

    vol = np.empty((s0.cols, s0.rows, nz), dtype=np.int16)
    cache: dict[int, np.ndarray] = {}
    for z in range(nz):
        target = z * step
        k = int(np.clip(np.searchsorted(t, target, side="right") - 1, 0, len(t) - 2))
        w = float(np.clip((target - t[k]) / max(t[k + 1] - t[k], 1e-6), 0.0, 1.0))
        for need in (k, k + 1):
            if need not in cache:
                cache[need] = hu(need)
        for old in [c for c in cache if c < k]:
            del cache[old]
        vol[:, :, z] = np.round((1 - w) * cache[k] + w * cache[k + 1]).astype(np.int16)

    # Stored bottom slice first, as dicom2nifti does (on patient 11 this volume and
    # dicom2nifti's are voxel-identical, same affine).
    lps = np.eye(4)
    lps[:3, 0] = s0.row_dir * s0.col_spacing
    lps[:3, 1] = s0.col_dir * s0.row_spacing
    lps[:3, 2] = -direction * step
    lps[:3, 3] = s0.position + direction * step * (nz - 1)
    ras = np.diag([-1.0, -1.0, 1.0, 1.0]) @ lps
    nib.save(nib.Nifti1Image(np.ascontiguousarray(vol[:, :, ::-1]), ras), out_path)
    return step
