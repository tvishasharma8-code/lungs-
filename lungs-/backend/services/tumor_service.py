"""
AI Abnormality Detection - runs the fine-tuned Faster R-CNN model (see
pipeline/tumor_infer.py) over the uploaded CT series, groups the per-slice
detections into tumors (pipeline/tumor_cluster.py) and reports them.

Besides the web results, it writes the VR findings next to the job's VR mesh
(<OUTPUT_ROOT>/<job>_vr/): findings.json - tumors in patient mm, the frame of the
mesh - and slices/NNN.png, the flagged CT slices with their boxes drawn. "Send to
VR" packs them with the lung (services/vr_service.py).

To swap in a different/updated model later: only pipeline/tumor_infer.py
should need to change (model path, preprocessing, class names), as long
as run_inference_on_series keeps returning Detection objects with the
same fields - nothing here or in main.py needs to change.
"""

from __future__ import annotations

import json
import os

from PIL import Image, ImageDraw
from pydicom import dcmread

from models.schemas import AbnormalityFinding, AbnormalityResult, StepStatus, TumorFinding
from pipeline import tumor_cluster
from pipeline.tumor_infer import list_series_files, normalize_to_grayscale_uint8, run_inference_on_series
from services.progress_service import Job

PIPELINE_KEY = "abnormality"

CONFIDENCE_THRESHOLD = 0.5
SLICE_STEP_MM = 0.0         # 0 = scan EVERY slice (set e.g. 2.5 to scan one slice per 2.5 mm, faster)
MAX_SLICES = 0              # 0 = no cap
MAX_FINDINGS_RETURNED = 25  # keep the per-slice list readable in the UI


def run_abnormality_detection(job: Job, dicom_dir: str, vr_dir: str) -> None:
    job.set_step(PIPELINE_KEY, "ab_prepare", StepStatus.RUNNING, "Loading CT slices", 10)
    job.set_step(PIPELINE_KEY, "ab_prepare", StepStatus.COMPLETED, "CT slices prepared", 100)

    job.set_step(PIPELINE_KEY, "ab_infer", StepStatus.RUNNING, "Running detection model", 5)

    def on_progress(done: int, total: int) -> None:
        pct = int(5 + (done / max(total, 1)) * 90)
        job.set_step(
            PIPELINE_KEY, "ab_infer", StepStatus.RUNNING,
            f"Scanned {done}/{total} slices", pct,
        )

    scan = run_inference_on_series(
        dicom_dir,
        confidence_threshold=CONFIDENCE_THRESHOLD,
        slice_step_mm=SLICE_STEP_MM,
        max_slices=MAX_SLICES,
        progress=on_progress,
    )
    tumors, dropped = tumor_cluster.cluster(scan)
    labels_npz = os.path.join(vr_dir, "labels.npz")  # from the reconstruction, if it ran
    if os.path.isfile(labels_npz):
        tumor_cluster.locate(tumors, labels_npz)

    job.set_step(
        PIPELINE_KEY, "ab_infer", StepStatus.COMPLETED,
        f"Found {len(tumors)} tumor(s) ({len(scan.detections)} detections on {len(scan.scanned)} slices)", 100,
    )

    job.set_step(PIPELINE_KEY, "ab_overlay", StepStatus.RUNNING, "Generating overlay", 50)

    # Per-slice findings (the web's slice cards): only detections that belong to a tumor.
    in_tumor = [d for t in tumors for d in t.detections]
    in_tumor.sort(key=lambda d: d.score, reverse=True)
    findings: list[AbnormalityFinding] = []
    tumor_of = {id(d): t.id for t in tumors for d in t.detections}
    for i, d in enumerate(in_tumor[:MAX_FINDINGS_RETURNED]):
        findings.append(
            AbnormalityFinding(
                id=f"finding-{i + 1}",
                label=f"{d.label} {tumor_of[id(d)]}",
                slice_index=d.slice_index,
                confidence=round(d.score, 3),
                bbox=_normalized(d),
                volume_mm3=None,
                note="",
            )
        )

    tumor_results = [
        TumorFinding(
            id=t.id,
            max_confidence=t.max_confidence,
            mean_confidence=t.mean_confidence,
            slice_count=len(t.slice_indices),
            slice_range=[t.slice_indices[0], t.slice_indices[-1]],
            key_slice=t.key.slice_index,
            key_bbox=_normalized(t.key),
            center_mm=t.center_mm,
            size_mm=t.size_mm,
            volume_mm3=t.volume_mm3,
            location=t.extra.get("location"),
        )
        for t in tumors
    ]

    _write_vr_findings(vr_dir, dicom_dir, tumor_cluster.findings_document(tumors, scan, CONFIDENCE_THRESHOLD, dropped))

    job.set_step(PIPELINE_KEY, "ab_overlay", StepStatus.COMPLETED, "Overlay ready", 100)

    if tumors:
        summary = (f"{len(tumors)} possible tumor(s) found above {int(CONFIDENCE_THRESHOLD * 100)}% "
                   f"confidence, from {len(in_tumor)} detections on {len(scan.scanned)} scanned slices.")
    else:
        summary = "No tumors were flagged above the confidence threshold."
    if dropped:
        summary += f" {dropped} isolated single-slice detection(s) were ignored as likely false alarms."

    job.results.abnormality = AbnormalityResult(
        mock=False,
        total_slices=scan.total_slices,
        findings=findings,
        summary=summary,
        tumors=tumor_results,
        scanned_slices=len(scan.scanned),
    )


def _normalized(d) -> list[float]:
    xmin, ymin, xmax, ymax = d.box
    return [round(xmin / d.slice_width, 4), round(ymin / d.slice_height, 4),
            round(xmax / d.slice_width, 4), round(ymax / d.slice_height, 4)]


def _write_vr_findings(vr_dir: str, dicom_dir: str, doc: dict) -> None:
    """findings.json + slices/NNN.png (what the model saw, boxes in red) for the VR package."""
    slices_dir = os.path.join(vr_dir, "slices")
    os.makedirs(slices_dir, exist_ok=True)
    files = list_series_files(dicom_dir)
    for s in doc["slices"]:
        gray = normalize_to_grayscale_uint8(dcmread(files[s["index"]]).pixel_array)
        img = Image.fromarray(gray).convert("RGB")
        draw = ImageDraw.Draw(img)
        for det in s["detections"]:
            draw.rectangle(det["box_px"], outline=(255, 40, 40), width=2)
            draw.text((det["box_px"][0], max(0, det["box_px"][1] - 12)),
                      f'{det["tumor"]} {det["confidence"]:.2f}', fill=(255, 70, 70))
        img.save(os.path.join(slices_dir, f'{s["index"]:03d}.png'))
    with open(os.path.join(vr_dir, "findings.json"), "w", encoding="utf-8") as fh:
        json.dump(doc, fh, indent=2)
