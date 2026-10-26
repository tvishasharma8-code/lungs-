using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.UI;
using TMPro;

/// <summary>
/// Builds the "Vitals" world-space panel, in two tabs (PanelTabs):
///   Overview - the four numbers that matter at a glance (respiratory rate, SpO2, tidal
///              volume, heart rate) as big tiles, the breath-by-breath lung volume
///              waveform, and the SpO2 / respiratory-rate trends side by side;
///   Details  - the other vitals, gas exchange and lung mechanics.
/// It used to show all ~20 values at the same size at once, so the important ones
/// didn't stand out. The data source line stays visible on both tabs.
/// Rebuilt from scratch every run; it has no inputs, so nothing is lost by rebuilding.
/// Called from BioGears > Fix VR Scene Layout, which owns placement.
/// </summary>
public static class VitalsMonitorBuilder
{
    public const string CanvasName = "VitalsMonitorCanvas";

    const float Width = 1000f;
    const float Padding = 40f;
    const float TitleTop = 18f;
    const float TitleFontSize = 54f;
    const float TitleHeight = 72f;
    const float TabTop = 100f;
    const float TabHeight = 72f;
    const float PageTop = TabTop + TabHeight + 22f;
    const float TileHeight = 132f;
    const float TileGap = 18f;
    const float TileLabelSize = 26f;
    const float TileValueSize = 60f;
    const float HeaderFontSize = 36f;
    const float HeaderHeight = 46f;
    const float WaveHeight = 150f;
    const float TrendHeight = 100f;
    const float CaptionFontSize = 24f;
    const float CaptionHeight = 32f;
    const float BlockGap = 18f;
    const float VitalsFontSize = 32f;
    const float VitalsHeight = 215f;     // five lines
    const float ColumnGap = 30f;
    const float SourceHeight = 40f;
    const float SectionFontSize = 30f;
    const float SectionHeight = 46f;
    const float BottomPadding = 24f;

    static readonly Color VolumeColor = new Color(0.25f, 0.95f, 1.00f, 1f); // cyan
    static readonly Color SpO2Color = new Color(0.45f, 1.00f, 0.55f, 1f);   // green
    static readonly Color RateColor = new Color(1.00f, 0.82f, 0.30f, 1f);   // amber
    static readonly Color HeartColor = new Color(1.00f, 0.45f, 0.55f, 1f);  // pink
    static readonly Color TextColor = new Color(0.85f, 0.95f, 1.00f, 1f);
    static readonly Color CaptionColor = new Color(0.85f, 0.95f, 1.00f, 0.55f);
    static readonly Color GraphBackground = new Color(0.04f, 0.06f, 0.08f, 1f);
    static readonly Color TabIdle = new Color(0.06f, 0.10f, 0.13f, 1f);

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
        canvasGO.AddComponent<CanvasScaler>().dynamicPixelsPerUnit = 10f;
        // The two tab buttons are the only ray targets; everything else is display only.
        canvasGO.AddComponent<GraphicRaycaster>().ignoreReversedGraphics = false;
        canvasGO.AddComponent<TrackedDeviceGraphicRaycaster>();

        GameObject bg = BioGearsIntakeUIBuilder.CreateImage(canvasRect, "PanelBackground", chassis);
        Stretch(bg.GetComponent<RectTransform>());
        bg.GetComponent<Image>().raycastTarget = false;

        TextMeshProUGUI title = BioGearsIntakeUIBuilder.CreateLabel(canvasRect, "Title", "VITALS", TitleFontSize, TextAlignmentOptions.Center);
        title.color = neon;
        title.characterSpacing = 6f;
        title.raycastTarget = false;
        Place(title.rectTransform, Width, TitleHeight, TitleTop);

        VitalsMonitor monitor = canvasGO.AddComponent<VitalsMonitor>();
        monitor.bio = Object.FindFirstObjectByType<BioGearsNativeRespiratory>();
        if (monitor.bio == null)
            Debug.LogWarning("[VitalsMonitorBuilder] No BioGearsNativeRespiratory in the scene - VitalsMonitor.bio left unassigned.");

        float inner = Width - Padding * 2f;

        // ---- Overview ---------------------------------------------------------------
        RectTransform overview = Page(canvasRect, "Page_Overview");
        float y = 0f;
        float tileWidth = (inner - TileGap * 3f) / 4f;
        monitor.rateTileText = Tile(overview, "RateTile", "Resp. rate", RateColor, Padding, tileWidth, y);
        monitor.spo2TileText = Tile(overview, "SpO2Tile", "SpO2", SpO2Color, Padding + (tileWidth + TileGap), tileWidth, y);
        monitor.tidalTileText = Tile(overview, "TidalTile", "Tidal volume", VolumeColor, Padding + 2f * (tileWidth + TileGap), tileWidth, y);
        monitor.heartTileText = Tile(overview, "HeartTile", "Heart rate", HeartColor, Padding + 3f * (tileWidth + TileGap), tileWidth, y);
        y += TileHeight + BlockGap;

