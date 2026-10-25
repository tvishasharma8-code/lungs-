using UnityEditor;
using UnityEngine;
using TMPro;

/// <summary>
/// Builds the "Vitals Monitor" world-space panel: three stacked graphs (lung volume
/// waveform, SpO2 trend, respiratory-rate trend) with live values, plus a small block
/// of other vitals. Rebuilt from scratch every run; it has no inputs, so nothing is
/// lost by rebuilding. Called from BioGears > Fix VR Scene Layout, which owns placement.
/// </summary>
public static class VitalsMonitorBuilder
{
    public const string CanvasName = "VitalsMonitorCanvas";

    const float Width = 1000f;
    const float Padding = 40f;
    const float TitleTop = 18f;
    const float TitleFontSize = 62f;
    const float TitleHeight = 80f;
    const float HeaderFontSize = 44f;
    const float HeaderHeight = 56f;
    const float GraphHeight = 140f;      // trimmed to make room for the gas-exchange block
    const float CaptionFontSize = 26f;
    const float CaptionHeight = 34f;
    const float BlockGap = 20f;
    const float VitalsFontSize = 32f;
    const float VitalsHeight = 215f;     // five lines
    const float ColumnGap = 30f;
    const float SourceHeight = 40f;
    const float SectionFontSize = 30f;
    const float SectionHeight = 46f;
    const float BottomPadding = 30f;

    static readonly Color VolumeColor = new Color(0.25f, 0.95f, 1.00f, 1f); // cyan
    static readonly Color SpO2Color = new Color(0.45f, 1.00f, 0.55f, 1f);   // green
    static readonly Color RateColor = new Color(1.00f, 0.82f, 0.30f, 1f);   // amber
    static readonly Color TextColor = new Color(0.85f, 0.95f, 1.00f, 1f);
    static readonly Color CaptionColor = new Color(0.85f, 0.95f, 1.00f, 0.55f);
    static readonly Color GraphBackground = new Color(0.04f, 0.06f, 0.08f, 1f);

    /// <summary>Is this canvas the vitals monitor (so other tools leave it alone)?</summary>
    public static bool IsMonitor(Canvas c) => c != null && c.gameObject.name == CanvasName;

