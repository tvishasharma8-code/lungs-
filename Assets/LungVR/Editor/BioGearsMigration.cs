using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// One-time migration: LungMeshDeformer/VRUIController/VRControls/VRStatsDisplay used
/// to reference SimulatedBioGearsRespiratory directly; they now reference
/// BioGearsNativeRespiratory (which wraps the real BioGears engine and falls back to
/// SimulatedBioGearsRespiratory automatically). Changing those field types breaks the
/// old serialized scene references, so this adds BioGearsNativeRespiratory next to
/// every existing SimulatedBioGearsRespiratory and repoints the four consumers at it.
/// Safe to delete after running once - re-run is a no-op if already migrated.
/// </summary>
public static class BioGearsMigration
{
    [MenuItem("BioGears/Migrate Scene To Native Respiratory System")]
    public static void Run()
    {
        string scenePath = "Assets/Scenes/SampleScene.unity";
        Scene scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);

        int patched = 0;

        foreach (GameObject root in scene.GetRootGameObjects())
        {
            foreach (SimulatedBioGearsRespiratory sim in root.GetComponentsInChildren<SimulatedBioGearsRespiratory>(true))
            {
                GameObject go = sim.gameObject;

                BioGearsNativeRespiratory native = go.GetComponent<BioGearsNativeRespiratory>();
                if (native == null)
                {
                    native = go.AddComponent<BioGearsNativeRespiratory>();
                    Debug.Log($"[BioGearsMigration] Added BioGearsNativeRespiratory to '{go.name}'");
                }
                if (native.fallback == null)
                    native.fallback = sim;

                PatchReferences(scene, native);
                patched++;
            }
        }

        EditorSceneManager.MarkSceneDirty(scene);
        bool saved = EditorSceneManager.SaveScene(scene);
        Debug.Log($"[BioGearsMigration] Done. Patched {patched} respiratory system(s) in '{scenePath}'. Saved: {saved}");
    }

    static void PatchReferences(Scene scene, BioGearsNativeRespiratory native)
    {
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            foreach (LungMeshDeformer c in root.GetComponentsInChildren<LungMeshDeformer>(true))
                AssignIfEmpty(c, "bioGearsSystem", native);

            foreach (VRUIController c in root.GetComponentsInChildren<VRUIController>(true))
                AssignIfEmpty(c, "bio", native);

            foreach (VRControls c in root.GetComponentsInChildren<VRControls>(true))
                AssignIfEmpty(c, "sim", native);

            foreach (VRStatsDisplay c in root.GetComponentsInChildren<VRStatsDisplay>(true))
                AssignIfEmpty(c, "sim", native);
        }
    }

    static void AssignIfEmpty(Object target, string fieldName, BioGearsNativeRespiratory value)
    {
        var so = new SerializedObject(target);
        var prop = so.FindProperty(fieldName);
        if (prop == null)
        {
            Debug.LogWarning($"[BioGearsMigration] '{fieldName}' not found on {target.GetType().Name} ({(target as Component)?.gameObject.name})");
            return;
        }
        if (prop.objectReferenceValue == null)
        {
            prop.objectReferenceValue = value;
            so.ApplyModifiedProperties();
            Debug.Log($"[BioGearsMigration] Set {target.GetType().Name}.{fieldName} on '{(target as Component)?.gameObject.name}' -> {value.gameObject.name}");
        }
    }
}
