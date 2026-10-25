# VR Lung Physiology Simulator

An interactive **virtual-reality respiratory education tool** built in Unity. The user
stands in front of a life-sized, CT-derived 3D lung model that **breathes in real time**,
driven by a real physiology engine (**BioGears**). Using a floating "patient intake"
console, the user can dial in patient parameters — smoking history, asthma, exercise,
respiratory rate, tidal volume, measured SpO₂ — and watch both the **lung's motion** and
its **visual appearance** respond: healthy pink tissue progressively darkens with tar and
anthracotic pigment as smoking exposure increases, while breathing rate and depth change
with the simulated respiratory state.

The goal is an *educational* visualisation (not a clinical/diagnostic tool) that makes the
link between behaviour (smoking, exercise), physiology (COPD, asthma), and lung condition
tangible and explorable in VR.

---

## Table of Contents
- [What the project does](#what-the-project-does)
- [How it works (architecture)](#how-it-works-architecture)
- [Key components](#key-components)
- [The VR console (user controls)](#the-vr-console-user-controls)
- [Editor tooling](#editor-tooling)
- [Tech stack](#tech-stack)
- [Project layout](#project-layout)
- [Requirements & setup](#requirements--setup)
- [Status — what is done so far](#status--what-is-done-so-far)
- [Known limitations](#known-limitations)
- [Possible next steps](#possible-next-steps)

---

## What the project does

- **Real-time breathing animation.** A CT-segmented lung mesh (~730k vertices) inflates
  and deflates every frame. The waveform (inhale/exhale asymmetry via the I:E ratio) and
  amplitude are derived from the respiratory engine, so exercise/asthma/COPD visibly
  change how the lung moves.
- **Physiology from a real engine.** On Windows the app runs the actual **BioGears** C++
  Common Data Model engine through a native plugin. On any other platform (e.g. a Quest
  Android build) it automatically falls back to a hand-written simulated respiratory model
  so the scene keeps working.
- **Smoking damage visualisation.** Cumulative exposure is measured in **pack-years**
  (cigarettes/day × years / 20). As pack-years rise, the tissue develops fine carbon
  specks → clustered apical/upper-lobe patches → confluent blackening, plus a dark
  "net" of pigment along the septa, following how a real smoker's lung darkens.
- **VR interaction.** The user can rotate the lung by twisting a controller and zoom with
  the thumbstick, without the model ever drifting from its anchored position. All
  parameters are adjusted from a world-space "medical console" panel.
- **Live vitals readout.** Tidal volume, SpO₂, heart rate, expansion %, and
  carboxyhemoglobin are displayed and update from the engine.

---

## How it works (architecture)

```
        VR Console (sliders)                 Editor Setup Tools (menu: "BioGears/…")
                │                                         │
                ▼                                         ▼
        VRUIController ──────────────┐        (build/lay out the scene, bake
                │                    │         materials, smooth the mesh, etc.)
                ▼                    ▼
   BioGearsNativeRespiratory   LungMeshDeformer ── LungTissue.shader
   (IBioGearsRespiratorySystem) │  • breathing deformation (GPU or CPU)
     │                          │  • smoking stain (shader _Damage01 or CPU texture)
     ├── Windows: native  ──────┘
     │   biogears_unity.dll  ── BioGears C++ engine (loads pre-baked state files)
     │
     └── Elsewhere/fallback:
         SimulatedBioGearsRespiratory (analytic physiological model)
```

- **`IBioGearsRespiratorySystem`** is the shared interface (respiration rate, tidal
  volume, SpO₂, expansion factor, etc.). Everything else talks to physiology through it.
- **`BioGearsNativeRespiratory`** is the component every scene references. It runs the
  native engine on its own background thread and publishes a physiology snapshot ~20×/sec,
  while computing the smooth per-frame breathing waveform locally. If the DLL, platform,
  or precomputed states are unavailable, it transparently switches to the simulated model.
- **Pre-baked BioGears states.** BioGears patient stabilisation (esp. with a COPD
  condition) takes minutes to converge — far too slow for a live slider. Instead, five
  COPD severity buckets (`healthy`, `mild`, `moderate`, `significant`, `severe`) are
  pre-stabilised offline and shipped as state files in
  `StreamingAssets/BioGears/states/`. At runtime the engine only *loads* the nearest
  bucket (seconds) and swaps buckets as the smoking slider crosses a threshold.

---

## Key components

| File | Role |
|------|------|
| `Assets/IBioGearsRespiratorySystem.cs` | Interface for any respiratory model. |
| `Assets/BioGearsNativeRespiratory.cs` | Real BioGears engine wrapper (P/Invoke, background thread, state-bucket swapping, asthma/exercise/SpO₂ actions) with automatic simulated fallback. |
| `Assets/SimulatedBioGearsRespiratory.cs` | Analytic physiology model based on published values; used on non-Windows platforms and as a fallback. Also owns respiratory-rate/tidal-volume baselines. |
| `Assets/LungMeshDeformer.cs` | The heart of the visuals: per-frame breathing deformation (GPU vertex-shader path with CPU fallback), pack-years → damage severity, and the smoking-stain rendering (shader `_Damage01` path, or a CPU-painted 512² stain texture with fBm/ridged noise for anthracotic detail). |
| `Assets/LungInspectionController.cs` | VR rotate + zoom of the lung about a visual-centre pivot; hard-anchors the lung's position every frame. Includes a mouse/keyboard fallback for editor testing. |
| `Assets/VRUIController.cs` | Wires the console sliders (smoking years, cigs/day, asthma, exercise, respiratory rate, tidal volume, SpO₂) to physiology + visuals, and computes pack-years/minute-ventilation read-outs. |
| `Assets/VRStatsDisplay.cs` | Live vitals text (tidal volume, SpO₂, HR, expansion, carboxyhemoglobin). |
| `Assets/VRControls.cs` | Minimal legacy slider binder (asthma/COPD/exercise). |
| `Assets/LungTissue.shader` (`BioGears/LungTissue`) | URP PBR shader with **triplanar object-space detail normals** (so sub-mm surface detail isn't limited by the coarse cylindrical UV unwrap), plus optional GPU breathing (`_BreathExpansion`) and GPU smoking damage (`_Damage01`). |
| `Assets/GradientSkybox.shader` (`BioGears/GradientSkybox`) | Dark two-colour gradient background for the "console" look. |

---

## The VR console (user controls)

Adjustable from the world-space panel (built/laid out by the editor tools):

- **Respiratory Rate** (breaths/min) — how often the lung cycles.
- **Tidal Volume** (mL) — how far the lung inflates per breath.
- **Smoking Years** and **Cigarettes/Day** — combine into **pack-years**, which drive both
  the visual tar staining and the COPD severity fed to physiology.
- **Asthma** and **Exercise** severities — applied as live BioGears actions.
- **Measured SpO₂** — injects a real pulse-oximeter reading (something a CT scan cannot
  provide) as an override on the engine's computed oxygen saturation.
- **Read-outs:** Pack-Years, illustrative Damage Stage, and Minute Ventilation (derived).

Interaction: hold the controller trigger on the lung and twist to rotate; thumbstick
up/down to zoom. In the Editor, right-drag rotates and the mouse wheel / `+`/`-` zoom.

---

## Editor tooling

Custom menu items under **`BioGears/`** automate scene setup and asset preparation (the
scene was authored from a CT model at ~100× VR scale, so several fix-up passes exist):

- **Fix VR Scene Layout** (`BioGearsVRSceneSetup`) — re-scales/positions the lung and
  console to human size in front of the user, sets up a soft three-light rig, gradient
  skybox, neon console theme, and clears leftover saturated condition values.
- **Rebuild Patient Intake UI** (`BioGearsIntakeUIBuilder`) — builds the world-space,
  XR-clickable console canvas and wires it to `VRUIController`.
- **Add VR Lung Inspection (Rotate + Zoom)** (`LungInspectionSetup`) — creates the pivot,
  collider, and `XRSimpleInteractable`, and attaches `LungInspectionController`.
- **Migrate Scene To Native Respiratory System** (`BioGearsMigration`) — one-time
  repointing of consumers from the simulated model to `BioGearsNativeRespiratory`.
- **Mesh/material prep** — `LungMeshSmoother` (Taubin smoothing to remove CT slice
  terracing), `LungTissueTextureBaker` (bakes albedo + normal tissue material),
  `LungSurfaceDiagnostic` (measures mesh topology/voxel spacing).

---

## Tech stack

- **Unity** with the **Universal Render Pipeline (URP 17.3)**.
- **XR Interaction Toolkit 3.3.1**, **OpenXR**, and the **Meta XR SDK** (targets Meta
  Quest / OpenXR headsets).
- **BioGears** physiology engine via a native Windows plugin
  (`biogears_unity.dll` + `libbiogears*.dll`, `xerces-c`).
- **TextMeshPro**, **Unity Input System**, C#.

---

## Project layout

```
Assets/
├── *.cs                         # Runtime scripts (physiology, deformer, VR, UI)
├── LungTissue.shader            # Lung tissue PBR + triplanar detail + GPU breathing/damage
├── GradientSkybox.shader        # Console background
├── Editor/                      # BioGears/* menu tools (scene setup, bakers, migration)
├── Scenes/SampleScene.unity     # The single main scene
├── Plugins/
│   ├── x86_64/                  # BioGears native DLLs (Windows)
│   └── Android/                 # Android plugin folder
├── StreamingAssets/BioGears/
│   ├── states/                  # Pre-baked COPD severity states (healthy…severe)
│   ├── patients/  substances/  environments/  config/  xsd/   # BioGears data
└── Samples/, TextMesh Pro/, TutorialInfo/   # Imported package samples
```

---

## Requirements & setup

- **Unity** (URP project; see `Packages/manifest.json` for exact package versions).
- **Windows** for the full native BioGears engine. Other platforms run the simulated
  model automatically.
- An **OpenXR / Meta Quest** headset for VR (mouse/keyboard fallback works in the Editor).

To run:
1. Open the project in Unity and load `Assets/Scenes/SampleScene.unity`.
2. If the scene looks mis-scaled or the console is out of reach, run
   **`BioGears → Fix VR Scene Layout`** (and, if needed, **Rebuild Patient Intake UI** and
   **Add VR Lung Inspection**).
3. Enter Play mode (in-Editor with mouse/keyboard) or build to the headset.

> The native BioGears engine only initialises on Windows Editor/Standalone. On Quest
> builds physiology is driven by `SimulatedBioGearsRespiratory`; smoking/asthma/exercise
> still affect the lung in both modes.

---

## Status — what is done so far

**Working / implemented:**
- ✅ Real-time breathing deformation of the full CT lung mesh, with **GPU vertex-shader**
  path (and a CPU fallback) for Quest-friendly performance.
- ✅ **Real BioGears engine** integrated on Windows via native plugin, running on a
  background thread with pre-baked COPD state buckets for instant severity changes.
- ✅ **Automatic fallback** to a validated simulated respiratory model on non-Windows
  platforms and on any native-load failure.
- ✅ **Smoking / pack-years damage system**: both a shader-driven (`_Damage01`) path and a
  CPU-painted stain texture with anatomically biased, fractal (fBm + ridged) staining;
  cylindrical UV auto-unwrap for meshes that ship without UVs.
- ✅ Smoking exposure also drives **real physiology** (COPD severity), not just cosmetics.
- ✅ **VR rotate + zoom** inspection with a hard position anchor, plus editor mouse/keyboard
  fallback.
- ✅ **World-space "patient intake" console** with sliders for smoking years, cigs/day,
  asthma, exercise, respiratory rate, tidal volume, and measured SpO₂; plus pack-years,
  damage-stage, and minute-ventilation read-outs.
- ✅ **Live vitals display** (tidal volume, SpO₂, heart rate, expansion, carboxyhemoglobin).
- ✅ Custom **URP lung-tissue shader** with triplanar detail normals; gradient skybox;
  soft three-light rig.
- ✅ **Editor automation** for scene layout, UI rebuild, VR inspection setup, native-system
  migration, mesh smoothing (Taubin), and tissue-material baking.
- ✅ Full **BioGears data set** shipped in StreamingAssets (patients, substances,
  environments, config, and precomputed states).

**Recent git history** reflects this progression:
`Add BioGears lung breathing system` → `Integrate real BioGears engine, VR console, and
lung tissue rendering`.

**In progress / uncommitted at time of writing** (`git status`):
- `LungMeshDeformer.cs` — modifications.
- `LungInspectionController.cs` + `Editor/LungInspectionSetup.cs` — **new**, VR rotate/zoom
  inspection (not yet committed).
- Scene (`SampleScene.unity`) edits.

---

## Known limitations

- The native BioGears engine is **Windows-only**; Quest/Android runs the simulated model.
- On the native engine, **respiratory rate and tidal volume are derived outputs**, so those
  two sliders only take effect in the simulated (Quest) mode.
- Damage staging and colouring are **illustrative/educational**, not clinical estimates —
  there is deliberately no "% of lung destroyed" figure.
- The lung mesh is a high-density (~730k vertex) CT segmentation; some tooling
  (mesh smoothing, baked materials) exists specifically to tame its cost/appearance.

---

## Possible next steps

- Export a BioGears override so respiratory-rate/tidal-volume sliders affect the native
  engine too.
- Commit the in-progress VR inspection feature and scene changes.
- Add more conditions/actions surfaced through the console (e.g. respiratory distress,
  bronchodilator response).
- Automated verification / a lightweight test scene for the fallback path.

---

*Educational VR visualisation only — not a medical or diagnostic device.*
