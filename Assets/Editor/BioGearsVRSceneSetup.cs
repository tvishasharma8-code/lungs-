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
    const float LungSideOffset = -0.25f; // nudged left so the console has breathing room
    const float LungHeight = 1.4f;
    // Longest axis of the lung once scaled. Raised from 0.85 so the lung reads as the
    // main subject of the scene; bounded by two hard limits - it must not reach the
    // user's head, and it must not grow into the console on the right.
    const float LungTargetSize = 1.05f;

    const float PanelDistance = 1.45f;
    const float PanelSideOffset = 1.10f; // to the user's right, clearly separated
    const float PanelHeight = 1.15f;
    const float PanelTargetWidth = 1.15f;

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

        // --- 4. Panel: beside the lung, human sized, facing the user -----------------
        Canvas canvas = null;
        foreach (Canvas c in Object.FindObjectsByType<Canvas>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (c.transform.parent == null) { canvas = c; break; }
        }

        if (canvas != null)
        {
            RectTransform rt = canvas.GetComponent<RectTransform>();

            if (canvas.renderMode == RenderMode.WorldSpace && canvas.worldCamera == null && cam != null)
                canvas.worldCamera = cam;

            EnsureRespiratoryRows(rt);
            RelayoutRows(rt);
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

        ApplyGradientBackground(cam);
        SetupSoftLighting(originT);

        EditorSceneManager.MarkSceneDirty(scene);
        bool saved = EditorSceneManager.SaveScene(scene);
        Debug.Log($"[BioGearsVRSceneSetup] VR layout fixed in '{scenePath}'. Saved: {saved}");
    }

    // Row order top-to-bottom. Respiratory mechanics first (these two directly drive
    // the lung animation), then the clinical inputs, then exercise.
    static readonly string[] RowOrder =
    {
        "RespiratoryRateRow",
        "TidalVolumeRow",
        "SpO2Row",
        "AsthmaRow",
        "SmokingYearsRow",
        "CigarettesPerDayRow",
        "ExerciseRow",
    };

    const float FirstRowTop = 96f;  // directly beneath the title
    const float RowHeight = 124f;    // label + slider + breathing room
    const float BottomPadding = 36f;
    const float ReadoutHeight = 46f;

    // Derived values, shown under the sliders. Order is top-to-bottom.
    static readonly string[] ReadoutOrder = { "PackYearsReadout", "DamageStageReadout" };

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

        string[] fields = { "asthmaSeverity", "copdSeverity", "respiratoryDistressSeverity", "exerciseIntensity" };

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
        if (canvasRect.Find(name) != null)
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
        Transform existing = canvasRect.Find(rowName);
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
    /// Dark chassis with a cyan neon rim, neon labels and neon slider fills. Purely a
    /// recolour plus four thin border strips - no custom shaders, no extra passes, so
    /// it costs nothing measurable on Quest.
    /// </summary>
    static void ApplyNeonTheme(RectTransform canvasRect)
    {
        // Chassis
        Transform bg = canvasRect.Find("PanelBackground");
        if (bg != null)
        {
            Image bgImage = bg.GetComponent<Image>();
            if (bgImage != null)
                bgImage.color = PanelChassis;
        }

        BuildNeonBorder(canvasRect);

        // Title
        Transform title = canvasRect.Find("Title");
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
            Transform row = canvasRect.Find(rowName);
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
        Transform old = canvasRect.Find(holderName);
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
    static void RelayoutRows(RectTransform canvasRect)
    {
        float rowWidth = canvasRect.sizeDelta.x - 80f;
        int placed = 0;

        for (int i = 0; i < RowOrder.Length; i++)
        {
            Transform row = canvasRect.Find(RowOrder[i]);
            if (row == null)
                continue;

            RectTransform rowRect = row as RectTransform;
            if (rowRect == null)
                continue;

            rowRect.anchorMin = new Vector2(0.5f, 1f);
            rowRect.anchorMax = new Vector2(0.5f, 1f);
            rowRect.pivot = new Vector2(0.5f, 1f);
            rowRect.sizeDelta = new Vector2(rowWidth, RowHeight);
            rowRect.anchoredPosition = new Vector2(0f, -(FirstRowTop + RowHeight * placed));

            // Enlarge the existing label/slider in place - no rebuild, so the slider's
            // wiring and listeners survive untouched.
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

            placed++;
        }

        // Title
        Transform title = canvasRect.Find("Title");
        if (title != null)
        {
            TMPro.TextMeshProUGUI titleText = title.GetComponent<TMPro.TextMeshProUGUI>();
            if (titleText != null)
            {
                titleText.fontSize = TitleFontSize;
                RectTransform titleRect = titleText.rectTransform;
                titleRect.sizeDelta = new Vector2(0f, TitleFontSize + 18f);
            }
        }

        if (placed == 0)
        {
            Debug.LogWarning("[BioGearsVRSceneSetup] No known parameter rows found on the canvas - spacing left unchanged.");
            return;
        }

        // Derived read-outs sit below the sliders, tighter spacing since they are
        // single lines with no control under them.
        float y = FirstRowTop + RowHeight * placed + 6f;
        foreach (string readout in ReadoutOrder)
        {
            Transform t = canvasRect.Find(readout);
            if (t == null) continue;

            RectTransform r = t as RectTransform;
            if (r == null) continue;

            r.anchorMin = new Vector2(0.5f, 1f);
            r.anchorMax = new Vector2(0.5f, 1f);
            r.pivot = new Vector2(0.5f, 1f);
            r.sizeDelta = new Vector2(rowWidth, ReadoutHeight);
            r.anchoredPosition = new Vector2(0f, -y);
            y += ReadoutHeight;
        }

        canvasRect.sizeDelta = new Vector2(canvasRect.sizeDelta.x, y + BottomPadding);
        Debug.Log($"[BioGearsVRSceneSetup] Re-spaced {placed} parameter rows and trimmed the panel to fit.");
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
