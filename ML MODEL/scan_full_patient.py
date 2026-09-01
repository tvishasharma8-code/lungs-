import torch
import torchvision
from torchvision.models.detection.faster_rcnn import FastRCNNPredictor
from PIL import Image, ImageDraw
import os
import glob

# ============================================
# CHANGE THIS to the folder containing all the patient's CT slice PNGs
# ============================================
patient_folder = r"C:\Users\Adminstrator\OneDrive\Desktop\lung detect\patients\A0265\png_export"

model_path = "tumor_model_v4.pth"
output_folder = "scan_results"
confidence_threshold = 0.5

# Rebuild model, load trained weights
model = torchvision.models.detection.fasterrcnn_resnet50_fpn(weights=None)
num_classes = 2
in_features = model.roi_heads.box_predictor.cls_score.in_features
model.roi_heads.box_predictor = FastRCNNPredictor(in_features, num_classes)
model.load_state_dict(torch.load(model_path, map_location="cpu"))
model.eval()

os.makedirs(output_folder, exist_ok=True)

png_files = sorted(glob.glob(os.path.join(patient_folder, "*.png")))
print(f"Found {len(png_files)} slices to scan in: {patient_folder}\n")

slices_with_tumor = []

for png_path in png_files:
    filename = os.path.basename(png_path)
    img = Image.open(png_path).convert("RGB")
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
        slices_with_tumor.append((filename, best_score))
        draw_img.save(os.path.join(output_folder, f"TUMOR_{filename}"))
        print(f"  [TUMOR FOUND] {filename} - confidence: {best_score:.2f}")

print(f"\n=== SCAN SUMMARY ===")
print(f"Total slices scanned: {len(png_files)}")
print(f"Slices with tumor detected: {len(slices_with_tumor)}")

if slices_with_tumor:
    print(f"\nOVERALL RESULT: Tumor detected in this patient's scan.")
    print(f"Flagged slices saved in: {output_folder}/")
else:
    print(f"\nOVERALL RESULT: No tumor detected across any slice in this scan.")