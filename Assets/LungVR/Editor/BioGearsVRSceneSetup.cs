using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using Unity.XR.CoreUtils;

/// <summary>
/// Lays the scene out for VR without touching the lung model, BioGears wiring, or any
/// simulation logic - it only moves, scales and orients what already exists.
///
/// The scene was authored around a CT-derived mesh whose units are ~100x VR scale: the
/// lung sat 311 m from where the headset actually puts the user, the control panel sat
/// 204 m away and 125 degrees off to the side (hence "behind me"), and the panel itself
/// measured 541 m x 310 m. In VR 1 unit == 1 metre, so none of that can work: the ray
/// interactor only reaches 30 m, so the panel was literally out of range of the
/// controller no matter where it pointed.
///
/// This positions the lung and the panel together in front of wherever the XR Origin
/// puts the user, at human scale.
/// </summary>
public static class BioGearsVRSceneSetup
{
    // Comfortable VR placement, all in metres relative to the XR Origin.
    // Sized so the lung reads as the main subject directly ahead (~30 degrees of arc) and
    // the panel is large enough to read and hit with a controller ray. The lung/panel
    // relationship (panel to the user's right, slightly lower, slightly nearer) is
    // unchanged from the previous layout - only the scale grew.
    const float LungDistance = 1.55f;
    const float LungSideOffset = 0f;     // centred: the tabbed panels are small enough to flank it
    const float LungHeight = 1.4f;
    // Longest axis of the lung once scaled. Raised from 0.85 so the lung reads as the
    // main subject of the scene; bounded by two hard limits - it must not reach the
    // user's head, and it must not grow into the console on the right.
    const float LungTargetSize = 1.05f;

    const float PanelDistance = 1.35f;
    const float PanelSideOffset = 1.12f; // to the user's right, mirrored by the monitor
    const float PanelHeight = 1.25f;   // centre - the whole panel sits around eye level
    const float PanelTargetWidth = 0.95f;

    // Vitals monitor to the user's left, the same distance out as the console, so the
    // lung sits between the controls and the readings.
    const float MonitorSideOffset = -1.12f;
    const float MonitorHeight = 1.25f;
    const float MonitorTargetWidth = 0.95f;

    // Neon medical-console palette. Dark chassis, cyan HUD accents.
    static readonly Color PanelChassis = new Color(0.020f, 0.026f, 0.035f, 0.95f);
    static readonly Color NeonPrimary = new Color(0.25f, 0.95f, 1.00f, 1f);   // cyan
    static readonly Color NeonDim = new Color(0.25f, 0.95f, 1.00f, 0.35f);
    static readonly Color TrackColor = new Color(0.06f, 0.10f, 0.13f, 1f);

    const float EyeHeight = 1.5f;        // only used to aim the panel at the viewer

