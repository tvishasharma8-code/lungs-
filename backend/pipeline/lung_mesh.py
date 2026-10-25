"""
Lung surface meshes in real patient coordinates, for the web viewer and the VR app.

The original pipeline ran marching cubes with `spacing=abs(diag(affine))`: correctly
scaled, but the scan's origin and axis directions were thrown away, so the mesh sat in
an arbitrary box. Tumor positions (DICOM patient coordinates, LPS mm - see
dicom_series.SliceInfo.pixel_to_lps) could not be placed on it.

Here the surface is extracted in voxel-index space and then pushed through the NIfTI's
full affine (index -> RAS mm) and flipped to LPS, giving vertices in exactly the frame
the tumor detections use.

For VR the surface is additionally decimated and every triangle is tagged with the
structure it belongs to (lobe or trachea), by sampling the label volume. One outer
surface, labelled - rather than one closed mesh per lobe - avoids duplicating every
fissure as internal faces and roughly halves the triangle count.
"""

from __future__ import annotations

import hashlib
import json
import struct
import time
from dataclasses import dataclass

import numpy as np
from skimage import measure
from skimage.filters import gaussian

MESH_MAGIC = b"LVMESH01"
MESH_VERSION = 1
PACKAGE_FORMAT = "lungvision-vr-package"
PACKAGE_VERSION = 1


@dataclass
class Surface:
    verts_lps: np.ndarray   # (N, 3) float64, DICOM patient coordinates, mm
    faces: np.ndarray       # (M, 3) int64, counter-clockwise seen from outside (outward normals)


def index_to_lps(points_ijk: np.ndarray, affine: np.ndarray) -> np.ndarray:
    """NIfTI voxel index coords -> RAS mm (the affine) -> LPS mm (negate x and y)."""
    ras = points_ijk @ affine[:3, :3].T + affine[:3, 3]
    return ras * np.array([-1.0, -1.0, 1.0])


def lps_to_index(points_lps: np.ndarray, affine: np.ndarray) -> np.ndarray:
    ras = points_lps * np.array([-1.0, -1.0, 1.0])
    inv = np.linalg.inv(affine)
    return ras @ inv[:3, :3].T + inv[:3, 3]


def signed_volume(verts: np.ndarray, faces: np.ndarray) -> float:
    a, b, c = verts[faces[:, 0]], verts[faces[:, 1]], verts[faces[:, 2]]
    return float(np.einsum("ij,ij->i", a, np.cross(b, c)).sum() / 6.0)


def extract_surface(mask: np.ndarray, affine: np.ndarray, sigma: float = 1.2) -> Surface:
    """Smoothed iso-surface of a binary mask, in LPS mm, normals pointing outward."""
    smooth = gaussian(mask.astype(np.float32), sigma=sigma, preserve_range=True)
    verts_ijk, faces, _, _ = measure.marching_cubes(smooth, level=0.5)
    verts = index_to_lps(verts_ijk.astype(np.float64), affine)
    faces = faces.astype(np.int64)
    # The affine may include a reflection, and LPS vs RAS is another; make the
    # winding outward in the final frame rather than reasoning about every flip.
    if signed_volume(verts, faces) < 0:
        faces = faces[:, ::-1].copy()
    return Surface(verts, faces)


def decimate(surface: Surface, target_triangles: int) -> Surface:
    """Quadric decimation to about `target_triangles` (no-op if already below)."""
    n = len(surface.faces)
    if n <= target_triangles:
        return surface
    import fast_simplification  # optional dependency, only needed for the VR package

    reduction = 1.0 - target_triangles / float(n)
    verts, faces = fast_simplification.simplify(
        surface.verts_lps.astype(np.float32), surface.faces.astype(np.int32), target_reduction=reduction
    )
    verts = verts.astype(np.float64)
    faces = faces.astype(np.int64)
    if signed_volume(verts, faces) < 0:
        faces = faces[:, ::-1].copy()
    return Surface(verts, faces)


