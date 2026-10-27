# System map – every part, every connection

What each part of the project produces, where it goes, and how the parts talk to each
other. Every connection below was checked against the code on 2026-10-27 (see
"Verified by"). If you change one side of a connection, change the other side and
re-run its check.

## 1. The parts

| # | Part | Lives in | Owner | Role |
|---|---|---|---|---|
| A | LungVision website | `lungs-/frontend/` (React, Vite) | team | upload a CT, start analyses, show the 3D lung + tumors, "Send to VR", "View in Unity", patient list |
| B | LungVision backend | `lungs-/backend/` (FastAPI, Python 3.12) | team | everything below, behind one HTTP API on port 8000 |
| B1 | DICOM loader | `backend/pipeline/dicom_series.py` | | picks the CT series, orders slices top → bottom, pixel → mm, even-grid rebuild for uneven spacing |
| B2 | 3D reconstruction | `backend/pipeline/run_totalseg.py`, `ct_refine.py`, `lung_mesh.py` | | TotalSegmentator lobes + trachea → CT-refined surface → STL + VR mesh, in patient mm |
| B3 | Tumor detection | `backend/pipeline/tumor_infer.py` + `weights/tumor_model_v5.pth` | ML team (model) | Faster R-CNN on each slice, boxes in mm |
| B4 | Tumor grouping | `backend/pipeline/tumor_cluster.py` | | boxes → tumors (centre, size, volume, lobe), `findings.json`, slice PNGs |
| B5 | Patient store | `backend/services/vr_service.py` | | Patient Packages in `%USERPROFILE%\LungVision\vr_patients`, the "active patient" |
| B6 | Desktop launcher | `backend/services/unity_service.py` | | starts `Builds/Desktop/LungVisionVR.exe`, or uses the open Unity Editor |
| C | Unity VR app | `Assets/LungVR/` | | lungs, breathing, diseases, console, vitals, tumors – headset, Editor or desktop window |
| C1 | Patient loader | `Scripts/Patient/PatientLungLoader.cs`, `LungPackageMesh.cs` | | follows the active patient, downloads + builds the mesh |
| C2 | Tumor markers | `Scripts/Patient/TumorMarkers.cs`, `Shaders/TumorMarker.shader` | | markers, labels, CT slice panel from `findings.json` |
| C3 | Physiology | `Scripts/Physiology/BioGearsNativeRespiratory.cs` (+ `SimulatedBioGearsRespiratory.cs`) | | BioGears via the DLL, or a simplified model as fallback |
| C4 | PC ↔ Quest link | `Scripts/Physiology/Link/*` | | streams BioGears from the PC to a standalone Quest |
| D | BioGears wrapper | `Assets/Plugins/x86_64/biogears_unity.dll` (source: `C:\Users\tvish\BioGears\wrapper`) | | C API over the BioGears C++ engine |
| E | BioGears data | `Assets/StreamingAssets/BioGears/` | | patients, substances, configs, 7 precomputed patient states |
| F | ML training reference | `lungs-/backend/pipeline/training_scripts_reference/` | ML team | the exact training code – the preprocessing ground truth |
| G | ML prototype (legacy) | `ML_Unity_Integration/` | ML team | the original Flask server + Unity scripts – **replaced, not used** (§5) |

## 2. How data flows

```
CT folder (DICOM)
   │  A: Upload (folder, sub-folders kept)                   POST /api/upload
   ▼
B1 DICOM loader ── one series, slices top→bottom, LPS mm geometry ──┐
   │                                                               │
   ├─► B2 reconstruction ──► STL (LPS mm)  ──────────────► A: 3D viewer, download
   │        │               mesh.bin + meta.json (LPS mm, lobes)  │
   │        └──────────────► labels.npz (lobe masks, stays on B) ─┤
   │                                                             ▼
   └─► B3 detection ──► boxes (px + LPS mm) ──► B4 grouping ──► tumors + findings.json + slices/NNN.png
                                                      │               │
                               A: tumor cards, 3D markers ◄───────────┤
                                                                      │
A: "Send to VR" / "View in Unity"  ──►  B5 Patient Package (copied, kept) + active patient
                                                                      │
          ┌──────────────────────────────── polls every 5 s ──────────┘
          ▼
C1 Unity loader: meta.json + mesh.bin ──► patient lungs (LPS → Unity axes) ──► C2 markers + slice panel
          │                                                       ▲ findings.json, slices/*.png
          ▼
C3 BioGears (D + E) ──► breathing, vitals, disease shading      (Quest standalone: via C4 from the PC)
```

