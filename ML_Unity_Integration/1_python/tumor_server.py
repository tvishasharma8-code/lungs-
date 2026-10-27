r"""
tumor_server.py - local inference server for tumor_model_v5 (Faster R-CNN), for Unity.

Unity (over Quest Link, same laptop) sends ONE request per patient:
    POST http://127.0.0.1:5000/predict_patient
    {"dicom_folder": "I:\\My Drive\\Patients\\lung_pet_ct_dx\\Lung_Dx-A0003", "threshold": 0.5}

and gets back every flagged slice, with boxes in pixels AND in real-world millimetres
(DICOM patient coordinates) for later 3D placement on the mesh.

Endpoints:
    GET  /health                                  -> is the model loaded, which device
    POST /predict_patient                         -> run the model on a whole patient
    GET  /slice_image?dicom_folder=...&slice=12   -> PNG of that slice (boxes drawn if flagged)

Run:
    pip install flask torch torchvision pydicom pillow numpy
    python tumor_server.py
"""
import glob
import hashlib
import io
import json
import os
import threading
import time
from collections import defaultdict

import numpy as np
import pydicom
import torch
import torchvision
from flask import Flask, jsonify, request, send_file
from PIL import Image, ImageDraw
from torchvision.models.detection.faster_rcnn import FastRCNNPredictor

MODEL_PATH = os.path.join(os.path.dirname(os.path.abspath(__file__)), "tumor_model_v5.pth")
DEFAULT_THRESHOLD = 0.5
HOST, PORT = "127.0.0.1", 5000   # Quest Link: Unity runs on this same laptop

app = Flask(__name__)
device = torch.device("cuda" if torch.cuda.is_available() else "cpu")
model_lock = threading.Lock()
cache = {}   # (folder, threshold) -> result, so a repeat request is instant
CACHE_DIR = os.path.join(os.path.dirname(os.path.abspath(__file__)), "results_cache")


def disk_cache_path(folder, threshold):
    """Results are also saved to disk, so they survive a server restart."""
    key = hashlib.md5(f"{os.path.normcase(folder)}|{threshold}|{os.path.basename(MODEL_PATH)}".encode()).hexdigest()
    return os.path.join(CACHE_DIR, key + ".json")
slice_lists = {}   # folder -> sorted list of DICOM paths (same order as results)


# ---------------------------------------------------------------- model
def load_model():
    m = torchvision.models.detection.fasterrcnn_resnet50_fpn(weights=None, weights_backbone=None)
    m.roi_heads.box_predictor = FastRCNNPredictor(m.roi_heads.box_predictor.cls_score.in_features, 2)
    m.load_state_dict(torch.load(MODEL_PATH, map_location=device))
    return m.to(device).eval()


# ---------------------------------------------------------------- DICOM helpers
def dicom_to_image(ds):
    """SAME conversion as process_all_patients.py (training). Do not change."""
    pixels = ds.pixel_array.astype(np.float32)
    pixels -= pixels.min()
    if pixels.max() > 0:
        pixels /= pixels.max()
    pixels *= 255.0
    return Image.fromarray(pixels.astype(np.uint8)).convert("RGB")


def slice_position(ds):
    try:
        return float(ds.ImagePositionPatient[2])
    except Exception:
        return float(getattr(ds, "InstanceNumber", 0) or 0)


def find_slices(folder):
    """All slices of the largest series in the folder, sorted top of chest -> bottom."""
    files = glob.glob(os.path.join(folder, "**", "*.dcm"), recursive=True)
    if not files:
        files = [f for f in glob.glob(os.path.join(folder, "**", "*"), recursive=True) if os.path.isfile(f)]
    by_series = defaultdict(list)
    for f in files:
        try:
            ds = pydicom.dcmread(f, stop_before_pixels=True)
        except Exception:
            continue
        if not hasattr(ds, "Rows"):
            continue
        by_series[getattr(ds, "SeriesInstanceUID", "unknown")].append((slice_position(ds), f))
    if not by_series:
        return [], 0
    series = max(by_series.values(), key=len)
    series.sort(key=lambda x: x[0], reverse=True)
    return [f for _, f in series], len(by_series)


def pixel_to_mm(ds, col, row):
    """Pixel (col=x, row=y) on this slice -> DICOM patient coordinates in mm.
    P = ImagePositionPatient + rowDir * col * colSpacing + colDir * row * rowSpacing"""
    try:
        ipp = np.array([float(v) for v in ds.ImagePositionPatient])
        iop = [float(v) for v in ds.ImageOrientationPatient]
        row_spacing, col_spacing = (float(v) for v in ds.PixelSpacing)
        p = ipp + np.array(iop[0:3]) * col * col_spacing + np.array(iop[3:6]) * row * row_spacing
        return [round(float(v), 2) for v in p]
    except Exception:
        return None


def run_model(img):
    tensor = torchvision.transforms.functional.to_tensor(img).to(device)
    with model_lock, torch.no_grad():
        out = model([tensor])[0]
    return out["boxes"].cpu().tolist(), out["scores"].cpu().tolist()


# ---------------------------------------------------------------- endpoints
@app.get("/health")
def health():
    return jsonify({"status": "ok", "model": os.path.basename(MODEL_PATH), "device": str(device)})


