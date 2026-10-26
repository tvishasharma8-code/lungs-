# Roadmap — closing the gap between the capstone report and the build

Scope: everything the Phase 2 report describes or the Phase 3 plan promises that is not
built yet. The ML side (`lungs-/`: TotalSegmentator, tumour inference, DICOM upload) is
out of scope for now.

Work goes one item at a time, in order. Tick each box when it is done and verified.

---

## Phase 1 — Console and readouts (Unity only, no engine rebuild)

- [x] **1.1 Scenario presets.** One-tap buttons on the console (Healthy, Asthma attack,
      Heavy smoker, Severe COPD, Exercise) that set every slider at once. Covers the
      report's "user selects a simulation scenario" step.
- [x] **1.2 Live trend graphs.** Rolling lung-volume waveform plus SpO2 and respiratory
      rate history on the console, instead of numbers only.
- [x] **1.3 Extra vitals the engine already computes.** Respiratory rate, total and
      left/right lung volume, FRC, I:E ratio, minute ventilation, transpulmonary pressure
      in the stats panel.

## Phase 2 — Visualization

- [x] **2.1 Disease-region highlighting.** Asthma → crimson, mucus-glossy airways plus a
      faint whole-lung flush; "Highlight regions" toggle glows amber (asthma airways) and
      violet (COPD scarring/emphysema). Driven by the same severities as the physiology.
      Known gap: emphysema patches are randomly placed, not upper-lobe biased.
- [x] **2.2 Airflow visualization.** Calm airflow view: faint glowing airway tree (3 lobar
      branches in the larger lung, 2 in the smaller), blue air flowing in step with the
      breath and turning pink in the deep lung, a soft glow at each branch end; asthma
      narrows the airways (amber), COPD traps air (dim violet). "Show airflow" toggle.

## Phase 3 — Native BioGears engine (wrapper rebuild in `C:\Users\tvish\BioGears\wrapper`)

- [x] **3.1 New outputs.** Export VO2 (oxygen consumption), airway resistance, alveolar
      gas exchange values from the wrapper; show them on the console.
- [x] **3.2 Respiratory distress (ARDS) and more diseases.** ARDS as a live BioGears action;
      lobar pneumonia as precomputed mild/severe patients. Console sliders + presets;
      lung shows consolidation per lung as the loaded patient has it, ARDS as diffuse wet
      lungs; green highlight. (Impaired alveolar exchange not added.)
- [x] **3.3 Respiratory rate / tidal volume in native mode.** The sliders pace the real
      engine breath by breath (BioGears conscious respiration, inserted directly to get
      round a BioGears marshalling bug), with breath-by-breath tidal-volume correction.
      Verified: 8x300 -> PaO2 61 / PaCO2 46; 28x900 -> PaCO2 22. Test: wrapper/tests/paced_test.c
- [ ] **3.4 Real BioGears on the Quest.** Run the engine on the Windows PC and stream the
      physiology snapshot to the headset over the local network; Quest keeps the
      simulated model as fallback when the PC is not reachable.

## Phase 4 — Data layer and personalisation

- [ ] **4.1 Patient profile.** Age, sex, height, weight on the console → simulated model
      and BioGears patient file.
- [ ] **4.2 Session logging.** Save each session (start/end, settings, vitals over time,
      results) locally, following the report's ER schema (USER, SESSION,
      SENSOR_READING, SIMULATION_RESULT).
- [ ] **4.3 Export / review.** CSV export of a session and an in-app list of past
      sessions.

## Phase 5 — Performance, validation, tests

- [ ] **5.1 Mesh LOD / decimation** for the ~730k-vertex lung on Quest.
- [ ] **5.2 Validation.** Compare simulated outputs with published reference values for
      healthy, asthma, COPD and exercise; write up the results table.
- [ ] **5.3 Automated tests.** Edit-mode tests for the simulated model and the native →
      fallback switch.

## Phase 6 — Future-facing (design-ready, not hardware)

- [ ] **6.1 Sensor input layer.** A `ISensorSource` interface plus a replay source that
      plays recorded spirometer / pulse-oximeter / RIP-belt data, so real sensors can be
      plugged in later.
- [ ] **6.2 Basic access control.** Instructor PIN to lock the console settings.

## Phase 7 — Report

- [ ] **7.1 Update the capstone report** to match the build (Ch 8 code, conclusion, the
      typo and consistency fixes) once the phases above are done.
