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
    is the top of the chest), the same order the ML team's tumor_server.py used;
  * geometry: each slice keeps its ImagePositionPatient / ImageOrientationPatient /
    PixelSpacing, so a pixel can be turned into DICOM patient coordinates (LPS, mm) -
    the frame the reconstructed mesh is written in too (see run_totalseg.py).

Folders are searched recursively, because uploads now keep their sub-folder structure
(see main.py) instead of flattening every file into one directory.
"""

from __future__ import annotations

import os
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
