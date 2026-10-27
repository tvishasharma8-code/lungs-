// TumorDemoSetup.cs — ONE-CLICK setup for the tumor-on-lung demo.
// Put this file in:  Assets/Editor/TumorDemoSetup.cs   (the folder MUST be called "Editor")
// Then in Unity's top menu:  Tools > Tumor Demo > Setup Scene
//
// It does all the wiring for you:
//   - finds the lung mesh in the SCENE (the object that has the calib_* children)
//   - finds TumorDetector, adds TumorMarker3D if missing, fills every slot
//   - finds the *_meta.json and sets the DICOM folder to the SAME patient as the mesh
//   - creates the pink see-through lung material + red tumor material and applies them
//   - points the Main Camera at the lung
//   - saves the scene

#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class TumorDemoSetup
{
    const string MatFolder = "Assets/TumorDemo";

    [MenuItem("Tools/Tumor Demo/Setup Scene")]
    public static void Setup()
    {
        if (EditorApplication.isPlaying)
        {
            EditorUtility.DisplayDialog("Tumor Demo", "Stop Play mode first, then run Setup again.", "OK");
            return;
        }

        // 1. lung mesh in the scene = object with a child named calib_origin
        Transform lungRoot = null;
        foreach (var t in Object.FindObjectsByType<Transform>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            if (t.name.ToLower().Contains("calib_origin") && t.parent != null) { lungRoot = t.parent; break; }
        if (lungRoot == null) { Fail("No lung mesh with calibration cubes in the scene.\nDrag A0265_lungs from Assets into the Hierarchy first."); return; }

        // 2. TumorDetector + TumorMarker3D
        var detector = Object.FindFirstObjectByType<TumorDetector>();
        if (detector == null) { Fail("No TumorDetector in the scene."); return; }
        var marker = detector.GetComponent<TumorMarker3D>() ?? detector.gameObject.AddComponent<TumorMarker3D>();

        // 3. meta json (prefer the one matching the lung's name)
        TextAsset meta = null;
        foreach (var guid in AssetDatabase.FindAssets("_meta t:TextAsset"))
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            var ta = AssetDatabase.LoadAssetAtPath<TextAsset>(path);
            if (ta == null) continue;
            if (meta == null || Path.GetFileNameWithoutExtension(path).StartsWith(lungRoot.name)) meta = ta;
        }
        if (meta == null) { Fail("Couldn't find the *_meta.json in Assets. Drag it into Assets."); return; }

        // 4. materials
        if (!AssetDatabase.IsValidFolder(MatFolder)) AssetDatabase.CreateFolder("Assets", "TumorDemo");
        var lungMat = GetOrCreate($"{MatFolder}/LungSeeThrough.mat");
        MakeFade(lungMat, new Color(0.94f, 0.60f, 0.63f, 0.45f));
        var tumorMat = GetOrCreate($"{MatFolder}/TumorRedGlow.mat");
        tumorMat.color = new Color(0.9f, 0.08f, 0.08f, 1f);
        tumorMat.EnableKeyword("_EMISSION");
        tumorMat.SetColor("_EmissionColor", new Color(0.7f, 0.05f, 0.05f));
        EditorUtility.SetDirty(lungMat); EditorUtility.SetDirty(tumorMat);

        // apply the lung material on the scene object itself (so it's pink before Play too)
        foreach (var r in lungRoot.GetComponentsInChildren<Renderer>(true))
        {
            Undo.RecordObject(r, "Tumor demo material");
            if (r.name.ToLower().Contains("calib")) r.enabled = false;
            else r.sharedMaterial = lungMat;
        }

        // 5. wire everything
        Undo.RecordObject(marker, "Tumor demo setup");
        marker.detector = detector;
        marker.lungRoot = lungRoot;
        marker.meshMeta = meta;
        marker.lungMaterialOverride = lungMat;
        marker.markerMaterialOverride = tumorMat;
        marker.makeLungTransparent = true;

        var info = JsonUtility.FromJson<MetaInfo>(meta.text);
        Undo.RecordObject(detector, "Tumor demo setup");
        if (info != null && !string.IsNullOrEmpty(info.source_dicom_folder))
            detector.dicomFolder = info.source_dicom_folder.Trim();   // SAME patient as the mesh

        // 6. camera in front of the lung (patient's front faces -Z)
        var cam = Camera.main;
        var bounds = LungBounds(lungRoot);
        if (cam != null && bounds.size != Vector3.zero)
        {
            Undo.RecordObject(cam.transform, "Tumor demo camera");
            float dist = Mathf.Max(bounds.size.x, bounds.size.y) * 1.6f;
            cam.transform.position = bounds.center - lungRoot.forward * dist;
            cam.transform.rotation = Quaternion.LookRotation(bounds.center - cam.transform.position, lungRoot.up);
            cam.nearClipPlane = Mathf.Min(cam.nearClipPlane, dist * 0.05f);
        }

        EditorUtility.SetDirty(marker); EditorUtility.SetDirty(detector);
        AssetDatabase.SaveAssets();
        EditorSceneManager.MarkSceneDirty(lungRoot.gameObject.scene);
        EditorSceneManager.SaveScene(lungRoot.gameObject.scene);

        EditorUtility.DisplayDialog("Tumor Demo",
            "Setup done!\n\n" +
            $"Lung: {lungRoot.name}\nMeta: {meta.name}\nDICOM folder: {detector.dicomFolder}\n\n" +
            "Make sure tumor_server.py is running, then press Play.", "OK");
        Selection.activeGameObject = lungRoot.gameObject;
    }

    static Material GetOrCreate(string path)
    {
        var m = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (m != null) return m;
        m = new Material(Shader.Find("Standard"));
        AssetDatabase.CreateAsset(m, path);
        return m;
    }

    static void MakeFade(Material m, Color c)
    {
        m.color = c;
        m.SetFloat("_Mode", 2);
        m.SetOverrideTag("RenderType", "Transparent");
        m.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        m.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        m.SetInt("_ZWrite", 0);
        m.DisableKeyword("_ALPHATEST_ON");
        m.EnableKeyword("_ALPHABLEND_ON");
        m.DisableKeyword("_ALPHAPREMULTIPLY_ON");
        m.renderQueue = 3000;
    }

    static Bounds LungBounds(Transform root)
    {
        Bounds b = new Bounds();
        bool any = false;
        foreach (var r in root.GetComponentsInChildren<Renderer>(true))
        {
            if (r.name.ToLower().Contains("calib")) continue;
            if (!any) { b = r.bounds; any = true; } else b.Encapsulate(r.bounds);
        }
        return b;
    }

    static void Fail(string msg)
    {
        Debug.LogError("[TumorDemoSetup] " + msg);
        EditorUtility.DisplayDialog("Tumor Demo", msg, "OK");
    }

    [System.Serializable] class MetaInfo { public string source_dicom_folder; }
}
#endif
