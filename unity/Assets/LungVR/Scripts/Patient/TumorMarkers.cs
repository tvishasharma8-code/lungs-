using System;
using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.UI;

/// <summary>
/// Shows the tumors the LungVision backend found (findings.json of the Patient Package,
/// see backend pipeline/tumor_cluster.py) inside the patient's lungs:
///  - a glowing marker at each tumor's real position (patient mm, the same frame as the
///    lung mesh), sized from its volume, riding the breath exactly like the tissue
///    around it (LungMeshDeformer.BreathingOffset) and the inspection rotate / zoom;
///  - red for tumors inside a lobe, amber for ones the backend places outside the lung
///    outline (often hilum vessels - a likely false alarm);
///  - a label per marker, always facing the user;
///  - a CT slice panel just below the lungs: the selected tumor's clearest slice with
///    the model's box, and "Next tumor" to step through them.
/// The console's "Tumors" toggle (VRUIController) shows / hides all of it.
/// </summary>
public class TumorMarkers : MonoBehaviour
{
    public PatientLungLoader loader;
    public LungMeshDeformer lung;
    [Tooltip("TumorMarker.shader material (an asset, so the shader ships in builds).")]
    public Material markerMaterial;

    public bool showTumors = true;
    [Tooltip("Smallest marker, mm - very small tumors stay visible.")]
    public float minDiameterMm = 10f;
    public Color inLungColor = new Color(1f, 0.12f, 0.12f, 1f);
    public Color outsideLungColor = new Color(1f, 0.62f, 0.1f, 1f);

    /// <summary>Raised when tumors are loaded / cleared, or shown / hidden.</summary>
    public event Action OnChanged;
    public int Count => markers.Count;

    [Serializable] public class Findings { public string frame; public Tumor[] tumors; public SliceEntry[] slices; }
    [Serializable]
    public class Tumor
    {
        public string id; public float[] center_mm; public float[] size_mm; public float volume_mm3;
        public float max_confidence; public float mean_confidence; public int detections;
        public int[] slice_range; public int key_slice; public string location;
    }
    [Serializable] public class SliceEntry { public int index; public string image; public int width; public int height; public Det[] detections; }
    [Serializable] public class Det { public string tumor; public float confidence; public float[] box_px; }

    class Marker
    {
        public Tumor tumor;
        public Transform sphere;
        public Vector3 basePosition;   // lung-local, before the breath
        public float radiusMm;
        public TextMeshPro label;
        public MaterialPropertyBlock block;
        public string image;           // package path of its key slice image
    }

    static readonly int ColorId = Shader.PropertyToID("_Color");
    static readonly int SelectedId = Shader.PropertyToID("_Selected");

    readonly List<Marker> markers = new List<Marker>();
    readonly Dictionary<string, Texture2D> sliceTextures = new Dictionary<string, Texture2D>();
    int selected;
    string patientId;
    Canvas panel;
    RawImage panelImage;
    TextMeshProUGUI panelTitle, panelCaption;
    Camera cam;

    void Start()
    {
        if (loader == null) loader = FindFirstObjectByType<PatientLungLoader>();
        if (lung == null) lung = FindFirstObjectByType<LungMeshDeformer>();
        if (loader != null)
            loader.OnPatientLoaded += OnPatientLoaded;
    }

    void OnDestroy()
    {
        if (loader != null)
            loader.OnPatientLoaded -= OnPatientLoaded;
        Clear();
    }

    void OnPatientLoaded(PatientLungLoader.PatientMeta meta)
    {
        StopAllCoroutines();
        Clear();
        if (meta != null && loader != null && !string.IsNullOrEmpty(loader.loadedPatientId))
            StartCoroutine(Fetch(loader.loadedPatientId));
    }

    string PackageUrl(string file) =>
        $"{loader.BackendBase}/api/vr/patients/{UnityWebRequest.EscapeURL(patientId)}/{file}";