    [MenuItem("BioGears/Fix VR Scene Layout")]
    public static void Run()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            Debug.Log("[BioGearsVRSceneSetup] Play mode is active - exiting Play mode, then fixing layout automatically.");
            EditorApplication.playModeStateChanged += ResumeAfterPlayMode;
            EditorApplication.isPlaying = false;
            return;
        }

        Fix();
    }

    static void ResumeAfterPlayMode(PlayModeStateChange state)
    {
        if (state != PlayModeStateChange.EnteredEditMode)
            return;

        EditorApplication.playModeStateChanged -= ResumeAfterPlayMode;
        EditorApplication.delayCall += Fix;
    }

    static void Fix()
    {
        string scenePath = "Assets/Scenes/SampleScene.unity";
        Scene scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);

        XROrigin origin = Object.FindFirstObjectByType<XROrigin>();
        if (origin == null)
        {
            Debug.LogError("[BioGearsVRSceneSetup] No XROrigin in the scene - cannot place content relative to the user.");
            return;
        }

        Transform originT = origin.transform;

        // Recentre the rig on world origin. The XR Origin was authored at
        // (-117.6, -18.1, 0), and because all content is positioned relative to it,
        // every object ended up ~118 m from world origin - so the Scene view opens on
        // empty grid and the content has to be hunted for with Frame Selected. Nothing
        // depends on the rig's absolute position (the headset defines the play space at
        // runtime, and lung/panel are re-placed relative to the origin just below), so
        // moving it to zero is safe and makes the Scene view usable again.
        if (originT.position.sqrMagnitude > 0.01f)
        {
            Debug.Log($"[BioGearsVRSceneSetup] Recentred XR Origin from {originT.position} to world origin so the scene is visible in the Scene view.");
            originT.position = Vector3.zero;
        }

        Vector3 forward = originT.forward;
        Vector3 right = originT.right;
        Vector3 floor = originT.position;
        Vector3 eye = floor + Vector3.up * EyeHeight;

        // --- 1. Hand the camera back to the tracking system -------------------------
        // The Main Camera had a hard-coded local offset of (344, 239, 344). On device
        // TrackedPoseDriver overwrites local position/rotation with the headset pose
        // every frame, so that offset silently vanished on Quest while still framing
        // the Editor's Game view. That is exactly why the scene "looked right" on the
        // laptop and put the panel behind the user in the headset.
        Camera cam = origin.Camera != null ? origin.Camera : Camera.main;
        if (cam != null)
        {
            Transform camT = cam.transform;
            if (camT.localPosition.sqrMagnitude > 0.0001f)
            {
                Debug.Log($"[BioGearsVRSceneSetup] Main Camera had a stale local offset of {camT.localPosition} - zeroing it so the Editor matches the headset (TrackedPoseDriver owns this transform at runtime).");
                camT.localPosition = Vector3.zero;
                camT.localRotation = Quaternion.identity;
            }
        }

        // --- 2. Lung: scaled to human size, straight ahead --------------------------
        LungMeshDeformer[] deformers = Object.FindObjectsByType<LungMeshDeformer>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        LungMeshDeformer deformer = deformers.Length > 0 ? deformers[0] : null;
        if (deformers.Length > 1)
            Debug.LogWarning($"[BioGearsVRSceneSetup] Found {deformers.Length} LungMeshDeformer components - only the first was placed. Duplicates will each spawn their own overlay.");

        if (deformer != null)
        {
            Transform lung = deformer.transform;

            if (TryGetWorldBounds(lung, out Bounds bounds) && bounds.size.magnitude > 0.0001f)
            {
                float longestAxis = Mathf.Max(bounds.size.x, Mathf.Max(bounds.size.y, bounds.size.z));
                if (longestAxis > 0.0001f)
                {
                    float k = LungTargetSize / longestAxis;
                    lung.localScale *= k;
                }

                // Re-read bounds after scaling, then translate so the *visual centre*
                // (not the pivot, which on this FBX is nowhere near the mesh) lands on
                // the target point.
                if (TryGetWorldBounds(lung, out Bounds scaled))
                {
                    Vector3 target = floor + forward * LungDistance + right * LungSideOffset + Vector3.up * LungHeight;
                    lung.position += target - scaled.center;
                }
            }

            // --- 3. Kill the runtime lung duplicate ---------------------------------
            // useOverlayMesh spawns "LungSootOverlay": a full clone of a 730k-vertex
            // mesh, re-uploaded with a freshly allocated 512x512 Color[] EVERY FRAME
            // (UpdateOverlayMaterial), wearing a material built from Shader.Find - which
            // returns null in a URP player and renders magenta. That is both the pink
            // duplicate lung and a large part of the frame-rate collapse.
            // Smoking visuals are unaffected: ApplySmokingColor writes the same stain
            // texture directly into the lung's own material, which is the primary path.
            SerializedObject so = new SerializedObject(deformer);
            SerializedProperty overlay = so.FindProperty("useOverlayMesh");
            if (overlay != null && overlay.boolValue)
            {
                overlay.boolValue = false;
                Debug.Log("[BioGearsVRSceneSetup] Disabled LungMeshDeformer.useOverlayMesh (source of the magenta duplicate lung and a per-frame 512x512 texture upload).");
            }

            // Full smoking progression across the slider's 0-20 year range. This was
            // 40, so a slider that stops at 20 could only ever reach the halfway point
            // of the effect - half the reason the change was barely perceptible.
            SerializedProperty maxYears = so.FindProperty("maxSmokingYears");
            if (maxYears != null)
                maxYears.floatValue = 20f;

            // Breathing amplitude and per-axis shaping, restored to the values the
            // scene originally shipped with. The animation is back to its earlier
            // uniform-radial form, so these are the numbers it was tuned against.
            SerializedProperty maxExpansion = so.FindProperty("maxExpansion");
            if (maxExpansion != null)
                maxExpansion.floatValue = 5f;

            SerializedProperty expansionScale = so.FindProperty("expansionScale");
            if (expansionScale != null)
                expansionScale.vector3Value = new Vector3(1f, 0.8f, 1.2f);

            // The scene had this at a mid-brown (0.22, 0.17, 0.12), so even "40 years"
            // only ever tanned the tissue. Severe anthracosis is near-black.
            SerializedProperty smokerColor = so.FindProperty("smokerLungColor");
            if (smokerColor != null)
                smokerColor.colorValue = new Color(0.045f, 0.042f, 0.038f, 1f);

            so.ApplyModifiedProperties();
        }
        else
        {
            Debug.LogWarning("[BioGearsVRSceneSetup] No LungMeshDeformer found - skipped lung placement.");
        }

        ResetSaturatedConditions();
        EnsureAirflowVisualizer(deformer);
        EnsureStreamServer();
        EnsurePatientLoader(deformer);
        EnsureTumorMarkers(deformer);

        // --- 4. Panel: beside the lung, human sized, facing the user -----------------
        Canvas canvas = null;
        foreach (Canvas c in Object.FindObjectsByType<Canvas>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (c.transform.parent == null && !VitalsMonitorBuilder.IsMonitor(c)) { canvas = c; break; }
        }

        if (canvas != null)
        {
            RectTransform rt = canvas.GetComponent<RectTransform>();

            if (canvas.renderMode == RenderMode.WorldSpace && canvas.worldCamera == null && cam != null)
                canvas.worldCamera = cam;

            EnsureRespiratoryRows(rt);
            EnsurePresetBar(rt);
            EnsureHighlightControls(rt);
            BuildTabbedLayout(rt);
            ApplyNeonTheme(rt);

            float width = rt.sizeDelta.x;
            if (width > 0.0001f)
                rt.localScale = Vector3.one * (PanelTargetWidth / width);

            rt.position = floor
                + forward * PanelDistance
                + right * PanelSideOffset
                + Vector3.up * PanelHeight;

            // A canvas's readable face looks down -Z, so point +Z away from the viewer.
            Vector3 awayFromViewer = rt.position - eye;
            if (awayFromViewer.sqrMagnitude > 0.0001f)
                rt.rotation = Quaternion.LookRotation(awayFromViewer.normalized, Vector3.up);

            // uGUI ignores back-facing graphics by default; in VR the user can walk
            // around a world-space panel, and a dead panel is worse than a mirrored one.
            GraphicRaycaster gr = canvas.GetComponent<GraphicRaycaster>();
            if (gr != null)
                gr.ignoreReversedGraphics = false;

            Debug.Log($"[BioGearsVRSceneSetup] Panel placed at {rt.position}, {PanelTargetWidth:F2} m wide, facing the user.");
        }
        else
        {
            Debug.LogWarning("[BioGearsVRSceneSetup] No root Canvas found - skipped panel placement.");
        }

        // --- 5. Vitals monitor: mirrors the console on the other side of the lung ----
        Vector3 monitorPos = floor
            + forward * PanelDistance
            + right * MonitorSideOffset
            + Vector3.up * MonitorHeight;
        VitalsMonitorBuilder.Build(monitorPos, eye, MonitorTargetWidth, cam, PanelChassis, NeonPrimary, NeonDim);

        ApplyGradientBackground(cam);
        SetupSoftLighting(originT);

        EditorSceneManager.MarkSceneDirty(scene);
        bool saved = EditorSceneManager.SaveScene(scene);
        Debug.Log($"[BioGearsVRSceneSetup] VR layout fixed in '{scenePath}'. Saved: {saved}");
    }

    // Every slider row of the console. Which tab each one is on: ConsolePages.
    static readonly string[] RowOrder =
    {
        "RespiratoryRateRow",
        "TidalVolumeRow",
        "SpO2Row",
        "AsthmaRow",
        "PneumoniaRow",
        "ArdsRow",
        "SmokingYearsRow",
        "CigarettesPerDayRow",
        "ExerciseRow",
    };

    const float RowHeight = 124f;    // label + slider + breathing room
    const float BottomPadding = 36f;
    const float ReadoutHeight = 46f;

    // Derived values (Smoking tab, under its sliders).
    static readonly string[] ReadoutOrder = { "PackYearsReadout", "DamageStageReadout" };

    // Scenario presets: the Scenarios tab, the first one the console opens on - picking
    // a scenario is the first thing a user does, then they fine-tune on the other tabs.
    // The read-out of the active scenario sits in the header, visible on every tab.
    const string PresetBarName = "ScenarioPresetBar";
    const string ScenarioReadoutName = "ScenarioReadout";
    const float PresetButtonGap = 14f;
    const float PresetFontSize = 34f;

    // Viewing options (highlight, airflow, tumors) and the colour legend: the View tab.
    const string HighlightButtonName = "ViewToggles";
    const string HighlightLegendName = "HighlightLegend";
    const float HighlightButtonHeight = 72f;
    const float HighlightLegendHeight = 118f;  // three legend lines

    // Type sized for reading at ~1.6 m in a headset. The original 22 px labels mapped
    // to roughly 2 cm tall in world space (~0.7 degrees of arc), which is below the
    // ~1.5 degrees needed to read comfortably in VR - especially once the Quest
    // pipeline renders at less than native resolution.
    const float TitleFontSize = 62f;
    const float LabelFontSize = 48f;
    const float LabelHeight = 60f;
    const float SliderHeight = 50f;
    const float LabelToSliderGap = 10f;

    /// <summary>
    /// Replaces the single hard key light with a soft three-light rig.
    ///
    /// The scene had exactly one directional light at intensity 2 and an ambient ground
    /// colour of 0.02. Anything angled away from that one light - the space between the
    /// lungs, the undersides, around the trachea - received no direct light and
    /// essentially no fill, so it rendered black. LungSurfaceDiagnostic confirmed the
    /// mesh is watertight (0 boundary edges of 2,188,248), which rules out the obvious
    /// alternative that those areas were holes: they are simply unlit.
    ///
    /// A key/fill/rim arrangement is how the reference photographs are lit, and it is
    /// what makes an organ read as photographed rather than rendered. Only the key
    /// casts shadows - the other two exist purely to put light into the concavities,
    /// and shadow-casting lights are the expensive ones on a Quest.
    /// </summary>
    static void SetupSoftLighting(Transform originT)
    {
        Light key = null;
        foreach (Light l in Object.FindObjectsByType<Light>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (l.type != LightType.Directional)
                continue;

            // Reuse our own lights on re-run rather than stacking up duplicates.
            if (l.gameObject.name == "BioGears Fill Light" || l.gameObject.name == "BioGears Rim Light")
                continue;

            if (key == null)
                key = l;
        }

        if (key != null)
        {
            // 2.0 against near-black ambient blew out whatever it hit while leaving
            // everything else black. Lower key + real fill gives a far wider tonal range.
            key.intensity = 0.95f;
            key.color = new Color(1f, 0.97f, 0.94f);   // faintly warm
            key.shadows = LightShadows.Soft;
            key.shadowStrength = 0.55f;                 // shadows present, not crushed
            // Raked, not frontal. The viewer looks down +Z, so putting the key ~62
            // degrees off that axis makes light graze the surface: every bump casts a
            // short shadow toward the camera and the microtexture becomes visible.
            // A frontal key (the previous -35) flattens relief - it lights the tops and
            // the pits equally, which is why detail work kept appearing to do nothing.
            key.transform.rotation = Quaternion.Euler(30f, -62f, 0f);
        }

        // Fill: opposite side, cool, no shadows. This is the light that actually removes
        // the black regions.
        Light fill = EnsureDirectional("BioGears Fill Light", originT);
        fill.intensity = 0.30f;
        // Neutral rather than cool. A blue fill on salmon tissue lifts the blue channel
        // proportionally more than the red, which narrows R-B and reads as desaturation -
        // the warm/cool contrast that flatters most subjects works against flesh tones.
        fill.color = new Color(0.96f, 0.94f, 0.92f);
        fill.shadows = LightShadows.None;
        // Opposite the raked key, and kept low so it fills without flattening again.
        fill.transform.rotation = Quaternion.Euler(14f, 128f, 0f);

        // Rim: from behind and below, to separate the organ from the dark background
        // and hint at the translucency URP/Lit cannot actually simulate.
        Light rim = EnsureDirectional("BioGears Rim Light", originT);
        rim.color = new Color(1f, 0.86f, 0.84f);
        rim.shadows = LightShadows.None;
        // Behind and slightly below - catches the silhouette and feeds the shader's
        // translucency term, which is strongest where the tissue is thinnest.
        rim.intensity = 0.30f;
        rim.transform.rotation = Quaternion.Euler(-16f, 96f, 0f);

        Debug.Log("[BioGearsVRSceneSetup] Soft three-light rig applied (key 1.45 + fill 0.60 + rim 0.38). " +
                  "Only the key casts shadows. This is what lifts the black regions between and beneath the lungs.");
    }

    static Light EnsureDirectional(string name, Transform originT)
    {
        GameObject go = GameObject.Find(name);
        if (go == null)
            go = new GameObject(name);

        Light l = go.GetComponent<Light>();
        if (l == null)
            l = go.AddComponent<Light>();

        l.type = LightType.Directional;
        go.transform.position = originT.position + Vector3.up * 2f;
        return l;
    }

    /// <summary>
    /// Clears condition severities that were left saturated in the scene from earlier
    /// testing: asthma 0.988, COPD 1.0, respiratory distress 1.0, exercise 1.0.
    ///
    /// These matter because they compound multiplicatively on tidal volume - together
    /// they cut it to roughly a third of normal - so once the lung's inflation depth is
    /// driven by tidal volume there is almost no headroom left for a slider to show a
    /// change. Respiratory distress is the worst offender: nothing in the UI exposes
    /// it, so it stayed pinned at maximum for the entire session.
    /// </summary>
    static void ResetSaturatedConditions()
    {
        SimulatedBioGearsRespiratory[] models = Object.FindObjectsByType<SimulatedBioGearsRespiratory>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        if (models.Length == 0)
            return;

        string[] fields = { "asthmaSeverity", "copdSeverity", "respiratoryDistressSeverity", "pneumoniaSeverity", "exerciseIntensity" };

        foreach (SimulatedBioGearsRespiratory model in models)
        {
            SerializedObject so = new SerializedObject(model);
            bool changed = false;

            foreach (string field in fields)
            {
                SerializedProperty p = so.FindProperty(field);
                if (p != null && p.propertyType == SerializedPropertyType.Float && p.floatValue > 0.0001f)
                {
                    Debug.Log($"[BioGearsVRSceneSetup] Reset {field} {p.floatValue:F3} -> 0 (was saturated, leaving no range for the sliders to act on).");
                    p.floatValue = 0f;
                    changed = true;
                }
            }

            if (changed)
                so.ApplyModifiedProperties();
        }
    }

    /// <summary>
    /// Adds the Respiratory Rate and Tidal Volume rows to the existing canvas if they
    /// aren't there yet, and binds them to VRUIController. These two are the only
    /// respiratory mechanics the model actually exposes as inputs, and they are what
    /// drive breathing frequency and inflation amplitude respectively - everything
    /// else BioGears reports (minute ventilation, FRC, lung volumes) is derived and
    /// belongs as a read-out, not a slider.
    ///
    /// Rows are appended to the live canvas rather than rebuilt so that every existing
    /// slider, listener and controller reference survives untouched.
    /// </summary>
    static void EnsureRespiratoryRows(RectTransform canvasRect)
    {
        VRUIController[] controllers = Object.FindObjectsByType<VRUIController>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        VRUIController controller = controllers.Length > 0 ? controllers[0] : null;
        if (controller == null)
        {
            Debug.LogWarning("[BioGearsVRSceneSetup] No VRUIController found - new respiratory rows were not created.");
            return;
        }

        SerializedObject so = new SerializedObject(controller);
        float rowWidth = canvasRect.sizeDelta.x - 80f;

        CreateRowIfMissing(canvasRect, so, "RespiratoryRateRow", "Respiratory Rate: 14 /min",
                           "respiratoryRateSlider", "respiratoryRateText", rowWidth);
        CreateRowIfMissing(canvasRect, so, "TidalVolumeRow", "Tidal Volume: 500 mL",
                           "tidalVolumeSlider", "tidalVolumeText", rowWidth);
        CreateRowIfMissing(canvasRect, so, "CigarettesPerDayRow", "Cigarettes/Day: 20",
                           "cigarettesPerDaySlider", "cigarettesPerDayText", rowWidth);

        CreateRowIfMissing(canvasRect, so, "PneumoniaRow", "Pneumonia: 0% (none)",
                           "pneumoniaSlider", "pneumoniaText", rowWidth);
        CreateRowIfMissing(canvasRect, so, "ArdsRow", "ARDS severity: 0%",
                           "ardsSlider", "ardsText", rowWidth);

        // The exercise row predates the read-out field; bind its existing label.
        Transform exerciseRow = FindDeep(canvasRect, "ExerciseRow");
        if (exerciseRow != null)
            so.FindProperty("exerciseText").objectReferenceValue =
                exerciseRow.GetComponentInChildren<TMPro.TextMeshProUGUI>(true);

        // Read-outs, not inputs: pack-years is derived, and the stage label is
        // descriptive. Both are labels with no slider.
        CreateReadoutIfMissing(canvasRect, so, "PackYearsReadout", "Pack-Years: 0.0", "packYearsText", rowWidth);
        CreateReadoutIfMissing(canvasRect, so, "DamageStageReadout", "Stage: Healthy - no smoking history", "damageStageText", rowWidth);

        so.ApplyModifiedProperties();
    }

    /// <summary>
    /// A label-only row for derived values. Pack-years and the damage stage are
    /// computed, not chosen, so giving them sliders would imply they are inputs.
    /// </summary>
    static void CreateReadoutIfMissing(RectTransform canvasRect, SerializedObject controller,
                                       string name, string labelText, string textField, float rowWidth)
    {
        if (FindDeep(canvasRect, name) != null)
            return;

        TMPro.TextMeshProUGUI label = BioGearsIntakeUIBuilder.CreateLabel(
            canvasRect, name, labelText, 34f, TMPro.TextAlignmentOptions.Left);

        RectTransform rect = label.rectTransform;
        rect.anchorMin = new Vector2(0.5f, 1f);
        rect.anchorMax = new Vector2(0.5f, 1f);
        rect.pivot = new Vector2(0.5f, 1f);
        rect.sizeDelta = new Vector2(rowWidth, 44f);

        SerializedProperty prop = controller.FindProperty(textField);
        if (prop != null)
            prop.objectReferenceValue = label;

        Debug.Log($"[BioGearsVRSceneSetup] Created read-out '{name}' bound to VRUIController.{textField}.");
    }

    static void CreateRowIfMissing(RectTransform canvasRect, SerializedObject controller,
                                   string rowName, string labelText,
                                   string sliderField, string textField, float rowWidth)
    {
        Transform existing = FindDeep(canvasRect, rowName);
        if (existing != null)
            return;

        // CreateRow names the child "<rowName-minus-Row>Slider"/"...Label"; the exact
        // names don't matter here because we bind the references directly.
        string baseName = rowName.EndsWith("Row") ? rowName.Substring(0, rowName.Length - 3) : rowName;
        var row = BioGearsIntakeUIBuilder.CreateRow(canvasRect, baseName, labelText, 0f, RowHeight, rowWidth);

        // CreateRow parents under canvasRect and names the object "<baseName>Row".
        SerializedProperty sliderProp = controller.FindProperty(sliderField);
        if (sliderProp != null)
            sliderProp.objectReferenceValue = row.slider;

        SerializedProperty textProp = controller.FindProperty(textField);
        if (textProp != null)
            textProp.objectReferenceValue = row.label;

        Debug.Log($"[BioGearsVRSceneSetup] Created '{rowName}' and bound it to VRUIController.{sliderField}.");
    }

    /// <summary>
    /// Adds ScenarioPresets next to VRUIController and (re)builds one button per preset
    /// plus a "Scenario: ..." read-out. The bar is rebuilt every run so the buttons
    /// always match the current preset list; the buttons hold no wiring of their own
    /// (ScenarioPresets binds them at runtime), so rebuilding loses nothing.
    /// </summary>
    static void EnsurePresetBar(RectTransform canvasRect)
    {
        VRUIController controller = Object.FindFirstObjectByType<VRUIController>(FindObjectsInactive.Include);
        if (controller == null)
        {
            Debug.LogWarning("[BioGearsVRSceneSetup] No VRUIController found - scenario presets were not added.");
            return;
        }

        ScenarioPresets presets = controller.GetComponent<ScenarioPresets>();
        if (presets == null)
        {
            presets = controller.gameObject.AddComponent<ScenarioPresets>();
            Debug.Log("[BioGearsVRSceneSetup] Added ScenarioPresets to the VRUIController object.");
        }

        // The scene saves the preset list, so presets added in code (Pneumonia, ARDS)
        // only appear once the saved list is refreshed. Replaces any Inspector edits.
        ScenarioPresets.Preset[] defaults = ScenarioPresets.DefaultPresets();
        if (presets.presets == null || presets.presets.Length < defaults.Length)
        {
            presets.presets = defaults;
            EditorUtility.SetDirty(presets);
            Debug.Log($"[BioGearsVRSceneSetup] Refreshed scenario presets to the {defaults.Length} built-in ones.");
        }

        Transform oldBar = FindDeep(canvasRect, PresetBarName);
        if (oldBar != null)
            Object.DestroyImmediate(oldBar.gameObject);

        GameObject bar = new GameObject(PresetBarName, typeof(RectTransform));
        bar.transform.SetParent(canvasRect, false);
        RectTransform barRect = bar.GetComponent<RectTransform>();
        barRect.anchorMin = new Vector2(0.5f, 1f);
        barRect.anchorMax = new Vector2(0.5f, 1f);
        barRect.pivot = new Vector2(0.5f, 1f);
        float barWidth = canvasRect.sizeDelta.x - 80f;

        // Two columns of big buttons (the Scenarios tab has the page to itself); an odd
        // last preset spans the full width.
        int count = presets.presets != null ? presets.presets.Length : 0;
        int gridRows = (count + 1) / 2;
        float halfWidth = (barWidth - PresetButtonGap) * 0.5f;
        barRect.sizeDelta = new Vector2(barWidth, gridRows * PageButtonHeight + Mathf.Max(0, gridRows - 1) * PresetButtonGap);
        Button[] buttons = new Button[count];

        for (int i = 0; i < count; i++)
        {
            GameObject buttonGO = BioGearsIntakeUIBuilder.CreateImage(bar.transform, "Preset_" + presets.presets[i].buttonLabel, presets.idleButtonColor);
            RectTransform br = buttonGO.GetComponent<RectTransform>();
            br.anchorMin = new Vector2(0f, 1f);
            br.anchorMax = new Vector2(0f, 1f);
            br.pivot = new Vector2(0f, 1f);
            bool fullWidth = i == count - 1 && count % 2 == 1;
            br.sizeDelta = new Vector2(fullWidth ? barWidth : halfWidth, PageButtonHeight);
            br.anchoredPosition = new Vector2((i % 2) * (halfWidth + PresetButtonGap), -(i / 2) * (PageButtonHeight + PresetButtonGap));

            Button button = buttonGO.AddComponent<Button>();
            button.targetGraphic = buttonGO.GetComponent<Image>();
            ColorBlock colors = button.colors;
            colors.highlightedColor = new Color(0.75f, 1f, 1f, 1f); // tint on ray hover
            colors.pressedColor = new Color(0.55f, 0.85f, 0.9f, 1f);
            button.colors = colors;

            TMPro.TextMeshProUGUI label = BioGearsIntakeUIBuilder.CreateLabel(
                buttonGO.transform, "Label", presets.presets[i].buttonLabel, PresetFontSize, TMPro.TextAlignmentOptions.Center);
            label.color = presets.idleLabelColor;
            label.raycastTarget = false; // let the ray hit the button's image, not the text
            label.enableAutoSizing = true;
            label.fontSizeMin = 24f;
            label.fontSizeMax = PageButtonFontSize;
            label.textWrappingMode = TMPro.TextWrappingModes.NoWrap;
            label.margin = new Vector4(6f, 0f, 6f, 0f);
            RectTransform lr = label.rectTransform;
            lr.anchorMin = Vector2.zero;
            lr.anchorMax = Vector2.one;
            lr.offsetMin = Vector2.zero;
            lr.offsetMax = Vector2.zero;

            buttons[i] = button;
        }

        TMPro.TextMeshProUGUI readout = null;
        Transform readoutT = FindDeep(canvasRect, ScenarioReadoutName);
        if (readoutT != null)
            readout = readoutT.GetComponent<TMPro.TextMeshProUGUI>();
        if (readout == null)
        {
            readout = BioGearsIntakeUIBuilder.CreateLabel(canvasRect, ScenarioReadoutName, "Scenario: Custom", 34f, TMPro.TextAlignmentOptions.Left);
            readout.color = NeonPrimary;
        }

        SerializedObject so = new SerializedObject(presets);
        so.FindProperty("ui").objectReferenceValue = controller;
        so.FindProperty("scenarioText").objectReferenceValue = readout;
        SerializedProperty buttonsProp = so.FindProperty("presetButtons");
        buttonsProp.arraySize = count;
        for (int i = 0; i < count; i++)
            buttonsProp.GetArrayElementAtIndex(i).objectReferenceValue = buttons[i];
        so.ApplyModifiedProperties();

        Debug.Log($"[BioGearsVRSceneSetup] Built {count} scenario preset buttons.");
    }

    /// <summary>
    /// (Re)builds the viewing toggles - highlight, airflow, tumors, stacked full width
    /// for the View tab - with the colour legend under them, and binds them to VRUIController. Rebuilt
    /// every run; the buttons hold no wiring of their own (VRUIController binds them at
    /// runtime).
    /// </summary>
    static void EnsureHighlightControls(RectTransform canvasRect)
    {
        VRUIController controller = Object.FindFirstObjectByType<VRUIController>(FindObjectsInactive.Include);
        if (controller == null)
        {
            Debug.LogWarning("[BioGearsVRSceneSetup] No VRUIController found - viewing toggles were not added.");
            return;
        }

        // "HighlightToggle" was the single full-width button of an earlier version.
        foreach (string name in new[] { HighlightButtonName, HighlightLegendName, "HighlightToggle" })
        {
            Transform old = FindDeep(canvasRect, name);
            if (old != null)
                Object.DestroyImmediate(old.gameObject);
        }

        float width = canvasRect.sizeDelta.x - 80f;

        GameObject row = new GameObject(HighlightButtonName, typeof(RectTransform));
        row.transform.SetParent(canvasRect, false);
        row.GetComponent<RectTransform>().sizeDelta = new Vector2(width, 3f * PageButtonHeight + 2f * PresetButtonGap);

        (Button highlight, TMPro.TextMeshProUGUI highlightLabel) =
            CreateToggleButton(row.transform, "HighlightRegions", "Highlight affected regions: OFF", 0f, width);
        (Button airflow, TMPro.TextMeshProUGUI airflowLabel) =
            CreateToggleButton(row.transform, "ShowAirflow", "Show airflow: OFF", 0f, width);
        (Button tumors, TMPro.TextMeshProUGUI tumorsLabel) =
            CreateToggleButton(row.transform, "ShowTumors", "Show tumors: none", 0f, width);
        Button[] stacked = { highlight, airflow, tumors };
        for (int i = 0; i < stacked.Length; i++)
        {
            RectTransform br = (RectTransform)stacked[i].transform;
            br.anchorMin = br.anchorMax = new Vector2(0f, 1f);
            br.pivot = new Vector2(0f, 1f);
            br.sizeDelta = new Vector2(width, PageButtonHeight);
            br.anchoredPosition = new Vector2(0f, -i * (PageButtonHeight + PresetButtonGap));
        }

        // Colours match LungTissue.shader's highlight colours and AirflowVisualizer's
        // fresh/exhaled air colours.
        TMPro.TextMeshProUGUI legend = BioGearsIntakeUIBuilder.CreateLabel(
            canvasRect, HighlightLegendName,
            "<color=#FF8A20>Amber</color> = asthma airways     <color=#A070FF>Violet</color> = COPD / smoke damage\n" +
            "<color=#4DFF73>Green</color> = pneumonia / ARDS     <color=#8C5AD9>Dim violet</color> = trapped air (COPD)\n" +
            "<color=#66CCFF>Blue</color> = fresh air (O2-rich)     <color=#FF667A>Pink</color> = exhaled air (CO2-rich)",
            28f, TMPro.TextAlignmentOptions.Center);
        legend.color = NeonPrimary;
        legend.raycastTarget = false;

        SerializedObject so = new SerializedObject(controller);
        so.FindProperty("highlightButton").objectReferenceValue = highlight;
        so.FindProperty("highlightButtonText").objectReferenceValue = highlightLabel;
        so.FindProperty("airflowButton").objectReferenceValue = airflow;
        so.FindProperty("airflowButtonText").objectReferenceValue = airflowLabel;
        so.FindProperty("tumorsButton").objectReferenceValue = tumors;
        so.FindProperty("tumorsButtonText").objectReferenceValue = tumorsLabel;
        so.FindProperty("tumors").objectReferenceValue = Object.FindFirstObjectByType<TumorMarkers>(FindObjectsInactive.Include);
        so.ApplyModifiedProperties();

        Debug.Log("[BioGearsVRSceneSetup] Built the highlight, airflow and tumor toggles.");
    }

    static (Button, TMPro.TextMeshProUGUI) CreateToggleButton(Transform parent, string name, string text, float left, float width)
    {
        GameObject buttonGO = BioGearsIntakeUIBuilder.CreateImage(parent, name, TrackColor);
        RectTransform br = buttonGO.GetComponent<RectTransform>();
        br.anchorMin = br.anchorMax = new Vector2(0f, 0.5f);
        br.pivot = new Vector2(0f, 0.5f);
        br.sizeDelta = new Vector2(width, HighlightButtonHeight);
        br.anchoredPosition = new Vector2(left, 0f);

        Button button = buttonGO.AddComponent<Button>();
        button.targetGraphic = buttonGO.GetComponent<Image>();
        ColorBlock colors = button.colors;
        colors.highlightedColor = new Color(0.75f, 1f, 1f, 1f);
        colors.pressedColor = new Color(0.55f, 0.85f, 0.9f, 1f);
        button.colors = colors;

        TMPro.TextMeshProUGUI label = BioGearsIntakeUIBuilder.CreateLabel(
            buttonGO.transform, "Label", text, PresetFontSize, TMPro.TextAlignmentOptions.Center);
        label.color = NeonPrimary;
        label.raycastTarget = false;
        RectTransform lr = label.rectTransform;
        lr.anchorMin = Vector2.zero;
        lr.anchorMax = Vector2.one;
        lr.offsetMin = Vector2.zero;
        lr.offsetMax = Vector2.zero;

        return (button, label);
    }

    /// <summary>
    /// Adds the PatientLungLoader, which swaps in a real patient's lungs from the
    /// LungVision backend ("Send to VR" in the browser). Harmless without a backend: it
    /// just keeps showing the built-in lung.
    /// </summary>
    static void EnsurePatientLoader(LungMeshDeformer deformer)
    {
        if (deformer == null)
            return;

        PatientLungLoader loader = Object.FindFirstObjectByType<PatientLungLoader>(FindObjectsInactive.Include);
        if (loader == null)
        {
            loader = new GameObject("PatientLungLoader").AddComponent<PatientLungLoader>();
            Debug.Log("[BioGearsVRSceneSetup] Added PatientLungLoader - patients sent from LungVision ('Send to VR') appear in the scene.");
        }
        loader.lung = deformer;
        loader.airflow = Object.FindFirstObjectByType<AirflowVisualizer>(FindObjectsInactive.Include);
        loader.bio = Object.FindFirstObjectByType<BioGearsNativeRespiratory>(FindObjectsInactive.Include);
        EditorUtility.SetDirty(loader);
    }

    /// <summary>
    /// Adds TumorMarkers (the patient's tumors from LungVision, inside the lung) and its
    /// material asset - saved as an asset so TumorMarker.shader ships in builds.
    /// </summary>
    static void EnsureTumorMarkers(LungMeshDeformer deformer)
    {
        if (deformer == null)
            return;
        Shader shader = Shader.Find("LungVision/TumorMarker");
        if (shader == null)
        {
            Debug.LogWarning("[BioGearsVRSceneSetup] TumorMarker shader not found - tumor markers not set up.");
            return;
        }

        const string matPath = "Assets/Settings/TumorMarker.mat";
        Material mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
        if (mat == null)
        {
            mat = new Material(shader);
            AssetDatabase.CreateAsset(mat, matPath);
        }
        mat.shader = shader;
        mat.enableInstancing = true;
        EditorUtility.SetDirty(mat);

        TumorMarkers markers = Object.FindFirstObjectByType<TumorMarkers>(FindObjectsInactive.Include);
        if (markers == null)
        {
            // A root object: labels and the slice panel are world-sized, and the markers
            // themselves are parented to the lung at runtime.
            markers = new GameObject("TumorMarkers").AddComponent<TumorMarkers>();
            Debug.Log("[BioGearsVRSceneSetup] Added TumorMarkers - tumors found by LungVision appear inside the patient's lungs.");
        }
        markers.loader = Object.FindFirstObjectByType<PatientLungLoader>(FindObjectsInactive.Include);
        markers.lung = deformer;
        markers.markerMaterial = mat;
        EditorUtility.SetDirty(markers);
    }

    /// <summary>
    /// Puts BioGearsStreamServer next to the engine component, so a PC running this
    /// scene serves its real BioGears to standalone Quest headsets. On the Quest the same
    /// component stays idle (no native engine there) and the engine component connects
    /// to the PC instead.
    /// </summary>
    static void EnsureStreamServer()
    {
        BioGearsNativeRespiratory bio = Object.FindFirstObjectByType<BioGearsNativeRespiratory>(FindObjectsInactive.Include);
        if (bio == null)
        {
            Debug.LogWarning("[BioGearsVRSceneSetup] No BioGearsNativeRespiratory found - stream server not added.");
            return;
        }

        BioGearsStreamServer server = bio.GetComponent<BioGearsStreamServer>();
        if (server == null)
        {
            server = bio.gameObject.AddComponent<BioGearsStreamServer>();
            Debug.Log("[BioGearsVRSceneSetup] Added BioGearsStreamServer - this PC will serve BioGears to standalone Quest headsets.");
        }
        server.bio = bio;
        EditorUtility.SetDirty(server);
    }

    /// <summary>
    /// Creates (or refreshes) the airflow particle object and its material asset. The
    /// material is saved as an asset and referenced from the scene so the shader ships
    /// in builds - Shader.Find alone would return null in a Quest player.
    /// </summary>
    static void EnsureAirflowVisualizer(LungMeshDeformer deformer)
    {
        if (deformer == null)
            return;

        Shader shader = Shader.Find("BioGears/AirflowParticle");
        if (shader == null)
        {
            Debug.LogWarning("[BioGearsVRSceneSetup] AirflowParticle shader not found - airflow not set up.");
            return;
        }

        const string matPath = "Assets/Settings/AirflowParticle.mat";
        Material mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
        if (mat == null)
        {
            mat = new Material(shader);
            AssetDatabase.CreateAsset(mat, matPath);
        }
        mat.shader = shader;
        EditorUtility.SetDirty(mat);

        AirflowVisualizer airflow = Object.FindFirstObjectByType<AirflowVisualizer>(FindObjectsInactive.Include);
        if (airflow == null)
        {
            // A root object, not a child of the lung: the lung's transform carries a
            // ~1/300 scale from the CT units, which would shrink world-sized particles.
            GameObject go = new GameObject("AirflowVisualizer");
            airflow = go.AddComponent<AirflowVisualizer>();
            Debug.Log("[BioGearsVRSceneSetup] Created AirflowVisualizer.");
        }

        SerializedObject so = new SerializedObject(airflow);
        so.FindProperty("lung").objectReferenceValue = deformer;
        so.FindProperty("bio").objectReferenceValue = Object.FindFirstObjectByType<BioGearsNativeRespiratory>();
        so.FindProperty("particleMaterial").objectReferenceValue = mat;
        // Earlier versions of this component saved other values into the scene, and saved
        // values override new code defaults - so set the current (calm) look explicitly.
        so.FindProperty("parcelCount").intValue = 350;
        so.FindProperty("terminalsPerSide").intValue = 12;
        so.FindProperty("particleSize").floatValue = 0.010f;
        so.FindProperty("asthmaTurbulence").floatValue = 0.02f;
        so.FindProperty("streaks").boolValue = false;
        so.FindProperty("swirlTurns").floatValue = 0f;
        so.FindProperty("tubeSpread").floatValue = 0.5f;
        so.FindProperty("treeIdleBrightness").floatValue = 0.10f;
        so.FindProperty("treeFlowBrightness").floatValue = 0.12f;
        so.FindProperty("treeWidth").floatValue = 0.5f;
        so.FindProperty("bandsPerPath").floatValue = 0f;
        so.FindProperty("alveoliPerTerminal").intValue = 1;
        so.FindProperty("alveolusSize").floatValue = 0.028f;
        so.FindProperty("alveolusOpacity").floatValue = 0.35f;
        so.FindProperty("showOxygenSparks").boolValue = false;
        so.ApplyModifiedProperties();

        // Point the particle renderer at the material in edit mode too, so the scene
        // never saves with Unity's default particle material.
        ParticleSystemRenderer r = airflow.GetComponent<ParticleSystemRenderer>();
        if (r != null)
            r.sharedMaterial = mat;

        AssetDatabase.SaveAssets();
    }

    /// <summary>
    /// Dark chassis with a cyan neon rim, neon labels and neon slider fills. Purely a
    /// recolour plus four thin border strips - no custom shaders, no extra passes, so
    /// it costs nothing measurable on Quest.
    /// </summary>
    static void ApplyNeonTheme(RectTransform canvasRect)
    {
        // Chassis
        Transform bg = FindDeep(canvasRect, "PanelBackground");
        if (bg != null)
        {
            Image bgImage = bg.GetComponent<Image>();
            if (bgImage != null)
                bgImage.color = PanelChassis;
        }

        BuildNeonBorder(canvasRect);

        // Title
        Transform title = FindDeep(canvasRect, "Title");
        if (title != null)
        {
            TMPro.TextMeshProUGUI t = title.GetComponent<TMPro.TextMeshProUGUI>();
            if (t != null)
            {
                t.color = NeonPrimary;
                t.characterSpacing = 6f;
            }
        }

        // Rows: neon labels, dark tracks, neon fill and handle.
        foreach (string rowName in RowOrder)
        {
            Transform row = FindDeep(canvasRect, rowName);
            if (row == null)
                continue;

            TMPro.TextMeshProUGUI label = row.GetComponentInChildren<TMPro.TextMeshProUGUI>(true);
            if (label != null)
                label.color = NeonPrimary;

            Slider slider = row.GetComponentInChildren<Slider>(true);
            if (slider == null)
                continue;

            Transform sliderT = slider.transform;

            Transform track = sliderT.Find("Background");
            if (track != null)
            {
                Image trackImage = track.GetComponent<Image>();
                if (trackImage != null)
                    trackImage.color = TrackColor;
            }

            if (slider.fillRect != null)
            {
                Image fill = slider.fillRect.GetComponent<Image>();
                if (fill != null)
                    fill.color = NeonPrimary;
            }

            if (slider.handleRect != null)
            {
                Image handle = slider.handleRect.GetComponent<Image>();
                if (handle != null)
                    handle.color = Color.white;
            }
        }

        Debug.Log("[BioGearsVRSceneSetup] Applied neon console theme.");
    }

    /// <summary>Four thin strips forming a glowing rim around the console.</summary>
    static void BuildNeonBorder(RectTransform canvasRect)
    {
        const string holderName = "NeonBorder";
        Transform old = FindDeep(canvasRect, holderName);
        if (old != null)
            Object.DestroyImmediate(old.gameObject);

        GameObject holder = new GameObject(holderName, typeof(RectTransform));
        holder.transform.SetParent(canvasRect, false);
        RectTransform holderRect = holder.GetComponent<RectTransform>();
        holderRect.anchorMin = Vector2.zero;
        holderRect.anchorMax = Vector2.one;
        holderRect.offsetMin = Vector2.zero;
        holderRect.offsetMax = Vector2.zero;
        // Must draw ON TOP of the chassis (uGUI paints later siblings last), otherwise
        // PanelBackground covers the rim entirely. Every image in here has
        // raycastTarget disabled, so sitting on top costs nothing in interaction - a
        // controller ray still passes straight through to the sliders underneath.
        holderRect.SetAsLastSibling();

        // Dim outer strips first (the halo), then the bright core on top of them.
        // Deliberately edge strips rather than one full-rect glow: a full-rect overlay
        // would tint the whole console cyan instead of haloing its border.
        AddEdge(holderRect, "TopGlow", new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, 14f), Vector2.zero, NeonDim);
        AddEdge(holderRect, "BottomGlow", new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, 14f), Vector2.zero, NeonDim);
        AddEdge(holderRect, "LeftGlow", new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(14f, 0f), Vector2.zero, NeonDim);
        AddEdge(holderRect, "RightGlow", new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(14f, 0f), Vector2.zero, NeonDim);

        AddEdge(holderRect, "Top", new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, 5f), Vector2.zero, NeonPrimary);
        AddEdge(holderRect, "Bottom", new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, 5f), Vector2.zero, NeonPrimary);
        AddEdge(holderRect, "Left", new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(5f, 0f), Vector2.zero, NeonPrimary);
        AddEdge(holderRect, "Right", new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(5f, 0f), Vector2.zero, NeonPrimary);
    }

    static void AddEdge(RectTransform parent, string name, Vector2 anchorMin, Vector2 anchorMax, Vector2 size, Vector2 offset, Color color)
    {
        GameObject edge = BioGearsIntakeUIBuilder.CreateImage(parent, name, color);
        RectTransform rect = edge.GetComponent<RectTransform>();
        rect.anchorMin = anchorMin;
        rect.anchorMax = anchorMax;
        rect.sizeDelta = size;
        rect.anchoredPosition = offset;
        edge.GetComponent<Image>().raycastTarget = false;
    }

    /// <summary>
    /// Replaces the default blue sky with a black -> dark grey vertical gradient.
    /// Uses a two-colour skybox shader rather than a solid clear colour so the
    /// environment still has depth, and costs effectively nothing on Quest.
    /// </summary>
    static void ApplyGradientBackground(Camera cam)
    {
        Shader shader = Shader.Find("BioGears/GradientSkybox");
        if (shader == null)
        {
            Debug.LogWarning("[BioGearsVRSceneSetup] GradientSkybox shader not found - background unchanged.");
            return;
        }

        const string matPath = "Assets/Settings/GradientSkybox.mat";
        Material mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
        if (mat == null)
        {
            mat = new Material(shader);
            AssetDatabase.CreateAsset(mat, matPath);
        }
        mat.shader = shader;
        mat.SetColor("_TopColor", new Color(0.16f, 0.17f, 0.19f, 1f));
        mat.SetColor("_BottomColor", new Color(0.015f, 0.015f, 0.02f, 1f));
        mat.SetFloat("_Exponent", 1.3f);
        EditorUtility.SetDirty(mat);

        RenderSettings.skybox = mat;
        // Ambient light follows the skybox, so a dark environment stays dark instead of
        // being washed out by the old blue ambient.
        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
        // Lifted from near-black. The previous values left the lung lit almost entirely
        // by the single directional light, so one side blew out to white while the other
        // crushed to black - which reads as "bleached plastic" no matter how good the
        // albedo is. This only affects ambient fill; the skybox colours above (and so
        // the dark background) are unchanged.
        // Enough fill to keep concavities out of pure black, but well short of the
        // level that was washing the tissue toward white. Still ~4x the original 0.02
        // ground term, so the dark regions do not come back.
        // Warm-tinted rather than neutral grey. Grey ambient has B == R, so it dilutes
        // the tissue's warmth everywhere it reaches, including the shadows.
        RenderSettings.ambientSkyColor = new Color(0.20f, 0.175f, 0.165f);
        RenderSettings.ambientEquatorColor = new Color(0.14f, 0.120f, 0.113f);
        RenderSettings.ambientGroundColor = new Color(0.07f, 0.058f, 0.054f);

        if (cam != null)
            cam.clearFlags = CameraClearFlags.Skybox;

        AssetDatabase.SaveAssets();
        Debug.Log("[BioGearsVRSceneSetup] Applied black -> dark grey gradient skybox.");
    }

    /// <summary>
    /// Re-spaces the existing rows in place and trims the panel to fit them. This
    /// deliberately moves the rows that are already in the scene rather than rebuilding
    /// the canvas, so every slider reference, listener and VRUIController wiring that
    /// currently works is left untouched.
    ///
    /// The old layout left a ~140px dead band under the title and emitted rows
    /// bottom-up, which scattered SpO2 / Asthma / Smoking down the panel with a large
    /// gap before Smoking.
    /// </summary>
    // ---- Tabbed console -----------------------------------------------------------------
    // Every control in one tall column made a panel nearly 2 m high: the user had to
    // tilt their head up and down to use it, and saw 9 sliders, 7 presets, 3 toggles and
    // a legend at once. Now: a fixed header that always says what is on show, a row of
    // tabs, and one page of 3-4 controls at a time. Rows are moved, never rebuilt, so
    // every slider keeps its wiring.
    const string TabBarName = "ConsoleTabs";
    const string PagesName = "ConsolePages";
    const string PatientReadoutName = "PatientReadout";
    const float HeaderTitleTop = 22f;
    const float HeaderTitleSize = 54f;
    const float StatusTop = 100f;
    const float StatusLineHeight = 44f;
    const float TabTop = 196f;
    const float TabHeight = 78f;
    const float PageTop = TabTop + TabHeight + 26f;
    const float PageButtonHeight = 84f;
    const float PageButtonFontSize = 38f;
    const float PageItemGap = 12f;

    // Tab name -> what is on that page, top to bottom.
    static readonly (string tab, string[] items)[] ConsolePages =
    {
        ("Scenarios",  new[] { PresetBarName }),
        ("Breathing",  new[] { "RespiratoryRateRow", "TidalVolumeRow", "SpO2Row", "ExerciseRow" }),
        ("Conditions", new[] { "AsthmaRow", "PneumoniaRow", "ArdsRow" }),
        ("Smoking",    new[] { "SmokingYearsRow", "CigarettesPerDayRow", "PackYearsReadout", "DamageStageReadout" }),
        ("View",       new[] { HighlightButtonName, HighlightLegendName }),
    };

    static void BuildTabbedLayout(RectTransform canvasRect)
    {
        float inner = canvasRect.sizeDelta.x - 80f;

        // Header: title, then the scenario and the lungs on show - visible on every tab.
        Transform titleT = FindDeep(canvasRect, "Title");
        if (titleT != null && titleT.TryGetComponent(out TMPro.TextMeshProUGUI title))
        {
            title.text = "PATIENT CONTROLS";
            title.fontSize = HeaderTitleSize;
            title.alignment = TMPro.TextAlignmentOptions.Center;
            PlaceTopAnchored(title.rectTransform, inner, HeaderTitleSize + 16f, HeaderTitleTop);
        }
        if (FindDeep(canvasRect, ScenarioReadoutName) is RectTransform scenario)
        {
            scenario.SetParent(canvasRect, false);
            PlaceTopAnchored(scenario, inner, StatusLineHeight, StatusTop);
            if (scenario.TryGetComponent(out TMPro.TextMeshProUGUI st))
            {
                st.fontSize = 34f;
                st.alignment = TMPro.TextAlignmentOptions.Center;
            }
        }
        TMPro.TextMeshProUGUI patientLine = FindDeep(canvasRect, PatientReadoutName)?.GetComponent<TMPro.TextMeshProUGUI>();
        if (patientLine == null)
            patientLine = BioGearsIntakeUIBuilder.CreateLabel(canvasRect, PatientReadoutName, "Lungs: Built-in lung model", 28f, TMPro.TextAlignmentOptions.Center);
        patientLine.color = new Color(NeonPrimary.r, NeonPrimary.g, NeonPrimary.b, 0.7f);
        patientLine.raycastTarget = false;
        PlaceTopAnchored(patientLine.rectTransform, inner, StatusLineHeight - 6f, StatusTop + StatusLineHeight);

        VRUIController controller = Object.FindFirstObjectByType<VRUIController>(FindObjectsInactive.Include);
        if (controller != null)
        {
            ConsoleStatus status = controller.GetComponent<ConsoleStatus>();
            if (status == null)
                status = controller.gameObject.AddComponent<ConsoleStatus>();
            status.text = patientLine;
            status.loader = Object.FindFirstObjectByType<PatientLungLoader>(FindObjectsInactive.Include);
            status.tumors = Object.FindFirstObjectByType<TumorMarkers>(FindObjectsInactive.Include);
            EditorUtility.SetDirty(status);
        }

        // Pages (kept between runs - they hold the rows), then the tab bar (rebuilt).
        RectTransform pagesRoot = FindDeep(canvasRect, PagesName) as RectTransform;
        if (pagesRoot == null)
        {
            pagesRoot = new GameObject(PagesName, typeof(RectTransform)).GetComponent<RectTransform>();
            pagesRoot.SetParent(canvasRect, false);
        }

        var pages = new GameObject[ConsolePages.Length];
        float tallest = 0f;
        for (int p = 0; p < ConsolePages.Length; p++)
        {
            string pageName = "Page_" + ConsolePages[p].tab;
            RectTransform page = pagesRoot.Find(pageName) as RectTransform;
            if (page == null)
            {
                page = new GameObject(pageName, typeof(RectTransform)).GetComponent<RectTransform>();
                page.SetParent(pagesRoot, false);
            }
            page.gameObject.SetActive(true);  // PanelTabs hides the others at runtime

            float y = 0f;
            foreach (string itemName in ConsolePages[p].items)
            {
                if (!(FindDeep(canvasRect, itemName) is RectTransform item))
                    continue;
                item.SetParent(page, false);
                // Labels come out of CreateLabel at TextMeshPro's default 50-unit height,
                // so text items get theirs here; built groups (presets, toggles) carry their own.
                float h = System.Array.IndexOf(RowOrder, itemName) >= 0 ? StyleRow(item)
                        : System.Array.IndexOf(ReadoutOrder, itemName) >= 0 ? ReadoutHeight
                        : itemName == HighlightLegendName ? HighlightLegendHeight
                        : item.sizeDelta.y;
                PlaceTopAnchored(item, inner, h, y);
                y += h + PageItemGap;
            }
            PlaceTopAnchored(page, inner, y, 0f);
            pages[p] = page.gameObject;
            tallest = Mathf.Max(tallest, y);
        }
        PlaceTopAnchored(pagesRoot, inner, tallest, PageTop);

        Transform oldTabs = FindDeep(canvasRect, TabBarName);
        if (oldTabs != null)
            Object.DestroyImmediate(oldTabs.gameObject);
        GameObject tabBar = new GameObject(TabBarName, typeof(RectTransform));
        tabBar.transform.SetParent(canvasRect, false);
        PlaceTopAnchored((RectTransform)tabBar.transform, inner, TabHeight, TabTop);
        float tabWidth = (inner - PresetButtonGap * (ConsolePages.Length - 1)) / ConsolePages.Length;
        var tabs = new Button[ConsolePages.Length];
        for (int p = 0; p < ConsolePages.Length; p++)
        {
            (Button tab, TMPro.TextMeshProUGUI label) =
                CreateToggleButton(tabBar.transform, "Tab_" + ConsolePages[p].tab, ConsolePages[p].tab, p * (tabWidth + PresetButtonGap), tabWidth);
            ((RectTransform)tab.transform).sizeDelta = new Vector2(tabWidth, TabHeight);
            label.enableAutoSizing = true;
            label.fontSizeMin = 22f;
            label.fontSizeMax = 32f;
            label.textWrappingMode = TMPro.TextWrappingModes.NoWrap;
            tabs[p] = tab;
        }

        PanelTabs panelTabs = canvasRect.GetComponent<PanelTabs>();
        if (panelTabs == null)
            panelTabs = canvasRect.gameObject.AddComponent<PanelTabs>();
        panelTabs.tabs = tabs;
        panelTabs.pages = pages;
        panelTabs.startPage = 0;
        EditorUtility.SetDirty(panelTabs);

        canvasRect.sizeDelta = new Vector2(canvasRect.sizeDelta.x, PageTop + tallest + BottomPadding);
        Debug.Log($"[BioGearsVRSceneSetup] Console rebuilt as {ConsolePages.Length} tabs (tallest page {tallest:F0}, panel {canvasRect.sizeDelta.y:F0} units tall).");
    }

    // Big label over a big slider, in place - the slider's wiring stays untouched.
    static float StyleRow(RectTransform rowRect)
    {
        TMPro.TextMeshProUGUI label = rowRect.GetComponentInChildren<TMPro.TextMeshProUGUI>(true);
        if (label != null)
        {
            label.fontSize = LabelFontSize;
            RectTransform labelRect = label.rectTransform;
            labelRect.anchorMin = new Vector2(0f, 1f);
            labelRect.anchorMax = new Vector2(1f, 1f);
            labelRect.pivot = new Vector2(0.5f, 1f);
            labelRect.sizeDelta = new Vector2(0f, LabelHeight);
            labelRect.anchoredPosition = Vector2.zero;
        }

        Slider slider = rowRect.GetComponentInChildren<Slider>(true);
        if (slider != null)
        {
            RectTransform sliderRect = slider.GetComponent<RectTransform>();
            sliderRect.anchorMin = new Vector2(0f, 1f);
            sliderRect.anchorMax = new Vector2(1f, 1f);
            sliderRect.pivot = new Vector2(0.5f, 1f);
            sliderRect.sizeDelta = new Vector2(0f, SliderHeight);
            sliderRect.anchoredPosition = new Vector2(0f, -(LabelHeight + LabelToSliderGap));
        }
        return RowHeight;
    }

    /// <summary>A child anywhere below <paramref name="root"/> (rows sit inside tab pages).</summary>
    static Transform FindDeep(Transform root, string name)
    {
        foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
            if (t != root && t.name == name)
                return t;
        return null;
    }

    static void PlaceTopAnchored(RectTransform rect, float width, float height, float top)
    {
        rect.anchorMin = new Vector2(0.5f, 1f);
        rect.anchorMax = new Vector2(0.5f, 1f);
        rect.pivot = new Vector2(0.5f, 1f);
        rect.sizeDelta = new Vector2(width, height);
        rect.anchoredPosition = new Vector2(0f, -top);
    }

    /// <summary>
    /// World-space bounds of every renderer under <paramref name="root"/>. The lung
    /// FBX's pivot sits far from the mesh, so placement has to work off rendered
    /// bounds rather than transform.position.
    /// </summary>
    static bool TryGetWorldBounds(Transform root, out Bounds bounds)
    {
        bounds = default;
        Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
        bool found = false;

        foreach (Renderer r in renderers)
        {
            // The soot overlay is a duplicate shell; never let it skew the bounds.
            if (r == null || r.gameObject.name == "LungSootOverlay")
                continue;

            if (!found) { bounds = r.bounds; found = true; }
            else bounds.Encapsulate(r.bounds);
        }

        return found;
    }
}
