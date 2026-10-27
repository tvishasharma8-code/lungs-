# LungVision website

React + Vite + Tailwind front end for the LungVision API (`../backend`): upload a CT
folder, run the 3D reconstruction / tumor detection, explore the 3D lung and tumors,
**Send to VR** or **View in Unity**.

```
frontend/
  src/
    pages/        Home, Upload, Processing (live progress), Results (3D viewer, tumors, VR)
    components/   file drop zone, analysis choice, progress list, STL viewer, VR panel
    services/     api.ts (every backend call), SessionContext (upload / job state)
    types/        response shapes, mirrored from backend/models/schemas.py
  vite.config.ts  dev server on :5173, proxies /api to the backend on 127.0.0.1:8000
```

## Setup and run (Node 22)

From the repository root:

```powershell
cd frontend
npm ci
npm run dev        # http://localhost:5173 - start the backend first (backend/README.md)
```

## Checks

```powershell
npx tsc -b         # type-check
npm run build      # production build into dist/
```
