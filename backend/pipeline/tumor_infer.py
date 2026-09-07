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
from collections import defaultdict
from dataclasses import dataclass
from typing import Callable, Optional

import numpy as np
import torch
import torchvision
from PIL import Image
from pydicom import dcmread

# Filename matches what the team currently hands off. If a future
# retrain produces a differently-named file, either rename it to this
# on copy, or update this constant.
MODEL_PATH = os.path.join(os.path.dirname(__file__), "weights", "tumor_model_v4.pth")
NUM_CLASSES = 2  # background + tumor
CLASS_NAMES = {1: "Tumor"}

_model: Optional[torch.nn.Module] = None
_model_lock = threading.Lock()


@dataclass
class Detection:
    slice_index: int
    label: str
    score: float
    box: tuple[float, float, float, float]  # native pixel coords: xmin, ymin, xmax, ymax
    slice_width: int   # the specific slice's actual width (native resolution)
    slice_height: int  # the specific slice's actual height (native resolution)


def _load_model() -> torch.nn.Module:
    global _model
    with _model_lock:
        if _model is not None:
            return _model
        if not os.path.exists(MODEL_PATH):
            raise FileNotFoundError(
                f"Tumor model weights not found at {MODEL_PATH}. "
                "Copy tumor_model_v4.pth into backend/pipeline/weights/."
            )
        model = torchvision.models.detection.fasterrcnn_resnet50_fpn(
            weights=None, weights_backbone=None, num_classes=NUM_CLASSES
        )
        state_dict = torch.load(MODEL_PATH, map_location="cpu")
        model.load_state_dict(state_dict)
        model.eval()
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
    """Same series-selection logic as the reconstruction pipeline: pick
    the largest series in the folder, so both pipelines analyze the
    same scan even if stray files are mixed into the upload.

    Public (no leading underscore) because main.py's slice-image
    endpoint calls this too, and it's critical that it uses the exact
    same ordering run_inference_on_series used - a `slice_index` only
    means anything if both sides agree on which file that index maps
    to."""
    series: dict[str, list[str]] = defaultdict(list)
    for f in os.listdir(dicom_dir):
        p = os.path.join(dicom_dir, f)
        if not os.path.isfile(p):
            continue
        try:
            dcm = dcmread(p, stop_before_pixels=True)
            series[dcm.SeriesInstanceUID].append(p)
        except Exception:
            pass
    if not series:
        return []
    _, files = max(series.items(), key=lambda x: len(x[1]))
    return sorted(files)


def run_inference_on_series(
    dicom_dir: str,
    confidence_threshold: float = 0.5,
    slice_stride: int = 3,
    max_slices: int = 150,
    progress: Optional[Callable[[int, int], None]] = None,
) -> tuple[list[Detection], int]:
    """
    Runs the Faster R-CNN model over a subset of slices in the series,
    one slice per forward pass (matching training - the model never
    sees a batched volume).

    Running full detection on every slice of a CT series (often
    300-1000+ slices) is slow on CPU - Faster R-CNN is roughly
    1-5s/image on CPU depending on resolution. `slice_stride` and
    `max_slices` bound the runtime for a research demo; tune them for
    your hardware, or lower slice_stride toward 1 / raise max_slices
    if you have a GPU (see _load_model - move the model and each
    tensor to a CUDA device if torch.cuda.is_available()).

    Returns (detections, total_slices_in_series).
    """
    model = _load_model()
    files = list_series_files(dicom_dir)
    total_slices = len(files)
    if total_slices == 0:
        return [], 0

    indices = list(range(0, total_slices, max(1, slice_stride)))[:max_slices]
    detections: list[Detection] = []

    for i, idx in enumerate(indices):
        dcm = dcmread(files[idx])
        pixel_array = dcm.pixel_array
        h, w = pixel_array.shape[-2], pixel_array.shape[-1]
        tensor = preprocess_slice(pixel_array)  # one slice -> one forward pass

        with torch.no_grad():
            output = model([tensor])[0]

        boxes = output["boxes"].cpu().numpy()
        scores = output["scores"].cpu().numpy()
        labels = output["labels"].cpu().numpy()

        for box, score, label in zip(boxes, scores, labels):
            if score < confidence_threshold:
                continue
            detections.append(
                Detection(
                    slice_index=idx,
                    label=CLASS_NAMES.get(int(label), f"Class {label}"),
                    score=float(score),
                    box=tuple(float(v) for v in box),
                    slice_width=w,
                    slice_height=h,
                )
            )

        if progress:
            progress(i + 1, len(indices))

    return detections, total_slices