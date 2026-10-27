// TumorMarker3D.cs — places a red tumor marker on the patient's 3D lung mesh,
// using the real-world (DICOM mm) positions sent by tumor_server.py.
//
// Needs:
//   - TumorDetector in the scene (it fetches the detections)
//   - the lung mesh made by build_patient_mesh.py (<name>.obj) dragged into the scene
//   - its <name>_meta.json in Assets
//
// The OBJ contains 4 tiny calibration cubes at known mm offsets. This script measures
// them, so the marker lands correctly however the mesh is moved / scaled / rotated.

using System.Collections.Generic;
using System.IO;
using UnityEngine;

public class TumorMarker3D : MonoBehaviour
{
    public TumorDetector detector;
    [Tooltip("The lung object you dragged into the scene from the .obj")]
    public Transform lungRoot;
    [Tooltip("The _meta.json file made by build_patient_mesh.py")]
    public TextAsset meshMeta;

    [Header("Look")]
    public float markerDiameterMm = 18f;
    public Color markerColor = new Color(1f, 0.1f, 0.1f);
    public bool makeLungTransparent = true;
    [Range(0.05f, 1f)] public float lungOpacity = 0.35f;
    [Tooltip("Detections closer than this (mm) are one tumor -> one marker")]
    public float clusterMm = 30f;
    [Tooltip("Ignore tiny clusters (likely false alarms). 1 = show everything")]
    public int minDetectionsPerMarker = 5;
    [Tooltip("Optional: your own materials (leave empty for automatic)")]
    public Material lungMaterialOverride;
    public Material markerMaterialOverride;

    MeshMeta meta;
    Vector3 originL, axisXL, axisYL, axisZL;   // calibration, in lungRoot local space
    readonly List<GameObject> markers = new List<GameObject>();
    bool calibrated;

    void Start()
    {
        if (detector == null || lungRoot == null || meshMeta == null)
        {
            Debug.LogError("[TumorMarker3D] Assign Detector, Lung Root and Mesh Meta in the Inspector.");
            return;
        }
        meta = JsonUtility.FromJson<MeshMeta>(meshMeta.text);
        calibrated = Calibrate();
        if (!calibrated) return;
        if (makeLungTransparent) MakeLungTransparent();
        CheckSamePatient();

        detector.OnResult += PlaceMarkers;
        if (detector.Result != null) PlaceMarkers(detector.Result);
    }

    void OnDestroy()
    {
        if (detector != null) detector.OnResult -= PlaceMarkers;
    }

    bool Calibrate()
    {
        Renderer o = FindPart("calib_origin"), x = FindPart("calib_x"), y = FindPart("calib_y"), z = FindPart("calib_z");
        if (o == null || x == null || y == null || z == null)
        {
            Debug.LogError("[TumorMarker3D] Calibration cubes not found under Lung Root. " +
                           "Drag the whole .obj (not just the 'lungs' child) into the scene and assign that.");
            return false;
        }
        originL = lungRoot.InverseTransformPoint(o.bounds.center);
        axisXL = lungRoot.InverseTransformPoint(x.bounds.center) - originL;
        axisYL = lungRoot.InverseTransformPoint(y.bounds.center) - originL;
        axisZL = lungRoot.InverseTransformPoint(z.bounds.center) - originL;
        foreach (var r in new[] { o, x, y, z }) r.enabled = false;   // hide the helper cubes
        return true;
    }

    Renderer FindPart(string name)
    {
        foreach (var r in lungRoot.GetComponentsInChildren<Renderer>(true))
            if (r.name.ToLower().Contains(name)) return r;
        return null;
    }

    // DICOM patient mm -> lungRoot local position
    Vector3 MmToLocal(float[] mm)
    {
        float s = meta.calib_mm;
        float dx = (mm[0] - meta.center_mm[0]) / s;
        float dy = (mm[1] - meta.center_mm[1]) / s;
        float dz = (mm[2] - meta.center_mm[2]) / s;
        return originL + dx * axisXL + dy * axisYL + dz * axisZL;
    }

