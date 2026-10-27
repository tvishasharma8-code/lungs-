import pydicom
import glob
import os
import xml.etree.ElementTree as ET
import json
import numpy as np
from PIL import Image

base_dir = r"C:\Users\Adminstrator\OneDrive\Desktop\lung detect"
patients_dir = os.path.join(base_dir, "patients")
ann_base = r"C:\Users\Adminstrator\Downloads\Lung-PET-CT-Dx-Annotations-XML-Files-rev12222020\Annotation"

all_data = []

# Find every patient folder under patients\, whether it's the old A0265 style
# or the new lung_pet_ct_dx\Lung_Dx-XXXX style
patient_dcm_folders = {}

# Old style: patients\A0265\dicom\*.dcm
old_style = glob.glob(os.path.join(patients_dir, "*", "dicom"))
for folder in old_style:
    patient_id = os.path.basename(os.path.dirname(folder))
    patient_dcm_folders[patient_id] = folder

# New style: patients\lung_pet_ct_dx\Lung_Dx-XXXX\*\*\  (contains .dcm files)
new_style_root = os.path.join(patients_dir, "lung_pet_ct_dx")
if os.path.exists(new_style_root):
    for patient_folder in glob.glob(os.path.join(new_style_root, "Lung_Dx-*")):
        patient_id = os.path.basename(patient_folder).replace("Lung_Dx-", "")
        # dive into study/series subfolders to find the one with .dcm files
        dcm_matches = glob.glob(os.path.join(patient_folder, "*", "*"))
        for m in dcm_matches:
            if os.path.isdir(m) and glob.glob(os.path.join(m, "*.dcm")):
                patient_dcm_folders[patient_id] = m

print(f"Found {len(patient_dcm_folders)} patient DICOM folders:")
for pid, path in patient_dcm_folders.items():
    print(f"  {pid}: {path}")

for patient_id, dicom_folder in patient_dcm_folders.items():
    ann_folder = os.path.join(ann_base, patient_id)
    if not os.path.exists(ann_folder):
        print(f"  WARNING: no annotation folder for {patient_id}, skipping")
        continue

    xml_uids = set(os.path.splitext(f)[0] for f in os.listdir(ann_folder) if f.endswith(".xml"))
    dcm_files = glob.glob(os.path.join(dicom_folder, "*.dcm"))

    png_folder = os.path.join(dicom_folder, "..", "png_export")
    png_folder = os.path.normpath(png_folder)
    os.makedirs(png_folder, exist_ok=True)

    patient_tumor_count = 0

    for dcm_path in dcm_files:
        d = pydicom.dcmread(dcm_path)
        uid = d.SOPInstanceUID
        has_tumor = uid in xml_uids

        boxes = []
        if has_tumor:
            patient_tumor_count += 1
            xml_path = os.path.join(ann_folder, uid + ".xml")
            tree = ET.parse(xml_path)
            root = tree.getroot()
            for obj in root.findall("object"):
                bnd = obj.find("bndbox")
                boxes.append({
                    "xmin": int(bnd.find("xmin").text),
                    "ymin": int(bnd.find("ymin").text),
                    "xmax": int(bnd.find("xmax").text),
                    "ymax": int(bnd.find("ymax").text),
                })

        # Convert to PNG
        pixels = d.pixel_array.astype(np.float32)
        pixels -= pixels.min()
        if pixels.max() > 0:
            pixels /= pixels.max()
        pixels *= 255.0
        img = Image.fromarray(pixels.astype(np.uint8))
        png_name = os.path.basename(dcm_path).replace(".dcm", ".png")
        img.save(os.path.join(png_folder, png_name))

        all_data.append({
            "patient_id": patient_id,
            "dicom_file": os.path.basename(dcm_path),
            "png_path": os.path.join(png_folder, png_name),
            "sop_uid": uid,
            "has_tumor": has_tumor,
            "boxes": boxes
        })

    print(f"  {patient_id}: {len(dcm_files)} slices, {patient_tumor_count} with tumor")

out_path = os.path.join(base_dir, "combined_dataset_index.json")
with open(out_path, "w") as f:
    json.dump(all_data, f, indent=2)

print(f"\nSaved {len(all_data)} total entries to {out_path}")
print(f"Total tumor slices: {sum(1 for e in all_data if e['has_tumor'])}")
print(f"Total patients: {len(set(e['patient_id'] for e in all_data))}")