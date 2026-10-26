using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;
using Unity.XR.CoreUtils;

/// <summary>
/// Adds rotate-and-zoom inspection to the lung using the XR Interaction Toolkit that is
/// already in the project (3.3.1). Nothing new is installed and no second interaction
/// framework is introduced.
///
/// What it builds:
///   1. A pivot GameObject at the lung's VISUAL centre, with the lung parented under it.
///      Necessary because the FBX's own transform origin sits well outside the mesh, so
///      rotating the lung directly would orbit it around a point in empty space.
///   2. A BoxCollider on the pivot sized to the lung's bounds - the ray interactor needs
///      something to hit. Deliberately a box and not a MeshCollider: the mesh is 730k
///      vertices, and a collider of that density would be enormously expensive to raycast
///      against every frame on a Quest for no perceptible benefit.
///   3. An XRSimpleInteractable, NOT an XRGrabInteractable. Grab interactables exist to
///      move objects; simple ones only report selection. Translation is therefore never
///      implemented rather than merely switched off.
///   4. LungInspectionController, which applies rotation and zoom and re-asserts the
///      anchored position every frame.
///
/// Re-running is safe: existing objects are reused rather than duplicated.
/// </summary>
public static class LungInspectionSetup
{
    const string PivotName = "LungInspectionPivot";

    [MenuItem("BioGears/Add VR Lung Inspection (Rotate + Zoom)")]
    public static void Run()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            Debug.Log("[LungInspectionSetup] Play mode is active - exiting Play mode, then setting up.");
            EditorApplication.playModeStateChanged += Resume;
            EditorApplication.isPlaying = false;
            return;
        }

        Setup();
    }

    static void Resume(PlayModeStateChange state)
    {
        if (state != PlayModeStateChange.EnteredEditMode) return;
        EditorApplication.playModeStateChanged -= Resume;
        EditorApplication.delayCall += Setup;
    }

    static void Setup()
    {
        LungMeshDeformer[] deformers = Object.FindObjectsByType<LungMeshDeformer>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);
        if (deformers.Length == 0)
        {
            Debug.LogError("[LungInspectionSetup] No LungMeshDeformer in the open scene.");
            return;
        }

        Transform lung = deformers[0].transform;

        if (!TryGetWorldBounds(lung, out Bounds bounds))
        {
            Debug.LogError("[LungInspectionSetup] Could not measure the lung's renderer bounds.");
            return;
        }

        // ---- 1. pivot at the visual centre ------------------------------------
        Transform pivot = lung.parent != null && lung.parent.name == PivotName
            ? lung.parent
            : null;

        if (pivot == null)
        {
            GameObject go = new GameObject(PivotName);
            pivot = go.transform;
            pivot.SetParent(lung.parent, false);
            pivot.position = bounds.center;
            pivot.rotation = Quaternion.identity;
            pivot.localScale = Vector3.one;

            // true = keep the lung's world transform. Its own position, rotation and
            // scale are all preserved exactly; only its parent changes.
            lung.SetParent(pivot, true);

            Debug.Log($"[LungInspectionSetup] Created '{PivotName}' at the lung's visual centre {bounds.center}. " +
                      "The lung keeps its exact world transform; only its parent changed.");
        }
        else
        {
            // Re-run: re-centre in case the layout moved the lung since.
            pivot.position = bounds.center;
        }

        // ---- 2. collider for the ray to hit -----------------------------------
        BoxCollider box = pivot.GetComponent<BoxCollider>();
        if (box == null)
            box = pivot.gameObject.AddComponent<BoxCollider>();

        box.isTrigger = false;
        box.center = Vector3.zero;
        // Local size, compensating for any scale on the pivot's parents.
        Vector3 lossy = pivot.lossyScale;
        box.size = new Vector3(
            bounds.size.x / Mathf.Max(0.0001f, lossy.x),
            bounds.size.y / Mathf.Max(0.0001f, lossy.y),
            bounds.size.z / Mathf.Max(0.0001f, lossy.z));

        // ---- 3. interactable (selection only, never translation) ---------------
        XRSimpleInteractable interactable = pivot.GetComponent<XRSimpleInteractable>();
        if (interactable == null)
            interactable = pivot.gameObject.AddComponent<XRSimpleInteractable>();

        XRInteractionManager manager = Object.FindFirstObjectByType<XRInteractionManager>();
        if (manager != null)
            interactable.interactionManager = manager;

        // Match whatever layers the existing ray interactor uses, otherwise the two
        // never see each other. The rig's interactor was configured with a non-default
        // layer mask, so copying it is safer than assuming Default.
        XRRayInteractor ray = Object.FindFirstObjectByType<XRRayInteractor>();
        if (ray != null)
            interactable.interactionLayers = ray.interactionLayers;

        // ---- 4. the controller -------------------------------------------------
        LungInspectionController controller = pivot.GetComponent<LungInspectionController>();
        if (controller == null)
            controller = pivot.gameObject.AddComponent<LungInspectionController>();

        SerializedObject so = new SerializedObject(controller);
        so.FindProperty("pivot").objectReferenceValue = pivot;
        so.FindProperty("interactable").objectReferenceValue = interactable;

        XROrigin origin = Object.FindFirstObjectByType<XROrigin>();
        if (origin != null)
            so.FindProperty("xrOrigin").objectReferenceValue = origin.transform;

        so.ApplyModifiedProperties();

        // ---- 5. make sure the ray can actually reach it ------------------------
        if (ray != null)
        {
            float distance = Vector3.Distance(
                ray.transform.position.sqrMagnitude > 0.001f ? ray.transform.position : Vector3.zero,
                bounds.center);

            if (ray.maxRaycastDistance < distance + 1f)
            {
                Debug.LogWarning($"[LungInspectionSetup] Ray interactor reaches {ray.maxRaycastDistance} m but the lung " +
                                 $"is about {distance:F1} m away. Raise Max Raycast Distance on RightHandController if it will not select.");
            }
        }

        EditorUtility.SetDirty(controller);
        EditorSceneManager.MarkSceneDirty(pivot.gameObject.scene);
        EditorSceneManager.SaveScene(pivot.gameObject.scene);

        Debug.Log("[LungInspectionSetup] VR lung inspection ready. Trigger-hold and turn the controller to rotate; " +
                  "thumbstick up/down to zoom. The lung's position is re-asserted every frame, so it cannot drift.");
    }

    static bool TryGetWorldBounds(Transform root, out Bounds bounds)
    {
        bounds = default;
        bool found = false;

        foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true))
        {
            if (r == null || r.gameObject.name == "LungSootOverlay")
                continue;

            if (!found) { bounds = r.bounds; found = true; }
            else bounds.Encapsulate(r.bounds);
        }

        return found;
    }
}