    IEnumerator Fetch(string id)
    {
        patientId = id;
        Findings findings;
        using (var req = UnityWebRequest.Get(PackageUrl("findings.json")))
        {
            req.timeout = 15;
            yield return req.SendWebRequest();
            if (req.result != UnityWebRequest.Result.Success)
            {
                Debug.LogWarning("[TumorMarkers] findings.json: " + req.error);
                yield break;
            }
            findings = JsonUtility.FromJson<Findings>(req.downloadHandler.text);
        }
        if (findings == null || findings.frame != "DICOM_LPS_mm" || findings.tumors == null)
            yield break;

        foreach (Tumor t in findings.tumors)
            if (t.center_mm != null && t.center_mm.Length == 3)
                AddMarker(t, findings);

        if (markers.Count > 0)
        {
            BuildPanel();
            Select(0);
            SetShowTumors(showTumors);
            DesktopMode.Refit();
        }
        Debug.Log($"[TumorMarkers] {markers.Count} tumor(s) for patient {id}.");
        OnChanged?.Invoke();
    }

    void AddMarker(Tumor t, Findings findings)
    {
        LungPackageMesh.LpsToUnity(t.center_mm[0], t.center_mm[1], t.center_mm[2], out float x, out float y, out float z);
        // Diameter of the sphere with the tumor's (rough) volume; else its mean extent.
        float diameter = t.volume_mm3 > 0f
            ? Mathf.Pow(t.volume_mm3 * 6f / Mathf.PI, 1f / 3f)
            : (t.size_mm != null && t.size_mm.Length == 3 ? (t.size_mm[0] + t.size_mm[1] + t.size_mm[2]) / 3f : minDiameterMm);
        diameter = Mathf.Max(minDiameterMm, diameter);

        GameObject sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        sphere.name = "Tumor_" + t.id;
        Destroy(sphere.GetComponent<Collider>());  // the inspection pivot's box takes the ray
        sphere.transform.SetParent(lung.transform, false);
        var m = new Marker
        {
            tumor = t,
            sphere = sphere.transform,
            basePosition = new Vector3(x, y, z),
            radiusMm = diameter * 0.5f,
            block = new MaterialPropertyBlock(),
        };
        sphere.transform.localPosition = m.basePosition;
        sphere.transform.localScale = Vector3.one * diameter;

        var renderer = sphere.GetComponent<Renderer>();
        renderer.sharedMaterial = markerMaterial != null ? markerMaterial : FallbackMaterial();
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        m.block.SetColor(ColorId, IsOutside(t) ? outsideLungColor : inLungColor);
        renderer.SetPropertyBlock(m.block);

        m.label = CreateLabel(t);
        if (findings.slices != null)
            foreach (SliceEntry s in findings.slices)
                if (s.index == t.key_slice) { m.image = s.image; break; }
        markers.Add(m);
    }

    static bool IsOutside(Tumor t) => t.location == "outside the lungs";

    static string Place(Tumor t)
    {
        if (string.IsNullOrEmpty(t.location)) return "";
        if (IsOutside(t)) return "Outside lung outline - possible false alarm";
        // "lung_lower_lobe_left" -> "Left lower lobe"
        string[] p = t.location.Split('_');
        if (p.Length == 4 && p[0] == "lung")
            return char.ToUpper(p[3][0]) + p[3].Substring(1) + " " + p[1] + " lobe";
        return t.location.Replace('_', ' ');
    }

    TextMeshPro CreateLabel(Tumor t)
    {
        var go = new GameObject("TumorLabel_" + t.id);
        go.transform.SetParent(transform, false);  // world-sized, not under the lung's mm scale
        var tmp = go.AddComponent<TextMeshPro>();
        string place = Place(t);
        tmp.text = $"<b>{t.id}</b>  {t.max_confidence * 100f:0}%" + (place.Length > 0 ? $"\n<size=70%>{place}</size>" : "");
        tmp.fontSize = 0.32f;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.textWrappingMode = TextWrappingModes.NoWrap;
        tmp.rectTransform.sizeDelta = new Vector2(0.6f, 0.12f);
        tmp.color = IsOutside(t) ? new Color(1f, 0.8f, 0.45f) : new Color(1f, 0.7f, 0.7f);
        tmp.outlineWidth = 0.2f;
        tmp.outlineColor = new Color32(0, 0, 0, 200);
        return tmp;
    }

    Material fallback;
    Material FallbackMaterial()
    {
        if (fallback == null)
        {
            Shader s = Shader.Find("LungVision/TumorMarker");
            if (s == null) s = Shader.Find("Universal Render Pipeline/Unlit");
            fallback = new Material(s);
        }
        return fallback;
    }

