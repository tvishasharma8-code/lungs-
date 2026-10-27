# Integration plan — LungVision + Tumor ML + VR BioGears

Three parts, one product:

| | LungVision (`backend/`, `frontend/`) | ML_Unity_Integration (prototype, since removed) | VR app (`unity/`) |
|---|---|---|---|
| Lung model | TotalSegmentator, 5 lobes + trachea → STL | air threshold → OBJ + calibration cubes | one fixed `LungModel.fbx` |
| Tumor model | Faster R-CNN v5 (FastAPI :8000) | same v5 (Flask :5000) | — |
| Viewing | web: three.js + slice cards | Unity desktop: see-through lung + marker | VR: BioGears, breathing, disease visuals, airflow |

Agreed decisions (team, 2026-10-25):
1. One backend — LungVision FastAPI, with the ML server's features merged in; `tumor_server.py` retired after parity.
2. Mesh: TotalSegmentator primary; `build_patient_mesh.py` threshold mesh kept as a fast fallback.
3. Patients persist only through an explicit "Send to VR".
4. Contract between the parts: the **Patient Package** (below).
5. Ownership: backend + web — LungVision/ML teammates; VR — Unity side.

Backend work lives on branch `integration/vr-package` in the `lungs-` repo until reviewed.

## Problems found while reading the code

1. Two tumor servers run the same model differently: slice order by **filename** (LungVision) vs by
   **body position** (ML server); every 3rd slice capped at 150 vs every slice; CPU vs GPU; no cache
   vs disk cache; no mm positions vs mm positions. "Slice 40" means different things.
2. `run_totalseg.py` meshes with `spacing=abs(diag(affine))` → origin and axis directions are lost,
   so tumors can't be placed on the LungVision mesh.
3. The ML mesh has exact coordinates but rough anatomy (threshold, no lobes).
4. `/api/upload` saves files by `basename` → same-named files in sub-folders overwrite each other.
5. ML Unity scripts: `Standard` materials (magenta in URP), laptop folder paths (impossible on a
   standalone Quest), both servers bound to localhost only.
6. The VR app can only show its one built-in lung; patient meshes must load at run time.
7. LungVision deletes everything on "New Analysis"; VR needs the patient to stay.
8. Repo layout: Unity project's remote is the `lungs-` repo, `lungs-/` is nested inside it,
   `ML_Unity_Integration/` is in no repo; model weights aren't distributed.

## Patient Package (the contract)

```
patient_package/
  meta.json        frame = DICOM patient coords (LPS, mm), centre, bbox, structures,
                   triangle counts, anonymised series id (no paths / names)
  mesh.bin         decimated mesh, one sub-mesh per lobe + trachea, LPS mm
  findings.json    tumors (centre mm, size, confidence, slice range) + per-slice boxes,
                   slices in body-position order
  slices/NNN.png   flagged CT slices
```

## Phases

- **I0 Housekeeping** — weights distribution (v5), repo layout, Python setup doc.
- **I1 Backend** — I1.1 shared DICOM loader (body-position order, geometry, upload fix) ·
  I1.2 merged detection (mm, GPU, cache, stride) · I1.3 tumor clustering on the backend ·
  I1.4 TotalSegmentator mesh in LPS mm, per-lobe parts, decimated for Quest ·
  I1.5 Patient Package endpoints, LAN bind · I1.6 retire Flask server ·
  I1.7 coordinate check (tumors inside mesh; matches ML calibrated mesh within a few mm).
- **I2 Web** — findings grouped by tumor, tumor markers in the 3D viewer, "Send to VR".
- **I3 VR loads patients** — PatientLibrary client, run-time mesh builder (+X left, +Y superior,
  +Z posterior; winding flipped), deformer/airflow re-init, real lobes for pneumonia / emphysema /
  airflow, display vs real size.
- **I4 VR tumors** — markers that breathe with the lung, Tumors toggle, X-ray mode, CT slice panel.
- **I5 Physiology (optional)** — central tumor → illustrative airway obstruction; DICOM demographics
  → BioGears patient.
- **I6 Validation** — coordinate tests on ≥3 patients, detection parity, package round-trip tests,
  Quest ≥72 fps, scripted end-to-end demo.

## Progress

- [x] I1.1 Shared DICOM loader — `backend/pipeline/dicom_series.py`; uploads keep sub-folders.
- [x] I1.4 TotalSegmentator mesh in LPS mm, per-lobe, decimated — `backend/pipeline/lung_mesh.py`;
      STL now in patient coordinates; VR package `<job>_vr/mesh.bin + meta.json` (~200k triangles,
      5 lobes + trachea as sub-meshes).
- [x] I1.7 Coordinate check — `backend/tools/check_coordinates.py`. First patient
      (anonym/patient11, 333-slice chest CT): DICOM→LPS→NIfTI voxel identity exact (60/60,
      0.0000 voxel); mesh on the independent DICOM-geometry lung surface (median 1.1 mm,
      90th pct 1.8 mm); walls within 3.7 mm; package round-trip clean.
      Note: TotalSegmentator's trachea stops ~50 mm below the scan's airway top — expected.
