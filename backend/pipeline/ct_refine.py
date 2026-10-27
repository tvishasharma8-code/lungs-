"""
CT-refined lung mask: TotalSegmentator decides WHERE the lungs are and which lobe is
which; the CT itself, at its native resolution, decides the exact surface.

Why: TotalSegmentator segments at 1.5 mm (3 mm with --fast) and its masks are resampled
back to the CT grid, so their outline is soft and blocky - and smoothing that away also
erases real anatomy (fissures, rib impressions, crisp borders). A lung model made in
3D Slicer by thresholding the CT (the VR app's own LungModel.fbx) is crisper because
it follows the CT at native resolution. This module does the same automatically:

  1. lung air = HU below the threshold (Slicer's lung range, -400 by default)
  2. keep only air inside the AI lungs/trachea grown by a few mm (the AI excludes
     air outside the body, stomach/bowel gas, etc.)
  3. add the airway air that connects to it - the main bronchi and trachea run
     between the AI masks, and TotalSegmentator's trachea stops early
  4. fill vessels / airways-in-lung per axial slice, so the inside isn't full of tubes

The result is a mask on the CT grid; lobes are labelled afterwards from the AI masks
(lung_mesh.label_faces), and only light smoothing is applied.
"""

from __future__ import annotations

import numpy as np
from scipy import ndimage


def _odd_size(mm: float, voxel_mm: np.ndarray) -> tuple:
    """A filter kernel of about `mm` per axis, in voxels, always odd and at least 1."""
    return tuple(int(max(1, 2 * round(mm / v / 2.0) + 1)) if mm > 0 else 1 for v in voxel_mm)


def slicer_smooth(mask: np.ndarray, voxel_mm: np.ndarray, closing_mm: float = 2.0, median_mm: float = 3.0) -> np.ndarray:
    """
    The 3D Slicer Segment Editor's usual clean-up, on a binary mask:
      closing (fill small gaps/pits), then median (remove speckle and fringe).
    Median keeps real edges and corners where a Gaussian blur rounds them off, which is
    why a Slicer model looks crisp rather than melted.
    """
    if closing_mm > 0:
        mask = ndimage.binary_closing(mask, structure=np.ones(_odd_size(closing_mm, voxel_mm), bool))
    if median_mm > 0:
        mask = ndimage.median_filter(mask.astype(np.uint8), size=_odd_size(median_mm, voxel_mm)) > 0
    return mask


