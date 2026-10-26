"""
Real tumor/nodule detection inference using a fine-tuned torchvision
Faster R-CNN (ResNet-50 + FPN) model.

This module owns everything related to loading the model and running
inference. tumor_model_v4.pth is a state_dict trained with 2 classes
(0 = background, 1 = tumor) on torchvision's standard
fasterrcnn_resnet50_fpn architecture, confirmed both by inspecting the
checkpoint's tensor shapes directly AND by reading the team's actual
training code (train_model_v2.py / process_all_patients.py) - so the
preprocessing below is not a guess, it's a direct port of that code:

  process_all_patients.py (dataset build):
      pixels = d.pixel_array.astype(np.float32)
      pixels -= pixels.min()
      if pixels.max() > 0: pixels /= pixels.max()
      pixels *= 255.0
      img = Image.fromarray(pixels.astype(np.uint8))   # grayscale, NATIVE size - no resize
      img.save(...)                                     # -> PNG

  train_model_v2.py (TumorDataset.__getitem__):
      img = Image.open(png_path).convert("RGB")          # grayscale -> RGB (channel replication)
      img = torchvision.transforms.functional.to_tensor(img)  # uint8 [0,255] -> float [0,1]

  Faster R-CNN was trained with num_classes=2 (background + tumor),
  using torchvision.models.detection.faster_rcnn.FastRCNNPredictor to
  replace the box predictor on a COCO-pretrained backbone.

Important: there is NO resize step anywhere in the training pipeline -
each slice is used at its native DICOM resolution. preprocess_slice()
below reproduces that exactly. Detection boxes returned by the model
are therefore in that slice's native pixel coordinate space, and
run_inference_on_series() uses each slice's actual (width, height) -
not a fixed size - when normalizing box coordinates to [0,1].

Also: no RescaleSlope/RescaleIntercept (Hounsfield unit) conversion is
applied - process_all_patients.py normalizes raw pixel_array values
directly, so this module does too.
"""

from __future__ import annotations

import os
import threading
from dataclasses import dataclass
from typing import Callable, Optional

import numpy as np
import torch
import torchvision
from PIL import Image
from pydicom import dcmread

from pipeline.dicom_series import load_series

# Filename matches what the team currently hands off. If a future
# retrain produces a differently-named file, either rename it to this
# on copy, or update this constant.
MODEL_PATH = os.path.join(os.path.dirname(__file__), "weights", "tumor_model_v5.pth")
NUM_CLASSES = 2  # background + tumor
CLASS_NAMES = {1: "Tumor"}
DEVICE = torch.device("cuda" if torch.cuda.is_available() else "cpu")

_model: Optional[torch.nn.Module] = None
_model_lock = threading.Lock()
_series_cache: dict[tuple, list[str]] = {}
_series_cache_lock = threading.Lock()


@dataclass
class Detection:
    slice_index: int
    label: str
    score: float
    box: tuple[float, float, float, float]  # native pixel coords: xmin, ymin, xmax, ymax
    slice_width: int   # the specific slice's actual width (native resolution)
    slice_height: int  # the specific slice's actual height (native resolution)
    # Where it is in the patient (DICOM LPS mm, the frame the VR mesh uses), from the
    # slice's own geometry; None if the series has no position/orientation tags.
    center_mm: Optional[tuple[float, float, float]] = None
    box_min_mm: Optional[tuple[float, float, float]] = None  # in-plane box corners, LPS
    box_max_mm: Optional[tuple[float, float, float]] = None


@dataclass
class SeriesScan:
    detections: list[Detection]
    total_slices: int
    scanned: list[int]          # slice indices the model ran on, in body order
    step_mm: float              # target spacing between scanned slices
    has_geometry: bool
    slab_mm: dict[int, float]   # scanned slice -> mm of body it stands for