        monitor.volumeGraph = Block(overview, "LungVolume", "Lung volume", "last 12 s - breath by breath",
                                    VolumeColor, Padding, inner, WaveHeight, y, out monitor.volumeText, out float waveBottom);
        monitor.volumeGraph.autoRange = true;
        monitor.volumeGraph.minAutoSpan = 400f; // mL - keeps a quiet breath readable
        monitor.volumeGraph.capacity = 360;     // 30 samples/s over 12 s
        monitor.volumeWindowSeconds = 12f;
        y = waveBottom;

        float half = (inner - ColumnGap) * 0.5f;
        monitor.spo2Graph = Block(overview, "SpO2", "SpO2", "2 min  |  70-100 %",
                                  SpO2Color, Padding, half, TrendHeight, y, out monitor.spo2Text, out float spo2Bottom);
        monitor.spo2Graph.minValue = 70f;
        monitor.spo2Graph.maxValue = 100f;
        monitor.spo2Graph.capacity = 240;
        monitor.rateGraph = Block(overview, "RespRate", "Resp. rate", "2 min  |  0-40 /min",
                                  RateColor, Padding + half + ColumnGap, half, TrendHeight, y, out monitor.rateText, out float rateBottom);
        monitor.rateGraph.minValue = 0f;
        monitor.rateGraph.maxValue = 40f;
        monitor.rateGraph.capacity = 240;
        monitor.trendWindowSeconds = 120f;
        float overviewHeight = Mathf.Max(spo2Bottom, rateBottom);

        // ---- Details ----------------------------------------------------------------
        RectTransform details = Page(canvasRect, "Page_Details");
        y = 0f;
        TextMeshProUGUI vitalsHeading = Label(details, "VitalsHeading", "VITALS", SectionFontSize, TextAlignmentOptions.Left, neon);
        vitalsHeading.characterSpacing = 3f;
        PlaceLeft(vitalsHeading.rectTransform, Padding, inner, SectionHeight, y);
        y += SectionHeight;
        monitor.leftColumnText = Label(details, "VitalsLeft", "--", VitalsFontSize, TextAlignmentOptions.TopLeft, TextColor);
        PlaceLeft(monitor.leftColumnText.rectTransform, Padding, half, VitalsHeight, y);
        monitor.rightColumnText = Label(details, "VitalsRight", "--", VitalsFontSize, TextAlignmentOptions.TopLeft, TextColor);
        PlaceLeft(monitor.rightColumnText.rectTransform, Padding + half + ColumnGap, half, VitalsHeight, y);
        y += VitalsHeight + BlockGap;

        // Gas exchange and lung mechanics - engine-only values, filled at runtime.
        TextMeshProUGUI section = Label(details, "GasExchangeHeading", "GAS EXCHANGE & LUNG MECHANICS", SectionFontSize, TextAlignmentOptions.Left, neon);
        section.characterSpacing = 3f;
        PlaceLeft(section.rectTransform, Padding, inner, SectionHeight, y);
        y += SectionHeight;
        monitor.gasLeftColumnText = Label(details, "GasLeft", "--", VitalsFontSize, TextAlignmentOptions.TopLeft, TextColor);
        PlaceLeft(monitor.gasLeftColumnText.rectTransform, Padding, half, VitalsHeight, y);
        monitor.gasRightColumnText = Label(details, "GasRight", "--", VitalsFontSize, TextAlignmentOptions.TopLeft, TextColor);
        PlaceLeft(monitor.gasRightColumnText.rectTransform, Padding + half + ColumnGap, half, VitalsHeight, y);
        y += VitalsHeight;
        float detailsHeight = y;

        float pageHeight = Mathf.Max(overviewHeight, detailsHeight);
        PlaceLeft(overview, 0f, Width, pageHeight, PageTop);
        PlaceLeft(details, 0f, Width, pageHeight, PageTop);

        // ---- Tabs and the always-visible source line ---------------------------------
        float tabWidth = (inner - ColumnGap) * 0.5f;
        Button overviewTab = Tab(canvasRect, "Tab_Overview", "Overview", Padding, tabWidth, neon);
        Button detailsTab = Tab(canvasRect, "Tab_Details", "Details", Padding + tabWidth + ColumnGap, tabWidth, neon);
        PanelTabs tabs = canvasGO.AddComponent<PanelTabs>();
        tabs.tabs = new[] { overviewTab, detailsTab };
        tabs.pages = new[] { overview.gameObject, details.gameObject };
        tabs.startPage = 0;

        float sourceTop = PageTop + pageHeight + 8f;
        monitor.sourceText = Label(canvasRect, "Source", "Source: --", CaptionFontSize, TextAlignmentOptions.Left, CaptionColor);
        PlaceLeft(monitor.sourceText.rectTransform, Padding, inner, SourceHeight, sourceTop);

