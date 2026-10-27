"""
Groups per-slice detections into tumors (integration plan I1.3), in patient mm.

The model looks at one CT slice at a time, so a single tumor shows up as a box on
each of several neighbouring slices. Two detections are taken to be the same tumor
when they are on nearby scanned slices (a gap of one missed slice is allowed) and
their centres are close in the slice plane, relative to the box size. Linked
detections form one tumor (union-find, so the result doesn't depend on order -
unlike growing clusters around a running mean, which the ML team's Unity prototype
did with a fixed 30 mm radius).

A tumor seen on only one scanned slice is dropped as a likely false alarm (a real
nodule of a few mm or more spans 2+ slices at the scan spacing tumor_infer uses).

Output positions are DICOM patient coordinates (LPS, mm) - the frame of the VR mesh
(lung_mesh.py) - so the VR app can place markers directly in the patient's lungs.
"""

from __future__ import annotations

import math
from dataclasses import dataclass, field
from typing import Optional

import numpy as np

from pipeline.tumor_infer import Detection, SeriesScan


@dataclass
class Tumor:
    id: str
    detections: list[Detection]
    slice_indices: list[int]                 # scanned slices it appears on, body order
    max_confidence: float
    mean_confidence: float
    key: Detection                           # most confident detection (for display)
    center_mm: Optional[list[float]] = None  # confidence-weighted centre, LPS
    bbox_min_mm: Optional[list[float]] = None
    bbox_max_mm: Optional[list[float]] = None
    size_mm: Optional[list[float]] = None    # bbox extent per LPS axis
    volume_mm3: Optional[float] = None       # rough: ellipse per slice x slice spacing
    z_range_mm: Optional[list[float]] = None
    extra: dict = field(default_factory=dict)


def _pos(d: Detection, scan: SeriesScan) -> np.ndarray:
    if d.center_mm is not None:
        return np.asarray(d.center_mm)
    # No geometry: pixels and slice steps stand in for mm (good enough for grouping).
    x0, y0, x1, y1 = d.box
    return np.array([(x0 + x1) / 2, (y0 + y1) / 2, -d.slice_index * scan.step_mm])


def _size(d: Detection) -> float:
    """Larger in-plane side of the box, mm (pixels without geometry)."""
    if d.box_min_mm is not None:
        return float(np.max(np.asarray(d.box_max_mm) - np.asarray(d.box_min_mm)))
    x0, y0, x1, y1 = d.box
    return max(x1 - x0, y1 - y0)


def cluster(scan: SeriesScan, min_slices: int = 2, min_link_mm: float = 10.0) -> tuple[list[Tumor], int]:
    """
    Returns (tumors sorted by confidence, number of single-slice detections dropped).
    """
    dets = scan.detections
    n = len(dets)
    if n == 0:
        return [], 0

    pos = [_pos(d, scan) for d in dets]
    size = [_size(d) for d in dets]
    # Neighbouring slices: at most one scanned slice missed in between (by rank, as the
    # spacing can vary within a series), or within 6 mm when scanning densely.
    rank = {idx: k for k, idx in enumerate(scan.scanned)}

    parent = list(range(n))

    def find(i: int) -> int:
        while parent[i] != i:
            parent[i] = parent[parent[i]]
            i = parent[i]
        return i

    for i in range(n):
        for j in range(i + 1, n):
            if dets[i].slice_index == dets[j].slice_index:
                continue  # two boxes on one slice are two findings, not one
            d = pos[i] - pos[j]
            ranks_apart = abs(rank.get(dets[i].slice_index, 0) - rank.get(dets[j].slice_index, 0))
            if ranks_apart > 2 and abs(d[2]) > 6.0:
                continue
            if math.hypot(d[0], d[1]) <= max(min_link_mm, 0.6 * max(size[i], size[j])):
                parent[find(i)] = find(j)

    groups: dict[int, list[int]] = {}
    for i in range(n):
        groups.setdefault(find(i), []).append(i)

    tumors, dropped = [], 0
    for members in groups.values():
        ds = [dets[i] for i in members]
        slices = sorted({d.slice_index for d in ds})
        if len(slices) < min_slices:
            dropped += len(ds)
            continue
        scores = np.array([d.score for d in ds])
        key = ds[int(np.argmax(scores))]
        t = Tumor(
            id="",
            detections=sorted(ds, key=lambda d: d.slice_index),
            slice_indices=slices,
            max_confidence=round(float(scores.max()), 3),
            mean_confidence=round(float(scores.mean()), 3),
            key=key,
        )
        if all(d.center_mm is not None for d in ds):
            _measure(t, scan)
        tumors.append(t)

    tumors.sort(key=lambda t: (t.max_confidence, len(t.slice_indices)), reverse=True)
    for k, t in enumerate(tumors, 1):
        t.id = f"T{k}"
    return tumors, dropped