def _load_model() -> torch.nn.Module:
    global _model
    with _model_lock:
        if _model is not None:
            return _model
        if not os.path.exists(MODEL_PATH):
            raise FileNotFoundError(
                f"Tumor model weights not found at {MODEL_PATH}. "
                "Copy tumor_model_v5.pth into backend/pipeline/weights/."
            )
        model = torchvision.models.detection.fasterrcnn_resnet50_fpn(
            weights=None, weights_backbone=None, num_classes=NUM_CLASSES
        )
        state_dict = torch.load(MODEL_PATH, map_location="cpu")
        model.load_state_dict(state_dict)
        model.to(DEVICE).eval()
        _model = model
        return _model


def normalize_to_grayscale_uint8(pixel_array: np.ndarray) -> np.ndarray:
    """
    Per-slice min-max normalize to 0-255 uint8 - the exact first step
    of process_all_patients.py. Exposed separately (not just inlined in
    preprocess_slice) so the slice-image endpoint in main.py can render
    a human-viewable PNG of exactly what the model saw, without
    duplicating this math.
    """
    arr = pixel_array.astype(np.float32)
    arr = arr - arr.min()
    if arr.max() > 0:
        arr = arr / arr.max()
    arr = arr * 255.0
    return arr.astype(np.uint8)


def preprocess_slice(pixel_array: np.ndarray) -> torch.Tensor:
    """
    Raw DICOM pixel array -> 3xHxW float tensor in [0,1], reproducing
    process_all_patients.py + train_model_v2.py exactly:
      1. Per-slice min-max normalize to 0-255 (uint8) - no HU windowing,
         no rescale slope/intercept.
      2. Grayscale -> RGB (channel replication), matching .convert("RGB").
      3. NO resize - native slice resolution is preserved.
      4. Scale to [0,1], matching to_tensor().
    """
    gray = normalize_to_grayscale_uint8(pixel_array)
    img = Image.fromarray(gray).convert("RGB")
    tensor = torch.from_numpy(np.array(img)).permute(2, 0, 1).float() / 255.0  # 3xHxW in [0,1]
    return tensor


def list_series_files(dicom_dir: str) -> list[str]:
    """The CT series' files in body order (index 0 = most superior slice),
    from the shared loader that reconstruction uses too.

    Public (no leading underscore) because main.py's slice-image
    endpoint calls this too, and it's critical that it uses the exact
    same ordering run_inference_on_series used - a `slice_index` only
    means anything if both sides agree on which file that index maps
    to. (It used to sort by file name, which is not body order on many
    exports and disagreed with the ML team's Unity tumor server.)

    Cached per folder: reading every header takes ~3 s on a 1,400-file upload, and the
    results page asks for a slice image per finding (up to ~30). An upload folder never
    changes after the upload; the folder's modification time is part of the key anyway.
    """
    try:
        key = (os.path.abspath(dicom_dir), os.stat(dicom_dir).st_mtime_ns)
    except OSError:
        return []
    with _series_cache_lock:
        cached = _series_cache.get(key)
    if cached is None:
        try:
            cached = load_series(dicom_dir).paths
        except FileNotFoundError:
            return []
        with _series_cache_lock:
            if len(_series_cache) >= 8:
                _series_cache.pop(next(iter(_series_cache)))
            _series_cache[key] = cached
    return list(cached)  # a deleted upload fails os.stat above


def scan_indices(series, step_mm: float, max_slices: int) -> tuple[list[int], float]:
    """
    Which slices to run the model on: the next slice at least `step_mm` further along
    the body each time, over the WHOLE series. (It used to be every 3rd slice capped at
    150 - on a thin-slice scan the cap was reached part-way down and the lower lungs
    were never looked at.) Uses each slice's real position, since spacing can change
    within a series (A0265: 0.7 mm, then gaps of 5-21 mm at the bottom). If that would
    be more than `max_slices`, the step widens instead.
    """
    n = len(series.slices)
    if n == 0:
        return [], step_mm
    if not series.has_geometry:
        stride = max(1, int(np.ceil(n / max(1, max_slices))))
        return list(range(0, n, stride)), float(stride)

    z = [float(s.position[2]) for s in series.slices]
    while True:
        indices = [0]
        for i in range(1, n):
            if abs(z[i] - z[indices[-1]]) >= step_mm - 1e-6:
                indices.append(i)
        if len(indices) <= max_slices:
            break
        step_mm *= len(indices) / max_slices
    if indices[-1] != n - 1:
        indices.append(n - 1)
    return indices, step_mm


