using System;
using System.Collections;
using System.Threading.Tasks;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.Rendering;

/// <summary>
/// Shows a real patient's lungs - reconstructed by the LungVision backend from their CT -
/// in place of the built-in lung model. Breathing, disease shading, smoking damage and
/// airflow all carry on, now on the patient's anatomy.
///
/// The backend keeps patients the user explicitly sent with "Send to VR" (see the
/// backend's services/vr_service.py). With followActivePatient on, this checks every few
/// seconds which patient is active there and loads it, so pressing "Send to VR" in the
/// browser makes it appear in the headset.
///
/// Backend address: -backend on the command line, else backendUrl if set; otherwise on a standalone headset the PC that
/// the BioGears link found (same PC, port 8000), and on the PC itself 127.0.0.1:8000.
/// Run the backend with  uvicorn main:app --host 0.0.0.0 --port 8000  so a headset can
/// reach it.
///
/// Placement: the patient's lung replaces the default one in the same spot (centred where
/// it was, inside the inspection pivot so rotate / zoom still work), facing the user,
/// either at the same apparent size or at real life size.
/// </summary>
public class PatientLungLoader : MonoBehaviour
{
    public LungMeshDeformer lung;
    public AirflowVisualizer airflow;
    public BioGearsNativeRespiratory bio;

    [Header("Backend")]
    [Tooltip("e.g. http://192.168.1.20:8000 - leave empty to work it out (see class notes).")]
    public string backendUrl = "";
    public int backendPort = 8000;
    [Tooltip("Load whichever patient is active on the backend, and follow changes.")]
    public bool followActivePatient = true;
    public float pollSeconds = 5f;

    [Header("Placement")]
    [Tooltip("Show the patient's lungs at real size instead of the size the default model is shown at.")]
    public bool realSize = false;

    [Header("Status (read-only)")]
    public string loadedPatientId = "";
    public string loadedLabel = "Default lung model";
    public string status = "Idle";

    /// <summary>Meta of the loaded patient (null for the default model). Tumor overlays use center_mm etc.</summary>
    public PatientMeta Meta { get; private set; }
    /// <summary>Raised after a patient's lung (or the default, with null) is in place.</summary>
    public event Action<PatientMeta> OnPatientLoaded;

    // The default lung, kept so the user can switch back.
    Mesh defaultMesh;
    Vector3 defaultLocalPosition;
    Quaternion defaultLocalRotation;
    Vector3 defaultLocalScale;
    // Where and how big the default lung is, in the parent's (pivot's) space - the patient's
    // lung goes in the same place at the same size, and later zoom keeps working on both.
    Vector3 anchorInParent;
    float longestInParent;
    float parentScaleAtStart = 1f;
    bool captured;
    bool loading;
    // A patient whose package failed to load is retried once a minute, not on every poll
    // (each attempt downloads a ~25 MB mesh). Load() retries immediately.
    string failedPatientId;
    float retryFailedAt;
    float nextPoll;
    // The patient the user put away with ShowDefaultLung: not reloaded while it stays the
    // active one, but polling goes on, so the next patient sent still appears.
    string dismissedPatientId;

    [Serializable]
    public class PatientMeta
    {
        public string format;
        public int version;
        public string frame;
        public float[] center_mm;
        public float[] bbox_min_mm;
        public float[] bbox_max_mm;
        public int vertices;
        public int triangles;
        public Structure[] structures;
        public Source source;
    }

    [Serializable] public class Structure { public string name; public int triangles; }
    [Serializable] public class Source { public string series_id; public int slices; public float[] voxel_spacing_mm; }
    // JSON replies (JsonUtility fills the fields; public so the compiler knows they can be set).
    [Serializable] public class ActiveReply { public string id; }
    [Serializable] public class PackageInfo { public string id; public string label; }
    [Serializable] public class PackageList { public PackageInfo[] items; }

    void Start()
    {
        // "View in Unity" starts the desktop build with  -backend http://host:port
        string[] args = Environment.GetCommandLineArgs();
        int at = Array.FindIndex(args, a => a.Equals("-backend", StringComparison.OrdinalIgnoreCase));
        if (at >= 0 && at + 1 < args.Length)
            backendUrl = args[at + 1];

        if (lung == null) lung = FindFirstObjectByType<LungMeshDeformer>();
        if (airflow == null) airflow = FindFirstObjectByType<AirflowVisualizer>();
        if (bio == null) bio = FindFirstObjectByType<BioGearsNativeRespiratory>();
        if (lung == null)
        {
            Debug.LogError("[PatientLungLoader] No LungMeshDeformer in the scene.");
            enabled = false;
        }
    }