        canvasRect.sizeDelta = new Vector2(Width, sourceTop + SourceHeight + BottomPadding);
        BuildBorder(canvasRect, neon, neonDim);

        canvasRect.localScale = Vector3.one * (worldWidth / Width);
        canvasRect.position = position;
        Vector3 away = position - eye;
        if (away.sqrMagnitude > 0.0001f)
            canvasRect.rotation = Quaternion.LookRotation(away.normalized, Vector3.up);

        EditorUtility.SetDirty(monitor);
        Debug.Log($"[VitalsMonitorBuilder] Built vitals monitor (Overview / Details) at {position}, {worldWidth:F2} m wide.");
    }

    static RectTransform Page(RectTransform canvasRect, string name)
    {
        var page = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>();
        page.SetParent(canvasRect, false);
        return page;
    }

    // A big live number with a small name above it.
    static TextMeshProUGUI Tile(RectTransform parent, string name, string caption, Color color, float left, float width, float top)
    {
        GameObject frame = BioGearsIntakeUIBuilder.CreateImage(parent, name, GraphBackground);
        frame.GetComponent<Image>().raycastTarget = false;
        RectTransform fr = frame.GetComponent<RectTransform>();
        PlaceLeft(fr, left, width, TileHeight, top);

        TextMeshProUGUI cap = Label(fr, "Caption", caption, TileLabelSize, TextAlignmentOptions.Center, CaptionColor);
        PlaceLeft(cap.rectTransform, 0f, width, 38f, 10f);
        TextMeshProUGUI value = Label(fr, "Value", "--", TileValueSize, TextAlignmentOptions.Center, color);
        value.fontStyle = FontStyles.Bold;
        value.enableAutoSizing = true;
        value.fontSizeMin = 30f;
        value.fontSizeMax = TileValueSize;
        value.textWrappingMode = TextWrappingModes.NoWrap;
        PlaceLeft(value.rectTransform, 6f, width - 12f, 80f, 46f);
        return value;
    }

    static Button Tab(RectTransform parent, string name, string text, float left, float width, Color neon)
    {
        GameObject go = BioGearsIntakeUIBuilder.CreateImage(parent, name, TabIdle);
        RectTransform r = go.GetComponent<RectTransform>();
        PlaceLeft(r, left, width, TabHeight, TabTop);
        Button button = go.AddComponent<Button>();
        button.targetGraphic = go.GetComponent<Image>();
        ColorBlock colors = button.colors;
        colors.highlightedColor = new Color(0.75f, 1f, 1f, 1f);
        colors.pressedColor = new Color(0.55f, 0.85f, 0.9f, 1f);
        button.colors = colors;
        TextMeshProUGUI label = Label(r, "Label", text, 34f, TextAlignmentOptions.Center, neon);
        Stretch(label.rectTransform);
        return button;
    }

    // Header row (name left, live value right), graph, caption under it.
    static TrendGraph Block(RectTransform parent, string name, string heading, string caption, Color color,
                            float left, float width, float graphHeight, float top,
                            out TextMeshProUGUI valueText, out float bottom)
    {
        float y = top;
        TextMeshProUGUI head = Label(parent, name + "Heading", heading, HeaderFontSize, TextAlignmentOptions.Left, color);
        PlaceLeft(head.rectTransform, left, width, HeaderHeight, y);

        valueText = Label(parent, name + "Value", "--", HeaderFontSize, TextAlignmentOptions.Right, color);
        PlaceLeft(valueText.rectTransform, left, width, HeaderHeight, y);
        y += HeaderHeight + 4f;

        GameObject frame = BioGearsIntakeUIBuilder.CreateImage(parent, name + "GraphFrame", GraphBackground);
        frame.GetComponent<Image>().raycastTarget = false;
        PlaceLeft(frame.GetComponent<RectTransform>(), left, width, graphHeight, y);

        GameObject graphGO = new GameObject(name + "Graph", typeof(RectTransform), typeof(CanvasRenderer));
        graphGO.transform.SetParent(frame.transform, false);
        RectTransform gr = graphGO.GetComponent<RectTransform>();
        Stretch(gr);
        gr.offsetMin = new Vector2(8f, 8f);   // keep the trace off the frame edge
        gr.offsetMax = new Vector2(-8f, -8f);
        TrendGraph graph = graphGO.AddComponent<TrendGraph>();
        graph.color = color;
        graph.raycastTarget = false;
        y += graphHeight + 2f;

        TextMeshProUGUI cap = Label(parent, name + "Caption", caption, CaptionFontSize, TextAlignmentOptions.Right, CaptionColor);
        PlaceLeft(cap.rectTransform, left, width, CaptionHeight, y);
        y += CaptionHeight + BlockGap;

        bottom = y;
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
        edge.GetComponent<Image>().raycastTarget = false;
    }
}
