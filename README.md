# LungVision v2

CT -> 3D Lung Reconstruction & AI Tumor Detection.

Two independent, real pipelines - the user can run either or both:

1. **3D Lung Reconstruction** - REAL: DICOM -> NIfTI -> TotalSegmentator ->
   mesh -> STL. This is your original `run_totalseg.py` logic, refactored
   to accept dynamic per-upload paths and report live progress.
2. **AI Tumor Detection** - REAL (as of v2): a fine-tuned torchvision
   Faster R-CNN (ResNet-50 + FPN) model, `tumor_model_v4.pth`, trained
   on 11 patients (522 slices, 71 tumor-positive) via
   `backend/pipeline/training_scripts_reference/train_model_v2.py`.
   Runs one CT slice at a time (never the whole volume batched together),
   exactly matching how it was trained.

## What changed in v2

- The AI Abnormality Detection pipeline is no longer mocked. It calls
  the real model in `backend/pipeline/tumor_infer.py`.
- Preprocessing in `tumor_infer.py` is a direct, verified port of the
  team's actual training code (`process_all_patients.py` +
  `train_model_v2.py`, kept for reference under
  `backend/pipeline/training_scripts_reference/`) - not a guess:
  per-slice min-max normalize to 0-255, grayscale -> RGB, **no resize**
  (each slice is used at its native DICOM resolution, matching training).
- `torch`, `torchvision`, and `Pillow` were added to `requirements.txt`.

## Project structure

```
backend/
  main.py                      FastAPI app (all HTTP endpoints)
  requirements.txt
  models/schemas.py            Pydantic request/response models
  pipeline/
    run_totalseg.py            3D reconstruction pipeline (dynamic paths)
    tumor_infer.py             REAL tumor detection model + inference
    weights/                   <- put tumor_model_v4.pth here (not in git, ~165MB)
    training_scripts_reference/  the team's actual training code, for provenance
  services/
    analysis_service.py        Orchestrates a job, runs it in a background thread
    stl_service.py             Wraps the reconstruction pipeline
    tumor_service.py           Wraps the tumor detection pipeline
    progress_service.py        In-memory job/progress store (no database)
  temp/                        Runtime scratch space - lives in the OS temp
                                dir at runtime (see analysis_service.py),
                                NOT under backend/, so uvicorn --reload
                                doesn't restart mid-upload/mid-job.

frontend/
  src/
    pages/                     Home, Upload, Processing, Results
    components/                 Stepper, AnalysisSelector, FileDropzone,
                                PipelineProgressList, STLViewer (Three.js),
                                LogoMark
    services/api.ts             Backend API client + resilient status polling
    services/SessionContext.tsx  Session/job state across the wizard
    types/index.ts               TypeScript mirror of the backend's Pydantic models
```

## How to run

### 1. Place the model weights

Copy `tumor_model_v4.pth` into:
```
backend/pipeline/weights/tumor_model_v4.pth
```
(This file is intentionally not in git/the zip - it's ~165MB. `.gitignore`
already excludes `backend/pipeline/weights/*.pth`.)

### 2. Backend

```powershell
cd backend
python -m venv .venv
.venv\Scripts\activate
pip install -r requirements.txt
uvicorn main:app --reload
```

Let `pip install` run to completion - `torch`/`torchvision`/`TotalSegmentator`
are large and can take several minutes. The API listens on
`http://localhost:8000`.

**GPU note:** `tumor_infer.py` and `run_totalseg.py` both run on CPU by
default. If you have a CUDA-capable GPU, both will be noticeably faster -
ask if you want CUDA device handling added.

### 3. Frontend

```powershell
cd frontend
npm install
npm run dev
```

Dev server runs on `http://localhost:5173` and proxies `/api/*` to the
backend on port 8000 (see `vite.config.ts`) - no env vars needed.

### 4. Use it

Open `http://localhost:5173`:
1. **Start Analysis**
2. Drag in a DICOM folder (or **Browse Folder**)
3. Pick **3D Reconstruction**, **AI Tumor Detection**, or **Both**
4. **Continue** - live per-step progress for whichever pipeline(s) you picked
5. **Results** - rotate/zoom/download the real STL, and/or see flagged
   tumor regions with slice index + confidence

## Performance notes

- **3D Reconstruction**: TotalSegmentator on CPU took ~8-9 minutes on a
  test run in earlier debugging. No GPU = expect several minutes.
- **AI Tumor Detection**: Faster R-CNN is roughly 1-5s/slice on CPU. A
  full series can have 300-1000+ slices, so `backend/services/tumor_service.py`
  bounds runtime with `SLICE_STRIDE = 3` (every 3rd slice) and
  `MAX_SLICES = 150` (hard cap). Tune these constants for your hardware -
  lower `SLICE_STRIDE` toward 1 and raise `MAX_SLICES` if you have a GPU
  or don't mind a longer wait for full coverage.
- If you get a `MemoryError` from TotalSegmentator's "Saving segmentations"
  step on a memory-constrained machine, `run_totalseg.py` already passes
  `--nr_thr_resamp 1 --nr_thr_saving 1` to keep that step single-threaded.

## Retraining / updating the model later

If the team retrains and hands off a new `.pth` file:
1. Copy it into `backend/pipeline/weights/`.
2. Update `MODEL_PATH` in `backend/pipeline/tumor_infer.py` to the new filename.
3. If preprocessing changed (different normalization, a resize step added,
   HU windowing introduced, etc.), update `preprocess_slice()` to match -
   and drop the new training script into
   `backend/pipeline/training_scripts_reference/` so the exact preprocessing
   is provable from code, not re-guessed.
4. If `CLASS_NAMES` changes (e.g. multiple tumor subtypes instead of one
   generic "Tumor" class), update the dict in `tumor_infer.py`.

Nothing else needs to change - `tumor_service.py`, `main.py`, and the
frontend all consume `run_inference_on_series()`'s stable return type.

## No login, no database

Per the original spec: no persisted history. Uploaded DICOMs and
generated outputs live under the OS temp directory
(`%TEMP%\lungvision` on Windows) and are deleted by `POST /api/reset`
(wired to the "New Analysis" button), plus the reconstruction workspace
is cleaned up after every job regardless of success or failure.