    void LateUpdate()
    {
        if (markers.Count == 0 || lung == null)
            return;
        if (cam == null) cam = Camera.main;

        Transform lungT = lung.transform;
        foreach (Marker m in markers)
        {
            // Same displacement the vertex shader gives the tissue at this point.
            m.sphere.localPosition = m.basePosition + lung.BreathingOffset(m.basePosition);

            if (m.label == null || cam == null) continue;
            Vector3 centre = m.sphere.position;
            float radius = m.radiusMm * lungT.lossyScale.x;
            Vector3 toCam = (cam.transform.position - centre).normalized;
            // Above the marker and pulled towards the viewer, so the lung never hides it.
            m.label.transform.position = centre + Vector3.up * (radius + 0.03f) + toCam * 0.25f;
            m.label.transform.rotation = Quaternion.LookRotation(m.label.transform.position - cam.transform.position, Vector3.up);
        }
    }

    public void SetShowTumors(bool on)
    {
        showTumors = on;
        foreach (Marker m in markers)
        {
            m.sphere.gameObject.SetActive(on);
            if (m.label != null) m.label.gameObject.SetActive(on);
        }
        if (panel != null) panel.gameObject.SetActive(on && markers.Count > 0);
        OnChanged?.Invoke();
    }

    public void SelectNext()
    {
        if (markers.Count > 0)
            Select((selected + 1) % markers.Count);
    }

    void Select(int index)
    {
        selected = index;
        for (int i = 0; i < markers.Count; i++)
        {
            Marker m = markers[i];
            m.block.SetFloat(SelectedId, i == index ? 1f : 0f);
            m.sphere.GetComponent<Renderer>().SetPropertyBlock(m.block);
        }

        Marker s = markers[index];
        Tumor t = s.tumor;
        if (panelTitle != null)
            panelTitle.text = $"Tumor {t.id}  ({index + 1} of {markers.Count})";
        if (panelCaption != null)
        {
            string size = t.size_mm != null && t.size_mm.Length == 3
                ? $"{t.size_mm[0]:0} x {t.size_mm[1]:0} x {t.size_mm[2]:0} mm" : "";
            string vol = t.volume_mm3 > 0f ? $"  ~{t.volume_mm3 / 1000f:0.0} cm3" : "";
            string place = Place(t);
            panelCaption.text =
                $"{t.max_confidence * 100f:0}% confidence   CT slice {t.key_slice}\n" +
                (place.Length > 0 ? place + "\n" : "") + size + vol +
                "\n<size=75%><i>AI research prototype - not a diagnosis</i></size>";
        }
        if (panelImage != null)
        {
            panelImage.texture = null;
            if (!string.IsNullOrEmpty(s.image))
                StartCoroutine(ShowSlice(s.image));
        }
    }

    IEnumerator ShowSlice(string image)
    {
        if (!sliceTextures.TryGetValue(image, out Texture2D tex))
        {
            using (var req = UnityWebRequestTexture.GetTexture(PackageUrl(image)))
            {
                req.timeout = 15;
                yield return req.SendWebRequest();
                if (req.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogWarning($"[TumorMarkers] {image}: {req.error}");
                    yield break;
                }
                tex = DownloadHandlerTexture.GetContent(req);
            }
            sliceTextures[image] = tex;
        }
        if (panelImage != null && markers.Count > selected && markers[selected].image == image)
            panelImage.texture = tex;
    }

    // ---- CT slice panel ---------------------------------------------------------------

    const float PanelPx = 620f;      // canvas units
    const float PanelWidthM = 0.42f; // in the scene