def label_faces(surface: Surface, labels: np.ndarray, affine: np.ndarray) -> np.ndarray:
    """
    Structure label (1..K) for every triangle. Each vertex takes the most common
    non-zero label within 1 voxel of it (2 voxels for the few that find none - the
    smoothing can move the surface about a voxel off the mask); a triangle takes the
    label two of its vertices agree on, else its first vertex's.
    """
    ijk = lps_to_index(surface.verts_lps, affine)
    shape = np.array(labels.shape)
    k = int(labels.max())
    vertex_label = np.zeros(len(ijk), dtype=np.int16)

    def vote(idx: np.ndarray, radius: int) -> np.ndarray:
        counts = np.zeros((len(idx), k + 1), dtype=np.int32)
        base = np.rint(ijk[idx]).astype(np.int64)
        rng = range(-radius, radius + 1)
        for dx in rng:
            for dy in rng:
                for dz in rng:
                    p = np.clip(base + (dx, dy, dz), 0, shape - 1)
                    lab = labels[p[:, 0], p[:, 1], p[:, 2]]
                    counts[np.arange(len(idx)), lab] += 1
        counts[:, 0] = 0
        best = counts.argmax(axis=1)
        best[counts.max(axis=1) == 0] = 0
        return best.astype(np.int16)

    all_idx = np.arange(len(ijk))
    vertex_label[:] = vote(all_idx, 1)
    missing = np.nonzero(vertex_label == 0)[0]
    if len(missing):
        vertex_label[missing] = vote(missing, 2)

    l0, l1, l2 = (vertex_label[surface.faces[:, i]] for i in range(3))
    face_label = np.where(l1 == l2, l1, l0)
    # A face whose chosen vertex still has no label borrows any labelled vertex's.
    unl = face_label == 0
    face_label[unl] = np.maximum(np.maximum(l0[unl], l1[unl]), l2[unl])
    return face_label.astype(np.int16)


def write_mesh_bin(path: str, surface: Surface, face_labels: np.ndarray, names: list[str]) -> list[dict]:
    """
    Binary mesh for the VR app (little-endian):
        8s   magic "LVMESH01"
        u32  version, u32 vertex_count, u32 submesh_count
        per submesh:  u8 name_length, name (utf-8), u32 index_count
        f32  vertex_count * 3   positions, DICOM patient coordinates (LPS, mm)
        u32  indices of each submesh in turn (triangles, outward winding in LPS)
    Sub-mesh i holds the triangles labelled i+1 (names[i]). Returns per-structure stats.
    """
    groups = []
    for i, name in enumerate(names):
        tri = surface.faces[face_labels == i + 1]
        groups.append((name, tri))
    unlabeled = surface.faces[face_labels == 0]
    if len(unlabeled):
        groups.append(("unlabeled", unlabeled))
    groups = [(n, t) for n, t in groups if len(t)]

    with open(path, "wb") as fh:
        fh.write(MESH_MAGIC)
        fh.write(struct.pack("<III", MESH_VERSION, len(surface.verts_lps), len(groups)))
        for name, tri in groups:
            raw = name.encode("utf-8")[:255]
            fh.write(struct.pack("<B", len(raw)))
            fh.write(raw)
            fh.write(struct.pack("<I", tri.size))
        fh.write(surface.verts_lps.astype("<f4").tobytes())
        for _, tri in groups:
            fh.write(tri.astype("<u4").tobytes())

    return [{"name": n, "triangles": int(len(t))} for n, t in groups]


def write_meta(path: str, surface: Surface, structures: list[dict], series_uid: str,
               slice_count: int, voxel_spacing_mm: list[float], mesh_file: str) -> dict:
    lo = surface.verts_lps.min(axis=0)
    hi = surface.verts_lps.max(axis=0)
    meta = {
        "format": PACKAGE_FORMAT,
        "version": PACKAGE_VERSION,
        "frame": "DICOM_LPS_mm",
        "frame_note": "x = patient Left, y = Posterior, z = Superior; millimetres. "
                      "Same frame as tumor detections' center_mm.",
        "mesh_file": mesh_file,
        "center_mm": [round(float(v), 3) for v in (lo + hi) / 2.0],
        "bbox_min_mm": [round(float(v), 2) for v in lo],
        "bbox_max_mm": [round(float(v), 2) for v in hi],
        "vertices": int(len(surface.verts_lps)),
        "triangles": int(len(surface.faces)),
        "structures": structures,
        "source": {
            # A one-way hash: lets results be matched to the same scan without
            # carrying the real UID (or any path / name) around.
            "series_id": hashlib.sha1(series_uid.encode("utf-8")).hexdigest()[:16],
            "slices": int(slice_count),
            "voxel_spacing_mm": [round(float(v), 4) for v in voxel_spacing_mm],
        },
        "created_utc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
    }
    with open(path, "w", encoding="utf-8") as fh:
        json.dump(meta, fh, indent=2)
    return meta