def _measure(t: Tumor, scan: SeriesScan) -> None:
    ds = t.detections
    w = np.array([d.score for d in ds])
    centres = np.array([d.center_mm for d in ds])
    t.center_mm = [round(float(v), 2) for v in (centres * w[:, None]).sum(0) / w.sum()]

    lo = np.min([d.box_min_mm for d in ds], axis=0)
    hi = np.max([d.box_max_mm for d in ds], axis=0)
    zs = centres[:, 2]
    first, last = t.slice_indices[0], t.slice_indices[-1]
    lo[2] = zs.min() - scan.slab_mm.get(last, scan.step_mm) / 2   # each scanned slice stands
    hi[2] = zs.max() + scan.slab_mm.get(first, scan.step_mm) / 2  # for its slab of the body
    t.bbox_min_mm = [round(float(v), 2) for v in lo]
    t.bbox_max_mm = [round(float(v), 2) for v in hi]
    t.size_mm = [round(float(v), 1) for v in hi - lo]
    t.z_range_mm = [round(float(zs.min()), 2), round(float(zs.max()), 2)]

    # Volume: per slice, an ellipse inscribed in the largest box, times the slice step.
    area_by_slice: dict[int, float] = {}
    for d in ds:
        e = np.asarray(d.box_max_mm) - np.asarray(d.box_min_mm)
        a, b = sorted(e)[-2:]  # the two in-plane extents (the third is ~0)
        area_by_slice[d.slice_index] = max(area_by_slice.get(d.slice_index, 0.0), math.pi / 4 * a * b)
    t.volume_mm3 = round(sum(a * scan.slab_mm.get(i, scan.step_mm) for i, a in area_by_slice.items()), 0)


def locate(tumors: list[Tumor], labels_npz: str, near_mm: float = 5.0) -> None:
    """
    Sets t.extra["location"] from TotalSegmentator's masks (saved by the reconstruction
    as labels.npz): the structure most of the tumor's detection centres fall in -
    within `near_mm`, as a tumor at the lung's edge pushes the AI outline - else
    "outside the lungs" (on A0265 that is where hilum vessels get flagged).
    """
    from pipeline.lung_mesh import lps_to_index, voxel_spacing_mm

    data = np.load(labels_npz)
    labels, affine, names = data["labels"], data["affine"], [str(n) for n in data["names"]]
    spacing = voxel_spacing_mm(affine)
    r = np.ceil(near_mm / spacing).astype(int)

    def structure_at(p_lps) -> str:
        c = np.round(lps_to_index(np.asarray(p_lps)[None], affine)[0]).astype(int)
        if not all(0 <= v < s for v, s in zip(c, labels.shape)):
            return "outside the scan"
        lo = np.maximum(c - r, 0)
        win = labels[lo[0]:c[0] + r[0] + 1, lo[1]:c[1] + r[1] + 1, lo[2]:c[2] + r[2] + 1]
        hit = np.argwhere(win > 0)
        if len(hit) == 0:
            return "outside the lungs"
        d = np.linalg.norm((hit + lo - c) * spacing, axis=1)
        if d.min() > near_mm:
            return "outside the lungs"
        return names[int(win[tuple(hit[int(np.argmin(d))])]) - 1]

    for t in tumors:
        if any(d.center_mm is None for d in t.detections):
            continue
        votes: dict[str, int] = {}
        for d in t.detections:
            name = structure_at(d.center_mm)
            votes[name] = votes.get(name, 0) + 1
        t.extra["location"] = max(votes, key=votes.get)


def findings_document(tumors: list[Tumor], scan: SeriesScan, threshold: float, dropped: int) -> dict:
    """
    findings.json of the Patient Package (see INTEGRATION_PLAN.md): tumors in LPS mm plus
    the flagged slices with their boxes, slices in body order (index 0 = most superior).
    Each flagged slice's image is slices/NNN.png in the package (NNN = slice index).
    """
    slices: dict[int, dict] = {}
    for t in tumors:
        for d in t.detections:
            s = slices.setdefault(d.slice_index, {
                "index": d.slice_index,
                "image": f"slices/{d.slice_index:03d}.png",
                "width": d.slice_width,
                "height": d.slice_height,
                "z_mm": round(d.center_mm[2], 2) if d.center_mm else None,
                "detections": [],
            })
            s["detections"].append({
                "tumor": t.id,
                "confidence": round(d.score, 3),
                "box_px": [round(v, 1) for v in d.box],
                "center_mm": [round(v, 2) for v in d.center_mm] if d.center_mm else None,
            })

    return {
        "frame": "DICOM_LPS_mm",
        "model": "tumor_model_v5 (Faster R-CNN)",
        "confidence_threshold": threshold,
        "scan": {
            "total_slices": scan.total_slices,
            "scanned_slices": len(scan.scanned),
            "step_mm": round(scan.step_mm, 2),
            "has_geometry": scan.has_geometry,
        },
        "single_slice_detections_dropped": dropped,
        "tumors": [{
            "id": t.id,
            "center_mm": t.center_mm,
            "bbox_min_mm": t.bbox_min_mm,
            "bbox_max_mm": t.bbox_max_mm,
            "size_mm": t.size_mm,
            "volume_mm3": t.volume_mm3,
            "max_confidence": t.max_confidence,
            "mean_confidence": t.mean_confidence,
            "detections": len(t.detections),
            "slice_range": [t.slice_indices[0], t.slice_indices[-1]],
            "z_range_mm": t.z_range_mm,
            "key_slice": t.key.slice_index,
            "location": t.extra.get("location"),
        } for t in tumors],
        "slices": [slices[k] for k in sorted(slices)],
        "note": "Research prototype - not a medical diagnosis.",
    }
