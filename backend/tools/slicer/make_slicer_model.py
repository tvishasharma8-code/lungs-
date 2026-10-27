# Runs inside 3D Slicer (Slicer.exe --no-main-window --python-script this.py <mask> <outdir> <smoothing>):
# builds the closed-surface model of a lung mask with Slicer's own segmentation-to-model
# conversion and exports it as STL in LPS - what "Show 3D" + Export to files produces.
import os
import sys
import traceback

import slicer

log = None
try:
    mask_path, out_dir = sys.argv[1], sys.argv[2]
    smoothing = sys.argv[3] if len(sys.argv) > 3 else "0.5"
    os.makedirs(out_dir, exist_ok=True)
    log = open(os.path.join(out_dir, "slicer_log.txt"), "w")

    labelmap = slicer.util.loadLabelVolume(mask_path)
    seg = slicer.mrmlScene.AddNewNodeByClass("vtkMRMLSegmentationNode")
    slicer.modules.segmentations.logic().ImportLabelmapToSegmentationNode(labelmap, seg)
    seg.GetSegmentation().SetConversionParameter("Smoothing factor", smoothing)
    seg.CreateClosedSurfaceRepresentation()

    ids = seg.GetSegmentation().GetSegmentIDs()
    poly = seg.GetClosedSurfaceInternalRepresentation(ids[0])
    log.write(f"segments={len(ids)} points={poly.GetNumberOfPoints()} cells={poly.GetNumberOfCells()} smoothing={smoothing}\n")

    ok = slicer.modules.segmentations.logic().ExportSegmentsClosedSurfaceRepresentationToFiles(
        out_dir, seg, None, "STL", True, 1.0, False)  # lps=True, sizeScale=1, merge=False
    log.write(f"export ok={ok} files={os.listdir(out_dir)}\n")
except Exception:
    if log:
        log.write(traceback.format_exc())
finally:
    if log:
        log.close()
    slicer.app.exit(0)
