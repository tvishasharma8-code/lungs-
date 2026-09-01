import torch
import torchvision
from torchvision.models.detection.faster_rcnn import FastRCNNPredictor
from PIL import Image, ImageDraw
import pydicom
import numpy as np
import os
import glob

# ============================================
# CHANGE THIS to the folder containing the patient's raw DICOM files (.dcm)
# ============================================
patient_folder = r"C:\Users\Adminstrator\OneDrive\Desktop\lungs-\ML MODEL\dicom"

model_path = "tumor_model_v4.pth"
output_folder = "scan_results"
confidence_threshold = 0.5

# Standard lung window settings (used across most lung CT pipelines)
WINDOW_CENTER = -600
WINDOW_WIDTH = 1500


def dicom_to_image(dcm_path):
    """Reads a raw DICOM slice and converts it into a normal 0-255 RGB image,
    fully in memory — no PNG file is saved as an intermediate step."""
    ds = pydicom.dcmread(dcm_path)
    pixel_array = ds.pixel_array.astype(np.float32)

    # Convert raw pixel values to Hounsfield Units
    slope = getattr(ds, "RescaleSlope", 1)
    intercept = getattr(ds, "RescaleIntercept", 0)
    hu = pixel_array * slope + intercept

    # Apply lung windowing (maps the huge HU range down to a usable range)
    lower = WINDOW_CENTER - WINDOW_WIDTH / 2
    upper = WINDOW_CENTER + WINDOW_WIDTH / 2
    windowed = np.clip(hu, lower, upper)

    # Normalize down to standard 0-255 image range
    normalized = ((windowed - lower) / (upper - lower) * 255).astype(np.uint8)

    return Image.fromarray(normalized).convert("RGB")


# Rebuild model, load trained weights
model = torchvision.models.detection.fasterrcnn_resnet50_fpn(weights=None)
num_classes = 2
in_features = model.roi_heads.box_predictor.cls_score.in_features
model.roi_heads.box_predictor = FastRCNNPredictor(in_features, num_classes)
model.load_state_dict(torch.load(model_path, map_location="cpu"))
model.eval()

os.makedirs(output_folder, exist_ok=True)

dcm_files = sorted(glob.glob(os.path.join(patient_folder, "*.dcm")))
print(f"Found {len(dcm_files)} DICOM slices to scan in: {patient_folder}\n")

slices_with_tumor = []

for dcm_path in dcm_files:
    filename = os.path.basename(dcm_path)

    # Convert DICOM -> usable image right here, on the fly
    img = dicom_to_image(dcm_path)
    img_tensor = torchvision.transforms.functional.to_tensor(img)

    with torch.no_grad():
        prediction = model([img_tensor])[0]

    boxes = prediction["boxes"]
    scores = prediction["scores"]

    found_tumor = False
    best_score = 0

    draw_img = img.copy()
    draw = ImageDraw.Draw(draw_img)

    for box, score in zip(boxes, scores):
        if score > confidence_threshold:
            found_tumor = True
            best_score = max(best_score, score.item())
            x1, y1, x2, y2 = box.tolist()
            draw.rectangle([x1, y1, x2, y2], outline="red", width=2)
            draw.text((x1, y1 - 10), f"{score:.2f}", fill="red")

    if found_tumor:
        out_name = f"TUMOR_{filename.replace('.dcm', '.png')}"
        slices_with_tumor.append((filename, best_score))
        draw_img.save(os.path.join(output_folder, out_name))
        print(f"  [TUMOR FOUND] {filename} - confidence: {best_score:.2f}")

print(f"\n=== SCAN SUMMARY ===")
print(f"Total slices scanned: {len(dcm_files)}")
print(f"Slices with tumor detected: {len(slices_with_tumor)}")

if slices_with_tumor:
    print(f"\nOVERALL RESULT: Tumor detected in this patient's scan.")
    print(f"Flagged slices saved in: {output_folder}/")
else:
    print(f"\nOVERALL RESULT: No tumor detected across any slice in this scan.")