@app.post("/predict_patient")
def predict_patient():
    body = request.get_json(silent=True) or {}
    folder = body.get("dicom_folder")
    threshold = float(body.get("threshold", DEFAULT_THRESHOLD))
    if not folder or not os.path.isdir(folder):
        return jsonify({"error": f"dicom_folder not found: {folder}"}), 400

    key = (os.path.normpath(folder), threshold)
    if key in cache:
        return jsonify(cache[key])
    disk = disk_cache_path(os.path.normpath(folder), threshold)
    if os.path.isfile(disk):
        with open(disk) as fh:
            result = json.load(fh)
        slice_lists[os.path.normpath(folder)] = find_slices(folder)[0]   # for /slice_image
        cache[key] = result
        print(f"[predict] {folder}: loaded saved result from disk (instant)", flush=True)
        return jsonify(result)

    t0 = time.time()
    slices, n_series = find_slices(folder)
    if not slices:
        return jsonify({"error": "no readable DICOM slices in that folder"}), 400
    slice_lists[os.path.normpath(folder)] = slices
    print(f"[predict] {folder}: {len(slices)} slices on {device}", flush=True)

    flagged, skipped = [], 0
    for i, path in enumerate(slices, 1):
        try:
            ds = pydicom.dcmread(path)
            boxes, scores = run_model(dicom_to_image(ds))
        except Exception as e:
            skipped += 1
            print(f"  slice {i}: skipped ({e})", flush=True)
            continue

        dets = []
        for (x1, y1, x2, y2), s in zip(boxes, scores):
            if s < threshold:
                continue
            cx, cy = (x1 + x2) / 2, (y1 + y2) / 2
            dets.append({
                "confidence": round(s, 3),
                "box": {"x1": round(x1, 1), "y1": round(y1, 1), "x2": round(x2, 1), "y2": round(y2, 1)},
                "center_px": {"x": round(cx, 1), "y": round(cy, 1)},
                "center_mm": pixel_to_mm(ds, cx, cy),   # [x, y, z] DICOM patient coords, or null
            })
        if dets:
            flagged.append({
                "slice": i,                              # 1-based, top of chest first
                "file": os.path.basename(path),
                "z_mm": round(slice_position(ds), 2),
                "image_size": {"width": int(ds.Columns), "height": int(ds.Rows)},
                "max_confidence": max(d["confidence"] for d in dets),
                "detections": dets,
            })
        if i % 25 == 0:
            print(f"  ...{i}/{len(slices)}", flush=True)

    result = {
        "dicom_folder": folder,
        "threshold": threshold,
        "total_slices": len(slices),
        "series_in_folder": n_series,
        "skipped_slices": skipped,
        "tumor_detected": bool(flagged),
        "flagged_count": len(flagged),
        "flagged_slices": flagged,
        "seconds": round(time.time() - t0, 1),
        "note": "research prototype, not a medical diagnosis",
    }
    cache[key] = result
    os.makedirs(CACHE_DIR, exist_ok=True)
    with open(disk, "w") as fh:
        json.dump(result, fh)
    print(f"[predict] done: {len(flagged)} flagged in {result['seconds']}s", flush=True)
    return jsonify(result)


@app.get("/slice_image")
def slice_image():
    folder = os.path.normpath(request.args.get("dicom_folder", ""))
    threshold = float(request.args.get("threshold", DEFAULT_THRESHOLD))
    try:
        idx = int(request.args.get("slice", "0"))
    except ValueError:
        return jsonify({"error": "slice must be a number"}), 400
    slices = slice_lists.get(folder)
    if slices is None:
        return jsonify({"error": "call /predict_patient for this folder first"}), 400
    if not 1 <= idx <= len(slices):
        return jsonify({"error": f"slice must be 1..{len(slices)}"}), 400

    img = dicom_to_image(pydicom.dcmread(slices[idx - 1]))
    result = cache.get((folder, threshold))
    if result:
        for fs in result["flagged_slices"]:
            if fs["slice"] == idx:
                draw = ImageDraw.Draw(img)
                for d in fs["detections"]:
                    b = d["box"]
                    draw.rectangle([b["x1"], b["y1"], b["x2"], b["y2"]], outline=(255, 0, 0), width=3)
                    draw.text((b["x1"], max(0, b["y1"] - 12)), f'{d["confidence"]:.2f}', fill=(255, 0, 0))
    buf = io.BytesIO()
    img.save(buf, format="PNG")
    buf.seek(0)
    return send_file(buf, mimetype="image/png")


if __name__ == "__main__":
    if not os.path.isfile(MODEL_PATH):
        raise SystemExit(f"Model not found: {MODEL_PATH}\nPut tumor_model_v5.pth next to tumor_server.py")
    print(f"Loading {os.path.basename(MODEL_PATH)} on {device}...", flush=True)
    model = load_model()
    run_model(Image.new("RGB", (512, 512)))   # warm-up so the first real request isn't extra slow
    print(f"Model loaded, server ready on http://{HOST}:{PORT}", flush=True)
    app.run(host=HOST, port=PORT, threaded=True)
