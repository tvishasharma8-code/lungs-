# VR Lung Physiology Simulator

A virtual-reality respiratory education tool. You stand in front of life-sized lungs –
the built-in CT model, or **a real patient's lungs reconstructed from their CT** – that
**breathe in real time, driven by the BioGears physiology engine**. A console lets you
set breathing, diseases (asthma, COPD, pneumonia, ARDS), smoking history and exercise,
and watch the lungs' motion, colour and the vitals respond. Tumors found in the CT by an
AI model are shown inside the lungs.

Education / research prototype – **not a medical device and not a diagnosis.**

## The system

```
 CT scan (DICOM folder)
        │  upload
        ▼
 LungVision website + backend  (lungs-/)                         BioGears C++ engine
   TotalSegmentator → 3D lungs (patient mm)                       (native plugin, Windows)
   Faster R-CNN → tumors (mm, lobe)                                        │
        │  "Send to VR" / "View in Unity" → Patient Package                │
        ▼                                                                  ▼
 Unity app (this folder) ── lungs + tumors + console + vitals ── breathing, gas exchange
   • Quest over Link / in the Editor         – BioGears runs on the PC
   • Quest standalone                        – BioGears streamed from the PC over Wi-Fi
   • desktop window (no headset)             – mouse + keyboard, BioGears on the PC
```

## Folder layout

```
lung project/                     this repo: the Unity 6000.3.5f1 project + LungVision + ML prototype
  Assets/
    LungVR/                       ← all of this project's own Unity code and assets
      Scripts/
        Physiology/               BioGears native engine, simulated fallback, shared interface
          Link/                   PC ↔ Quest BioGears streaming (UDP protocol, server, client)
        Lung/                     breathing / disease deformation, rotate + zoom, airflow
        Patient/                  Patient Package reader, patient lung loader, tumor markers
        UI/                       console (tabs, presets, status), vitals monitor, graphs
        App/                      desktop (no headset) mode
        Legacy/                   old components kept for the scene migration tool
      Editor/                     BioGears/* menu tools (scene setup, desktop build, …)
      Shaders/                    lung tissue, airflow particles, tumor markers, skybox
      Models/                     LungModel.fbx (the built-in CT lung)
    Scenes/SampleScene.unity      the one scene
    Settings/                     URP settings + this project's materials/textures
    Plugins/x86_64/               BioGears native DLLs (Windows)
    StreamingAssets/BioGears/     BioGears data + precomputed patient states
    Samples/ XRI/ XR/ Oculus/ TextMesh Pro/ CompositionLayers/   packages' own folders
  Docs/                           ROADMAP.md, INTEGRATION_PLAN.md, capstone report
  Tools/                          C# tests that run outside Unity (see Tests)
  lungs-/                         LungVision website + backend (setup: lungs-/README.md)
  ML_Unity_Integration/           ML team's original tumor prototype (superseded by the backend)
  Builds/                         build outputs (ignored)
```

BioGears wrapper source (P/Invoke DLL): `C:\Users\tvish\BioGears\wrapper` – build with
`cmake --build build --config Release --target biogears_unity`, then copy
`build/Release/biogears_unity.dll` into `Assets/Plugins/x86_64/` **with Unity closed**
(Unity locks loaded DLLs).

## Requirements

- Windows PC, **Unity 6000.3.5f1** (URP 17.3, XR Interaction Toolkit 3.3, OpenXR, Meta XR SDK).
- Meta Quest headset for VR (optional – the desktop mode needs none).
- For patients: LungVision (Python 3.12, Node 22) – see `lungs-/README.md`.

## Running

1. **Unity:** open the project, load `Assets/Scenes/SampleScene.unity`, then run
   **BioGears > Fix VR Scene Layout** once after pulling changes (places the lungs and
   panels, builds the console tabs and vitals monitor, wires everything). Save the scene.
2. **Play:** in the Editor without a headset the scene runs in desktop mode (mouse);
   with Quest Link it runs in VR. BioGears runs natively on Windows.
3. **Patients (optional):** start the LungVision backend and website (`lungs-/README.md`),
   upload a CT, press **Send to VR**. The app shows the active patient within ~5 s;
   tumors appear inside the lungs (console: *View > Show tumors*).
4. **Desktop app:** **BioGears > Build Desktop App (Windows)** → `Builds/Desktop/LungVisionVR.exe`
   (the website's **View in Unity** starts it).
5. **Quest standalone:** build for Android. Keep this scene playing on the PC (it serves
   BioGears over Wi-Fi, UDP 47810/47811) and run the backend with `--host 0.0.0.0` for
   patients; the headset finds the PC by itself. Without a PC it uses the simulated model.

### In the app

- **Patient Controls** (right): header shows the scenario and whose lungs are shown;
  tabs *Scenarios* (presets), *Breathing* (rate, tidal volume, SpO2, exercise),
  *Conditions* (asthma, pneumonia, ARDS), *Smoking* (years, cigarettes/day, pack-years),
  *View* (highlight affected regions, airflow, tumors, colour legend).
- **Vitals** (left): *Overview* – respiratory rate, SpO2, tidal volume, heart rate, the
  lung volume waveform and trends; *Details* – gas exchange and lung mechanics.
- **Lungs**: grab and twist with the controller (desktop: right-drag), thumbstick / mouse
  wheel to zoom, *R* resets (desktop). The CT slice panel below the lungs shows each
  tumor's clearest slice; *Next tumor* steps through them.

## Tests

| Where | Command | Checks |
|---|---|---|
| `Tools/BioGearsLinkTests` | `dotnet run` | PC ↔ headset BioGears link: discovery, commands, snapshots, reconnect |
| `Tools/PatientPackageTests` | `dotnet run -- <package folder>` | mesh.bin reader, LPS → Unity axes, anatomy, tumors placed at their lobe |
| `lungs-/backend` | see `lungs-/README.md` | coordinates against the CT, VR endpoints, static checks |

Package folders live in `%USERPROFILE%\LungVision\vr_patients\<id>`.

## Docs

- `Docs/ROADMAP.md` – the phase plan from the capstone report and its status.
- `Docs/INTEGRATION_PLAN.md` – how LungVision, the tumor model and the VR app fit
  together, decisions taken, progress.
- `Docs/Capstone Report.pdf`.