## 3. What goes to Unity – and what doesn't

**Goes to Unity** (the Patient Package, `GET /api/vr/patients/{id}/…`):

| File | Made by | Read by | Content |
|---|---|---|---|
| `meta.json` | B2 `lung_mesh.write_meta` | C1 `PatientMeta` | frame `DICOM_LPS_mm`, centre, bbox, structures, counts, anonymised series id |
| `mesh.bin` | B2 `lung_mesh.write_mesh_bin` | C1 `LungPackageMesh.Read` | ~1.46 M triangles, one sub-mesh per lobe + trachea, LPS mm |
| `findings.json` | B4 `findings_document` | C2 `TumorMarkers.Findings` | tumors (centre/size mm, volume, confidence, lobe, key slice) + flagged slices with boxes |
| `slices/NNN.png` | B `tumor_service._write_vr_findings` | C2 slice panel | flagged CT slice NNN with the boxes drawn |

Plus one value: the **active patient id** (`GET /api/vr/active`), which every Unity
instance follows.

**Stays in the backend / website only:** the uploaded DICOM, the STL (website viewer
and download), `labels.npz` (lobe lookup for tumors), the model weights, job progress.

**Never goes to Unity or git:** patient DICOM (`.gitignore`: `/patient*/`, `*.dcm`),
model weights (`*.pth`), personal paths or names (meta.json carries none – checked).

**Not sent by the website:** slider values, scenario, patient age/sex – the simulation
always starts as the default adult (ROADMAP 4.1 / INTEGRATION_PLAN I5).

## 4. Every connection, and how it was checked

| # | From → To | Mechanism | Contract | Verified by |
|---|---|---|---|---|
| 1 | A → B | HTTP via Vite proxy `/api` → `127.0.0.1:8000` | 14 calls in `frontend/src/services/api.ts` | every call matches a route + method in `main.py` |
| 2 | B → A | JSON | `models/schemas.py` ↔ `frontend/src/types/index.ts` | all 10 shapes field-for-field equal |
| 3 | B1 → B2 / B3 / slice images | function calls | one slice order (`load_series`), shared by detection, slice images and slice PNG names | same function everywhere; `list_series_files` cached per upload |
| 4 | B1 → B2 | NIfTI file | dicom2nifti, or `write_uniform_nifti` for uneven spacing | voxel-identical to dicom2nifti on patient 11; A0265 passes `check_coordinates` |
| 5 | B2 mesh ↔ B3 boxes | shared frame DICOM LPS mm | NIfTI affine vs DICOM tags | `tools/check_coordinates.py --full`: 0.000-voxel identity, surface ~1 mm from the CT (both patients) |
| 6 | F → B3 | preprocessing | per-slice min–max, RGB, no resize, `to_tensor` | v5 finds all 5 annotated A0265 slices, box IoU 0.91 |
| 7 | B4 → B5 → C1 / C2 | Patient Package files | §3 | JSON keys vs C# fields (all 9 shapes match); `mesh.bin` via `Tools/PatientPackageTests` |
| 8 | B5 → C1 | HTTP | `/api/vr/active`, `/api/vr/patients`, package files | 4 Unity URLs match routes; reply fields match `ActiveReply` / `PackageInfo` |
| 9 | LPS → Unity | axis swap (x, y, z) → (x, z, y), winding flipped | `LungPackageMesh.LpsToUnity` (mesh and tumors) | PatientPackageTests: anatomy (sides, up, front), tumors inside the lungs and at their lobe |
| 10 | C3 → D | P/Invoke | 19 `DllImport`s; 2 structs by reference | names exist in the DLL; argument/return types match the C source; both structs equal field by field, sequential layout |
| 11 | C3 → E | files | 7 state files + `StandardMale.xml` | every name in the code exists in `StreamingAssets/BioGears` |
| 12 | C4 PC ↔ Quest | UDP beacon 47810, link 47811 | `BioGearsLinkProtocol` | `Tools/BioGearsLinkTests` 11/11 |
| 13 | B6 → desktop app | process start `LungVisionVR.exe -desktop -backend http://127.0.0.1:8000` | exe path + arguments | `DesktopBuild.ExeRelativePath` = backend default path; both arguments parsed (`DesktopMode`, `PatientLungLoader`) |
| 14 | Quest → B | HTTP to `http://<pc>:8000` | needs "Allow downloads over HTTP" + `usesCleartextTraffic` | both set (were blocking every patient request on a Quest build) |
| 15 | D in builds | plugin import settings | Editor + Windows 64-bit only, never Android | explicit in the five `.dll.meta` files |

