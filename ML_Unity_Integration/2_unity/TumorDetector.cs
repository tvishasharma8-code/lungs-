// TumorDetector.cs — calls the local tumor_server.py (Faster R-CNN v5) and shows
// flagged CT slices on a world-space VR panel.
//
// Setup (see instructions in chat):
//   1. Start the server on the laptop:  python tumor_server.py
//   2. Add this script to an empty GameObject (e.g. "TumorDetector")
//   3. Assign: Slice Image (RawImage), Status Text (TMP), optional Next/Prev/Run buttons
//   4. Set Dicom Folder to the patient's folder on the laptop
//
// Works over Quest Link (Unity runs on the laptop, so 127.0.0.1 is the laptop).

using System;
using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;

public class TumorDetector : MonoBehaviour
{
    [Header("Server")]
    public string serverUrl = "http://127.0.0.1:5000";
    [Tooltip("Patient DICOM folder on this laptop")]
    public string dicomFolder = @"I:\My Drive\Patients\lung_pet_ct_dx\Lung_Dx-A0003";
    [Range(0.05f, 0.95f)] public float threshold = 0.5f;
    public bool runOnStart = true;

    [Header("UI (world-space canvas)")]
    public RawImage sliceImage;     // shows the CT slice with red boxes
    public TMP_Text statusText;     // "Slice 13 of 27 — confidence 0.97"

    // ---- results (public so other scripts, e.g. a 3D marker, can read them) ----
    public PatientResult Result { get; private set; }
    public bool IsBusy { get; private set; }
    public event Action<PatientResult> OnResult;

    int current = -1;   // index into Result.flagged_slices

    void Start()
    {
        if (runOnStart) RunDetection();
    }

    // Hook these to VR buttons (Button.onClick)
    public void RunDetection()
    {
        if (!IsBusy) StartCoroutine(PredictPatient());
    }

    public void NextSlice() { Show(current + 1); }
    public void PrevSlice() { Show(current - 1); }

    IEnumerator PredictPatient()
    {
        IsBusy = true;
        SetStatus("Analysing CT scan…");

        // health check first, so a stopped server gives a clear message
        using (var ping = UnityWebRequest.Get(serverUrl + "/health"))
        {
            ping.timeout = 5;
            yield return ping.SendWebRequest();
            if (ping.result != UnityWebRequest.Result.Success)
            {
                SetStatus("Tumor server not running.\nStart: python tumor_server.py");
                Debug.LogError("[TumorDetector] Server unreachable: " + ping.error);
                IsBusy = false;
                yield break;
            }
        }

        var body = JsonUtility.ToJson(new PredictRequest { dicom_folder = dicomFolder, threshold = threshold });
        using (var req = new UnityWebRequest(serverUrl + "/predict_patient", "POST"))
        {
            req.uploadHandler = new UploadHandlerRaw(System.Text.Encoding.UTF8.GetBytes(body));
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            req.timeout = 0;   // no timeout: a big patient can take minutes on CPU the first time
            yield return req.SendWebRequest();

            if (req.result != UnityWebRequest.Result.Success)
            {
                SetStatus("Detection failed:\n" + req.downloadHandler.text);
                Debug.LogError("[TumorDetector] " + req.error + " " + req.downloadHandler.text);
                IsBusy = false;
                yield break;
            }

            Result = JsonUtility.FromJson<PatientResult>(req.downloadHandler.text);
        }

        Debug.Log($"[TumorDetector] {Result.total_slices} slices, {Result.flagged_count} flagged, {Result.seconds}s");
        IsBusy = false;
        OnResult?.Invoke(Result);

        if (Result.flagged_count == 0)
        {
            SetStatus($"No tumor detected\n({Result.total_slices} slices scanned)");
            yield break;
        }
        Show(0);
    }

    void Show(int index)
    {
        if (Result == null || Result.flagged_count == 0 || IsBusy) return;
        current = (index % Result.flagged_count + Result.flagged_count) % Result.flagged_count; // wrap around
        var s = Result.flagged_slices[current];
        SetStatus($"TUMOR DETECTED — slice {s.slice} of {Result.total_slices}\n" +
                  $"Confidence {s.max_confidence:0.00}   ({current + 1}/{Result.flagged_count} flagged)");
        StartCoroutine(LoadSliceImage(s.slice));
    }

    IEnumerator LoadSliceImage(int slice)
    {
        if (sliceImage == null) yield break;
        string url = $"{serverUrl}/slice_image?dicom_folder={UnityWebRequest.EscapeURL(dicomFolder)}" +
                     $"&slice={slice}&threshold={threshold.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        using (var req = UnityWebRequestTexture.GetTexture(url))
        {
            yield return req.SendWebRequest();
            if (req.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError("[TumorDetector] slice image failed: " + req.error);
                yield break;
            }
            if (sliceImage.texture != null && sliceImage.texture is Texture2D old) Destroy(old);
            sliceImage.texture = DownloadHandlerTexture.GetContent(req);
        }
    }

    void SetStatus(string msg)
    {
        if (statusText != null) statusText.text = msg;
    }

    // ---------------- JSON shapes (match tumor_server.py) ----------------
    [Serializable] class PredictRequest { public string dicom_folder; public float threshold; }

    [Serializable] public class PatientResult
    {
        public string dicom_folder;
        public float threshold;
        public int total_slices;
        public int flagged_count;
        public bool tumor_detected;
        public float seconds;
        public FlaggedSlice[] flagged_slices;
    }

    [Serializable] public class FlaggedSlice
    {
        public int slice;
        public string file;
        public float z_mm;
        public float max_confidence;
        public ImageSize image_size;
        public Detection[] detections;
    }

    [Serializable] public class ImageSize { public int width; public int height; }

    [Serializable] public class Detection
    {
        public float confidence;
        public Box box;
        public Point center_px;
        public float[] center_mm;   // [x, y, z] DICOM patient mm (for 3D placement later)
    }

    [Serializable] public class Box { public float x1, y1, x2, y2; }
    [Serializable] public class Point { public float x, y; }
}
