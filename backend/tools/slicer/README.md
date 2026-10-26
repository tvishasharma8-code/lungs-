# Compare the pipeline with real 3D Slicer output

`make_slicer_model.py` runs **inside 3D Slicer** (no window) and turns a lung mask into a
model with Slicer's own segmentation-to-model conversion (default smoothing factor 0.5),
exported as STL in LPS — exactly what *Show 3D* + *Export to files* produces.

```
"C:\Users\<you>\AppData\Local\slicer.org\3D Slicer 5.10.0\Slicer.exe" --no-splash --no-main-window ^
    --python-script tools\slicer\make_slicer_model.py <lung_mask.nii.gz> <out_folder> 0.5
```

Then bring it to VR (lobes labelled from TotalSegmentator masks of the same CT):

```
.venv\Scripts\python tools\import_model.py <out_folder>\Segmentation_1.stl --dicom <DICOM folder> --seg <seg_output> --send
```

Patient 11 (2026-10-25): pipeline surface vs Slicer's surface of the same mask —
median 0.31 mm, max 0.8 mm apart; 652k vs 653k triangles; same volume (2.55 L).