    void PlaceMarkers(TumorDetector.PatientResult result)
    {
        if (!calibrated) return;
        foreach (var m in markers) Destroy(m);
        markers.Clear();
        if (result == null || result.flagged_slices == null) return;

        // gather all detection centres (mm) and group nearby ones into one tumor
        var clusters = new List<List<Vector3>>();
        foreach (var s in result.flagged_slices)
            foreach (var d in s.detections)
            {
                if (d.center_mm == null || d.center_mm.Length != 3) continue;
                var p = new Vector3(d.center_mm[0], d.center_mm[1], d.center_mm[2]);
                List<Vector3> home = null;
                foreach (var c in clusters)
                    if (Vector3.Distance(Mean(c), p) < clusterMm) { home = c; break; }
                if (home == null) clusters.Add(new List<Vector3> { p }); else home.Add(p);
            }

        float mmToLocal = axisXL.magnitude / meta.calib_mm;
        int n = 0;
        foreach (var c in clusters)
        {
            if (c.Count < minDetectionsPerMarker)
            {
                Debug.Log($"[TumorMarker3D] Skipped a small cluster ({c.Count} detections) - likely a false alarm.");
                continue;
            }
            Vector3 mm = Mean(c);
            var marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            marker.name = $"TumorMarker_{++n}";
            Destroy(marker.GetComponent<Collider>());
            marker.transform.SetParent(lungRoot, false);
            marker.transform.localPosition = MmToLocal(new[] { mm.x, mm.y, mm.z });
            marker.transform.localScale = Vector3.one * markerDiameterMm * mmToLocal;
            marker.GetComponent<Renderer>().material = MarkerMaterial();
            markers.Add(marker);
            Debug.Log($"[TumorMarker3D] Tumor {n}: {c.Count} detections, centre ({mm.x:0.0}, {mm.y:0.0}, {mm.z:0.0}) mm");
        }
        if (n == 0) Debug.Log("[TumorMarker3D] No tumor detections to place.");
    }

    static Vector3 Mean(List<Vector3> pts)
    {
        var sum = Vector3.zero;
        foreach (var p in pts) sum += p;
        return sum / pts.Count;
    }

    // Works in both the Built-in pipeline and URP (Standard shader shows magenta in URP).
    static bool IsURP => UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline != null;

    Material MarkerMaterial()
    {
        if (markerMaterialOverride != null) return markerMaterialOverride;
        Material m;
        if (IsURP)
        {
            m = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
            m.SetColor("_BaseColor", markerColor);
        }
        else
        {
            m = new Material(Shader.Find("Standard"));
            m.color = markerColor;
            m.EnableKeyword("_EMISSION");
            m.SetColor("_EmissionColor", markerColor * 0.8f);
        }
        return m;
    }

    void MakeLungTransparent()
    {
        var lung = FindPart("lungs");
        if (lung == null) return;
        if (lungMaterialOverride != null) { lung.material = lungMaterialOverride; return; }

        var c = new Color(0.95f, 0.6f, 0.65f, lungOpacity);
        Material m;
        if (IsURP)
        {
            m = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            m.SetFloat("_Surface", 1);   // Transparent
            m.SetFloat("_Blend", 0);     // Alpha
            m.SetColor("_BaseColor", c);
            m.SetOverrideTag("RenderType", "Transparent");
            m.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            m.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            m.SetInt("_ZWrite", 0);
            m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            m.DisableKeyword("_ALPHATEST_ON");
        }
        else
        {
            m = new Material(Shader.Find("Standard"));
            m.color = c;
            m.SetFloat("_Mode", 2);   // Fade
            m.SetOverrideTag("RenderType", "Transparent");
            m.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            m.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            m.SetInt("_ZWrite", 0);
            m.DisableKeyword("_ALPHATEST_ON");
            m.EnableKeyword("_ALPHABLEND_ON");
            m.DisableKeyword("_ALPHAPREMULTIPLY_ON");
        }
        m.renderQueue = 3000;
        lung.material = m;
    }

    void CheckSamePatient()
    {
        string a = Norm(meta.source_dicom_folder), b = Norm(detector.dicomFolder);
        if (a != b)
            Debug.LogWarning($"[TumorMarker3D] Mesh was built from '{meta.source_dicom_folder}' but detection runs on " +
                             $"'{detector.dicomFolder}'. Use the SAME patient or the marker will be meaningless.");
    }

    static string Norm(string p) =>
        string.IsNullOrEmpty(p) ? "" : Path.GetFullPath(p).TrimEnd('\\', '/').ToLowerInvariant();

    [System.Serializable]
    class MeshMeta
    {
        public string source_dicom_folder;
        public float[] center_mm;
        public float calib_mm;
    }
}
