using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.UI;
using TMPro;

/// <summary>
/// Tears out whatever Canvas/UI currently exists in SampleScene and builds a fresh
/// "Patient Intake" panel wired directly to BioGearsNativeRespiratory: smoking years,
/// asthma severity, exercise intensity, and a measured SpO2 override. Rebuilt from
/// scratch per request, but keeps the two pieces that make it actually work in VR
/// (TrackedDeviceGraphicRaycaster on the canvas, the existing EventSystem's
/// XRUIInputModule) - those aren't "the UI", they're what makes a world-space canvas
/// clickable with an XR controller at all; a plain Canvas would render fine and simply
/// never receive input.
/// </summary>
public static class BioGearsIntakeUIBuilder
{
    [MenuItem("BioGears/Rebuild Patient Intake UI")]
    public static void Run()
    {
        // Opening and saving a scene is illegal during Play mode - calling this while
        // playing throws InvalidOperationException before anything happens, which looks
        // exactly like "the menu item did nothing". Leave Play mode and pick up again
        // once the editor is back in edit mode (that transition lands a frame later).
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            Debug.Log("[BioGearsIntakeUIBuilder] Play mode is active - exiting Play mode, then rebuilding automatically.");
            EditorApplication.playModeStateChanged += ResumeAfterPlayMode;
            EditorApplication.isPlaying = false;
            return;
        }