- [x] I1.5 Patient Package store + endpoints — `backend/services/vr_service.py`, `/api/vr/*` in
      `main.py`; packages in `~/LungVision/vr_patients` (`LUNGVISION_VR_DIR`). Tested:
      `backend/tools/test_vr_endpoints.py` (16 checks: send/list/active/files/rejects/survives
      reset/delete).
- [~] I3 VR loading — mesh reader `unity/Assets/LungVR/Scripts/Patient/LungPackageMesh.cs` (LPS → Unity, winding
      flipped). Tested on the real package: `tools/PatientPackageTests` (anatomy: right lobes on
      −X, uppers above lowers, trachea top/midline, middle lobe in front; 2.59 L enclosed).
      Unity side: `unity/Assets/LungVR/Scripts/Patient/PatientLungLoader.cs` (follows the backend's active patient,
      downloads + parses off the main thread, places it where the default lung was, facing the
      user; display or real size; can switch back), `LungMeshDeformer.ReplaceMesh` (re-inits
      breathing/UVs/tangents/airway frame, one material copy per lobe keeps disease state),
      `AirflowVisualizer.Rebuild`. Added by BioGears > Fix VR Scene Layout. Awaiting first
      in-Unity test. Not yet: patient picker / "default lung" button on the console.
- [x] Patient lung quality parity with the app's own 3D Slicer model — mm-based blur + Taubin
      smoothing (roughness 11.0° → 1.4°, volume unchanged), VR budget ~1.46M triangles (matches
      `LungModel.fbx`; full-resolution surfaces go unreduced, ~600k). `backend/tools/import_model.py`
      brings a hand-made Slicer model to VR at its own quality (LPS/RAS auto-detected against
      the CT, misaligned models rejected).
- [x] I1.2 Detection in mm — `tumor_model_v5.pth` from the `faster-rcnn-model` branch (in
      `backend/pipeline/weights/`, git-ignored). Preprocessing checked on A0265: the backend's
      per-slice min–max and the teammate's lung window both find all 5 known tumor slices
      (box IoU 0.9), so it stays. Every box gets LPS mm; slices sampled every ~2.5 mm by real
      position over the whole series (was every 3rd slice capped at 150); GPU if present.
- [x] I1.3 Tumor grouping — `backend/pipeline/tumor_cluster.py`: neighbouring-slice boxes →
      tumors (centre/size/volume/slice range/confidence), lobe from TotalSegmentator's masks
      (`labels.npz` saved by reconstruction) or "outside the lungs". A0265: T1 left lower lobe
      (the annotated tumor), T2/T3 at the hilum flagged "outside the lungs" (v4 doesn't find
      them; v5 does). `findings.json` + `slices/NNN.png` go in the Patient Package.
- [x] Uneven slice spacing — dicom2nifti refuses such series; `dicom_series.write_uniform_nifti`
      resamples onto an even grid from true positions (voxel-identical to dicom2nifti on
      patient 11). A0265 now reconstructs.
- [x] I2 (part) Web — "Send to VR" / "View in Unity", patients list, tumor cards (key slice,
      lobe, size, volume, outside-lung warning).
- [~] I4 VR tumors — `unity/Assets/LungVR/Scripts/Patient/TumorMarkers.cs` + `unity/Assets/LungVR/Shaders/TumorMarker.shader`: markers at
      each tumor's LPS position (volume-sized; red in a lobe, amber outside the lungs), solid in
      view and a pulsing ghost behind tissue, riding the breath (`LungMeshDeformer.BreathingOffset`)
      and rotate/zoom; labels; CT slice panel (key slice + box, "Next tumor"); console "Tumors"
      toggle. Set up by BioGears > Fix VR Scene Layout. Compiles; awaiting in-Unity test.
      Not done: X-ray (see-through lung) mode.
- [x] I0 Housekeeping — Unity code under `unity/Assets/LungVR/` (Scripts/Physiology, Lung, Patient, UI,
      App, Legacy; Editor; Shaders; Models), docs in `docs/`, READMEs for the Unity project and
      LungVision (setup, run, Patient Package format, tests), weights via Git LFS documented,
      `.gitignore` covers IDE files, the nested repos and generated files.
- [x] Error pass (2026-10-26) — BioGears log moved out of `unity/Assets/StreamingAssets` (Editor
      re-imported it on every write; needs the rebuilt wrapper DLL), console legend height,
      slice-image endpoint re-reading the whole series per request (3 s → cached), endless
      re-download of a failing package, Reset View orientation, compiler warnings → 0,
      endpoint and package tests extended (findings, slice images, tumor placement).
- [ ] Still needed: 1 more test patient (have patient 11 and A0265).