def slab_thickness(series, indices: list[int]) -> dict[int, float]:
    """How much of the body (mm along z) each scanned slice stands for: half the gap
    to the scanned slice before and after it. Used for tumor volumes."""
    if not series.has_geometry or len(indices) < 2:
        return {i: 1.0 for i in indices}
    z = [float(series.slices[i].position[2]) for i in indices]
    out = {}
    for k, i in enumerate(indices):
        before = abs(z[k] - z[k - 1]) if k > 0 else abs(z[1] - z[0])
        after = abs(z[k + 1] - z[k]) if k + 1 < len(z) else before
        out[i] = (before + after) / 2
    return out


def run_inference_on_series(
    dicom_dir: str,
    confidence_threshold: float = 0.5,
    slice_step_mm: float = 2.5,
    max_slices: int = 300,
    progress: Optional[Callable[[int, int], None]] = None,
) -> SeriesScan:
    """
    Runs the Faster R-CNN model over the series, one slice per forward pass (matching
    training - the model never sees a batched volume), about every `slice_step_mm`.

    Faster R-CNN is ~2.5 s per 512x512 slice on CPU, so scanning every slice of a
    thin-slice CT would take far too long; a tumor worth showing spans several mm,
    so every ~2.5 mm still sees it on 2+ slices (tumor_cluster.py relies on that).
    (On A0265, every 0.7 mm slice vs every ~2.5 mm: same 3 tumors, centres within 0.5 mm.)
    Uses the GPU when there is one.

    Every detection also gets its position in patient mm (LPS), from the slice's own
    DICOM geometry - the frame the VR lung mesh is in.
    """
    model = _load_model()
    try:
        series = load_series(dicom_dir)
    except FileNotFoundError:
        return SeriesScan([], 0, [], slice_step_mm, False, {})

    indices, step = scan_indices(series, slice_step_mm, max_slices)
    detections: list[Detection] = []

    for i, idx in enumerate(indices):
        info = series.slices[idx]
        dcm = dcmread(info.path)
        pixel_array = dcm.pixel_array
        h, w = pixel_array.shape[-2], pixel_array.shape[-1]
        tensor = preprocess_slice(pixel_array).to(DEVICE)  # one slice -> one forward pass

        with torch.no_grad():
            output = model([tensor])[0]

        boxes = output["boxes"].cpu().numpy()
        scores = output["scores"].cpu().numpy()
        labels = output["labels"].cpu().numpy()

        for box, score, label in zip(boxes, scores, labels):
            if score < confidence_threshold:
                continue
            x0, y0, x1, y1 = (float(v) for v in box)
            centre = lo = hi = None
            if series.has_geometry:
                centre = tuple(float(v) for v in info.pixel_to_lps((x0 + x1) / 2, (y0 + y1) / 2))
                a, b = info.pixel_to_lps(x0, y0), info.pixel_to_lps(x1, y1)
                lo = tuple(float(v) for v in np.minimum(a, b))
                hi = tuple(float(v) for v in np.maximum(a, b))
            detections.append(
                Detection(
                    slice_index=idx,
                    label=CLASS_NAMES.get(int(label), f"Class {label}"),
                    score=float(score),
                    box=(x0, y0, x1, y1),
                    slice_width=w,
                    slice_height=h,
                    center_mm=centre,
                    box_min_mm=lo,
                    box_max_mm=hi,
                )
            )

        if progress:
            progress(i + 1, len(indices))

    return SeriesScan(detections, len(series.slices), indices, step, series.has_geometry,
                      slab_thickness(series, indices))