        Build();
    }

    static void ResumeAfterPlayMode(PlayModeStateChange state)
    {
        if (state != PlayModeStateChange.EnteredEditMode)
            return;

        EditorApplication.playModeStateChanged -= ResumeAfterPlayMode;
        EditorApplication.delayCall += Build;
    }

    static void Build()
    {
        string scenePath = "Assets/Scenes/SampleScene.unity";
        Scene scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);

        // Remove every VRUIController from prior runs of this tool - without this,
        // re-running the builder piles up duplicates that point at sliders destroyed
        // by the current run's canvas rebuild, each logging "missing required
        // references" and disabling itself, which can mask whether the live one works.
        // Only delete the GameObject when it exists purely to host this component;
        // otherwise strip the component and leave the rest of the object alone.
        foreach (VRUIController old in Object.FindObjectsByType<VRUIController>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (old == null)
                continue;

            GameObject owner = old.gameObject;
            Object.DestroyImmediate(old);

            if (owner.GetComponents<Component>().Length <= 1 && owner.transform.childCount == 0)
                Object.DestroyImmediate(owner);
        }

        // Preserve where the old canvas was placed in the world, if one exists.
        Vector3 pos = new Vector3(-0.3f, 0.74f, 0f);
        Quaternion rot = Quaternion.identity;
        Vector3 scale = Vector3.one * 0.5f;
        Vector2 sizeDelta = new Vector2(1081.5f, 74f + 96f * 4f + 30f);

        // There can be more than one Canvas component in the hierarchy (e.g. a child
        // text/label object with its own Canvas for sort-order overrides) - destroying
        // a parent cascades onto its children, so any of those already-nested canvases
        // will be null by the time we reach them in this loop.
        Canvas[] existingCanvases = Object.FindObjectsByType<Canvas>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        foreach (Canvas c in existingCanvases)
        {
            if (c == null) continue;

            RectTransform rt = c.GetComponent<RectTransform>();
            if (c.transform.parent == null)
            {
                pos = rt.localPosition;
                rot = rt.localRotation;
                scale = rt.localScale;
                // Height is derived from the content (title + 4 rows + padding) rather
                // than inherited, so the panel has no unused space at the bottom.
                sizeDelta = new Vector2(rt.sizeDelta.x, 74f + 96f * 4f + 30f);
            }
            Object.DestroyImmediate(c.gameObject);
        }

        EventSystem eventSystem = Object.FindFirstObjectByType<EventSystem>();
        if (eventSystem == null)
        {
            GameObject esGO = new GameObject("EventSystem");
            eventSystem = esGO.AddComponent<EventSystem>();
            esGO.AddComponent<XRUIInputModule>();
            Debug.Log("[BioGearsIntakeUIBuilder] No EventSystem found, created one with XRUIInputModule.");
        }

        // ---- Canvas ----
        GameObject canvasGO = new GameObject("PatientIntakeCanvas", typeof(RectTransform));
        RectTransform canvasRT = canvasGO.GetComponent<RectTransform>();
        canvasRT.localPosition = pos;
        canvasRT.localRotation = rot;
        canvasRT.localScale = scale;
        canvasRT.sizeDelta = sizeDelta;

        Canvas canvas = canvasGO.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        // Required for a World Space canvas: GraphicRaycaster needs a camera to convert
        // a screen point (mouse position, or an XR controller ray) into world space
        // against this canvas's plane. Without it, nothing on the canvas is ever
        // clickable - mouse or VR controller alike.
        Camera mainCamera = Camera.main;
        if (mainCamera == null)
            mainCamera = Object.FindFirstObjectByType<Camera>();
        canvas.worldCamera = mainCamera;
        if (mainCamera == null)
            Debug.LogWarning("[BioGearsIntakeUIBuilder] No camera found in the scene to assign as the canvas's worldCamera - sliders will not be clickable until one is assigned manually.");

        CanvasScaler scaler = canvasGO.AddComponent<CanvasScaler>();
        scaler.dynamicPixelsPerUnit = 10f;

        GraphicRaycaster raycaster = canvasGO.AddComponent<GraphicRaycaster>();
        // uGUI refuses to raycast a graphic that is facing away from the event camera.
        // UI shaders don't backface-cull, so a back-facing canvas still renders (with
        // mirrored text) while silently swallowing every click. The orientation below
        // should make this moot, but a viewer can always walk behind a world-space
        // panel in VR - so don't let "behind" mean "dead".
        raycaster.ignoreReversedGraphics = false;

        // The component that actually lets an XR controller ray hit uGUI elements on a
        // world-space canvas - without this, sliders render but never receive clicks.
        canvasGO.AddComponent<TrackedDeviceGraphicRaycaster>();

        // Face the camera. A canvas's readable side looks along -Z, so pointing its
        // +Z away from the camera turns the front toward the viewer. Without this the
        // panel inherits whatever rotation the old canvas had, which in this scene had
        // it turned ~180 degrees away from the camera.
        if (mainCamera != null)
        {
            Vector3 awayFromCamera = canvasRT.position - mainCamera.transform.position;
            if (awayFromCamera.sqrMagnitude > 0.0001f)
                canvasRT.rotation = Quaternion.LookRotation(awayFromCamera.normalized, Vector3.up);
        }

        GameObject panelBG = CreateImage(canvasGO.transform, "PanelBackground", new Color(0.06f, 0.06f, 0.08f, 0.92f));
        RectTransform panelRT = panelBG.GetComponent<RectTransform>();
        panelRT.anchorMin = Vector2.zero;
        panelRT.anchorMax = Vector2.one;
        panelRT.offsetMin = Vector2.zero;
        panelRT.offsetMax = Vector2.zero;

        TextMeshProUGUI title = CreateLabel(canvasGO.transform, "Title", "Patient Intake", 28, TextAlignmentOptions.Center);
        RectTransform titleRT = title.GetComponent<RectTransform>();
        titleRT.anchorMin = new Vector2(0f, 1f);
        titleRT.anchorMax = new Vector2(1f, 1f);
        titleRT.pivot = new Vector2(0.5f, 1f);
        titleRT.sizeDelta = new Vector2(0f, 50f);
        titleRT.anchoredPosition = new Vector2(0f, -10f);

        // ---- Rows: label+value on top, slider below, stacked down the panel ----
        // yPos is measured downward from the top edge, so rows are listed in the order
        // they appear. Previously the first row started 200px below the title (a large
        // dead band) and the rows were emitted bottom-up, which scattered the related
        // controls; this packs them directly under the heading in reading order.
        float firstRowTop = 74f;
        float rowHeight = 96f;
        float rowWidth = sizeDelta.x - 80f;

        var spo2Row = CreateRow(canvasGO.transform, "SpO2", "SpO2: 100%", firstRowTop, rowHeight, rowWidth);
        Slider spo2Slider = spo2Row.slider;

        var asthmaRow = CreateRow(canvasGO.transform, "Asthma", "Asthma severity: 0%", firstRowTop + rowHeight, rowHeight, rowWidth);
        Slider asthmaSlider = asthmaRow.slider;

        var smokingRow = CreateRow(canvasGO.transform, "SmokingYears", "Smoking: 0.0 years", firstRowTop + rowHeight * 2f, rowHeight, rowWidth);
        Slider smokingSlider = smokingRow.slider;

        var exerciseRow = CreateRow(canvasGO.transform, "Exercise", "Exercise intensity: 0%", firstRowTop + rowHeight * 3f, rowHeight, rowWidth);
        Slider exerciseSlider = exerciseRow.slider;

        // ---- Controller wiring ----
        BioGearsNativeRespiratory bio = Object.FindFirstObjectByType<BioGearsNativeRespiratory>();
        LungMeshDeformer lungVisual = Object.FindFirstObjectByType<LungMeshDeformer>();

        GameObject controllerGO = new GameObject("VRUIController");
        VRUIController controller = controllerGO.AddComponent<VRUIController>();

        var so = new SerializedObject(controller);
        so.FindProperty("bio").objectReferenceValue = bio;
        so.FindProperty("lungVisual").objectReferenceValue = lungVisual;
        so.FindProperty("asthmaSlider").objectReferenceValue = asthmaSlider;
        so.FindProperty("exerciseSlider").objectReferenceValue = exerciseSlider;
        so.FindProperty("smokingYearsSlider").objectReferenceValue = smokingSlider;
        so.FindProperty("smokingYearsText").objectReferenceValue = smokingRow.label;
        so.FindProperty("spo2Slider").objectReferenceValue = spo2Slider;
        so.FindProperty("spo2Text").objectReferenceValue = spo2Row.label;
        so.ApplyModifiedProperties();

        smokingSlider.minValue = 0f;
        smokingSlider.maxValue = 40f;
        asthmaSlider.minValue = 0f;
        asthmaSlider.maxValue = 1f;
        exerciseSlider.minValue = 0f;
        exerciseSlider.maxValue = 1f;
        spo2Slider.minValue = 70f;
        spo2Slider.maxValue = 100f;
        spo2Slider.value = 100f;

        if (bio == null)
            Debug.LogWarning("[BioGearsIntakeUIBuilder] No BioGearsNativeRespiratory found in the scene - VRUIController.bio left unassigned.");
        if (lungVisual == null)
            Debug.LogWarning("[BioGearsIntakeUIBuilder] No LungMeshDeformer found in the scene - VRUIController.lungVisual left unassigned.");

        EditorSceneManager.MarkSceneDirty(scene);
        bool saved = EditorSceneManager.SaveScene(scene);
        Debug.Log($"[BioGearsIntakeUIBuilder] Rebuilt patient intake UI in '{scenePath}'. Saved: {saved}");
    }

    public struct Row
    {
        public Slider slider;
        public TextMeshProUGUI label;
    }

    public static Row CreateRow(Transform parent, string name, string initialLabel, float yPos, float height, float width)
    {
        GameObject rowGO = new GameObject(name + "Row", typeof(RectTransform));
        rowGO.transform.SetParent(parent, false);
        RectTransform rowRT = rowGO.GetComponent<RectTransform>();
        rowRT.anchorMin = new Vector2(0.5f, 1f);
        rowRT.anchorMax = new Vector2(0.5f, 1f);
        rowRT.pivot = new Vector2(0.5f, 1f);
        rowRT.sizeDelta = new Vector2(width, height);
        rowRT.anchoredPosition = new Vector2(0f, -yPos);

        TextMeshProUGUI label = CreateLabel(rowGO.transform, name + "Label", initialLabel, 22, TextAlignmentOptions.Left);
        RectTransform labelRT = label.GetComponent<RectTransform>();
        labelRT.anchorMin = new Vector2(0f, 1f);
        labelRT.anchorMax = new Vector2(1f, 1f);
        labelRT.pivot = new Vector2(0.5f, 1f);
        labelRT.sizeDelta = new Vector2(0f, 36f);
        labelRT.anchoredPosition = Vector2.zero;

        Slider slider = CreateSlider(rowGO.transform, name + "Slider");
        RectTransform sliderRT = slider.GetComponent<RectTransform>();
        sliderRT.anchorMin = new Vector2(0f, 1f);
        sliderRT.anchorMax = new Vector2(1f, 1f);
        sliderRT.pivot = new Vector2(0.5f, 1f);
        sliderRT.sizeDelta = new Vector2(0f, 40f);
        sliderRT.anchoredPosition = new Vector2(0f, -46f);

        return new Row { slider = slider, label = label };
    }

    public static GameObject CreateImage(Transform parent, string name, Color color)
    {
        GameObject go = new GameObject(name, typeof(RectTransform), typeof(Image));
        go.transform.SetParent(parent, false);
        go.GetComponent<Image>().color = color;
        return go;
    }

    public static TextMeshProUGUI CreateLabel(Transform parent, string name, string text, float fontSize, TextAlignmentOptions alignment)
    {
        GameObject go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        TextMeshProUGUI tmp = go.AddComponent<TextMeshProUGUI>();
        tmp.text = text;
        tmp.fontSize = fontSize;
        tmp.alignment = alignment;
        tmp.color = Color.white;
        return tmp;
    }

    // Hand-built to mirror the structure Unity's own "GameObject > UI > Slider" menu
    // creates (Background / Fill Area > Fill / Handle Slide Area > Handle) - written
    // out directly rather than invoked via the menu because EditorApplication
    // .ExecuteMenuItem requires an interactive GUI and won't run in batch mode.
    public static Slider CreateSlider(Transform parent, string name)
    {
        GameObject sliderGO = new GameObject(name, typeof(RectTransform));
        sliderGO.transform.SetParent(parent, false);

        GameObject bg = CreateImage(sliderGO.transform, "Background", new Color(0.15f, 0.15f, 0.17f, 1f));
        RectTransform bgRT = bg.GetComponent<RectTransform>();
        bgRT.anchorMin = new Vector2(0f, 0.25f);
        bgRT.anchorMax = new Vector2(1f, 0.75f);
        bgRT.offsetMin = Vector2.zero;
        bgRT.offsetMax = Vector2.zero;

        GameObject fillArea = new GameObject("Fill Area", typeof(RectTransform));
        fillArea.transform.SetParent(sliderGO.transform, false);
        RectTransform fillAreaRT = fillArea.GetComponent<RectTransform>();
        fillAreaRT.anchorMin = new Vector2(0f, 0.25f);
        fillAreaRT.anchorMax = new Vector2(1f, 0.75f);
        fillAreaRT.offsetMin = new Vector2(5f, 0f);
        fillAreaRT.offsetMax = new Vector2(-15f, 0f);

        GameObject fill = CreateImage(fillArea.transform, "Fill", new Color(0.35f, 0.65f, 1f, 1f));
        RectTransform fillRT = fill.GetComponent<RectTransform>();
        fillRT.anchorMin = new Vector2(0f, 0f);
        fillRT.anchorMax = new Vector2(1f, 1f);
        fillRT.sizeDelta = new Vector2(10f, 0f);
        fillRT.offsetMin = Vector2.zero;
        fillRT.offsetMax = Vector2.zero;

        GameObject handleArea = new GameObject("Handle Slide Area", typeof(RectTransform));
        handleArea.transform.SetParent(sliderGO.transform, false);
        RectTransform handleAreaRT = handleArea.GetComponent<RectTransform>();
        handleAreaRT.anchorMin = Vector2.zero;
        handleAreaRT.anchorMax = Vector2.one;
        handleAreaRT.offsetMin = new Vector2(10f, 0f);
        handleAreaRT.offsetMax = new Vector2(-10f, 0f);

        GameObject handle = CreateImage(handleArea.transform, "Handle", Color.white);
        RectTransform handleRT = handle.GetComponent<RectTransform>();
        handleRT.sizeDelta = new Vector2(24f, 0f);
        handleRT.anchorMin = new Vector2(0f, 0f);
        handleRT.anchorMax = new Vector2(0f, 1f);

        Slider slider = sliderGO.AddComponent<Slider>();
        slider.targetGraphic = handle.GetComponent<Image>();
        slider.fillRect = fillRT;
        slider.handleRect = handleRT;
        slider.direction = Slider.Direction.LeftToRight;

        return slider;
    }
}