    /// <summary>
    /// Builds the panel, sizes it to <paramref name="worldWidth"/> metres, centres it on
    /// <paramref name="position"/> and turns its face toward <paramref name="eye"/>.
    /// </summary>
    public static void Build(Vector3 position, Vector3 eye, float worldWidth, Camera cam,
                             Color chassis, Color neon, Color neonDim)
    {
        foreach (Canvas c in Object.FindObjectsByType<Canvas>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            if (IsMonitor(c))
                Object.DestroyImmediate(c.gameObject);

        GameObject canvasGO = new GameObject(CanvasName, typeof(RectTransform));
        RectTransform canvasRect = canvasGO.GetComponent<RectTransform>();
        Canvas canvas = canvasGO.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.worldCamera = cam;
        canvasGO.AddComponent<UnityEngine.UI.CanvasScaler>().dynamicPixelsPerUnit = 10f;
        // Display only: no raycaster, so controller rays pass straight through.

        GameObject bg = BioGearsIntakeUIBuilder.CreateImage(canvasRect, "PanelBackground", chassis);
        Stretch(bg.GetComponent<RectTransform>());
        bg.GetComponent<UnityEngine.UI.Image>().raycastTarget = false;

        TextMeshProUGUI title = BioGearsIntakeUIBuilder.CreateLabel(canvasRect, "Title", "VITALS MONITOR", TitleFontSize, TextAlignmentOptions.Center);
        title.color = neon;
        title.characterSpacing = 6f;
        Place(title.rectTransform, Width, TitleHeight, TitleTop);

        VitalsMonitor monitor = canvasGO.AddComponent<VitalsMonitor>();
        monitor.bio = Object.FindFirstObjectByType<BioGearsNativeRespiratory>();
        if (monitor.bio == null)
            Debug.LogWarning("[VitalsMonitorBuilder] No BioGearsNativeRespiratory in the scene - VitalsMonitor.bio left unassigned.");

        float y = TitleTop + TitleHeight + 6f;
        float inner = Width - Padding * 2f;

        monitor.volumeGraph = Block(canvasRect, "LungVolume", "Lung volume", "last 12 s - breath by breath",
                                    VolumeColor, inner, ref y, out monitor.volumeText);
        monitor.volumeGraph.autoRange = true;
        monitor.volumeGraph.minAutoSpan = 400f; // mL - keeps a quiet breath readable
        monitor.volumeGraph.capacity = 360;     // 30 samples/s over 12 s
        monitor.volumeWindowSeconds = 12f;

        monitor.spo2Graph = Block(canvasRect, "SpO2", "SpO2", "last 2 min  -  70 to 100 %",
                                  SpO2Color, inner, ref y, out monitor.spo2Text);
        monitor.spo2Graph.minValue = 70f;
        monitor.spo2Graph.maxValue = 100f;
        monitor.spo2Graph.capacity = 240;

        monitor.rateGraph = Block(canvasRect, "RespRate", "Respiratory rate", "last 2 min  -  0 to 40 /min",
                                  RateColor, inner, ref y, out monitor.rateText);
        monitor.rateGraph.minValue = 0f;
        monitor.rateGraph.maxValue = 40f;
        monitor.rateGraph.capacity = 240;
        monitor.trendWindowSeconds = 120f;

        // Two columns of other vitals (filled in at runtime), then the data source.
        float columnWidth = (inner - ColumnGap) * 0.5f;
        monitor.leftColumnText = Label(canvasRect, "VitalsLeft", "--", VitalsFontSize, TextAlignmentOptions.TopLeft, TextColor);
        PlaceLeft(monitor.leftColumnText.rectTransform, Padding, columnWidth, VitalsHeight, y);
        monitor.rightColumnText = Label(canvasRect, "VitalsRight", "--", VitalsFontSize, TextAlignmentOptions.TopLeft, TextColor);
        PlaceLeft(monitor.rightColumnText.rectTransform, Padding + columnWidth + ColumnGap, columnWidth, VitalsHeight, y);
        y += VitalsHeight;

        // Gas exchange and lung mechanics - engine-only values, filled at runtime.
        TextMeshProUGUI section = Label(canvasRect, "GasExchangeHeading", "GAS EXCHANGE & LUNG MECHANICS", SectionFontSize, TextAlignmentOptions.Left, neon);
        section.characterSpacing = 3f;
        PlaceLeft(section.rectTransform, Padding, inner, SectionHeight, y);
        y += SectionHeight;

        monitor.gasLeftColumnText = Label(canvasRect, "GasLeft", "--", VitalsFontSize, TextAlignmentOptions.TopLeft, TextColor);
        PlaceLeft(monitor.gasLeftColumnText.rectTransform, Padding, columnWidth, VitalsHeight, y);
        monitor.gasRightColumnText = Label(canvasRect, "GasRight", "--", VitalsFontSize, TextAlignmentOptions.TopLeft, TextColor);
        PlaceLeft(monitor.gasRightColumnText.rectTransform, Padding + columnWidth + ColumnGap, columnWidth, VitalsHeight, y);
        y += VitalsHeight;

        monitor.sourceText =Label(canvasRect, "Source", "Source: --", CaptionFontSize, TextAlignmentOptions.Left, CaptionColor);
        PlaceLeft(monitor.sourceText.rectTransform, Padding, inner, SourceHeight, y);
        y += SourceHeight + BottomPadding;

        canvasRect.sizeDelta = new Vector2(Width, y);
        BuildBorder(canvasRect, neon, neonDim);

        canvasRect.localScale = Vector3.one * (worldWidth / Width);
        canvasRect.position = position;
        Vector3 away = position - eye;
        if (away.sqrMagnitude > 0.0001f)
            canvasRect.rotation = Quaternion.LookRotation(away.normalized, Vector3.up);

        EditorUtility.SetDirty(monitor);
        Debug.Log($"[VitalsMonitorBuilder] Built vitals monitor at {position}, {worldWidth:F2} m wide.");
    }

    // Header row (name left, live value right), graph, caption under it.
    static TrendGraph Block(RectTransform parent, string name, string heading, string caption,
                            Color color, float inner, ref float y, out TextMeshProUGUI valueText)
    {
        TextMeshProUGUI head = Label(parent, name + "Heading", heading, HeaderFontSize, TextAlignmentOptions.Left, color);
        PlaceLeft(head.rectTransform, Padding, inner, HeaderHeight, y);

        valueText = Label(parent, name + "Value", "--", HeaderFontSize, TextAlignmentOptions.Right, color);
        PlaceLeft(valueText.rectTransform, Padding, inner, HeaderHeight, y);
        y += HeaderHeight + 4f;

        GameObject frame = BioGearsIntakeUIBuilder.CreateImage(parent, name + "GraphFrame", GraphBackground);
        frame.GetComponent<UnityEngine.UI.Image>().raycastTarget = false;
        PlaceLeft(frame.GetComponent<RectTransform>(), Padding, inner, GraphHeight, y);

        GameObject graphGO = new GameObject(name + "Graph", typeof(RectTransform), typeof(CanvasRenderer));
        graphGO.transform.SetParent(frame.transform, false);
        RectTransform gr = graphGO.GetComponent<RectTransform>();
        Stretch(gr);
        gr.offsetMin = new Vector2(8f, 8f);   // keep the trace off the frame edge
        gr.offsetMax = new Vector2(-8f, -8f);
        TrendGraph graph = graphGO.AddComponent<TrendGraph>();
        graph.color = color;
        graph.raycastTarget = false;
        y += GraphHeight + 2f;

        TextMeshProUGUI cap = Label(parent, name + "Caption", caption, CaptionFontSize, TextAlignmentOptions.Right, CaptionColor);
        PlaceLeft(cap.rectTransform, Padding, inner, CaptionHeight, y);
        y += CaptionHeight + BlockGap;

        return graph;
    }

    static TextMeshProUGUI Label(RectTransform parent, string name, string text, float size, TextAlignmentOptions align, Color color)
    {
        TextMeshProUGUI t = BioGearsIntakeUIBuilder.CreateLabel(parent, name, text, size, align);
        t.color = color;
        t.raycastTarget = false;
        return t;
    }

    static void Stretch(RectTransform r)
    {
        r.anchorMin = Vector2.zero;
        r.anchorMax = Vector2.one;
        r.offsetMin = Vector2.zero;
        r.offsetMax = Vector2.zero;
    }

    // Centred horizontally, measured down from the top edge.
    static void Place(RectTransform r, float width, float height, float top)
    {
        r.anchorMin = r.anchorMax = new Vector2(0.5f, 1f);
        r.pivot = new Vector2(0.5f, 1f);
        r.sizeDelta = new Vector2(width, height);
        r.anchoredPosition = new Vector2(0f, -top);
    }

    // Measured from the top-left corner.
    static void PlaceLeft(RectTransform r, float left, float width, float height, float top)
    {
        r.anchorMin = r.anchorMax = new Vector2(0f, 1f);
        r.pivot = new Vector2(0f, 1f);
        r.sizeDelta = new Vector2(width, height);
        r.anchoredPosition = new Vector2(left, -top);
    }

    // Same rim as the console: dim halo strips with a bright core on top.
    static void BuildBorder(RectTransform canvasRect, Color neon, Color neonDim)
    {
        GameObject holder = new GameObject("NeonBorder", typeof(RectTransform));
        holder.transform.SetParent(canvasRect, false);
        RectTransform h = holder.GetComponent<RectTransform>();
        Stretch(h);
        h.SetAsLastSibling();

        Edge(h, "TopGlow", new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, 14f), neonDim);
        Edge(h, "BottomGlow", new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, 14f), neonDim);
        Edge(h, "LeftGlow", new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(14f, 0f), neonDim);
        Edge(h, "RightGlow", new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(14f, 0f), neonDim);
        Edge(h, "Top", new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, 5f), neon);
        Edge(h, "Bottom", new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, 5f), neon);
        Edge(h, "Left", new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(5f, 0f), neon);
        Edge(h, "Right", new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(5f, 0f), neon);
    }

    static void Edge(RectTransform parent, string name, Vector2 anchorMin, Vector2 anchorMax, Vector2 size, Color color)
    {
        GameObject edge = BioGearsIntakeUIBuilder.CreateImage(parent, name, color);
        RectTransform r = edge.GetComponent<RectTransform>();
        r.anchorMin = anchorMin;
        r.anchorMax = anchorMax;
        r.sizeDelta = size;
        r.anchoredPosition = Vector2.zero;
        edge.GetComponent<UnityEngine.UI.Image>().raycastTarget = false;
    }
}
