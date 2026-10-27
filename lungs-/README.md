# LungVision

Web app and backend that turn a chest CT (a folder of DICOM files) into:

- **a 3D lung model** – TotalSegmentator finds the 5 lobes and the trachea; the surface is
  built the way a hand-made 3D Slicer model is (the CT's own lung outline, Slicer's
  surface method), in real patient coordinates (DICOM LPS, mm);
- **tumor findings** – a Faster R-CNN model (`tumor_model_v5.pth`) looks at the slices;
  boxes on neighbouring slices are grouped into tumors with a position, size, rough
  volume and lobe;
- **a Patient Package for the VR app** – "Send to VR" keeps the model + tumors so the
  Unity app (headset or desktop window) shows that patient's lungs, breathing with
  BioGears.

Research / education prototype – **not a medical device and not a diagnosis.**

```
lungs-/
  backend/                FastAPI server (Python 3.12)
    main.py               all HTTP endpoints (list at the top of the file)
    pipeline/             CT processing
      dicom_series.py       picks the CT series, orders slices, pixel -> mm; resamples uneven spacing
      run_totalseg.py       reconstruction: DICOM -> NIfTI -> TotalSegmentator -> surface -> STL + VR mesh
      ct_refine.py          lung outline from the CT itself (+ airway up to the top of the scan)
      lung_mesh.py          surfaces, decimation, lobe labels, mesh.bin / meta.json writers
      tumor_infer.py        Faster R-CNN on the slices, boxes in mm
      tumor_cluster.py      boxes -> tumors (centre, size, volume, lobe), findings.json
      weights/              tumor_model_v5.pth goes here (not in git - see Setup)
      training_scripts_reference/  the ML team's training code, kept as the preprocessing reference
    services/             jobs, progress, Patient Packages (vr_service), desktop Unity launcher
    tools/                checks and helpers (see Tests)
  frontend/               React + Vite + Tailwind website
```

## Setup (Windows, once)

**Backend**

```powershell
cd backend
python -m venv .venv
.venv\Scripts\pip install -r requirements.txt
```

TotalSegmentator downloads its own model weights on the first run (a few hundred MB).
A GPU is optional; on CPU a reconstruction takes ~5 minutes and detection ~3.

**Tumor model** – `tumor_model_v5.pth` is stored with Git LFS on the `faster-rcnn-model`
branch (`ML MODEL/tumor_model_v5.pth`). Put it at `backend/pipeline/weights/tumor_model_v5.pth`:

```powershell
git fetch origin faster-rcnn-model:faster-rcnn-model
git show "faster-rcnn-model:ML MODEL/tumor_model_v5.pth" | git lfs smudge "ML MODEL/tumor_model_v5.pth" > backend\pipeline\weights\tumor_model_v5.pth
```

(SHA-256 `49e417ea…a9fec9`, 165,729,772 bytes.)

**Website**

```powershell
cd frontend
npm ci
```

## Run

```powershell
# backend - this PC only
cd backend; .venv\Scripts\python -m uvicorn main:app --host 127.0.0.1 --port 8000
# backend - also reachable by a standalone Quest on the same Wi-Fi
cd backend; .venv\Scripts\python -m uvicorn main:app --host 0.0.0.0 --port 8000

# website (proxies /api to the backend)
cd frontend; npm run dev        # http://localhost:5173
```

Use: **Upload** a CT folder (browse or drag & drop – sub-folders are kept) → pick
*3D Lung Reconstruction* or *Tumor Detection + 3D Lung* (detection always builds the lung
too; if one part fails the other is still shown, with a warning) → on the results page
**Send to VR** (the headset switches to this patient within
seconds) or **View in Unity** (opens the Unity app as a desktop window; build it first
in Unity with *BioGears > Build Desktop App (Windows)*). The **Patients in VR** list
switches between or removes kept patients.

Nothing is stored except what you send to VR: uploads and results live in the OS temp
folder and are deleted by *New Analysis*.

## Configuration (environment variables)

| Variable | Default | What |
|---|---|---|
| `LUNGVISION_VR_DIR` | `~/LungVision/vr_patients` | where Patient Packages are kept |
| `LUNGVISION_UNITY_EXE` | `<unity project>/Builds/Desktop/LungVisionVR.exe` | the desktop app "View in Unity" starts |
| `LUNGVISION_LUNG_HU` | `-600` | lung air threshold for the surface |
| `LUNGVISION_FILL_VESSELS` | `0` | `1` fills vessels inside the lung (smoother, less detail) |
| `LUNGVISION_SLICER_SMOOTHING` | `0.5` | 3D Slicer's smoothing factor |
| `LUNGVISION_VR_TRIANGLES` | `1460000` | triangle budget of the VR mesh |
| `LUNGVISION_SLICE_STEP_MM` | `2.5` | tumor detection scans one slice per this many mm; `0` = every slice (slow on CPU) |

## Patient Package (the contract with the VR app)

`GET /api/vr/patients/{id}/…`

| File | Content |
|---|---|
| `meta.json` | frame `DICOM_LPS_mm`, centre, bounding box, structures, triangle counts, anonymised series id |
| `mesh.bin` | `LVMESH01`, u32 version, u32 vertex count, u32 sub-mesh count, per sub-mesh (u8 name length, name, u32 index count), float32 LPS mm positions, u32 indices – one sub-mesh per lobe + trachea |
| `findings.json` | tumors (centre / bbox / size in mm, volume, confidence, slice range, lobe or "outside the lungs") and the flagged slices with their boxes |
| `slices/NNN.png` | flagged CT slice NNN (body order, 0 = top) with the boxes drawn |

The VR app maps LPS (x, y, z) to Unity (x, z, y) and flips triangle winding
(`Assets/LungVR/Scripts/Patient/LungPackageMesh.cs` in the Unity project).

## Tests and checks

From `backend/`:

| Command | Checks |
|---|---|
| `.venv\Scripts\python tools\check_coordinates.py <dicom folder> --full` | full reconstruction; the mesh lies on the CT's lung surface in patient mm (walls, diaphragm, airway top, ~1 mm median), package files valid |
| `.venv\Scripts\python tools\test_vr_endpoints.py <folder with mesh.bin + meta.json>` | all `/api/vr` endpoints, incl. findings + slice images and path-traversal rejection |
| `.venv\Scripts\python -m pyflakes main.py models services pipeline tools` | static check |
| `cd frontend; npx tsc -b; npm run build` | website type-check + build |

Other tools: `tools/render_mesh.py` (render meshes to PNG for comparison),
`tools/import_model.py` (bring a hand-made 3D Slicer model into VR),
`tools/slicer/make_slicer_model.py` (reproduce a model in real 3D Slicer).
