using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using UnityEngine.XR;
using UnityEngine.XR.Interaction.Toolkit.Interactors;
using UnityEngine.XR.Interaction.Toolkit.UI;
using Unity.XR.CoreUtils;

/// <summary>
/// Desktop mode: the same scene - lungs, vitals monitor, every slider - in a normal
/// window with mouse and keyboard, no headset. This is what the LungVision website's
/// "View in Unity" opens (the backend starts the Windows build with -desktop).
///
/// Turns on by itself whenever no XR headset is running (the Windows build has no XR
/// loader, so it always is; in the Editor it is the Game view), or with -desktop.
///  - the camera stops following head tracking and is pulled back so the monitor, the
///    lung and the console all fit the window (refits on resize and when a patient loads);
///  - world-space panels take mouse clicks; the controller ray is hidden;
///  - lung: right-drag rotates, wheel or +/- zooms, R resets (LungInspectionController).
/// </summary>
public class DesktopMode : MonoBehaviour
{
    public static bool Active { get; private set; }
    static DesktopMode instance;

    /// <summary>Re-frames the view, e.g. after a panel was added (TumorMarkers). No-op in VR.</summary>
    public static void Refit()
    {
        if (instance != null && instance.cam != null)
            instance.Fit();
    }

    [Tooltip("Empty space kept around the content, as a fraction of the view.")]
    [Range(0f, 0.3f)] public float margin = 0.06f;

    Camera cam;
    Transform origin;
    PatientLungLoader loader;
    LungInspectionController inspection;
    Vector2Int fittedFor;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        bool forced = Array.Exists(Environment.GetCommandLineArgs(),
            a => a.Equals("-desktop", StringComparison.OrdinalIgnoreCase));
        if (!forced && XRSettings.isDeviceActive)
            return;
        Active = true;
        new GameObject("DesktopMode").AddComponent<DesktopMode>();
    }

    IEnumerator Start()
    {
        instance = this;
        XROrigin xr = FindFirstObjectByType<XROrigin>();
        origin = xr != null ? xr.transform : null;
        cam = xr != null && xr.Camera != null ? xr.Camera : Camera.main;
        loader = FindFirstObjectByType<PatientLungLoader>();
        inspection = FindFirstObjectByType<LungInspectionController>();
        if (cam == null)
        {
            Debug.LogWarning("[DesktopMode] No camera in the scene.");
            yield break;
        }

        // Head tracking would otherwise reset the camera to the (absent) headset pose.
        foreach (var driver in cam.GetComponents<UnityEngine.InputSystem.XR.TrackedPoseDriver>())
            driver.enabled = false;
        // An untracked controller sits at the rig's feet with its ray drawn - in view once
        // the camera backs off.
        foreach (var interactor in FindObjectsByType<XRBaseInteractor>(FindObjectsSortMode.None))
            interactor.gameObject.SetActive(false);

        foreach (Canvas canvas in FindObjectsByType<Canvas>(FindObjectsSortMode.None))
        {
            if (canvas.renderMode != RenderMode.WorldSpace) continue;
            canvas.worldCamera = cam;  // GraphicRaycaster turns mouse positions into rays with it
            if (!canvas.TryGetComponent(out GraphicRaycaster _))
                canvas.gameObject.AddComponent<GraphicRaycaster>();
        }
        if (EventSystem.current != null && EventSystem.current.TryGetComponent(out XRUIInputModule module))
            module.enableMouseInput = true;

        if (loader != null)
            loader.OnPatientLoaded += _ => Fit();

        // Let the scene settle first (patient lung, panel layout run their Start this frame).
        yield return null;
        Fit();
    }

    void Update()
    {
        if (cam != null && fittedFor != new Vector2Int(Screen.width, Screen.height))
            Fit();
        if (Keyboard.current != null && Keyboard.current.rKey.wasPressedThisFrame && inspection != null)
            inspection.ResetView();
    }

    /// <summary>
    /// Places the camera straight back from the content, level, at the distance where
    /// every corner of the lung and the panels is inside the view.
    /// </summary>
    void Fit()
    {
        fittedFor = new Vector2Int(Screen.width, Screen.height);
        var points = new List<Vector3>();

        foreach (LungMeshDeformer lung in FindObjectsByType<LungMeshDeformer>(FindObjectsSortMode.None))
            if (lung.TryGetComponent(out Renderer r))
                AddCorners(points, r.bounds);
        var corners = new Vector3[4];
        foreach (Canvas canvas in FindObjectsByType<Canvas>(FindObjectsSortMode.None))
        {
            if (canvas.renderMode != RenderMode.WorldSpace || !canvas.isActiveAndEnabled) continue;
            ((RectTransform)canvas.transform).GetWorldCorners(corners);
            points.AddRange(corners);
        }
        if (points.Count == 0)
            return;

        Vector3 forward = origin != null ? origin.forward : cam.transform.forward;
        forward.y = 0f;
        forward = forward.sqrMagnitude > 1e-6f ? forward.normalized : Vector3.forward;
        Vector3 right = Vector3.Cross(Vector3.up, forward);

        var box = new Bounds(points[0], Vector3.zero);
        foreach (Vector3 p in points) box.Encapsulate(p);
        Vector3 centre = box.center;

        float tanV = Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad) * (1f - margin);
        float tanH = tanV * cam.aspect;
        float distance = 0.5f;
        foreach (Vector3 p in points)
        {
            Vector3 d = p - centre;
            float depth = Vector3.Dot(d, forward);
            distance = Mathf.Max(distance,
                Mathf.Abs(Vector3.Dot(d, right)) / tanH - depth,
                Mathf.Abs(d.y) / tanV - depth);
        }

        cam.transform.SetPositionAndRotation(centre - forward * distance, Quaternion.LookRotation(forward, Vector3.up));
    }

    static void AddCorners(List<Vector3> points, Bounds b)
    {
        for (int i = 0; i < 8; i++)
            points.Add(b.center + Vector3.Scale(b.extents,
                new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1)));
    }

    void OnGUI()
    {
        string patient = loader != null ? loader.status : "";
        GUI.Label(new Rect(12, Screen.height - 28, Screen.width - 24, 24),
            $"Right-drag: rotate lungs   Scroll or +/-: zoom   R: reset view      {patient}");
    }
}