    /// <summary>Where the backend is, as worked out right now.</summary>
    public string BackendBase
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(backendUrl))
                return backendUrl.TrimEnd('/');
            string pc = bio != null ? bio.PcAddress : null;
            return $"http://{(string.IsNullOrEmpty(pc) ? "127.0.0.1" : pc)}:{backendPort}";
        }
    }

    void Update()
    {
        if (!followActivePatient || loading || Time.unscaledTime < nextPoll)
            return;
        nextPoll = Time.unscaledTime + Mathf.Max(1f, pollSeconds);
        StartCoroutine(CheckActive());
    }

    IEnumerator CheckActive()
    {
        using (var req = UnityWebRequest.Get(BackendBase + "/api/vr/active"))
        {
            req.timeout = 4;
            yield return req.SendWebRequest();
            if (req.result != UnityWebRequest.Result.Success)
            {
                status = $"Backend not reachable at {BackendBase}";
                yield break;
            }
            var reply = JsonUtility.FromJson<ActiveReply>(req.downloadHandler.text);
            if (reply != null && !string.IsNullOrEmpty(reply.id) && reply.id != loadedPatientId
                && reply.id != dismissedPatientId
                && (reply.id != failedPatientId || Time.unscaledTime >= retryFailedAt) && !loading)
                yield return LoadPatient(reply.id);
        }
    }

    /// <summary>Downloads a patient's package from the backend and puts their lung in the scene.</summary>
    public void Load(string patientId)
    {
        dismissedPatientId = null;
        if (!loading)
            StartCoroutine(LoadPatient(patientId));
    }

    IEnumerator LoadPatient(string patientId)
    {
        loading = true;
        failedPatientId = patientId;  // cleared once it has loaded
        status = $"Downloading patient {patientId}...";
        string url = $"{BackendBase}/api/vr/patients/{UnityWebRequest.EscapeURL(patientId)}/";

        PatientMeta meta;
        using (var req = UnityWebRequest.Get(url + "meta.json"))
        {
            req.timeout = 15;
            yield return req.SendWebRequest();
            if (req.result != UnityWebRequest.Result.Success) { Fail("meta.json: " + req.error); yield break; }
            meta = JsonUtility.FromJson<PatientMeta>(req.downloadHandler.text);
        }
        if (meta == null || meta.frame != "DICOM_LPS_mm")
        {
            Fail($"unexpected package frame '{meta?.frame}'");
            yield break;
        }

        byte[] bytes;
        using (var req = UnityWebRequest.Get(url + "mesh.bin"))
        {
            req.timeout = 60;
            yield return req.SendWebRequest();
            if (req.result != UnityWebRequest.Result.Success) { Fail("mesh.bin: " + req.error); yield break; }
            bytes = req.downloadHandler.data;
        }

        // Parse off the main thread: a few MB of floats would otherwise hitch a frame.
        status = "Building the patient's lung...";
        Task<LungPackageMesh> parse = Task.Run(() => LungPackageMesh.Read(bytes));
        while (!parse.IsCompleted)
            yield return null;
        if (parse.IsFaulted)
        {
            Fail("mesh.bin: " + parse.Exception?.GetBaseException().Message);
            yield break;
        }

        Mesh mesh = BuildMesh(parse.Result, patientId);
        Apply(mesh, parse.Result, meta);

        loadedPatientId = patientId;
        failedPatientId = null;
        loadedLabel = $"Patient {meta.source?.series_id?.Substring(0, Math.Min(6, meta.source.series_id.Length)) ?? patientId}";
        // The name given in "Send to VR", if any (the package list has it; meta.json doesn't).
        using (var req = UnityWebRequest.Get(BackendBase + "/api/vr/patients"))
        {
            req.timeout = 5;
            yield return req.SendWebRequest();
            if (req.result == UnityWebRequest.Result.Success)
            {
                var list = JsonUtility.FromJson<PackageList>("{\"items\":" + req.downloadHandler.text + "}");
                foreach (PackageInfo p in list?.items ?? Array.Empty<PackageInfo>())
                    if (p.id == patientId && !string.IsNullOrWhiteSpace(p.label)) { loadedLabel = p.label; break; }
            }
        }
        Meta = meta;
        status = $"Showing {loadedLabel} ({meta.triangles:N0} triangles, {meta.structures?.Length ?? 0} structures)";
        Debug.Log("[PatientLungLoader] " + status);
        loading = false;
        OnPatientLoaded?.Invoke(meta);
    }

    void Fail(string why)
    {
        status = "Load failed - " + why;
        Debug.LogWarning("[PatientLungLoader] " + status);
        loading = false;
        retryFailedAt = Time.unscaledTime + 60f;
    }

    static Mesh BuildMesh(LungPackageMesh src, string name)
    {
        var vertices = new Vector3[src.VertexCount];
        for (int i = 0; i < vertices.Length; i++)
            vertices[i] = new Vector3(src.Positions[i * 3], src.Positions[i * 3 + 1], src.Positions[i * 3 + 2]);

        var mesh = new Mesh { name = "PatientLung_" + name, indexFormat = IndexFormat.UInt32 };
        mesh.SetVertices(vertices);
        mesh.subMeshCount = src.SubMeshes.Count;
        for (int s = 0; s < src.SubMeshes.Count; s++)
            mesh.SetTriangles(src.SubMeshes[s].Indices, s, false);
        mesh.RecalculateBounds();
        mesh.RecalculateNormals();
        return mesh;
    }

    void CaptureDefault()
    {
        if (captured)
            return;
        captured = true;

        Transform t = lung.transform;
        defaultMesh = lung.CurrentMesh;
        defaultLocalPosition = t.localPosition;
        defaultLocalRotation = t.localRotation;
        defaultLocalScale = t.localScale;

        Renderer r = lung.GetComponent<Renderer>();
        Bounds world = r != null ? r.bounds : new Bounds(t.position, Vector3.one);
        Transform parent = t.parent;
        parentScaleAtStart = parent != null ? parent.lossyScale.x : 1f;
        anchorInParent = parent != null ? parent.InverseTransformPoint(world.center) : world.center;
        // Renderer bounds include the breathing allowance; close enough for sizing.
        longestInParent = Mathf.Max(world.size.x, Mathf.Max(world.size.y, world.size.z)) / parentScaleAtStart;
    }

    void Apply(Mesh mesh, LungPackageMesh src, PatientMeta meta)
    {
        CaptureDefault();
        Transform t = lung.transform;
        Transform parent = t.parent;

        src.GetBounds(out float[] mn, out float[] mx);
        var size = new Vector3(mx[0] - mn[0], mx[1] - mn[1], mx[2] - mn[2]);
        var centre = new Vector3((mx[0] + mn[0]) * 0.5f, (mx[1] + mn[1]) * 0.5f, (mx[2] + mn[2]) * 0.5f);
        float longestMm = Mathf.Max(size.x, Mathf.Max(size.y, size.z));

        // Size: same as the default model, or life size (mm -> m).
        float targetInParent = realSize ? longestMm * 0.001f / parentScaleAtStart : longestInParent;
        float scale = targetInParent / Mathf.Max(1e-3f, longestMm);

        // Face the user: the mesh's -Z is the patient's front. Faced towards the XR Origin
        // (where the scene setup lays out the lung and panels), not the headset: the head
        // can be anywhere when a patient arrives mid-session, which turned the lung sideways.
        XROrigin origin = FindFirstObjectByType<XROrigin>();
        Camera cam = Camera.main;
        Vector3 viewer = origin != null ? origin.transform.position
                       : cam != null ? cam.transform.position : Vector3.zero;
        Vector3 worldAnchor = parent != null ? parent.TransformPoint(anchorInParent) : anchorInParent;
        Vector3 away = worldAnchor - viewer;
        away.y = 0f;
        if (away.sqrMagnitude < 1e-6f) away = Vector3.forward;
        Quaternion worldRotation = Quaternion.LookRotation(away.normalized, Vector3.up);

        t.localRotation = parent != null ? Quaternion.Inverse(parent.rotation) * worldRotation : worldRotation;
        t.localScale = Vector3.one * scale;
        t.localPosition = anchorInParent - t.localRotation * (centre * scale);

        // Keep the default mesh alive (destroyPrevious only for an earlier patient's).
        lung.ReplaceMesh(mesh, destroyPrevious: lung.CurrentMesh != defaultMesh);
        FitInspectionCollider();
        if (airflow != null)
            airflow.Rebuild();
    }

    /// <summary>Puts the built-in lung model back.</summary>
    public void ShowDefaultLung()
    {
        if (!captured || loading)
            return;
        Transform t = lung.transform;
        t.localPosition = defaultLocalPosition;
        t.localRotation = defaultLocalRotation;
        t.localScale = defaultLocalScale;
        lung.ReplaceMesh(defaultMesh, destroyPrevious: true);
        FitInspectionCollider();
        if (airflow != null)
            airflow.Rebuild();

        // Don't bring this patient straight back on the next poll; a different one sent
        // from the website still loads. (Turning followActivePatient off here stopped
        // polling for good - nothing turned it back on.)
        dismissedPatientId = loadedPatientId;
        loadedPatientId = "";
        loadedLabel = "Default lung model";
        Meta = null;
        status = "Showing the default lung model";
        OnPatientLoaded?.Invoke(null);
    }

    // The inspection pivot's BoxCollider is what the controller ray hits; keep it around the lung.
    void FitInspectionCollider()
    {
        Transform parent = lung.transform.parent;
        if (parent == null) return;
        var box = parent.GetComponent<BoxCollider>();
        var r = lung.GetComponent<Renderer>();
        if (box == null || r == null) return;

        Bounds world = r.bounds;
        Vector3 c = parent.InverseTransformPoint(world.center);
        Vector3 s = parent.InverseTransformVector(world.size);
        box.center = c;
        box.size = new Vector3(Mathf.Abs(s.x), Mathf.Abs(s.y), Mathf.Abs(s.z));
    }
}