    void BuildPanel()
    {
        if (panel != null) return;

        var go = new GameObject("TumorSlicePanel", typeof(RectTransform));
        go.transform.SetParent(transform, false);
        panel = go.AddComponent<Canvas>();
        panel.renderMode = RenderMode.WorldSpace;
        panel.worldCamera = Camera.main;
        go.AddComponent<GraphicRaycaster>();          // mouse (Editor / desktop mode)
        go.AddComponent<TrackedDeviceGraphicRaycaster>(); // controller ray
        var rt = (RectTransform)go.transform;
        rt.sizeDelta = new Vector2(PanelPx, PanelPx + 300f);
        rt.localScale = Vector3.one * (PanelWidthM / PanelPx);

        Image bg = NewImage(rt, "Background", new Color(0.02f, 0.05f, 0.07f, 0.92f));
        Stretch(bg.rectTransform, 0, 0, 0, 0);

        panelTitle = NewText(rt, "Title", 38f, FontStyles.Bold);
        Place(panelTitle.rectTransform, 0f, -14f, PanelPx - 40f, 50f);

        panelImage = new GameObject("Slice", typeof(RectTransform)).AddComponent<RawImage>();
        panelImage.transform.SetParent(rt, false);
        panelImage.color = Color.white;
        Place(panelImage.rectTransform, 0f, -70f, PanelPx - 60f, PanelPx - 60f);

        panelCaption = NewText(rt, "Caption", 26f, FontStyles.Normal);
        Place(panelCaption.rectTransform, 0f, -(PanelPx + 18f), PanelPx - 40f, 150f);

        Image buttonBg = NewImage(rt, "NextTumor", new Color(0.06f, 0.10f, 0.13f, 1f));
        Place(buttonBg.rectTransform, 0f, -(PanelPx + 178f), PanelPx - 160f, 76f);
        var button = buttonBg.gameObject.AddComponent<Button>();
        button.targetGraphic = buttonBg;
        button.onClick.AddListener(SelectNext);
        TextMeshProUGUI buttonText = NewText(buttonBg.rectTransform, "Label", 32f, FontStyles.Bold);
        Stretch(buttonText.rectTransform, 0, 0, 0, 0);
        buttonText.text = "Next tumor  >";
        buttonText.color = new Color(0.25f, 0.95f, 1f);

        PlacePanel();
    }

    /// <summary>Just below and in front of the lungs, tilted up towards the user like a lectern.</summary>
    void PlacePanel()
    {
        var r = lung.GetComponent<Renderer>();
        if (r == null || panel == null) return;
        Bounds b = r.bounds;

        var xr = FindFirstObjectByType<Unity.XR.CoreUtils.XROrigin>();
        Vector3 viewer = xr != null ? xr.transform.position : (Camera.main != null ? Camera.main.transform.position : Vector3.zero);
        Vector3 eye = viewer + Vector3.up * 1.5f;
        Vector3 towardViewer = viewer - b.center;
        towardViewer.y = 0f;
        towardViewer = towardViewer.sqrMagnitude > 1e-6f ? towardViewer.normalized : Vector3.back;

        float height = PanelWidthM * (PanelPx + 300f) / PanelPx;
        Vector3 pos = new Vector3(b.center.x, b.min.y - 0.05f - height * 0.45f, b.center.z) + towardViewer * 0.45f;
        pos.y = Mathf.Max(pos.y, viewer.y + height * 0.5f + 0.15f);
        panel.transform.position = pos;
        panel.transform.rotation = Quaternion.LookRotation(pos - eye, Vector3.up);
    }

    static Image NewImage(Transform parent, string name, Color color)
    {
        var img = new GameObject(name, typeof(RectTransform)).AddComponent<Image>();
        img.transform.SetParent(parent, false);
        img.color = color;
        return img;
    }

    static TextMeshProUGUI NewText(Transform parent, string name, float size, FontStyles style)
    {
        var t = new GameObject(name, typeof(RectTransform)).AddComponent<TextMeshProUGUI>();
        t.transform.SetParent(parent, false);
        t.fontSize = size;
        t.fontStyle = style;
        t.alignment = TextAlignmentOptions.Center;
        t.color = new Color(0.85f, 0.95f, 1f);
        t.raycastTarget = false;
        return t;
    }

    static void Stretch(RectTransform r, float l, float b, float rgt, float t)
    {
        r.anchorMin = Vector2.zero; r.anchorMax = Vector2.one;
        r.offsetMin = new Vector2(l, b); r.offsetMax = new Vector2(-rgt, -t);
    }

    // Top-centre anchored at (x, y) from the panel's top edge.
    static void Place(RectTransform r, float x, float y, float w, float h)
    {
        r.anchorMin = r.anchorMax = new Vector2(0.5f, 1f);
        r.pivot = new Vector2(0.5f, 1f);
        r.anchoredPosition = new Vector2(x, y);
        r.sizeDelta = new Vector2(w, h);
    }

    void Clear()
    {
        foreach (Marker m in markers)
        {
            if (m.sphere != null) Destroy(m.sphere.gameObject);
            if (m.label != null) Destroy(m.label.gameObject);
        }
        markers.Clear();
        foreach (Texture2D t in sliceTextures.Values)
            if (t != null) Destroy(t);
        sliceTextures.Clear();
        if (panel != null) Destroy(panel.gameObject);
        panel = null;
        OnChanged?.Invoke();
    }
}