def refine_lung_mask(ct_hu: np.ndarray, ai_labels: np.ndarray, voxel_mm: np.ndarray,
                     hu_threshold: float = -400.0, grow_mm: float = 4.0,
                     airway_search_mm: float = 30.0, trachea_label: int | None = None,
                     closing_mm: float = 2.0, median_mm: float = 3.0,
                     fill_holes: bool = True, affine: np.ndarray | None = None,
                     airway_column_mm: float = 30.0) -> np.ndarray:
    """
    ct_hu:      CT volume in Hounsfield units, same grid as ai_labels.
    ai_labels:  0 = background, 1..K = TotalSegmentator structures.
    voxel_mm:   voxel size per array axis (mm).
    trachea_label + affine: when given, the airway is followed up through the
                connected air all the way to the top of the scan (see _extend_airway) -
                TotalSegmentator's trachea stops well below it.
    Returns a boolean lung + airway mask on the CT grid.
    """
    ai_full = ai_labels > 0
    if not ai_full.any():
        return ai_full

    # Work in a box around the AI lungs (plus the airway search margin): a whole CT is
    # ~90M voxels, and the distance map below would not fit in memory for all of it.
    margin = np.ceil(airway_search_mm / voxel_mm).astype(int) + 2
    nz = np.nonzero(ai_full)
    lo = np.maximum([int(a.min()) for a in nz] - margin, 0)
    hi = np.minimum([int(a.max()) + 1 for a in nz] + margin, ai_full.shape)
    box = tuple(slice(int(a), int(b)) for a, b in zip(lo, hi))

    ai = ai_full[box]
    air = ct_hu[box] < hu_threshold
    # Distance (mm) from the AI masks; cheaper than dilating with a large sphere.
    dist = ndimage.distance_transform_edt(~ai, sampling=voxel_mm).astype(np.float32)

    # 1-2. Lung air near where the AI found lungs/trachea.
    lung = air & (dist <= grow_mm)

    # 3. Airway: air components within a wider margin of the AI masks that touch them -
    # picks up the main bronchi and the rest of the trachea; leaves out stomach gas and
    # air outside the body, which never touch the AI lungs.
    search = air & (dist <= airway_search_mm)
    del dist
    comp, n = ndimage.label(search)
    if n:
        touching = np.unique(comp[lung & (comp > 0)])
        lung |= np.isin(comp, touching[touching > 0])

    # Keep the big connected parts (both lungs + airway); drop specks.
    comp, n = ndimage.label(lung)
    if n > 1:
        sizes = ndimage.sum(lung, comp, index=np.arange(1, n + 1))
        keep = np.nonzero(sizes >= 0.02 * sizes.max())[0] + 1
        lung = np.isin(comp, keep)

    # Slicer-style clean-up of the threshold's partial-volume fringe (bumpy trachea,
    # frayed edges) before the surface is built.
    lung = slicer_smooth(lung, voxel_mm, closing_mm=closing_mm, median_mm=median_mm)

    # 4. Vessels and small airways inside the lung are denser than air; fill them per
    # axial slice (like a Slicer "fill holes") so the model is solid lung, not a sponge.
    # Off: only cavities fully enclosed in 3D are filled, so vessels and airways that
    # reach the surface stay as the small channels and dents a Slicer threshold shows.
    if fill_holes:
        axial = int(np.argmax(voxel_mm))  # slice axis = the largest spacing on typical CT
        lung = np.moveaxis(lung, axial, 0)
        for i in range(lung.shape[0]):
            lung[i] = ndimage.binary_fill_holes(lung[i])
        lung = np.moveaxis(lung, 0, axial)
    else:
        lung = ndimage.binary_fill_holes(lung)

    out = np.zeros(ai_full.shape, dtype=bool)
    out[box] = lung

    if trachea_label is not None and affine is not None:
        _extend_airway(out, ct_hu, ai_labels == trachea_label, voxel_mm, affine,
                       hu_threshold, airway_column_mm)
    return out


def _extend_airway(mask: np.ndarray, ct_hu: np.ndarray, trachea: np.ndarray, voxel_mm: np.ndarray,
                   affine: np.ndarray, hu_threshold: float, column_mm: float) -> None:
    """
    Adds (in place) the airway above the lungs: the air connected to the trachea inside
    a column around it, from the trachea's lower end to the top of the scan. A hand-made
    3D Slicer model includes this (upper trachea, larynx); TotalSegmentator's trachea
    stops well below, and the 30 mm search around the AI masks didn't reach it. The
    column keeps the search away from air outside the body.
    """
    if not trachea.any():
        return

    # Which array axis runs head-to-foot, and which way is up (RAS/LPS z = superior).
    axis = int(np.argmax(np.abs(affine[2, :3])))
    superior_increasing = affine[2, axis] > 0

    nz = np.nonzero(trachea)
    region = []
    for a in range(3):
        lo, hi = int(nz[a].min()), int(nz[a].max()) + 1
        if a == axis:
            lo, hi = (lo, mask.shape[a]) if superior_increasing else (0, hi)
        else:
            m = int(np.ceil(column_mm / voxel_mm[a]))
            lo, hi = max(0, lo - m), min(mask.shape[a], hi + m)
        region.append(slice(lo, hi))
    region = tuple(region)

    air = ct_hu[region] < hu_threshold
    comp, n = ndimage.label(air)
    if not n:
        return
    seeds = np.unique(comp[mask[region] & air])
    seeds = seeds[seeds > 0]
    if len(seeds):
        mask[region] |= np.isin(comp, seeds)
