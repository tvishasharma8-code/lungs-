"""
AI Abnormality Detection - runs the fine-tuned Faster R-CNN model (see
pipeline/tumor_infer.py) over the uploaded CT series and reports
findings above a confidence threshold.

To swap in a different/updated model later: only pipeline/tumor_infer.py
should need to change (model path, preprocessing, class names), as long
as run_inference_on_series keeps returning Detection objects with the
same fields - nothing here or in main.py needs to change.
"""

from __future__ import annotations

from models.schemas import AbnormalityFinding, AbnormalityResult, StepStatus
from pipeline.tumor_infer import run_inference_on_series
from services.progress_service import Job

PIPELINE_KEY = "abnormality"

CONFIDENCE_THRESHOLD = 0.5
SLICE_STRIDE = 3            # process every Nth slice - tune for your hardware
MAX_SLICES = 150            # hard cap on slices scanned per run
MAX_FINDINGS_RETURNED = 25  # keep the results list readable in the UI


def run_abnormality_detection(job: Job, dicom_dir: str) -> None:
    job.set_step(PIPELINE_KEY, "ab_prepare", StepStatus.RUNNING, "Loading CT slices", 10)
    job.set_step(PIPELINE_KEY, "ab_prepare", StepStatus.COMPLETED, "CT slices prepared", 100)

    job.set_step(PIPELINE_KEY, "ab_infer", StepStatus.RUNNING, "Running detection model", 5)

    def on_progress(done: int, total: int) -> None:
        pct = int(5 + (done / max(total, 1)) * 90)
        job.set_step(
            PIPELINE_KEY, "ab_infer", StepStatus.RUNNING,
            f"Scanned {done}/{total} slices", pct,
        )

    detections, total_slices = run_inference_on_series(
        dicom_dir,
        confidence_threshold=CONFIDENCE_THRESHOLD,
        slice_stride=SLICE_STRIDE,
        max_slices=MAX_SLICES,
        progress=on_progress,
    )

    detections.sort(key=lambda d: d.score, reverse=True)
    detections = detections[:MAX_FINDINGS_RETURNED]

    job.set_step(
        PIPELINE_KEY, "ab_infer", StepStatus.COMPLETED,
        f"Found {len(detections)} region(s) of interest", 100,
    )

    job.set_step(PIPELINE_KEY, "ab_overlay", StepStatus.RUNNING, "Generating overlay", 50)

    findings: list[AbnormalityFinding] = []
    for i, d in enumerate(detections):
        xmin, ymin, xmax, ymax = d.box
        findings.append(
            AbnormalityFinding(
                id=f"finding-{i + 1}",
                label=d.label,
                slice_index=d.slice_index,
                confidence=round(d.score, 3),
                bbox=[
                    round(xmin / d.slice_width, 4),
                    round(ymin / d.slice_height, 4),
                    round(xmax / d.slice_width, 4),
                    round(ymax / d.slice_height, 4),
                ],
                volume_mm3=None,
                note="",
            )
        )

    job.set_step(PIPELINE_KEY, "ab_overlay", StepStatus.COMPLETED, "Overlay ready", 100)

    job.results.abnormality = AbnormalityResult(
        mock=False,
        total_slices=total_slices,
        findings=findings,
        summary=(
            f"{len(findings)} region(s) of interest flagged above "
            f"{int(CONFIDENCE_THRESHOLD * 100)}% confidence."
            if findings
            else "No regions of interest were flagged above the confidence threshold."
        ),
    )