## 5. The ML prototype (`ML_Unity_Integration/`) – what replaced each piece

| Prototype piece | Replaced by | Why |
|---|---|---|
| `1_python/tumor_server.py` (Flask, port 5000) | B3 `tumor_infer.py` + B4 + `/api/vr/*` | one backend; same model, preprocessing, slice order and pixel→mm formula |
| `1_python/build_patient_mesh.py` (threshold OBJ + calibration cubes) | B2 reconstruction (`mesh.bin` in LPS mm) | lobes, Slicer-quality surface, no calibration needed |
| `2_unity/TumorDetector.cs` (calls the Flask server) | C2 `TumorMarkers.cs` (reads `findings.json`) | no second server; works on the Quest too |
| `2_unity/TumorMarker3D.cs` (30 mm greedy clustering, min 5 boxes) | B4 `tumor_cluster.py` + C2 | order-independent grouping, lobe check, size from volume |

**Do not copy these scripts into `Assets/`** – they would compile and talk to a server
that isn't running. The folder is kept for reference only.

## 6. Running it – what must be up for each setup

| Setup | Needs running | Network |
|---|---|---|
| Website only | backend (`uvicorn … --host 127.0.0.1`), website (`npm run dev`) | – |
| Unity Editor / Quest over Link | + Unity in Play mode | – |
| Desktop window ("View in Unity") | + `Builds/Desktop/LungVisionVR.exe` built once, or the Editor open | – |
| Quest standalone | backend with `--host 0.0.0.0`; **the PC scene playing** (the headset finds the PC through the BioGears beacon and uses the same address for patients) | same Wi-Fi; Windows Firewall must allow Python on TCP 8000 and Unity on UDP 47810/47811 |

## 7. Coordinate frames (the chain every tumor marker depends on)

1. DICOM pixel (col, row) on slice k → LPS mm: `ImagePositionPatient + rowDir·col·colSpacing + colDir·row·rowSpacing` (B1; same formula as the prototype).
2. NIfTI voxel → RAS mm (affine) → LPS mm (negate x, y) for the mesh (B2).
3. LPS mm → Unity (x, z, y), inside the lung's transform (C1); markers use the same mapping and the lung's breathing displacement (C2).
4. Website viewer: STL in LPS mm, markers offset by the same centring (A).

## 8. Before a demo

- [ ] backend + website up; `GET /api/health` → ok
- [ ] Unity Console free of red errors after opening; *BioGears > Fix VR Scene Layout* run once after pulling
- [ ] a patient sent; the headset/Editor shows it within ~5 s
- [ ] tumors visible (*View > Show tumors*), slice panel shows a slice
- [ ] Quest standalone: PC scene playing, backend on `0.0.0.0`, firewall allowed
